namespace ImaginedNT.AppShared.Gui;

public static class CompositorInput
{
    public static bool? KeyDown(WindowManager? manager, int key, uint scanCode, bool extended, bool wasDown)
    {
        if (manager is null || key is <= 0 or > 255 || !manager.KeyDown(key, scanCode, extended, wasDown))
            return null;
        var character = key switch
        {
            Win32.VK_RETURN => '\r',
            Win32.VK_BACK => '\b',
            Win32.VK_TAB => '\t',
            _ => '\0'
        };
        if (character != '\0')
        {
            manager.Character(character);
            return true;
        }
        // Printable keys must reach the host's CharacterReceived event.
        return key is Win32.VK_ESCAPE or >= Win32.VK_PRIOR and <= Win32.VK_DOWN or
            Win32.VK_INSERT or Win32.VK_DELETE or >= Win32.VK_F1 and <= Win32.VK_F24;
    }

    public static bool KeyUp(WindowManager? manager, int key, uint scanCode, bool extended) =>
        manager is not null && key is > 0 and <= 255 && manager.KeyUp(key, scanCode, extended);
}
