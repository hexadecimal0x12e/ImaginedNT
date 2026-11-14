using ImaginedNT.Core.Runtime;

namespace ImaginedNT.AppShared.Gui;

/// <summary>
/// Minimal DWM surface.  The App compositor always reports composition as enabled and accepts
/// the handful of window-attribute calls that GUI guests probe during startup.  These exports
/// exist so a guest does not fall through to the host's DWM and attach to an invisible window.
/// </summary>
public static class DwmApis
{
    private const ulong S_OK = 0;
    private const ulong E_INVALIDARG = 0x8007_0057;
    private const ulong E_NOTIMPL = 0x8000_4001;

    private const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint DWMWA_CLOAKED = 14;

    [VirtualApi("dwmapi.dll", "DwmIsCompositionEnabled", ApiCategory.Gui)]
    private static ulong DwmIsCompositionEnabled(ApiCallContext ctx)
    {
        var enabled = ctx.Arg(0);
        if (enabled == 0) return E_INVALIDARG;
        ctx.Write(enabled, 1u);
        return S_OK;
    }

    [VirtualApi("dwmapi.dll", "DwmEnableComposition", ApiCategory.Gui)]
    private static ulong DwmEnableComposition(ApiCallContext ctx) => S_OK;

    [VirtualApi("dwmapi.dll", "DwmExtendFrameIntoClientArea", ApiCategory.Gui)]
    private static ulong DwmExtendFrameIntoClientArea(ApiCallContext ctx)
    {
        // The guest's client area is already composed by our XAML surface; accepting the
        // request keeps custom-chrome guests on the managed compositor.
        return ctx.Arg(1) == 0 ? E_INVALIDARG : S_OK;
    }

    [VirtualApi("dwmapi.dll", "DwmSetWindowAttribute", ApiCategory.Gui)]
    private static ulong DwmSetWindowAttribute(ApiCallContext ctx)
    {
        if (ctx.Arg(0) == 0 || ctx.Arg(2) == 0 || ctx.Arg32(3) == 0) return E_INVALIDARG;
        return S_OK;
    }

    [VirtualApi("dwmapi.dll", "DwmGetWindowAttribute", ApiCategory.Gui)]
    private static ulong DwmGetWindowAttribute(ApiCallContext ctx)
    {
        var handle = ctx.Arg(0);
        var attribute = ctx.Arg32(1);
        var output = ctx.Arg(2);
        var size = ctx.Arg32(3);
        if (handle == 0 || output == 0) return E_INVALIDARG;
        var manager = GuiLibrary.GetManager(ctx.Runtime);
        switch (attribute)
        {
            case DWMWA_EXTENDED_FRAME_BOUNDS:
                if (size < 16 || !manager.GetWindowRect(handle, out var rect, out _)) return E_INVALIDARG;
                ctx.Write(output, new NativeRect(rect.X, rect.Y, rect.Right, rect.Bottom));
                return S_OK;
            case DWMWA_CLOAKED:
                if (size < 4) return E_INVALIDARG;
                ctx.Write(output, 0u);
                return S_OK;
            default:
                return E_INVALIDARG;
        }
    }

    [VirtualApi("dwmapi.dll", "DwmFlush", ApiCategory.Gui)]
    private static ulong DwmFlush(ApiCallContext ctx) => S_OK;

    [VirtualApi("dwmapi.dll", "DwmSetPresentParameters", ApiCategory.Gui)]
    private static ulong DwmSetPresentParameters(ApiCallContext ctx) => S_OK;

    [VirtualApi("dwmapi.dll", "DwmGetCompositionTimingInfo", ApiCategory.Gui)]
    private static ulong DwmGetCompositionTimingInfo(ApiCallContext ctx) => E_NOTIMPL;

    [VirtualApi("dwmapi.dll", "DwmRegisterThumbnail", ApiCategory.Gui)]
    private static ulong DwmRegisterThumbnail(ApiCallContext ctx) => E_NOTIMPL;

    [VirtualApi("dwmapi.dll", "DwmUnregisterThumbnail", ApiCategory.Gui)]
    private static ulong DwmUnregisterThumbnail(ApiCallContext ctx) => S_OK;

    [VirtualApi("dwmapi.dll", "DwmUpdateThumbnailProperties", ApiCategory.Gui)]
    private static ulong DwmUpdateThumbnailProperties(ApiCallContext ctx) => E_NOTIMPL;

    [VirtualApi("dwmapi.dll", "DwmSetIconicThumbnail", ApiCategory.Gui)]
    private static ulong DwmSetIconicThumbnail(ApiCallContext ctx) => E_NOTIMPL;

    [VirtualApi("dwmapi.dll", "DwmSetIconicLivePreviewBitmap", ApiCategory.Gui)]
    private static ulong DwmSetIconicLivePreviewBitmap(ApiCallContext ctx) => E_NOTIMPL;
}
