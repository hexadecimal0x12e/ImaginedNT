using System;
using System.Collections.Generic;
using System.Linq;
using ImaginedNT.App.Gui;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace ImaginedNT.App.Controls;

/// <summary>
/// The simplest DWM compositor: every guest top-level window becomes a XAML surface with an
/// App-drawn caption, z-order, focus ring and child controls, and the control forwards pointer and
/// keyboard input back to the guest's window message queues.  No guest drawing API is involved;
/// the guest supplies geometry, title and WM_ messages, while XAML owns presentation.
/// </summary>
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
        public Button? CloseButton;
        public Border? ContentSurface;
        public Canvas? ChildCanvas;
        public TextBlock? ChildText;
        public Button? ChildButton;
    }

    private static readonly Color AccentColor = Color.FromArgb(0xFF, 0x38, 0x8B, 0xFD);
    private static readonly Color InactiveBorderColor = Color.FromArgb(0xFF, 0x30, 0x36, 0x3D);
    private static readonly Color ChromeColor = Color.FromArgb(0xFF, 0x22, 0x27, 0x2E);
    private static readonly Color FrameColor = Color.FromArgb(0xFF, 0x16, 0x1B, 0x22);
    private static readonly Color TitleTextColor = Color.FromArgb(0xFF, 0xD8, 0xDF, 0xE8);
    private static readonly Color MutedTextColor = Color.FromArgb(0xFF, 0x9A, 0xA4, 0xB2);

    private readonly Dictionary<ulong, WindowVisual> _visuals = new();
    private WindowManager? _manager;
    private int _lastVersion = -1;
    private ulong _dragHandle;
    private int _dragOffsetX;
    private int _dragOffsetY;
    private int _pressedButton = -1;

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
    }

    public int WindowCount => _visuals.Values.Count(v => !v.IsChild);

    public void Attach(WindowManager? manager)
    {
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
        if (version == _lastVersion) return;
        _lastVersion = version;
        UpdateVisuals(manager.Snapshot());
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_manager is not null)
            _manager.SetScreenSize(Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight));
        Sync();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        _manager?.SetScreenSize(Math.Max(1, (int)args.NewSize.Width), Math.Max(1, (int)args.NewSize.Height));
        Sync();
    }

    private void ClearVisuals()
    {
        Surface.Children.Clear();
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

        EmptyHint.Visibility = _visuals.Values.Any(v => !v.IsChild) ? Visibility.Collapsed : Visibility.Visible;
    }

    private WindowVisual CreateVisual(WindowSnapshot snapshot)
    {
        var visual = new WindowVisual { Handle = snapshot.Handle, IsChild = snapshot.IsChild };
        var frame = new Border
        {
            Background = new SolidColorBrush(FrameColor),
            BorderBrush = new SolidColorBrush(InactiveBorderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(snapshot.IsChild ? 3 : 7),
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
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x44, 0x4C, 0x56))
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
        var hasCaption = snapshot.HasCaption;
        if (hasCaption)
        {
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var titleBar = new Grid
            {
                Background = new SolidColorBrush(ChromeColor),
                Height = 30
            };
            Grid.SetRow(titleBar, 0);
            var title = new TextBlock
            {
                Text = string.IsNullOrEmpty(snapshot.Title) ? snapshot.ClassName : snapshot.Title,
                Margin = new Thickness(10, 0, 42, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(TitleTextColor)
            };
            var close = new Button
            {
                Content = "\u2715",
                Width = 36,
                Height = 24,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(MutedTextColor)
            };
            close.Click += (_, _) =>
            {
                _manager?.Activate(snapshot.Handle);
                _manager?.RequestClose(snapshot.Handle);
            };
            titleBar.Children.Add(title);
            titleBar.Children.Add(close);
            layout.Children.Add(titleBar);
            visual.TitleBar = titleBar;
            visual.TitleText = title;
            visual.CloseButton = close;
        }
        else
        {
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

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
        Grid.SetRow(contentSurface, hasCaption ? 1 : 0);
        layout.Children.Add(contentSurface);
        visual.ContentSurface = contentSurface;
        visual.ChildCanvas = childCanvas;
        frame.Child = layout;
        return visual;
    }

    private void UpdateVisual(WindowVisual visual, WindowSnapshot snapshot, IReadOnlyDictionary<ulong, WindowSnapshot> byHandle)
    {
        var frame = visual.Frame;
        frame.Visibility = snapshot.Visible ? Visibility.Visible : Visibility.Collapsed;
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
        if (visual.TitleText is not null)
        {
            visual.TitleText.Text = string.IsNullOrEmpty(snapshot.Title) ? snapshot.ClassName : snapshot.Title;
        }
        if (visual.ContentSurface is not null)
            visual.ContentSurface.Background = new SolidColorBrush(ColorFromArgb(snapshot.BackgroundArgb));
        if (visual.TitleBar is not null)
            visual.TitleBar.Visibility = snapshot.HasCaption ? Visibility.Visible : Visibility.Collapsed;
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
        manager.SetPointerPosition(x, y);
        manager.Activate(hit.Handle);

        _pressedButton = button;
        manager.MouseDown(hit, button);
        if (button == 0 && hit.Region == WindowHitRegion.Caption)
        {
            _dragHandle = hit.TopLevelHandle;
            _dragOffsetX = x - hit.WindowRect.X;
            _dragOffsetY = y - hit.WindowRect.Y;
        }
        CapturePointer(args.Pointer);
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
            if (manager.GetWindowRect(_dragHandle, out var rect, out _))
            {
                var newX = x - _dragOffsetX;
                var newY = y - _dragOffsetY;
                manager.MoveWindow(_dragHandle, newX, newY, rect.Width, rect.Height);
                manager.NotifyWindowMoved(_dragHandle, newX, newY);
                Sync();
            }
            args.Handled = true;
            return;
        }
        manager.PointerMove(x, y);
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
        _dragHandle = 0;
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
        _dragHandle = 0;
        _pressedButton = -1;
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
