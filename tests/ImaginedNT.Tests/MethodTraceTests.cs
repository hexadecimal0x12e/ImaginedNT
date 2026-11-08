using ImaginedNT.Core.Diagnostics;

namespace ImaginedNT.Tests;

internal static class MethodTraceTests
{
    public static void Register(List<(string, Action)> tests)
    {
        tests.Add(("MethodTrace logs matching entries and filters others", LogsEntries));
        tests.Add(("MethodTrace is inert while disabled", Disabled));
    }

    private static void LogsEntries()
    {
        MethodTrace.Disable();
        var writer = new StringWriter();
        MethodTrace.Enable(writer, "Example.Target");
        try
        {
            MethodTrace.Enter("Example.Target", "Run", [42, "hello", null]);
            MethodTrace.Enter("Other.Type", "Skip", [1]);

            var text = writer.ToString();
            Assert.True(text.Contains("Example.Target.Run(42, \"hello\", null)", StringComparison.Ordinal),
                "matching entry is logged with arguments");
            Assert.False(text.Contains("Other.Type.Skip", StringComparison.Ordinal),
                "non-matching entry is filtered");
        }
        finally
        {
            MethodTrace.Disable();
        }
    }

    private static void Disabled()
    {
        MethodTrace.Disable();
        var writer = new StringWriter();
        MethodTrace.Enable(writer);
        MethodTrace.Disable();
        MethodTrace.Enter("Example.Target", "Run", []);
        Assert.Equal(string.Empty, writer.ToString(), "no output while disabled");
    }
}
