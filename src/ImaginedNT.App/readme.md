# App

A simple UWP host for ImaginedNT using the **UI-layer window personality** (USER32
and DWM) from `ImaginedNT.AppShared`.  The same app runs on Windows 10/11 desktop and other UWP-Compact targets supported by
the runtime (x64 for guest execution).

## Window desktop

The `窗口桌面` navigation page starts `samples/bin/window.exe` in a second `ImaginedRuntime` and
shows its windows through `Controls/DwmCompositor.xaml`:

- `../ImaginedNT.AppShared/Gui/WindowManager.cs` owns the App-side window classes, geometry, z-order, focus, system
  colours, per-guest-thread message queues, timers and pointer/key state.
- `../ImaginedNT.AppShared/Gui/WindowApis.cs` implements the minimal USER32 surface used by plain `CreateWindowExW`
  applications: class registration, create/show/destroy, message loop (`GetMessageW`,
  `PeekMessageW`, `DispatchMessageW`, `PostMessageW`, `SendMessageW`, `DefWindowProcW`),
  window text/geometry/z-order/focus, mouse and keyboard state, and timers.
- `../ImaginedNT.AppShared/Gui/DwmApis.cs` reports composition as enabled and accepts the common window-attribute calls.
- `Controls/DwmCompositor.xaml` is the simplest DWM compositor: each top-level guest window is a
  XAML surface with an App-drawn caption and client area, child `STATIC`/`BUTTON` windows are XAML
  controls, and the compositor forwards pointer/keyboard events into the guest message queues.
- `Gui/GuiSession.cs` creates the runtime, registers the AppShared API assembly and owns the manager.

There is deliberately **no GDI**: the guest supplies geometry, titles, child-control ids and
window messages, while XAML draws the chrome, backgrounds and text.  Painting APIs
(`BeginPaint`/`GetDC`/`FillRect`/...) are not emulated, so a guest that only creates a window and
runs a message loop can be displayed, but an arbitrary GDI application cannot render yet.

The visual result is therefore an App-composited approximation, not a native Win32 window: the
non-client area is a fixed 30 px XAML caption (no system menu/icon/minimize/maximize, no
DWM shadow or snap), the simulated system palette is dark, and child `STATIC`/`BUTTON` windows
are UWP controls rather than USER32/GDI controls.  Matching Windows pixel-for-pixel would require
native non-client painting, theme metrics, and either guest pixels or a GDI subset.

The window sample is built by `samples/build-samples.ps1` and copied into the package as
`window.exec` (the storage-broker-safe extension used by this app).

## Sandbox

The sandbox file access and shell pages remain as before.  `GuiSession` uses a separate runtime
from the command-line shell session, so a window guest and a console guest can coexist in the app.
