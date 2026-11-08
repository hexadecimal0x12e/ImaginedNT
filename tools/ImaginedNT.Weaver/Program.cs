using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ImaginedNT.Weaver;

/// <summary>
/// Post-build IL weaver that injects a call to
/// <c>ImaginedNT.Core.Diagnostics.MethodTrace.Enter(type, method, args)</c> at the start of every
/// method body in an instrumented copy of the ImaginedNT runtime.
///
/// <para>
/// The injected prologue is:
/// <code>
///   ldsfld  bool ImaginedNT.Core.Diagnostics.MethodTrace::Enabled
///   brfalse body
///   ldstr   "Namespace.Type"
///   ldstr   "Method"
///   ldc.i4  paramCount
///   newarr  object
///   ... boxed arguments ...
///   call    void ImaginedNT.Core.Diagnostics.MethodTrace::Enter(string, string, object[])
/// body:
///   ... original body ...
/// </code>
/// Instrumentation is entry-only: it records the call order (and arguments) without touching the
/// original control flow, return values, exception handlers or locals.
/// </para>
/// </summary>
internal static class Program
{
    private const string AbstractionsAssemblyName = "ImaginedNT.Abstractions";
    private const string TraceNamespace = "ImaginedNT.Core.Diagnostics";
    private const string TraceTypeName = "MethodTrace";
    private const string MarkerKey = "ImaginedNT.Instrumented";

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"weaver: {ex.GetType().Name}: {ex.Message}");
            if (Environment.GetEnvironmentVariable("IMAGINEDNT_DEBUG") == "1")
                Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        var target = Path.GetFullPath(args[0]);
        var options = new Options(
            IncludeGenerated: args.Contains("--include-generated", StringComparer.OrdinalIgnoreCase),
            IncludeAccessors: !args.Contains("--no-accessors", StringComparer.OrdinalIgnoreCase),
            Force: args.Contains("--force", StringComparer.OrdinalIgnoreCase),
            DryRun: args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase));

        var isDirectory = Directory.Exists(target);
        if (!isDirectory && !File.Exists(target))
        {
            Console.Error.WriteLine($"weaver: target not found: {target}");
            return 2;
        }

        var directory = isDirectory ? target : Path.GetDirectoryName(target)!;
        var abstractionsPath = Path.Combine(directory, AbstractionsAssemblyName + ".dll");
        if (!File.Exists(abstractionsPath))
        {
            Console.Error.WriteLine($"weaver: {abstractionsPath} not found; the target directory must contain the ImaginedNT runtime");
            return 2;
        }

        var paths = isDirectory
            ? Directory.GetFiles(directory, "ImaginedNT.*.dll")
                .Where(path => !string.Equals(Path.GetFileName(path), "ImaginedNT.Weaver.dll", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => string.Equals(Path.GetFileName(path), AbstractionsAssemblyName + ".dll", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [target];

        var resolver = new InMemoryResolver(directory);
        using var resolverScope = resolver;

        // Load the pristine Abstractions image once; it owns the MethodTrace type that every
        // rewritten assembly has to reference.
        using var abstractions = AssemblyDefinition.ReadAssembly(abstractionsPath, ReaderParameters(resolver));
        var traceType = abstractions.MainModule.GetType(TraceNamespace, TraceTypeName)
            ?? throw new InvalidOperationException($"{TraceNamespace}.{TraceTypeName} is missing from {AbstractionsAssemblyName}");
        var traceEnter = traceType.Methods.SingleOrDefault(method => method.Name == "Enter" && method.Parameters.Count == 3)
            ?? throw new InvalidOperationException("MethodTrace.Enter(string, string, object[]) is missing");
        var traceEnabled = traceType.Fields.SingleOrDefault(field => field.Name == "Enabled")
            ?? throw new InvalidOperationException("MethodTrace.Enabled is missing");

        var totalMethods = 0;
        var totalAssemblies = 0;

        foreach (var path in paths)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path, ReaderParameters(resolver));
            var name = Path.GetFileName(path);

            if (!options.Force && HasMarker(assembly))
            {
                Console.WriteLine($"skip   {name} (already instrumented)");
                continue;
            }

            var isAbstractions = string.Equals(assembly.Name.Name, AbstractionsAssemblyName, StringComparison.OrdinalIgnoreCase);
            MethodReference enterReference;
            FieldReference enabledReference;
            if (isAbstractions)
            {
                // Use the member definitions of the image being rewritten; a MethodDefinition from
                // the separately loaded 'abstractions' image belongs to another module.
                var currentTraceType = assembly.MainModule.GetType(TraceNamespace, TraceTypeName)
                    ?? throw new InvalidOperationException($"{TraceNamespace}.{TraceTypeName} is missing from {name}");
                enterReference = currentTraceType.Methods.Single(method => method.Name == "Enter" && method.Parameters.Count == 3);
                enabledReference = currentTraceType.Fields.Single(field => field.Name == "Enabled");
            }
            else
            {
                enterReference = assembly.MainModule.ImportReference(traceEnter);
                enabledReference = assembly.MainModule.ImportReference(traceEnabled);
            }

            var instrumented = 0;
            var skipped = 0;
            foreach (var module in assembly.Modules)
            {
                foreach (var type in module.GetTypes())
                {
                    if (type.Name == "<Module>") continue;
                    if (IsTraceType(type)) continue;
                    if (!options.IncludeGenerated && IsCompilerGenerated(type))
                    {
                        skipped += type.Methods.Count(HasBody);
                        continue;
                    }

                    foreach (var method in type.Methods)
                    {
                        if (!CanInstrument(method, options))
                        {
                            if (HasBody(method)) skipped++;
                            continue;
                        }

                        Inject(method, enabledReference, enterReference);
                        instrumented++;
                    }
                }
            }

            totalMethods += instrumented;
            totalAssemblies++;

            if (options.DryRun)
            {
                Console.WriteLine($"dry    {name,-38} {instrumented,6} methods ({skipped} skipped)");
                continue;
            }

            AddMarker(assembly);
            var temporary = path + ".instrumented";
            assembly.Write(temporary);
            File.Move(temporary, path, true);
            Console.WriteLine($"weave  {name,-38} {instrumented,6} methods ({skipped} skipped)");
        }

        Console.WriteLine($"done: {totalMethods} methods in {totalAssemblies} assemblies{(options.DryRun ? " (dry run)" : string.Empty)}");
        return 0;
    }

    private static ReaderParameters ReaderParameters(IAssemblyResolver resolver) => new()
    {
        AssemblyResolver = resolver,
        InMemory = true,
        ReadSymbols = false
    };

    private static bool HasBody(MethodDefinition method) => method.HasBody && !method.IsAbstract && !method.IsPInvokeImpl && !method.IsRuntime;

    private static bool CanInstrument(MethodDefinition method, Options options)
    {
        if (!HasBody(method)) return false;
        if (!options.IncludeAccessors && (method.IsGetter || method.IsSetter || method.IsAddOn || method.IsRemoveOn || method.IsFire))
            return false;
        if (!options.IncludeGenerated && HasAttribute(method, "System.Runtime.CompilerServices.CompilerGeneratedAttribute"))
            return false;
        if (HasAttribute(method, "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute"))
            return false;
        if (method.CallingConvention == MethodCallingConvention.VarArg) return false;
        return true;
    }

    private static bool IsTraceType(TypeDefinition type) =>
        type.FullName == TraceNamespace + "." + TraceTypeName;

    private static bool IsCompilerGenerated(TypeDefinition type) =>
        type.Name.StartsWith('<') ||
        type.Name.Contains("<>", StringComparison.Ordinal) ||
        HasAttribute(type, "System.Runtime.CompilerServices.CompilerGeneratedAttribute");

    private static bool HasAttribute(ICustomAttributeProvider provider, string fullName) =>
        provider.HasCustomAttributes && provider.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == fullName);

    private static bool HasMarker(AssemblyDefinition assembly) =>
        assembly.HasCustomAttributes && assembly.CustomAttributes.Any(attribute =>
            attribute.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute" &&
            attribute.ConstructorArguments.Count > 0 &&
            attribute.ConstructorArguments[0].Value is string key &&
            key == MarkerKey);

    private static void AddMarker(AssemblyDefinition assembly)
    {
        var constructor = typeof(System.Reflection.AssemblyMetadataAttribute)
            .GetConstructor([typeof(string), typeof(string)])!;
        var attribute = new CustomAttribute(assembly.MainModule.ImportReference(constructor));
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(assembly.MainModule.TypeSystem.String, MarkerKey));
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(assembly.MainModule.TypeSystem.String, "method-trace"));
        assembly.CustomAttributes.Add(attribute);
    }

    private static void Inject(MethodDefinition method, FieldReference enabledField, MethodReference enterMethod)
    {
        var body = method.Body;
        var il = body.GetILProcessor();

        if (body.Instructions.Count == 0)
            il.Append(il.Create(OpCodes.Ret));

        var anchor = body.Instructions[0];
        var prologue = new List<Instruction>
        {
            il.Create(OpCodes.Ldsfld, enabledField),
            il.Create(OpCodes.Brfalse, anchor),
            il.Create(OpCodes.Ldstr, method.DeclaringType.FullName),
            il.Create(OpCodes.Ldstr, method.Name),
            il.Create(OpCodes.Ldc_I4, method.Parameters.Count),
            il.Create(OpCodes.Newarr, method.Module.TypeSystem.Object)
        };

        for (var i = 0; i < method.Parameters.Count; i++)
        {
            var parameter = method.Parameters[i];
            var type = parameter.ParameterType;

            prologue.Add(il.Create(OpCodes.Dup));
            prologue.Add(il.Create(OpCodes.Ldc_I4, i));

            if (CanBox(type))
            {
                prologue.Add(il.Create(OpCodes.Ldarg, parameter));
                if (type.IsValueType || type is GenericParameter)
                    prologue.Add(il.Create(OpCodes.Box, type));
            }
            else
            {
                prologue.Add(il.Create(OpCodes.Ldnull));
            }

            prologue.Add(il.Create(OpCodes.Stelem_Ref));
        }

        prologue.Add(il.Create(OpCodes.Call, enterMethod));

        foreach (var instruction in prologue)
            il.InsertBefore(anchor, instruction);
    }

    /// <summary>Whether a parameter value can be placed into an <c>object[]</c>.</summary>
    private static bool CanBox(TypeReference type)
    {
        if (type is ByReferenceType or PointerType or FunctionPointerType or PinnedType or SentinelType)
            return false;

        if (type.FullName is "System.TypedReference" or "System.ArgIterator" or "System.RuntimeArgumentHandle")
            return false;

        if (type is GenericParameter) return true;

        var definition = type;
        if (type is GenericInstanceType instance) definition = instance.ElementType;

        if (definition.FullName is "System.Span`1" or "System.ReadOnlySpan`1")
            return false;

        try
        {
            return definition.Resolve()?.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute") != true;
        }
        catch
        {
            // If the type cannot be resolved we cannot prove that it is safe to box it.
            return false;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            ImaginedNT.Weaver - inject debug-only method-entry tracing into ImaginedNT assemblies.

            Usage:
              ImaginedNT.Weaver <directory-or-assembly> [options]

            The directory must contain ImaginedNT.Abstractions.dll (the output of
            src/ImaginedNT.Host).  Every ImaginedNT.*.dll in that directory is rewritten in place;
            an assembly is only instrumented once unless --force is passed.

            Options:
              --include-generated   Also instrument compiler-generated types/methods (async/iterator
                                    state machines, lambdas; very noisy)
              --no-accessors        Skip property/event accessors
              --force               Re-instrument assemblies that already carry the marker
              --dry-run             Report what would be instrumented without writing files

            Runtime:
              The instrumented code is inert until MethodTrace is enabled:
                ImaginedNT.Host run app.exe --trace-methods [--trace-methods-filter <list>]
              or set IMAGINEDNT_TRACE_METHODS=1 in the environment.
            """);
    }

    /// <summary>
    /// Resolves referenced assemblies from the target directory using in-memory reads.  Cecil's
    /// default resolver keeps the resolved file open (which would block the later File.Move over
    /// the instrumented DLL), so the weaver owns a small cache of unlocked images instead.
    /// </summary>
    private sealed class InMemoryResolver : IAssemblyResolver
    {
        private readonly Dictionary<string, AssemblyDefinition> _assemblies = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);

        public InMemoryResolver(string directory)
        {
            // Target directory first (it contains the runtime being instrumented), then the
            // shared framework so Cecil can resolve constants/forwarders from System.Runtime etc.
            foreach (var path in Directory.GetFiles(directory, "*.dll"))
                _paths[Path.GetFileNameWithoutExtension(path)] = path;

            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedPlatformAssemblies)
            {
                foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    if (!_paths.ContainsKey(name)) _paths[name] = path;
                }
            }
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (_assemblies.TryGetValue(name.Name, out var cached)) return cached;
            if (!_paths.TryGetValue(name.Name, out var path) || !File.Exists(path))
                throw new AssemblyResolutionException(name);
            var definition = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
            {
                InMemory = true,
                ReadSymbols = false,
                AssemblyResolver = this
            });
            _assemblies[name.Name] = definition;
            return definition;
        }

        public void Dispose()
        {
            foreach (var definition in _assemblies.Values)
            {
                try { definition.Dispose(); } catch { /* ignore */ }
            }
            _assemblies.Clear();
        }
    }

    private readonly record struct Options(bool IncludeGenerated, bool IncludeAccessors, bool Force, bool DryRun);
}
