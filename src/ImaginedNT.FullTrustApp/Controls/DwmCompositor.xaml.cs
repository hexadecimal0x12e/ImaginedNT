using System;
using System.Collections.Generic;
using System.Linq;
using ImaginedNT.App.Gui;
using ImaginedNT.FullTrustApp.Gui;
using ImaginedNT.FullTrustApp.Hosting;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;

namespace ImaginedNT.FullTrustApp.Controls;

public sealed partial class DwmCompositor : UserControl
{
    private sealed class WindowVisual
    {
        public ulong Handle;
        public bool IsChild;
        public Border Frame = null!;
        public Grid Layout = null!;
        public Grid? TitleBar;
        public TextBlock? TitleText;
        public Button? MinimizeButton;
        public Button? MaximizeButton;
        public Button? CloseButton;
        public WindowSnapshot Snapshot = null!;
        public Border? ContentSurface;
        public Canvas? ChildCanvas;
        public TextBlock? ChildText;
        public Button? ChildButton;
        public Image? BitmapImage;
        public WriteableBitmap? Bitmap;
        public long FrameVersion;
    }

    private static readonly Color AccentColor = Color.FromArgb(0xFF, 0x80, 0x80, 0x80);
    private static readonly Color InactiveBorderColor = Color.FromArgb(0xFF, 0xB8, 0xB8, 0xB8);
    private static readonly Color ChromeColor = Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);
    private static readonly Color FrameColor = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color TitleTextColor = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
    private static readonly Color MutedTextColor = Color.FromArgb(0xFF, 0x77, 0x77, 0x77);

    [Flags]
    private enum ResizeEdge
    {
        None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8,
        TopLeft = Top | Left, TopRight = Top | Right, BottomLeft = Bottom | Left, BottomRight = Bottom | Right
    }

    private readonly Dictionary<ulong, WindowVisual> _visuals = new();
    private WindowManager? _manager;
    private int _lastVersion = -1;
    private ulong _dragHandle;
    private int _dragOffsetX;
    private int _dragOffsetY;
    private ResizeEdge _resizeEdge;
    private GuiRect _dragRect;
    private InputSystemCursorShape _cursorShape;
    private readonly Dictionary<InputSystemCursorShape, InputSystemCursor> _cursors = new();
    private int _pressedButton = -1;
    private Window? _hostWindow;
    private bool _restoreKeyboardFocus;
    private ImaginedNT.Core.Runtime.DesktopObject? _desktop;

    public DwmCompositor()
    {
        InitializeComponent();
        AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), true);
        AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);
        AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), true);
        AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPointerCanceled), true);
        AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnPointerCaptureLost), true);
        PointerExited += OnPointerExited;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        CharacterReceived += OnCharacterReceived;
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;
    }

    public int WindowCount => _visuals.Values.Count(v => !v.IsChild);

    public void Attach(WindowManager? manager)
    {
        if (!ReferenceEquals(_manager, manager))
        {
            ResetPointerState();
            ReleasePointerCaptures();
            _manager?.ReleaseKeyboard();
            ClearVisuals();
        }
        _manager = manager;
        _lastVersion = -1;
        if (manager is null)
        {
            ClearVisuals();
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }
        manager.SetScreenSize(Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight));
        Sync();
    }

    /// <summary>Called by the App's timer; cheap when the manager version has not changed.</summary>
    public void Sync()
    {
        var manager = _manager;
        if (manager is null)
        {
            if (_visuals.Count != 0) ClearVisuals();
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }
        var version = manager.Version;
        if (_desktop != manager.DisplayDesktop || IsEnabled != manager.IsInputDesktop)
        {
            _desktop = manager.DisplayDesktop;
            ResetPointerState();
            ReleasePointerCaptures();
        }
        IsEnabled = manager.IsInputDesktop;
        if (version != _lastVersion)
        {
            _lastVersion = version;
            var snapshots = manager.Snapshot();
            UpdateVisuals(snapshots.Where(s => s.Desktop == manager.DisplayDesktop).ToArray());
            (manager.Drawing as GdiDrawing)?.Prune(snapshots);
        }
        if (manager.Drawing is GdiDrawing drawing)
            foreach (var visual in _visuals.Values) UpdateBitmap(visual, drawing.GetFrame(visual.Handle));
    }

    private static void UpdateBitmap(WindowVisual visual, GdiFrame? frame)
    {
        if (frame is null || visual.FrameVersion == frame.Version) return;
        if (visual.BitmapImage is null)
        {
            visual.BitmapImage = new Image
            {
                IsHitTestVisible = false, Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
            };
            if (visual.ChildCanvas is not null)
            {
                Canvas.SetZIndex(visual.BitmapImage, -1);
                visual.ChildCanvas.Children.Add(visual.BitmapImage);
            }
            else
            {
                var content = visual.Frame.Child;
                var grid = new Grid();
                visual.Frame.Child = grid;
                if (content is not null) grid.Children.Add(content);
                grid.Children.Add(visual.BitmapImage);
            }
        }
        if (visual.Bitmap is null || visual.Bitmap.PixelWidth != frame.Width || visual.Bitmap.PixelHeight != frame.Height)
        {
            visual.Bitmap = new WriteableBitmap(frame.Width, frame.Height);
            visual.BitmapImage.Source = visual.Bitmap;
        }
        using (var stream = visual.Bitmap.PixelBuffer.AsStream()) stream.Write(frame.Pixels);
        visual.Bitmap.Invalidate();
        visual.BitmapImage.Width = frame.Width;
        visual.BitmapImage.Height = frame.Height;
        visual.FrameVersion = frame.Version;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_hostWindow is null)
        {
            _hostWindow = (Application.Current as App)?.MainWindow;
            if (_hostWindow is not null) _hostWindow.Activated += OnHostActivated;
        }
        if (_manager is not null)
            _manager.SetScreenSize(Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight));
        Sync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_hostWindow is not null) _hostWindow.Activated -= OnHostActivated;
        _hostWindow = null;
        _restoreKeyboardFocus = false;
        _manager?.ReleaseKeyboard();
        ResetPointerState();
        ReleasePointerCaptures();
    }

    private void OnGotFocus(object sender, RoutedEventArgs args) => _restoreKeyboardFocus = true;

    private void OnLostFocus(object sender, RoutedEventArgs args)
    {
        _manager?.ReleaseKeyboard();
        if (XamlRoot is null) return;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (focused is null) return;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, this)) return;
            focused = VisualTreeHelper.GetParent(focused);
        }
        _restoreKeyboardFocus = false;
    }

    private void OnHostActivated(object sender, WindowActivatedEventArgs args)
    {
        _manager?.ReleaseKeyboard();
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            ResetPointerState();
            ReleasePointerCaptures();
            return;
        }
        if (!_restoreKeyboardFocus || _manager is null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded && _restoreKeyboardFocus && _manager is not null && !Focus(FocusState.Programmatic))
                AppRuntime.Log.Warn("desktop keyboard focus restore failed");
        });
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        Surface.Clip = new RectangleGeometry { Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };
        _manager?.SetScreenSize(Math.Max(1, (int)args.NewSize.Width), Math.Max(1, (int)args.NewSize.Height));
        Sync();
    }

    private void ClearVisuals()
    {
        Surface.Children.Clear();
        MinimizedWindows.Children.Clear();
        _visuals.Clear();
        _lastVersion = -1;
    }

    private void UpdateVisuals(IReadOnlyList<WindowSnapshot> snapshots)
    {
        var byHandle = snapshots.ToDictionary(s => s.Handle);
        var present = new HashSet<ulong>();
        foreach (var snapshot in snapshots.OrderBy(s => s.IsChild ? 1 : 0).ThenBy(s => s.ZOrder))
        {
            present.Add(snapshot.Handle);
            if (!_visuals.TryGetValue(snapshot.Handle, out var visual))
            {
                visual = CreateVisual(snapshot);
                _visuals.Add(snapshot.Handle, visual);
            }
            UpdateVisual(visual, snapshot, byHandle);
        }

        foreach (var handle in _visuals.Keys.Where(h => !present.Contains(h)).ToArray())
        {
            RemoveVisual(_visuals[handle]);
            _visuals.Remove(handle);
        }

        MinimizedWindows.Children.Clear();
        foreach (var snapshot in snapshots.Where(s => !s.IsChild && s.Minimized && (s.Style & Win32.WS_VISIBLE) != 0))
        {
            var restore = new Button
            {
                Content = snapshot.Title, MaxWidth = 220, IsTabStop = false, IsEnabled = snapshot.Enabled
            };
            ToolTipService.SetToolTip(restore, "还原窗口");
            restore.Click += (_, _) => SendCaptionCommand(snapshot.Handle, Win32.SC_RESTORE);
            MinimizedWindows.Children.Add(restore);
        }
        EmptyHint.Visibility = snapshots.Any(s => !s.IsChild && (s.Visible || s.Minimized)) ? Visibility.Collapsed : Visibility.Visible;
    }

    private WindowVisual CreateVisual(WindowSnapshot snapshot)
    {
        var visual = new WindowVisual { Handle = snapshot.Handle, IsChild = snapshot.IsChild };
        var frame = new Border
        {
            Background = new SolidColorBrush(FrameColor),
            BorderBrush = new SolidColorBrush(InactiveBorderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(snapshot.IsChild ? 0 : 7),
            RequestedTheme = ElementTheme.Light,
            Tag = visual
        };
        visual.Frame = frame;

        if (snapshot.IsChild)
        {
            if (snapshot.IsButton)
            {
                var button = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    MinWidth = 0,
                    MinHeight = 0,
                    Padding = new Thickness(4, 0, 4, 0),
                    FontSize = 13,
                    Background = new SolidColorBrush(ColorFromArgb(snapshot.BackgroundArgb)),
                    Foreground = new SolidColorBrush(ColorFromArgb(snapshot.ForegroundArgb)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(InactiveBorderColor)
                };
                button.Click += (_, _) =>
                {
                    Focus(FocusState.Programmatic);
                    _manager?.Activate(snapshot.Handle);
                    _manager?.PostCommand(snapshot.Parent, snapshot.Handle);
                };
                visual.ChildButton = button;
                frame.Child = button;
            }
            else
            {
                var text = new TextBlock
                {
                    Margin = new Thickness(8, 4, 8, 4),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(ColorFromArgb(snapshot.ForegroundArgb)),
                    FontSize = 13
                };
                visual.ChildText = text;
                frame.Child = text;
            }
            return visual;
        }

        var layout = new Grid();
        visual.Layout = layout;
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(WindowGeometry.CaptionHeight) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var titleBar = new Grid { Background = new SolidColorBrush(ChromeColor) };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Margin = new Thickness(10, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12,
            Foreground = new SolidColorBrush(TitleTextColor)
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(buttons, 1);
        visual.MinimizeButton = CreateCaptionButton(visual, "\uE921", "最小化", Win32.SC_MINIMIZE);
        visual.MaximizeButton = CreateCaptionButton(visual, "\uE922", "最大化", Win32.SC_MAXIMIZE);
        visual.CloseButton = CreateCaptionButton(visual, "\uE8BB", "关闭", Win32.SC_CLOSE);
        visual.CloseButton.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
        visual.CloseButton.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.White);
        visual.CloseButton.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(0xFF, 0xA0, 0x20, 0x15));
        visual.CloseButton.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Microsoft.UI.Colors.White);
        buttons.Children.Add(visual.MinimizeButton);
        buttons.Children.Add(visual.MaximizeButton);
        buttons.Children.Add(visual.CloseButton);
        titleBar.Children.Add(title);
        titleBar.Children.Add(buttons);
        layout.Children.Add(titleBar);
        visual.TitleBar = titleBar;
        visual.TitleText = title;

        var contentSurface = new Border
        {
            Background = new SolidColorBrush(ColorFromArgb(snapshot.BackgroundArgb)),
            CornerRadius = new CornerRadius(0, 0, 6, 6),
            Child = null
        };
        var childCanvas = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        contentSurface.Child = childCanvas;
        Grid.SetRow(contentSurface, 1);
        layout.Children.Add(contentSurface);
        visual.ContentSurface = contentSurface;
        visual.ChildCanvas = childCanvas;
        frame.Child = layout;
        return visual;
    }

    private Button CreateCaptionButton(WindowVisual visual, string glyph, string tooltip, uint command)
    {
        var button = new Button
        {
            Style = (Style)Resources["CaptionButtonStyle"],
            Content = new FontIcon { FontFamily = new FontFamily("Segoe MDL2 Assets"), Glyph = glyph, FontSize = 10 }
        };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => SendCaptionCommand(visual.Handle,
            command == Win32.SC_MAXIMIZE && visual.Snapshot.Maximized ? Win32.SC_RESTORE : command);
        return button;
    }

    private void SendCaptionCommand(ulong handle, uint command)
    {
        Focus(FocusState.Programmatic);
        _manager?.Activate(handle);
        if (_manager?.PostMessage(handle, Win32.WM_SYSCOMMAND, command, 0) == false)
            AppRuntime.Log.Warn($"desktop window command failed: hwnd=0x{handle:X}, command=0x{command:X}");
    }

    private void UpdateVisual(WindowVisual visual, WindowSnapshot snapshot, IReadOnlyDictionary<ulong, WindowSnapshot> byHandle)
    {
        visual.Snapshot = snapshot;
        var frame = visual.Frame;
        frame.Visibility = snapshot.Visible ? Visibility.Visible : Visibility.Collapsed;
        frame.IsHitTestVisible = snapshot.Enabled;
        frame.Width = Math.Max(1, snapshot.Outer.Width);
        frame.Height = Math.Max(1, snapshot.Outer.Height);
        Canvas.SetZIndex(frame, snapshot.ZOrder);
        frame.BorderBrush = new SolidColorBrush(snapshot.Active ? AccentColor : InactiveBorderColor);

        if (snapshot.IsChild)
        {
            if (visual.ChildText is not null)
            {
                visual.ChildText.Text = snapshot.Title;
                visual.ChildText.Foreground = new SolidColorBrush(ColorFromArgb(snapshot.ForegroundArgb));
            }
            if (visual.ChildButton is not null)
            {
                visual.ChildButton.Content = snapshot.Title;
                visual.ChildButton.Background = new SolidColorBrush(ColorFromArgb(snapshot.BackgroundArgb));
                visual.ChildButton.Foreground = new SolidColorBrush(ColorFromArgb(snapshot.ForegroundArgb));
            }
            frame.Background = new SolidColorBrush(ColorFromArgb(snapshot.BackgroundArgb));
            frame.BorderThickness = new Thickness((snapshot.Style & 0x00800000) != 0 || (snapshot.ExStyle & 0x00000200) != 0 ? 1 : 0);

            if (!byHandle.TryGetValue(snapshot.Parent, out var parent))
            {
                frame.Visibility = Visibility.Collapsed;
                return;
            }
            var parentVisual = _visuals.GetValueOrDefault(parent.Handle);
            if (parentVisual?.ChildCanvas is null)
            {
                frame.Visibility = Visibility.Collapsed;
                return;
            }
            if (!ReferenceEquals(frame.Parent, parentVisual.ChildCanvas))
            {
                (frame.Parent as Canvas)?.Children.Remove(frame);
                parentVisual.ChildCanvas.Children.Add(frame);
            }
            Canvas.SetLeft(frame, snapshot.Outer.X - parent.Client.X);
            Canvas.SetTop(frame, snapshot.Outer.Y - parent.Client.Y);
            return;
        }

        if (!ReferenceEquals(frame.Parent, Surface))
        {
            (frame.Parent as Canvas)?.Children.Remove(frame);
            Surface.Children.Add(frame);
        }
        Canvas.SetLeft(frame, snapshot.Outer.X);
        Canvas.SetTop(frame, snapshot.Outer.Y);
        frame.BorderThickness = new Thickness(snapshot.Client.X - snapshot.Outer.X);
        if (visual.TitleText is not null)
        {
            visual.TitleText.Text = snapshot.Title;
            visual.TitleText.Foreground = new SolidColorBrush(snapshot.Active ? TitleTextColor : MutedTextColor);
        }
        if (visual.ContentSurface is not null)
        {
            visual.ContentSurface.Background = new SolidColorBrush(ColorFromArgb(snapshot.BackgroundArgb));
            visual.ContentSurface.Clip = new RectangleGeometry { Rect = new Rect(0, 0, snapshot.Client.Width, snapshot.Client.Height) };
            visual.ContentSurface.CornerRadius = snapshot.Maximized || frame.BorderThickness.Left == 0
                ? new CornerRadius(0) : new CornerRadius(0, 0, 6, 6);
        }
        if (visual.TitleBar is not null)
        {
            visual.TitleBar.Visibility = snapshot.HasCaption ? Visibility.Visible : Visibility.Collapsed;
            visual.Layout.RowDefinitions[0].Height = new GridLength(snapshot.HasCaption ? WindowGeometry.CaptionHeight : 0);
        }
        frame.CornerRadius = new CornerRadius(snapshot.Maximized || frame.BorderThickness.Left == 0 ? 0 : 7);
        var hasSystemMenu = (snapshot.Style & Win32.WS_SYSMENU) != 0;
        var isToolWindow = (snapshot.ExStyle & Win32.WS_EX_TOOLWINDOW) != 0;
        if (visual.CloseButton is not null)
        {
            visual.CloseButton.Visibility = hasSystemMenu ? Visibility.Visible : Visibility.Collapsed;
            visual.CloseButton.IsEnabled = snapshot.Enabled;
        }
        if (visual.MinimizeButton is not null)
        {
            visual.MinimizeButton.Visibility = hasSystemMenu && !isToolWindow && (snapshot.Style & Win32.WS_MINIMIZEBOX) != 0
                ? Visibility.Visible : Visibility.Collapsed;
            visual.MinimizeButton.IsEnabled = snapshot.Enabled;
        }
        if (visual.MaximizeButton is not null)
        {
            visual.MaximizeButton.Visibility = hasSystemMenu && !isToolWindow && (snapshot.Style & Win32.WS_MAXIMIZEBOX) != 0
                ? Visibility.Visible : Visibility.Collapsed;
            visual.MaximizeButton.IsEnabled = snapshot.Enabled;
            ((FontIcon)visual.MaximizeButton.Content).Glyph = snapshot.Maximized ? "\uE923" : "\uE922";
            ToolTipService.SetToolTip(visual.MaximizeButton, snapshot.Maximized ? "还原" : "最大化");
        }
    }

    private void RemoveVisual(WindowVisual visual)
    {
        (visual.Frame.Parent as Canvas)?.Children.Remove(visual.Frame);
    }

    // --- pointer input ---------------------------------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var manager = _manager;
        if (manager is null) return;
        if (IsButtonSource(args.OriginalSource))
        {
            Focus(FocusState.Programmatic);
            return;
        }

        var point = args.GetCurrentPoint(Surface);
        var properties = point.Properties;
        var button = properties.IsLeftButtonPressed ? 0
            : properties.IsRightButtonPressed ? 1
            : properties.IsMiddleButtonPressed ? 2 : -1;
        if (button < 0) return;

        var x = (int)Math.Round(point.Position.X);
        var y = (int)Math.Round(point.Position.Y);
        Focus(FocusState.Programmatic);
        var hit = manager.HitTest(x, y);
        if (!hit.IsValid) return;
        if (!_visuals.TryGetValue(hit.TopLevelHandle, out var visual) || !visual.Snapshot.Enabled) return;
        manager.SetPointerPosition(x, y);

        _pressedButton = button;
        _resizeEdge = button == 0 ? GetResizeEdge(hit, x, y) : ResizeEdge.None;
        if (_resizeEdge != ResizeEdge.None)
            hit = new WindowHit(hit.TopLevelHandle, hit.TopLevelHandle, WindowHitRegion.Border,
                x, y, visual.Snapshot.Outer, visual.Snapshot.Client, false);
        manager.Activate(hit.Handle);
        manager.MouseDown(hit, button);
        if (button == 0 && !visual.Snapshot.Maximized && (_resizeEdge != ResizeEdge.None || hit.Region == WindowHitRegion.Caption))
        {
            _dragHandle = hit.TopLevelHandle;
            _dragRect = visual.Snapshot.Outer;
            _dragOffsetX = x;
            _dragOffsetY = y;
            manager.PostMessage(_dragHandle, Win32.WM_ENTERSIZEMOVE, 0, 0);
        }
        CapturePointer(args.Pointer);
        SetResizeCursor(_resizeEdge);
        args.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        var manager = _manager;
        if (manager is null) return;
        var point = args.GetCurrentPoint(Surface);
        var x = (int)Math.Round(point.Position.X);
        var y = (int)Math.Round(point.Position.Y);
        manager.SetPointerPosition(x, y);
        if (_dragHandle != 0)
        {
            if (!_visuals.TryGetValue(_dragHandle, out var visual) || !visual.Snapshot.Visible ||
                !visual.Snapshot.Enabled || visual.Snapshot.Maximized ||
                (_resizeEdge != ResizeEdge.None && (visual.Snapshot.Style & Win32.WS_THICKFRAME) == 0))
            {
                ResetPointerState();
                ReleasePointerCaptures();
                return;
            }
            var dx = x - _dragOffsetX;
            var dy = y - _dragOffsetY;
            if (_resizeEdge == ResizeEdge.None)
            {
                if (manager.GetWindowRect(_dragHandle, out var rect, out _) &&
                    (rect.X != _dragRect.X + dx || rect.Y != _dragRect.Y + dy))
                {
                    manager.MoveWindow(_dragHandle, _dragRect.X + dx, _dragRect.Y + dy, rect.Width, rect.Height);
                    manager.NotifyWindowMoved(_dragHandle, _dragRect.X + dx, _dragRect.Y + dy);
                }
            }
            else
            {
                var left = _dragRect.X;
                var top = _dragRect.Y;
                var right = _dragRect.Right;
                var bottom = _dragRect.Bottom;
                if ((_resizeEdge & ResizeEdge.Left) != 0) left = Math.Min(right - 160, left + dx);
                if ((_resizeEdge & ResizeEdge.Right) != 0) right = Math.Max(left + 160, right + dx);
                if ((_resizeEdge & ResizeEdge.Top) != 0) top = Math.Min(bottom - 60, top + dy);
                if ((_resizeEdge & ResizeEdge.Bottom) != 0) bottom = Math.Max(top + 60, bottom + dy);
                manager.ResizeWindow(_dragHandle, left, top, right - left, bottom - top);
            }
            Sync();
            args.Handled = true;
            return;
        }
        SetResizeCursor(IsButtonSource(args.OriginalSource) ? ResizeEdge.None : GetResizeEdge(manager.HitTest(x, y), x, y));
        manager.PointerMove(x, y);
    }

    private ResizeEdge GetResizeEdge(WindowHit hit, int x, int y)
    {
        if (!hit.IsValid || !_visuals.TryGetValue(hit.TopLevelHandle, out var visual)) return ResizeEdge.None;
        var snapshot = visual.Snapshot;
        if (!snapshot.Enabled || snapshot.Maximized || (snapshot.Style & Win32.WS_THICKFRAME) == 0) return ResizeEdge.None;
        var rect = snapshot.Outer;
        var edge = ResizeEdge.None;
        if (x < rect.X + 6) edge |= ResizeEdge.Left;
        else if (x >= rect.Right - 6) edge |= ResizeEdge.Right;
        if (y < rect.Y + 6) edge |= ResizeEdge.Top;
        else if (y >= rect.Bottom - 6) edge |= ResizeEdge.Bottom;
        return edge;
    }

    private void SetResizeCursor(ResizeEdge edge)
    {
        var shape = edge switch
        {
            ResizeEdge.Left or ResizeEdge.Right => InputSystemCursorShape.SizeWestEast,
            ResizeEdge.Top or ResizeEdge.Bottom => InputSystemCursorShape.SizeNorthSouth,
            ResizeEdge.TopLeft or ResizeEdge.BottomRight => InputSystemCursorShape.SizeNorthwestSoutheast,
            ResizeEdge.TopRight or ResizeEdge.BottomLeft => InputSystemCursorShape.SizeNortheastSouthwest,
            _ => InputSystemCursorShape.Arrow
        };
        if (_cursorShape == shape) return;
        if (!_cursors.TryGetValue(shape, out var cursor))
            _cursors.Add(shape, cursor = InputSystemCursor.Create(shape));
        ProtectedCursor = cursor;
        _cursorShape = shape;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        var manager = _manager;
        if (manager is null) return;
        var point = args.GetCurrentPoint(Surface);
        var x = (int)Math.Round(point.Position.X);
        var y = (int)Math.Round(point.Position.Y);
        manager.SetPointerPosition(x, y);
        if (_pressedButton >= 0)
        {
            manager.MouseUp(x, y, _pressedButton);
            _pressedButton = -1;
        }
        EndDrag();
        ReleasePointerCapture(args.Pointer);
        args.Handled = true;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        var manager = _manager;
        if (manager is null) return;
        var point = args.GetCurrentPoint(Surface);
        var x = (int)Math.Round(point.Position.X);
        var y = (int)Math.Round(point.Position.Y);
        manager.SetPointerPosition(x, y);
        var hit = manager.HitTest(x, y);
        if (hit.IsValid) manager.MouseWheel(hit, point.Properties.MouseWheelDelta);
        args.Handled = true;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (_dragHandle == 0 && _pressedButton < 0) _manager?.PointerLeave();
        if (_dragHandle == 0) SetResizeCursor(ResizeEdge.None);
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs args) => ResetPointerState();

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs args) => ResetPointerState();

    private void ResetPointerState()
    {
        if (_pressedButton >= 0)
        {
            var (x, y) = _manager?.GetCursorPosition() ?? (0, 0);
            _manager?.MouseUp(x, y, _pressedButton);
        }
        EndDrag();
        _pressedButton = -1;
    }

    private void EndDrag()
    {
        if (_dragHandle != 0)
        {
            _manager?.PostMessage(_dragHandle, Win32.WM_EXITSIZEMOVE, 0, 0);
            if (_resizeEdge != ResizeEdge.None && _manager is { } manager && manager.GetWindowRect(_dragHandle, out var rect, out _))
                AppRuntime.Log.Info($"desktop window resized: hwnd=0x{_dragHandle:X}, rect={rect}");
        }
        _dragHandle = 0;
        _resizeEdge = ResizeEdge.None;
        SetResizeCursor(ResizeEdge.None);
    }

    private static bool IsButtonSource(object source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is Button) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    // --- keyboard input --------------------------------------------------------

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var manager = _manager;
        if (manager is null) return;
        var key = (int)args.Key;
        if (key is <= 0 or > 255) return;
        if (!manager.KeyDown(key, args.KeyStatus.ScanCode, args.KeyStatus.IsExtendedKey, args.KeyStatus.WasKeyDown)) return;
        var character = args.Key switch
        {
            VirtualKey.Enter => '\r',
            VirtualKey.Back => '\b',
            VirtualKey.Tab => '\t',
            _ => '\0'
        };
        if (character != '\0')
        {
            manager.Character(character);
            args.Handled = true;
        }
        else
        {
            // Handling printable keys here suppresses the following CharacterReceived event.
            args.Handled = args.Key is VirtualKey.Escape or VirtualKey.Left or VirtualKey.Right or
                VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End or
                VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Insert or VirtualKey.Delete ||
                key >= (int)VirtualKey.F1 && key <= (int)VirtualKey.F24;
        }
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs args)
    {
        var manager = _manager;
        if (manager is null) return;
        var key = (int)args.Key;
        if (key is <= 0 or > 255) return;
        if (manager.KeyUp(key, args.KeyStatus.ScanCode, args.KeyStatus.IsExtendedKey))
            args.Handled = true;
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (_manager?.Character(args.Character) == true) args.Handled = true;
    }

    // --- helpers ---------------------------------------------------------------

    private static Color ColorFromArgb(uint argb) => Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}

