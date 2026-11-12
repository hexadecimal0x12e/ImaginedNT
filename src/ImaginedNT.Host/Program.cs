using System.Diagnostics;
using System.Globalization;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Io;
using ImaginedNT.Core.Pe;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Host;

/// <summary>
/// The wine-like front end.  Each invocation is the "imagined NT process"; the guest image,
/// its threads, and its handles all live inside this host process.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            var optionArguments = args;
            var guestArgumentSeparator = Array.IndexOf(args, "--");
            if (guestArgumentSeparator >= 0) optionArguments = args[..guestArgumentSeparator];
            if (optionArguments.Contains("--debugger"))
            {
                while (!System.Diagnostics.Debugger.IsAttached)
                {
                    Console.Error.WriteLine("Waiting for debugger to attach...");
                    Thread.Sleep(1000);
                }
                Debugger.Break();
            }

            return args[0].ToLowerInvariant() switch
            {
                "inspect" => Inspect(args.Skip(1).ToArray()),
                "run" => Run(args.Skip(1).ToArray()),
                "apis" => PrintApis(args.Skip(1).ToArray()),
                "help" or "--help" or "-h" => PrintUsageAndReturn(),
                _ => Run(args)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"imaginednt: {ex.GetType().Name}: {ex.Message}");
            if (Environment.GetEnvironmentVariable("IMAGINEDNT_DEBUG") == "1")
                Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int PrintUsageAndReturn()
    {
        PrintUsage();
        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            ImaginedNT - a managed, Wine-like NT user-mode layer.

            Usage:
              ImaginedNT.Host run <image.exe> [options]     Load, patch the IAT, and run a PE image
              ImaginedNT.Host inspect <image.exe>           Parse and dump PE headers/imports/sections
              ImaginedNT.Host apis                          List managed APIs and native declarations
              ImaginedNT.Host <image.exe> [options]         Same as run

              Guest shell:
                ImaginedNT.Host run samples/bin/shell.exe --strict --sandbox sandbox\\shell
                ImaginedNT.Host run samples/bin/shell.exe --strict -- /c "dir"
                (dir, cd, pwd, type, echo, mkdir, rmdir, del, copy, move, run, cls, exit)

            Options:
              --sandbox <dir>       Host directory that backs C:\ (default: ./sandbox/process-<pid>)
              --drive D=<dir>       Map a drive to a directory or YAML; bare YAML uses a free drive
              --packaged            Simulate MSIX package identity using default/configured fields
              --session <id>        Run in a virtual NT session (default: 0)
              --unpackaged          Simulate a process without package identity (default)
              --package-identity <json> Load package identity from a JSON file and enable simulation
              --host-system-dlls    Resolve by-name system imports from the host System32 instead of the sandbox
              --windows-version <v> Report a simulated Windows version (major.minor.build[.revision])
              --copyright <text>    Override the branding copyright string seen by the guest
              --product-name <text> Override the branding Windows product name
              --import <dll>        Import Virtual APIs or make a guest DLL available (repeatable)
              --com-server <dll>    Register an in-process COM server DLL for CoGetClassObject (repeatable)
              --redirect-dll <a=b>   Redirect DLL a to DLL name or host path b (repeatable)
              --timeout <seconds>   Guest execution timeout (default: no timeout)
              --trace <file>        Write an event trace (JSONL) to <file>
              --verbose             Mirror the trace to stderr
              --trace-methods       Log every instrumented method entry (requires an instrumented build)
              --trace-methods-filter <list>  Only log methods whose Type.Method contains one of the comma-separated substrings
              --trace-methods-file <file>    Write method-entry logs to <file> instead of stderr
              --trace-native        Log every native-forwarded call with its arguments (diagnostics)
              --no-wrap-failfast    Do not turn a guest fail-fast into a host Environment.FailFast
              --syscalls            Rewrite and trap direct syscall instructions (opt-in)
              --wrap-native-handles Return ImaginedHandles for native NT handles
              --expand-managed-handles Expand supported managed handles for native NT calls
              --max-physical-memory <bytes>  Shared guest committed-memory budget (0: unlimited)
              --max-virtual-memory <bytes>   Shared guest virtual-memory reservation budget (0: unlimited)
              --job                 Apply an OS job object (memory limit, kill-on-close)
              --strict              Managed-only imports, no native forwarding, fail if unresolved
              --native-declared-only  Allow only declared native APIs; fail on anything else
              --policy <name>       managed-first (default), managed-only
              --no-native-fallback  Disable undeclared native fallback; keep declared native forwards
              --native-policy <name> allow-all (default), declared-only
              --no-exec             Load and patch the image, then stop (safe inspection)
              --base <hex>          Force a load base (relocations are applied when possible)
              --no-protect          Leave mapped sections RWX (diagnostics only)
              --read-only           Reject managed file writes inside the sandbox
              --allow-process       Allow guests to spawn child processes (default)
              --no-process          Reject guest CreateProcessW/A and WinExec
              -- <args...>          Pass the remaining arguments to the guest command line
            """);
    }

    private static int Run(string[] args)
    {
        var options = ParseRunOptions(args, out var imagePath, out var timeout, out var noExec, out var forcedBase, out var guestArguments, out var imports);
        if (imagePath is null)
        {
            Console.Error.WriteLine("imaginednt: no image specified");
            return 2;
        }

        using var runtime = BuiltinApis.CreateRuntime(options);
        ModuleImporter.Import(runtime, imports);
        var process = runtime.CreateProcess(imagePath, BuildGuestCommandLine(imagePath, guestArguments));
        Console.Error.WriteLine($"ImaginedNT: virtual pid {process.Id}, host pid {Environment.ProcessId}");
        Console.Error.WriteLine($"ImaginedNT: sandbox {process.FileSystem.Root}");

        var image = process.LoadImage(imagePath, forcedBase);
        Console.Error.WriteLine($"ImaginedNT: {image.Image.FileName} base=0x{image.BaseAddress:X16} entry=0x{image.EntryPoint:X16} relocated={image.WasRelocated}");
        Console.Error.WriteLine($"ImaginedNT: {image.Imports.Count} imports ({image.Imports.Count(i => i.Resolution.Kind == Core.Pe.ImportResolutionKind.Managed)} managed, {image.Imports.Count(i => i.Resolution.Kind == Core.Pe.ImportResolutionKind.Native)} native, {image.Imports.Count(i => i.Resolution.Kind == Core.Pe.ImportResolutionKind.Deferred)} deferred)");
        if (noExec)
        {
            foreach (var binding in image.Imports)
                Console.Error.WriteLine($"  {binding.Import.ModuleName}!{binding.Import.DisplayName} -> 0x{binding.Resolution.Address:X} [{binding.Resolution.Kind}] {binding.Resolution.Detail}");
            return 0;
        }

        var info = process.Run(timeout);
        Console.Error.WriteLine($"ImaginedNT: guest exited with {info.ExitCode} after {info.Duration.TotalMilliseconds:0} ms{(info.TimedOut ? " (timeout)" : string.Empty)}");
        return info.ExitCode;
    }

    private static string BuildGuestCommandLine(string imagePath, IReadOnlyList<string> guestArguments)
    {
        var builder = new System.Text.StringBuilder();
        AppendQuoted(builder, Path.GetFullPath(imagePath));
        foreach (var argument in guestArguments)
        {
            builder.Append(' ');
            AppendQuoted(builder, argument);
        }
        return builder.ToString();
    }

    /// <summary>Quote one guest argument using the standard Windows command-line rules.</summary>
    private static void AppendQuoted(System.Text.StringBuilder builder, string value)
    {
        if (value.Length == 0 || value.IndexOfAny([' ', '\t', '"']) >= 0)
        {
            builder.Append('"');
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (character == '"')
                    builder.Append('\\', backslashes * 2 + 1).Append('"');
                else
                    builder.Append('\\', backslashes).Append(character);
                backslashes = 0;
            }
            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return;
        }
        builder.Append(value);
    }

    private static RuntimeOptions ParseRunOptions(string[] args, out string? imagePath, out TimeSpan timeout, out bool noExec,
        out ulong forcedBase, out List<string> guestArguments, out List<string> imports)
    {
        imagePath = null;
        timeout = Timeout.InfiniteTimeSpan;
        noExec = false;
        forcedBase = 0;
        guestArguments = new List<string>();
        imports = new List<string>();
        var guestArgumentsOnly = false;
        var traceMethods = false;
        string? traceMethodsFilter = null;
        string? traceMethodsFile = null;
        var options = new RuntimeOptions
        {
            TerminationMode = ProcessTerminationMode.HostExit,
            ImportPolicy = ImportPolicy.ManagedFirst,
            InterceptSyscalls = false,
            TraceToConsole = false,
            DiagnosticWriter = Console.Error
        };

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? driveValue = null;
            if (arg.StartsWith("--drive=", StringComparison.Ordinal)) { driveValue = arg[8..]; arg = "--drive"; }
            if (guestArgumentsOnly)
            {
                guestArguments.Add(arg);
                continue;
            }

            switch (arg)
            {
                case "--":
                    guestArgumentsOnly = true;
                    break;
                case "--sandbox":
                    options.SandboxRoot = RequireValue(args, ref i, arg);
                    break;
                case "--drive":
                    var mapping = driveValue ?? RequireValue(args, ref i, arg);
                    var explicitLetter = mapping.Length > 2 && mapping[1] == '=' && char.IsAsciiLetter(mapping[0]);
                    var letter = explicitLetter ? char.ToUpperInvariant(mapping[0]) : Enumerable.Range('D', 23).Concat(Enumerable.Range('A', 2)).Select(c => (char)c).FirstOrDefault(c => !options.Drives.ContainsKey(c));
                    if (letter == default || options.Drives.ContainsKey(letter)) throw new ArgumentException("drive already mapped or no free drive letter");
                    var source = explicitLetter ? mapping[2..] : mapping;
                    var yaml = Path.GetExtension(source).ToLowerInvariant() is ".yaml" or ".yml";
                    if (!explicitLetter && !yaml) throw new ArgumentException("--drive expects D=<directory-or-yaml> or <yaml>");
                    if (yaml) _ = DriveMapTable.Load(source);
                    options.Drives[letter] = yaml ? new VirtualDriveOptions(".") { MapTablePath = Path.GetFullPath(source) } : new(source);
                    break;
                case "--packaged":
                    options.IsPackaged = true;
                    break;
                case "--session":
                    if (!uint.TryParse(RequireValue(args, ref i, arg), out var sessionId) || sessionId == uint.MaxValue)
                        throw new ArgumentException("--session requires an ID between 0 and 4294967294");
                    options.DefaultSessionId = sessionId;
                    break;
                case "--unpackaged":
                    options.IsPackaged = false;
                    break;
                case "--package-identity":
                    options.PackageIdentity = System.Text.Json.JsonSerializer.Deserialize<PackageIdentity>(
                        File.ReadAllText(RequireValue(args, ref i, arg)),
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? throw new ArgumentException("package identity must be a JSON object");
                    options.IsPackaged = true;
                    break;
                case "--host-system-dlls":
                    options.PreferHostSystemDlls = true;
                    break;
                case "--windows-version":
                    var versionText = RequireValue(args, ref i, arg);
                    if (!Version.TryParse(versionText, out var windowsVersion) ||
                        windowsVersion.Major < 0 || windowsVersion.Minor < 0 || windowsVersion.Build < 0)
                        throw new ArgumentException($"--windows-version expects major.minor.build[.revision], got '{versionText}'");
                    options.SimulatedWindowsVersion = new Version(windowsVersion.Major, windowsVersion.Minor,
                        windowsVersion.Build, Math.Max(windowsVersion.Revision, 0));
                    break;
                case "--copyright":
                    options.CopyrightText = RequireValue(args, ref i, arg);
                    break;
                case "--product-name":
                    options.ProductName = RequireValue(args, ref i, arg);
                    break;
                case "--import":
                    imports.Add(RequireValue(args, ref i, arg));
                    break;
                case "--com-server":
                    options.ComServerPaths.Add(RequireValue(args, ref i, arg));
                    break;
                case "--redirect-dll":
                    var redirect = RequireValue(args, ref i, arg);
                    var separator = redirect.IndexOf('=');
                    if (separator <= 0 || string.IsNullOrWhiteSpace(redirect[..separator]) ||
                        string.IsNullOrWhiteSpace(redirect[(separator + 1)..]))
                        throw new ArgumentException("--redirect-dll expects <source-dll>=<target-dll-or-host-path>");
                    options.DllRedirects[ApiRegistry.NormalizeModule(redirect[..separator].Trim())] = redirect[(separator + 1)..].Trim();
                    break;
                case "--timeout":
                    timeout = TimeSpan.FromSeconds(double.Parse(RequireValue(args, ref i, arg), CultureInfo.InvariantCulture));
                    break;
                case "--trace":
                    options.TraceFile = RequireValue(args, ref i, arg);
                    break;
                case "--verbose":
                    options.TraceToConsole = true;
                    break;
                case "--trace-native":
                    options.TraceNativeCalls = true;
                    break;
                case "--trace-methods":
                    traceMethods = true;
                    break;
                case "--trace-methods-filter":
                    traceMethods = true;
                    traceMethodsFilter = RequireValue(args, ref i, arg);
                    break;
                case "--trace-methods-file":
                    traceMethods = true;
                    traceMethodsFile = RequireValue(args, ref i, arg);
                    break;
                case "--syscalls":
                    options.InterceptSyscalls = true;
                    break;
                case "--wrap-native-handles":
                    options.WrapNativeHandles = true;
                    break;
                case "--expand-managed-handles":
                    options.ExpandManagedHandles = true;
                    break;
                case "--max-physical-memory":
                    options.MaximumPhysicalMemoryBytes = ulong.Parse(RequireValue(args, ref i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--max-virtual-memory":
                    options.MaximumVirtualMemoryBytes = ulong.Parse(RequireValue(args, ref i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--job":
                    options.LimitJobObject = true;
                    break;
                case "--strict":
                    options.ImportPolicy = ImportPolicy.ManagedOnly;
                    options.AllowNativeForwarding = false;
                    options.FailOnUnresolvedImport = true;
                    break;
                case "--native-declared-only":
                    options.NativeApiPolicy = NativeApiPolicy.DeclaredOnly;
                    options.AllowNativeForwarding = true;
                    options.FailOnUnresolvedImport = true;
                    break;
                case "--no-native-forward":
                    options.AllowNativeForwarding = false;
                    break;
                case "--no-native-fallback":
                    options.AllowUnimplementedApiForwarding = false;
                    break;
                case "--fail-on-unresolved":
                    options.FailOnUnresolvedImport = true;
                    break;
                case "--native-policy":
                    options.NativeApiPolicy = RequireValue(args, ref i, arg).ToLowerInvariant() switch
                    {
                        "allow-all" => NativeApiPolicy.AllowAll,
                        "declared-only" => NativeApiPolicy.DeclaredOnly,
                        var value => throw new ArgumentException($"unknown native policy: {value}")
                    };
                    break;
                case "--policy":
                    options.ImportPolicy = RequireValue(args, ref i, arg).ToLowerInvariant() switch
                    {
                        "managed-first" => ImportPolicy.ManagedFirst,
                        "managed-only" => ImportPolicy.ManagedOnly,
                        var value => throw new ArgumentException($"unknown policy: {value}")
                    };
                    break;
                case "--no-exec":
                    noExec = true;
                    break;
                case "--no-protect":
                    options.ApplySectionProtections = false;
                    break;
                case "--with-cet":
                    options.AllowWithCET = true;
                    break;
                case "--no-wrap-failfast":
                    options.WrapFailFast = false;
                    break;
                case "--read-only":
                    options.AllowFileWrites = false;
                    break;
                case "--allow-process":
                    options.AllowProcessCreation = true;
                    break;
                case "--no-process":
                    options.AllowProcessCreation = false;
                    break;
                case "--base":
                    forcedBase = ulong.Parse(RequireValue(args, ref i, arg), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    break;
                case "--debugger":
                    break; //ignore, handled in Main
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"unknown option: {arg}");
                    if (imagePath is not null) throw new ArgumentException($"unexpected argument: {arg} (use -- to pass guest arguments)");
                    imagePath = arg;
                    break;
            }
        }

        if (traceMethods) MethodTrace.Enable(traceMethodsFilter, traceMethodsFile);
        if (imagePath is not null) options.SandboxRoot ??= Path.Combine(Environment.CurrentDirectory, "sandbox", $"process-{Environment.ProcessId}");
        return options;
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length) throw new ArgumentException($"{option} requires a value");
        return args[++index];
    }

    private static int PrintApis(string[] args)
    {
        var imports = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != "--import") throw new ArgumentException($"unknown option: {args[i]}");
            imports.Add(RequireValue(args, ref i, "--import"));
        }
        var options = new RuntimeOptions
        {
            StandardOutput = Stream.Null,
            StandardError = Stream.Null,
            StandardInput = Stream.Null,
            DiagnosticWriter = null
        };
        using var runtime = BuiltinApis.CreateRuntime(options);
        ModuleImporter.Import(runtime, imports);
        foreach (var group in runtime.Apis.All.GroupBy(a => a.Category).OrderBy(g => g.Key))
        {
            Console.WriteLine($"[{group.Key}]");
            foreach (var api in group.OrderBy(a => a.Module).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"  {api.Module}!{api.Name}{(api.IsPartiallyImagined ? " [Partially Imagined]" : "")}");
        }
        Console.WriteLine();
        Console.WriteLine($"total managed entry points: {runtime.Apis.Count}");

        Console.WriteLine();
        Console.WriteLine("[Native declarations]");
        foreach (var declaration in runtime.NativeDeclarations.All
                     .OrderBy(d => d.Module, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"  {declaration.Module}!{declaration.Name} [Native]");
        Console.WriteLine();
        Console.WriteLine($"total native declarations: {runtime.NativeDeclarations.Count}");
        return 0;
    }

    private static int Inspect(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("imaginednt: inspect requires an image path");
            return 2;
        }

        var image = PeImage.Load(args[0]);
        Console.WriteLine($"File        : {image.Path}");
        Console.WriteLine($"Machine     : {image.Machine} ({(image.Is64Bit ? "PE32+" : "PE32")})");
        Console.WriteLine($"ImageBase   : 0x{image.ImageBase:X16}");
        Console.WriteLine($"EntryPoint  : 0x{image.EntryPointRva:X8}");
        Console.WriteLine($"SizeOfImage : 0x{image.SizeOfImage:X8}");
        Console.WriteLine($"Subsystem   : {image.Subsystem}");
        Console.WriteLine($"DLL         : {image.IsDll}");
        Console.WriteLine($"Relocations : {image.Relocations.Count}");
        Console.WriteLine($"DelayImport : {image.HasDelayImports}");
        Console.WriteLine($"TLS         : {(image.Tls is null ? "none" : $"{image.Tls.CallbackVas.Count} callback(s)")}");
        Console.WriteLine();
        Console.WriteLine("Sections:");
        foreach (var section in image.Sections)
            Console.WriteLine($"  0x{section.VirtualAddress:X8} {section.Name,-8} vsize=0x{section.VirtualSize:X8} raw=0x{section.SizeOfRawData:X8} {(section.IsExecutable ? "X" : "-")}{(section.IsWritable ? "W" : "-")}");
        Console.WriteLine();
        Console.WriteLine("Imports:");
        foreach (var group in image.Imports.GroupBy(i => i.ModuleName, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  {group.Key} ({group.Count()})");
            foreach (var import in group)
                Console.WriteLine($"    {import.DisplayName,-40} IAT RVA 0x{import.IatRva:X8}");
        }
        if (image.Exports.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Exports:");
            foreach (var export in image.Exports)
                Console.WriteLine($"  {export}");
        }
        return 0;
    }
}
