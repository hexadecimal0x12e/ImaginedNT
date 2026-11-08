using System.Diagnostics;
using System.Text;

namespace ImaginedNT.Tests;

internal static class TestHost
{
    private static readonly Lazy<string> RepoRoot = new(FindRepoRoot);
    private static readonly Lazy<string> HostDll = new(() => Path.Combine(RepoRoot.Value, "src", "ImaginedNT.Host", "bin", "Debug", "net10.0", "ImaginedNT.Host.dll"));

    public static string Root => RepoRoot.Value;
    public static string Samples => Path.Combine(Root, "samples", "bin");
    public static string HostAssembly => HostDll.Value;

    public static string Sample(string name) => Path.Combine(Samples, name);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ImaginedNT.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("could not locate the ImaginedNT repository root");
    }

    public static void RequireSamples()
    {
        foreach (var sample in new[] { "hello.exe", "syscall.exe", "threads.exe", "fileio.exe", "memory.exe", "native.exe", "reloc.exe", "spin.exe", "dllhost.exe", "helper.dll", "directsyscall.exe", "spawn.exe", "child.exe", "shell.exe" })
        {
            if (!File.Exists(Sample(sample)))
                throw new SkipTestException($"sample {sample} is missing; run samples/build-samples.ps1");
        }
    }

    public static void RequireHost()
    {
        if (!File.Exists(HostAssembly))
            throw new SkipTestException($"host is not built: {HostAssembly}");
    }

    public static HostResult RunHost(params string[] arguments) => RunHostCore(null, arguments);

    /// <summary>Run the host with a pre-recorded stdin script; used by the interactive shell tests.</summary>
    public static HostResult RunHostWithInput(string standardInput, params string[] arguments) =>
        RunHostCore(standardInput, arguments);

    private static HostResult RunHostCore(string? standardInput, string[] arguments)
    {
        RequireHost();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(HostAssembly);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (standardInput is not null)
        {
            try
            {
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The guest may exit before consuming all input; that is a valid script outcome.
            }
        }
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"host timed out: {string.Join(' ', arguments)}");
        }
        process.WaitForExit();
        return new HostResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public static string NewTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), "imaginednt-tests", prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

internal sealed record HostResult(int ExitCode, string StdOut, string StdErr)
{
    public string AllOutput => StdOut + "\n" + StdErr;
}

internal sealed class SkipTestException(string message) : Exception(message);

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new AssertionException(message);
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{message}: expected={expected} actual={actual}");
    }

    public static void NotEqual<T>(T unexpected, T actual, string message)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            throw new AssertionException($"{message}: unexpectedly equal to {unexpected}");
    }

    public static void NotNull(object? value, string message) => True(value is not null, message);

    public static T Throws<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new AssertionException($"{message}: expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}");
        }
        throw new AssertionException($"{message}: no exception was thrown");
    }
}

internal sealed class AssertionException(string message) : Exception(message);
