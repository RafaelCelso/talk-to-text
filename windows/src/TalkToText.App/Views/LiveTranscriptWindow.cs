using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TalkToText.App.Controls;
using TalkToText.App.Design;
using TalkToText.Core;

namespace TalkToText.App.Views;

/// <summary>
/// The meeting being transcribed, floating in the bottom-right corner of the screen while the
/// app is minimised: the live chat when expanded, only the app's icon when collapsed.
/// </summary>
/// <remarks>
/// Like the recording pill it never takes focus — the user is in their call — but its buttons
/// and scrolling still work. The expanded or collapsed choice is remembered for the next time it
/// appears.
/// </remarks>
public sealed class LiveTranscriptWindow : Window
{
    private const double ExpandedWidth = 380;
    private const double ExpandedHeight = 460;
    private const double CollapsedSize = 44;

    private const double ScreenMargin = 24;

    private readonly MeetingStore _store;
    private readonly Border _tint;
    private readonly Grid _glass;
    private readonly Border _expanded;
    private readonly Button _collapsed;
    private readonly ContentControl _chat = new();
    private MeetingSession? _session;
    private bool _styled;

    /// <summary>A window over the meetings in <paramref name="store"/>.</summary>
    public LiveTranscriptWindow(MeetingStore store)
    {
        _store = store;

        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Title = "Talk-to-Text — transcrição ao vivo";

        var open = Ui.IconButton(Icons.Expand, "Abrir o Talk-to-Text");
        open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        var collapse = Ui.IconButton(Icons.Collapse, "Recolher");
        collapse.Click += (_, _) => SetCollapsed(true);

        var heading = new StackPanel
        {
            Spacing = Tokens.Space.Hair,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = Tokens.Space.Snug,
                    Children = { Ui.Dot(Tokens.Colors.Recording, 8), Ui.Text("Transcrevendo ao vivo", Tokens.Fonts.Body, weight: FontWeight.SemiBold) },
                },

                // The meeting's name is on the chat's own header just below; not repeated here.
            },
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space.Hair, Children = { open, collapse } };
        Grid.SetColumn(actions, 1);

        _expanded = new Border
        {
            Padding = new Thickness(Tokens.Space.Base),
            Child = new DockPanel
            {
                Children =
                {
                    Ui.Docked(new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        Margin = new Thickness(Tokens.Space.Tight, 0, 0, Tokens.Space.Snug),
                        Children = { heading, actions },
                    }, Dock.Top),
                    _chat,
                },
            },
        };

        // Collapsed: only the app's own icon, as in the taskbar, with the red "live" dot on its
        // corner — no glass behind it.
        _collapsed = new Button
        {
            Width = CollapsedSize,
            Height = CollapsedSize,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Content = new Grid
            {
                Children =
                {
                    Ui.AppIcon(CollapsedSize),

                    // On the corner of the icon's visible square, inside its transparent margin.
                    new Border
                    {
                        Width = 11,
                        Height = 11,
                        CornerRadius = new CornerRadius(Tokens.Radius.Pill),
                        Background = new SolidColorBrush(Tokens.Colors.Recording),
                        BorderBrush = Brushes.White,
                        BorderThickness = new Thickness(1.5),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 1, 1, 0),
                    },
                },
            },
        };
        ToolTip.SetTip(_collapsed, "Mostrar a transcrição");
        AutomationProperties.SetName(_collapsed, "Mostrar a transcrição");
        _collapsed.Click += (_, _) => SetCollapsed(false);

        var radius = new CornerRadius(Tokens.Radius.Shell + 2);
        _tint = Glass.TintLayer(radius);
        _glass = Glass.Panel(_tint, new Grid { Children = { _expanded, _collapsed } }, radius);
        Content = _glass;

        SetCollapsed(false);
    }

    /// <summary>Whether only the icon is showing. Exposed for tests.</summary>
    public bool IsCollapsed { get; private set; }

    /// <summary>The user asked to open the app.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>Shows <paramref name="session"/>'s chat, rebuilding it when the meeting changes.</summary>
    public void ShowSession(MeetingSession session)
    {
        if (ReferenceEquals(session, _session)) return;
        _session = session;
        _chat.Content = new ChatView(session, null, _store);
    }

    /// <summary>Shows the window in the bottom-right corner, expanded or collapsed as last left.</summary>
    public void ShowInCorner()
    {
        if (!IsVisible)
        {
            Show();
            var glass = ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur
                        || ActualTransparencyLevel == WindowTransparencyLevel.Blur;
            _tint.Background = new SolidColorBrush(Tokens.Colors.Overlay, glass ? Glass.Tint : Glass.SolidTint);
        }

        if (!_styled && TryGetPlatformHandle() is { } handle)
        {
            PlatformFactory.MakeOverlay(handle.Handle);
            _styled = true;
        }

        Place();
    }

    /// <summary>Collapses to the icon, or expands to the chat.</summary>
    public void SetCollapsed(bool collapsed)
    {
        IsCollapsed = collapsed;
        _expanded.IsVisible = !collapsed;
        _collapsed.IsVisible = collapsed;

        // The glass layers (tint, sheen, rim) belong to the expanded card only; collapsed, the
        // window is plain transparent around the icon, with no blur showing in its corners.
        for (var i = 0; i < _glass.Children.Count - 1; i++) _glass.Children[i].IsVisible = !collapsed;
        TransparencyLevelHint = collapsed
            ? [WindowTransparencyLevel.Transparent]
            : [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent];
        Width = collapsed ? CollapsedSize : ExpandedWidth;
        Height = collapsed ? CollapsedSize : ExpandedHeight;
        if (IsVisible) Place();
    }

    /// <summary>Anchors the bottom-right corner, whatever the size, and rounds the window to match.</summary>
    private void Place()
    {
        if (Screens?.Primary is not { } screen) return;

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);
        Position = new PixelPoint(
            area.X + area.Width - width - (int)(ScreenMargin * scale),
            area.Y + area.Height - height - (int)(ScreenMargin * scale));

        if (TryGetPlatformHandle() is { } handle)
        {
            // Collapsed, the icon's own transparency does the shaping: the region stays square.
            var corner = IsCollapsed ? 0 : (int)((Tokens.Radius.Shell + 2) * scale);
            PlatformFactory.SetRoundedRegion(handle.Handle, 0, 0, width, height, corner);
        }
    }
}
