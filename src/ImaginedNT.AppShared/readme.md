# AppShared

Shared components for the UWP and WinUI 3 hosts:

- `Gui`: USER32/DWM APIs, window models, message queues and compositor keyboard input.
- `Settings`: runtime settings persistence and validation.
- `Terminal`: buffered terminal input and output streams.

Both hosts reference this `net10.0` library. `WindowManager` accepts host-specific
color and border settings. FullTrustApp also registers its own GDI API assembly.

`Views/RuntimeSettingsView.cs` is linked into each host because UWP and WinUI 3 use
different XAML types; it is excluded from the library's compilation.
