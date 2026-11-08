namespace ImaginedNT.Tests;

/// <summary>
/// End-to-end coverage for the guest-side mini cmd shell.  The shell is a real CRT-free PE that
/// exercises directory enumeration, relative path resolution, file copy/delete/move and child
/// process creation through the managed layer, all under --strict.
/// </summary>
internal static class ShellTests
{
    public static void Register(List<(string, Action)> tests)
    {
        tests.Add(("E2E minimal cmd shell (filesystem + child process)", ScriptedSession));
        tests.Add(("E2E shell /c one-shot command mode", CommandMode));
    }

    private static void CommandMode()
    {
        TestHost.RequireSamples();
        var sandbox = TestHost.NewTempDirectory("shell-c");
        try
        {
            File.Copy(TestHost.Sample("shell.exe"), Path.Combine(sandbox, "shell.exe"));
            File.WriteAllText(Path.Combine(sandbox, "note.txt"), "one-shot");

            // No --timeout: the CLI must not add a default watchdog.  /c also must not touch
            // stdin, so this returns even when the test harness does not provide interactive input.
            var result = TestHost.RunHost(
                "run", Path.Combine(sandbox, "shell.exe"),
                "--sandbox", sandbox,
                "--strict",
                "--", "/c", "type", "note.txt");

            Assert.Equal(0, result.ExitCode, "shell /c exit code");
            Assert.True(result.StdOut.Contains("one-shot", StringComparison.Ordinal), "/c command output");
            Assert.False(result.StdOut.Contains("ImaginedNT minimal shell", StringComparison.Ordinal), "/c skips the REPL banner");
            Assert.False(result.StdErr.Contains("(timeout)", StringComparison.Ordinal), "/c did not hit a default watchdog");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    private static void ScriptedSession()
    {
        TestHost.RequireSamples();
        var sandbox = TestHost.NewTempDirectory("shell");
        try
        {
            File.Copy(TestHost.Sample("shell.exe"), Path.Combine(sandbox, "shell.exe"));
            File.Copy(TestHost.Sample("hello.exe"), Path.Combine(sandbox, "hello.exe"));
            File.WriteAllText(Path.Combine(sandbox, "note.txt"), "hello from the sandbox\r\n");

            var script = string.Join("\r\n",
            [
                "echo shell-ready",
                "dir *.txt",
                "type note.txt",
                "copy note.txt copy.txt",
                "del note.txt",
                "move copy.txt moved.txt",
                "mkdir sub",
                "cd sub",
                "pwd",
                "mkdir nested",
                "cd ..",
                "dir",
                "run hello.exe",
                "exit"
            ]) + "\r\n";

            var result = TestHost.RunHostWithInput(script,
                "run", Path.Combine(sandbox, "shell.exe"),
                "--sandbox", sandbox,
                "--strict",
                "--timeout", "30");

            Assert.Equal(0, result.ExitCode, "shell exit code");
            Assert.True(result.StdOut.Contains("ImaginedNT minimal shell", StringComparison.Ordinal), "shell banner");
            Assert.True(result.StdOut.Contains("shell-ready", StringComparison.Ordinal), "echo output");
            Assert.True(result.StdOut.Contains("hello from the sandbox", StringComparison.Ordinal), "type output");
            Assert.True(result.StdOut.Contains("byte(s) copied", StringComparison.Ordinal), "copy output");
            Assert.True(result.StdOut.Contains("C:\\sub", StringComparison.Ordinal), "pwd shows the virtual cwd");
            Assert.True(result.StdOut.Contains("exit code 42", StringComparison.Ordinal), "run launched hello.exe and observed its exit code");
            Assert.True(result.StdOut.Contains("<DIR>", StringComparison.Ordinal), "dir marks directories");
            Assert.True(result.StdOut.Contains("moved.txt", StringComparison.Ordinal), "move output is listed by dir");

            Assert.False(File.Exists(Path.Combine(sandbox, "note.txt")), "del removed the source file");
            Assert.False(File.Exists(Path.Combine(sandbox, "copy.txt")), "move removed the intermediate copy");
            Assert.Equal("hello from the sandbox\r\n", File.ReadAllText(Path.Combine(sandbox, "moved.txt")), "copied bytes survive move");
            Assert.True(Directory.Exists(Path.Combine(sandbox, "sub", "nested")), "mkdir created C:\\sub\\nested in the sandbox");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }
}
