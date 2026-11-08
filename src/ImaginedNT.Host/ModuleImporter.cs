using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Host;

internal static class ModuleImporter
{
    public static void Import(ImaginedRuntime runtime, IEnumerable<string> paths)
    {
        foreach (var path in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (pe.HasMetadata)
                    runtime.RegisterApis(new ModuleLoadContext(path).LoadFromAssemblyPath(path));
                else
                    runtime.RegisterLibrary(path);
            }
            catch (Exception ex)
            {
                runtime.Trace.Error($"import '{path}' failed: {ex}");
                throw new InvalidOperationException($"could not import '{path}': {ex.Message}", ex);
            }
        }
    }

    private sealed class ModuleLoadContext(string path) : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);

        protected override Assembly? Load(AssemblyName name)
        {
            // API contracts must have the same type identity as the host's contracts.
            var shared = Default.Assemblies.FirstOrDefault(a => AssemblyName.ReferenceMatchesDefinition(a.GetName(), name));
            if (shared is not null) return shared;
            if (name.Name?.StartsWith("ImaginedNT.", StringComparison.Ordinal) == true)
            {
                try { return Default.LoadFromAssemblyName(name); }
                catch (FileNotFoundException) { }
            }

            var resolved = _resolver.ResolveAssemblyToPath(name);
            if (resolved is null && name.Name is not null)
            {
                var adjacent = Path.Combine(Path.GetDirectoryName(path)!, name.Name + ".dll");
                if (File.Exists(adjacent)) resolved = adjacent;
            }
            return resolved is null ? null : LoadFromAssemblyPath(resolved);
        }

        protected override nint LoadUnmanagedDll(string name)
        {
            var resolved = _resolver.ResolveUnmanagedDllToPath(name);
            return resolved is null ? 0 : LoadUnmanagedDllFromPath(resolved);
        }
    }
}
