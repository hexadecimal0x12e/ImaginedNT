using System.Runtime.CompilerServices;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.App.Gui;

/// <summary>
/// Associates an <see cref="ImaginedRuntime"/> with the App-side window manager.  The virtual
/// USER32/DWM APIs live in this assembly; embedding hosts opt in by registering
/// <see cref="WindowApis"/> and attaching a manager before running a guest.
/// </summary>
public static class GuiLibrary
{
    private static readonly ConditionalWeakTable<ImaginedRuntime, WindowManager> Managers = new();

    public static WindowManager Attach(ImaginedRuntime runtime, WindowManager manager)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(manager);
        Managers.Remove(runtime);
        Managers.Add(runtime, manager);
        return manager;
    }

    public static WindowManager GetManager(ImaginedRuntime runtime) =>
        Managers.GetValue(runtime, static value => new WindowManager(value));

    public static bool TryGetManager(ImaginedRuntime runtime, out WindowManager? manager) =>
        Managers.TryGetValue(runtime, out manager);
}
