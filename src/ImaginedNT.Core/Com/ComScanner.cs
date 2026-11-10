using System.Reflection;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Core.Com;

/// <summary>
/// Discovers <see cref="VirtualComInterfaceAttribute"/> interfaces and
/// <see cref="VirtualComClassAttribute"/>/<see cref="PartiallyImaginedComClassAttribute"/> classes
/// in a managed API assembly and registers them with the COM runtime.
/// </summary>
public static class ComScanner
{
    private const BindingFlags AllInstanceMethods =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    public static int Register(Assembly assembly, ComRuntime com, TraceLog? trace = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(com);
        var types = LoadableTypes(assembly);
        var declaredInterfaces = new HashSet<Guid>();

        // Pass 1: interface metadata, including names usable from attribute arguments.
        foreach (var type in types)
        {
            var attribute = type.GetCustomAttribute<VirtualComInterfaceAttribute>(inherit: false);
            if (attribute is null) continue;
            if (!type.IsInterface)
                throw new InvalidOperationException(
                    $"{type.FullName}: [VirtualComInterface] is only valid on an interface");
            var interfaceId = ParseGuid(attribute.InterfaceId, $"{type.FullName} interface id");
            declaredInterfaces.Add(interfaceId);
            var methods = new Dictionary<int, string>();
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                                                   BindingFlags.DeclaredOnly))
            {
                foreach (var slotAttribute in method.GetCustomAttributes<VirtualComMethodAttribute>(inherit: false))
                {
                    var slotInterface = com.ResolveInterfaceId(slotAttribute.InterfaceId);
                    if (slotInterface != interfaceId)
                        throw new InvalidOperationException(
                            $"{type.FullName}.{method.Name}: slot declares interface {slotAttribute.InterfaceId} " +
                            $"but the enclosing interface is {attribute.InterfaceId}");
                    var name = slotAttribute.Name ?? method.Name;
                    if (methods.TryGetValue(slotAttribute.Slot, out var existing) &&
                        !string.Equals(existing, name, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"{type.FullName}: slot {slotAttribute.Slot} is declared as both '{existing}' and '{name}'");
                    methods[slotAttribute.Slot] = name;
                }
            }
            ValidateSlots(methods, type.FullName ?? type.Name);
            com.RegisterInterface(new ComInterfaceDefinition(interfaceId, attribute.Name ?? type.Name, methods));
        }

        // Pass 2: classes and their method bindings.
        var registered = 0;
        foreach (var type in types)
        {
            var classAttribute = type.GetCustomAttribute<VirtualComClassAttribute>(inherit: false);
            if (classAttribute is null) continue;
            if (!type.IsClass || type.IsAbstract)
                throw new InvalidOperationException(
                    $"{type.FullName}: [VirtualComClass] requires a concrete class");

            var partialAttribute = classAttribute as PartiallyImaginedComClassAttribute;
            var isPartial = partialAttribute is not null;
            var classId = ParseGuid(classAttribute.ClassId, $"{type.FullName} class id");
            var hostClassId = partialAttribute is null
                ? (Guid?)null
                : ParseGuid(partialAttribute.HostClassId, $"{type.FullName} host class id");

            var bindings = new Dictionary<Guid, Dictionary<int, ComMethodBinding>>();
            var missingInterface = Guid.Empty;

            void Add(Guid interfaceId, ComMethodBinding binding)
            {
                // Only classes whose interfaces are known to this runtime can be materialized.
                // A test/host assembly may be registered alone while a COM class refers to an
                // interface owned by another API assembly; skip that class instead of failing the
                // whole assembly registration.
                if (!com.TryGetInterface(interfaceId, out _))
                {
                    if (missingInterface == Guid.Empty) missingInterface = interfaceId;
                    return;
                }
                if (!bindings.TryGetValue(interfaceId, out var slots))
                    bindings[interfaceId] = slots = new Dictionary<int, ComMethodBinding>();
                if (slots.TryGetValue(binding.Slot, out var existing) && existing.Method != binding.Method)
                    throw new InvalidOperationException(
                        $"{type.FullName}: COM slot {binding.Slot} of {interfaceId:B} is claimed by both " +
                        $"{existing.Method.Name} and {binding.Method.Name}");
                slots[binding.Slot] = binding;
            }

            // Direct [VirtualComMethod] methods. Partially imagined classes use these as overrides;
            // fully managed classes may also use them instead of implementing the interface.
            foreach (var method in type.GetMethods(AllInstanceMethods))
            {
                foreach (var methodAttribute in method.GetCustomAttributes<VirtualComMethodAttribute>(inherit: false))
                {
                    ValidateMethod(type, method, methodAttribute.Name ?? method.Name);
                    var interfaceId = com.ResolveInterfaceId(methodAttribute.InterfaceId);
                    var hostSlot = methodAttribute is PartiallyImaginedComMethodAttribute partialMethod &&
                                   partialMethod.HostSlot >= 0
                        ? partialMethod.HostSlot
                        : methodAttribute.Slot;
                    Add(interfaceId, new ComMethodBinding(method, methodAttribute.Slot,
                        methodAttribute.Name ?? method.Name, hostSlot));
                }
            }

            // Interfaces implemented by the class: bind through the interface map so the class can
            // implement a normal C# interface with COM-style methods.
            foreach (var interfaceType in type.GetInterfaces())
            {
                var interfaceAttribute = interfaceType.GetCustomAttribute<VirtualComInterfaceAttribute>(inherit: false);
                if (interfaceAttribute is null) continue;
                var interfaceId = ParseGuid(interfaceAttribute.InterfaceId, $"{interfaceType.FullName} interface id");
                if (!com.TryGetInterface(interfaceId, out _))
                {
                    if (missingInterface == Guid.Empty) missingInterface = interfaceId;
                    continue;
                }

                var map = type.GetInterfaceMap(interfaceType);
                for (var index = 0; index < map.InterfaceMethods.Length; index++)
                {
                    var interfaceMethod = map.InterfaceMethods[index];
                    var target = map.TargetMethods[index];
                    foreach (var methodAttribute in interfaceMethod.GetCustomAttributes<VirtualComMethodAttribute>(inherit: false))
                    {
                        var slotInterface = com.ResolveInterfaceId(methodAttribute.InterfaceId);
                        if (slotInterface != interfaceId)
                            throw new InvalidOperationException(
                                $"{interfaceType.FullName}.{interfaceMethod.Name}: slot belongs to {methodAttribute.InterfaceId}, " +
                                $"not the enclosing interface {interfaceAttribute.InterfaceId}");
                        ValidateMethod(type, target, $"{interfaceType.Name}.{interfaceMethod.Name}");
                        Add(interfaceId, new ComMethodBinding(target, methodAttribute.Slot,
                            methodAttribute.Name ?? interfaceMethod.Name));
                    }
                }
            }

            if (missingInterface != Guid.Empty)
            {
                if (declaredInterfaces.Contains(missingInterface))
                    throw new InvalidOperationException(
                        $"{type.FullName}: COM interface {missingInterface:B} was declared in this assembly " +
                        "but is not registered in this runtime");
                trace?.Warn(
                    $"COM class {type.FullName} was skipped: interface {missingInterface:B} is not registered " +
                    "in this runtime (register the API assembly that declares it first)");
                continue;
            }

            if (bindings.Count == 0)
                throw new InvalidOperationException(
                    $"{type.FullName}: COM class does not declare any interface methods");

            var interfaceBindings = new List<ComInterfaceBinding>();
            foreach (var (interfaceId, slots) in bindings)
            {
                if (!com.TryGetInterface(interfaceId, out var definition) || definition is null)
                    throw new InvalidOperationException(
                        $"{type.FullName}: COM interface {interfaceId:B} is not registered");
                if (!isPartial)
                {
                    foreach (var slot in definition.Methods.Keys)
                    {
                        if (!slots.ContainsKey(slot))
                            throw new InvalidOperationException(
                                $"{type.FullName}: fully managed COM class does not implement " +
                                $"{definition.Name} slot {slot} ({definition.NameOf(slot)})");
                    }
                }
                interfaceBindings.Add(new ComInterfaceBinding(definition, slots));
            }

            var constructor = type.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (constructor is null)
                throw new InvalidOperationException(
                    $"{type.FullName}: [VirtualComClass] requires a parameterless constructor");

            var descriptor = new ComClassDescriptor
            {
                ClassId = classId,
                Name = classAttribute.Name ?? type.Name,
                ThreadingModel = classAttribute.ThreadingModel,
                Interfaces = interfaceBindings,
                HostClassId = hostClassId,
                Factory = () => Activator.CreateInstance(type, nonPublic: true) ??
                                throw new InvalidOperationException($"could not create {type.FullName}")
            };
            com.RegisterClass(descriptor);
            registered++;
        }
        return registered;
    }

    private static void ValidateSlots(IReadOnlyDictionary<int, string> methods, string owner)
    {
        if (methods.Count == 0) return;
        var max = methods.Keys.Max();
        for (var slot = 3; slot <= max; slot++)
        {
            if (!methods.ContainsKey(slot))
                throw new InvalidOperationException(
                    $"{owner}: COM vtable slots must be contiguous from 3; slot {slot} is missing");
        }
    }

    private static void ValidateMethod(Type type, MethodInfo method, string displayName)
    {
        if (method.IsStatic)
            throw new InvalidOperationException(
                $"{type.FullName}.{method.Name}: COM method implementations must be instance methods");
        if (method.ReturnType != typeof(ulong))
            throw new InvalidOperationException(
                $"{type.FullName}.{method.Name}: COM method implementations must return ulong (HRESULT)");
        var parameters = method.GetParameters();
        if (parameters.Length != 1 || parameters[0].ParameterType != typeof(ComCallContext))
            throw new InvalidOperationException(
                $"{type.FullName}.{method.Name}: COM method implementations must take a single " +
                $"{nameof(ComCallContext)} parameter ({displayName})");
        if (method.ContainsGenericParameters)
            throw new InvalidOperationException(
                $"{type.FullName}.{method.Name}: COM method implementations must not be generic");
    }

    private static Guid ParseGuid(string text, string what)
    {
        if (Guid.TryParse(text, out var guid)) return guid;
        throw new InvalidOperationException($"{what} is not a GUID: '{text}'");
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            throw new InvalidOperationException(
                $"could not enumerate COM classes in '{assembly.FullName}': " +
                string.Join("; ", ex.LoaderExceptions.OfType<Exception>().Select(e => e.Message)), ex);
        }
    }
}
