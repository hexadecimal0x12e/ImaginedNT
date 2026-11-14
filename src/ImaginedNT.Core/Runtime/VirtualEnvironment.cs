using ImaginedNT.Core.Io;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ImaginedNT.Core.Runtime;

internal static class VirtualEnvironment
{
    internal static string Expand(string value, IReadOnlyDictionary<string, string> variables) =>
        Regex.Replace(value, "%([^%]+)%", m => variables.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    internal static IReadOnlyDictionary<string, string> Create(ImaginedRuntime runtime, string root, string? userSid = null)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = @"C:\Windows",
            ["windir"] = @"C:\Windows",
            ["SystemDrive"] = "C:",
            ["COMSPEC"] = @"C:\Windows\System32\cmd.exe",
            ["TEMP"] = @"C:\Temp",
            ["TMP"] = @"C:\Temp",
            ["USERNAME"] = "void",
            ["USERPROFILE"] = @"C:\Users\void",
            ["PATH"] = @"C:\Windows\System32;C:\Windows",
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["OS"] = runtime.Options.ProductName ?? "Windows_NT",
            ["PROCESSOR_ARCHITECTURE"] = "AMD64",
            ["PROCESSOR_IDENTIFIER"] = "Imagined Family 67 Model 6767 Stepping 67, CoreCLR",
            ["PROCESSOR_LEVEL"] = "42",
            ["PROCESSOR_REVISION"] = RuntimeInformation.FrameworkDescription,
            ["NUMBER_OF_PROCESSORS"] = Environment.ProcessorCount.ToString(),
            ["USERDOMAIN"] = "ImaginedMachine",
            ["USERDNSDOMAIN"] = "ImaginedMachine.local",
            ["COMPUTERNAME"] = "ImaginedMachine",
            ["ProgramData"] = @"C:\ProgramData",
            ["ProgramFiles"] = @"C:\Program Files",
            ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
            ["CommonProgramFiles"] = @"C:\Program Files\Common Files",
            ["CommonProgramFiles(x86)"] = @"C:\Program Files (x86)\Common Files"
        };
        try
        {
            HostFileSystem.CreateDirectory(root);
            var store = RegistryStore.Get(runtime, root);
            lock (store.Gate)
            {
                foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
                {
                    var values = store.ReadEnvironment(target, userSid);
                    foreach (var (name, value) in values.Where(p => !p.Value.Expand)) Apply(name, value.Value, target);
                    // Expand against the completed REG_SZ layer, before applying expanded values.
                    foreach (var (name, value) in values.Where(p => p.Value.Expand)
                        .Select(p => (p.Key, Expand(p.Value.Value, variables))).ToArray()) Apply(name, value, target);
                }
            }
        }
        catch (Exception ex)
        {
            runtime.Trace.Error($"could not load virtual environment from registry: {ex}");
            throw;
        }
        return variables;

        void Apply(string name, string value, EnvironmentVariableTarget target)
        {
            if (target == EnvironmentVariableTarget.User && name.Equals("PATH", StringComparison.OrdinalIgnoreCase) &&
                variables.TryGetValue(name, out var path) && path.Length > 0)
                variables[name] = value.Length == 0 ? path : path.TrimEnd(';') + ";" + value;
            else variables[name] = value;
        }
    }
}
