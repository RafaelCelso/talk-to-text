using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TalkToText.App.Controls;
using TalkToText.App.Design;
using TalkToText.Core;

namespace TalkToText.App.Views;

/// <summary>
/// "Reunião detectada — Transcrever?": a glass card in the bottom-right corner.
/// </summary>
/// <remarks>
/// <para>
/// Like the recording pill, it never takes focus — the user is in a call, typing in the
/// meeting's chat perhaps — and it answers "no" by itself after a while if ignored.
/// </para>
/// <para>
/// A thin bar along the bottom shows that time running out. It pauses while the pointer is
/// over the card, so the prompt cannot vanish from under a click, and turns amber for the
/// last stretch.
/// </para>
/// </remarks>
public sealed class MeetingPromptWindow : Window
{
    private static readonly TimeSpan AutoDismiss = TimeSpan.FromSeconds(45);
    private const double CardWidth = 340;
    private const double CardHeight = 144;

    /// <summary>The bar turns amber when this little time is left.</summary>
    private static readonly TimeSpan Warning = TimeSpan.FromSeconds(10);
    private const double ScreenMargin = 24;

    private readonly Border _tint;
    private readonly DispatcherTimer _countdown;
    private readonly Border _bar;
    private readonly ScaleTransform _barScale = new(1, 1);
    private TimeSpan _remaining = AutoDismiss;
    private DateTime _lastTick;
    private bool _paused;
    private readonly TextBlock _status;
    private readonly Button _accept;

    /// <summary>Builds the prompt for <paramref name="meeting"/>.</summary>
    /// <param name="meeting">What was detected.</param>
    /// <param name="needsDownload">Whether accepting must first fetch the meeting models.</param>
    public MeetingPromptWindow(DetectedMeeting meeting, bool needsDownload)
    {
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Width = CardWidth;
        Height = CardHeight;
        Title = "Talk-to-Text — reunião detectada";

        _countdown = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _countdown.Tick += (_, _) => Tick();

        _bar = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(Tokens.Radius.Pill),
            Background = Tokens.Brushes.PrimaryGlass,
            RenderTransform = _barScale,
            RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        };

        var radius = new CornerRadius(Tokens.Radius.Shell + 2);
        _tint = Glass.TintLayer(radius);

        _status = Ui.Text(
            needsDownload ? "Primeiro uso: baixa os modelos de voz (27 MB)." : "A conversa aparece em Reuniões, como um chat.",
            Tokens.Fonts.Caption, Tokens.Brushes.Muted);

        _accept = Ui.Primary(needsDownload ? "Baixar e transcrever" : "Transcrever", Icons.Mic);
        _accept.Click += async (_, _) =>
        {
            StopCountdown();
            _accept.IsEnabled = false;
            if (Accepted is { } accepted) await accepted(this).ConfigureAwait(true);
        };

        var dismiss = Ui.Secondary("Agora não");
        dismiss.Click += (_, _) => Close();

        var content = new StackPanel
        {
            Margin = new Thickness(Tokens.Space.Roomy),
            Spacing = Tokens.Space.Snug,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = Tokens.Space.Snug,
                    Children =
                    {
                        Ui.Dot(Tokens.Colors.Recording, 8),
                        Ui.Text("Reunião detectada", Tokens.Fonts.Body, weight: FontWeight.SemiBold),
                    },
                },
                Ui.Text($"{meeting.App} · {meeting.Title}", Tokens.Fonts.Label),
                _status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = Tokens.Space.Snug,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { dismiss, _accept },
                },

                // Track and fill: the fill shrinks from the right as time runs out.
                new Grid
                {
                    Margin = new Thickness(0, Tokens.Space.Tight, 0, 0),
                    Children =
                    {
                        new Border
                        {
                            Height = 3,
                            CornerRadius = new CornerRadius(Tokens.Radius.Pill),
                            Background = Tokens.Brushes.Tint(Avalonia.Media.Colors.White, 0.1),
                        },
                        _bar,
                    },
                },
            },
        };

        Content = Glass.Panel(_tint, content, radius);

        PointerEntered += (_, _) => _paused = true;
        PointerExited += (_, _) => _paused = false;

    }

    /// <summary>Share of the waiting time still left, 1 → 0. Exposed for headless tests.</summary>
    public double Countdown => _remaining / AutoDismiss;

    /// <summary>Whether the countdown is paused (pointer over the card). Exposed for tests.</summary>
    public bool IsPaused => _paused;

    private void Tick()
    {
        var now = DateTime.UtcNow;
        if (!_paused) _remaining -= now - _lastTick;
        _lastTick = now;

        if (_remaining <= TimeSpan.Zero)
        {
            StopCountdown();
            Close();
            return;
        }

        _barScale.ScaleX = Countdown;
        if (_remaining <= Warning) _bar.Background = new SolidColorBrush(Tokens.Colors.Warning);
    }

    private void StopCountdown()
    {
        _countdown.Stop();
        _bar.IsVisible = false;
    }

    /// <summary>Runs when the user accepts; the window stays open until it returns.</summary>
    public Func<MeetingPromptWindow, Task>? Accepted { get; set; }

    /// <summary>Shows a progress or error line in place of the hint.</summary>
    public void SetStatus(string text) => _status.Text = text;

    /// <summary>Re-enables the accept button after a failure.</summary>
    public void AllowRetry() => _accept.IsEnabled = true;

    /// <summary>Shows the card in the bottom-right of the primary screen, without focus.</summary>
    public void ShowInCorner()
    {
        if (Screens?.Primary is { } screen)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            Position = new PixelPoint(
                area.X + area.Width - (int)((CardWidth + ScreenMargin) * scale),
                area.Y + area.Height - (int)((CardHeight + ScreenMargin) * scale));
        }

        Show();
        if (TryGetPlatformHandle() is { } handle) PlatformFactory.MakeOverlay(handle.Handle);
        _tint.Background = new SolidColorBrush(Tokens.Colors.Overlay, Glass.IsBlurred(this) ? Glass.Tint : Glass.SolidTint);
        _lastTick = DateTime.UtcNow;
        _countdown.Start();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _countdown.Stop();
        base.OnClosed(e);
    }
}
