using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using TalkToText.App.Controls;
using TalkToText.App.Design;
using TalkToText.Core;

namespace TalkToText.App.Views;

/// <summary>What the pill is showing.</summary>
public enum OverlayPhase
{
    /// <summary>Not on screen.</summary>
    Hidden,

    /// <summary>Recording: pulsing dot, live waveform, timer.</summary>
    Recording,

    /// <summary>Key released; the speech is being transcribed.</summary>
    Transcribing,

    /// <summary>Text delivered; shown briefly before leaving.</summary>
    Done,

    /// <summary>Nothing was typed because something failed; the reason stays a few seconds.</summary>
    Failed,

    /// <summary>Animating out.</summary>
    Leaving,
}

/// <summary>
/// The floating glass pill at the bottom centre of the screen that shows a recording start,
/// run and end.
/// </summary>
/// <remarks>
/// <para>
/// <b>It must never take focus.</b> It appears while the user is dictating into another app,
/// and the transcript is pasted into whatever has focus. It is shown without activation and
/// then marked <c>WS_EX_NOACTIVATE</c>, so even clicking it leaves focus where it was.
/// </para>
/// <para>
/// <b>Glass, for real.</b> The window is exactly the pill's size and asks the system for an
/// acrylic backdrop, so what is behind it is genuinely blurred. On top sit a translucent
/// tint, a top-down sheen and a rim that is brighter along the top edge. The system blurs the
/// whole window rectangle, so a window region rounds it into a pill.
/// </para>
/// <para>
/// <b>It grows from a dot.</b> Because the blur belongs to the window, it cannot be faded
/// like a control. Instead the region is animated: a circle at the centre widens into the
/// pill while the window rises a few pixels, and contracts back on the way out — the same
/// clip-path reveal OpenWhispr uses, on <c>cubic-bezier(0.2, 0, 0, 1)</c>.
/// </para>
/// </remarks>
public sealed class RecordingOverlay : Window
{
    private readonly Border _tint;
    private readonly Control _content;
    private readonly Control _live;
    private readonly Control _message;
    private readonly TextBlock _messageText;
    private readonly ContentControl _messageIcon = new();
    private readonly Ellipse _dot;
    private readonly Arc _spinner;
    private readonly Waveform _waveform;
    private readonly TextBlock _timer;
    private readonly DispatcherTimer _leaveTimer;
    private readonly DispatcherTimer _motion;

    private readonly DispatcherTimer _loopTimer;
    private DateTime _loopStart;
    private bool _styled;
    private bool _completed;
    private string? _failure;

    // Reveal animation: 0 = a dot, 1 = the full pill.
    private double _reveal;
    private double _revealFrom;
    private double _revealTo;
    private DateTimeOffset _revealStarted;
    private PixelPoint _restingPosition;

    /// <summary>Builds the pill, off screen.</summary>
    public RecordingOverlay()
    {
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Width = Tokens.Size.PillWidth;
        Height = Tokens.Size.PillHeight;
        Title = "Talk-to-Text — gravando";

        _dot = Ui.Dot(Tokens.Colors.Recording, 9);
        _waveform = new Waveform { Width = 92, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        _timer = Ui.Text("0:00", Tokens.Fonts.Label, Tokens.Brushes.Tint(Avalonia.Media.Colors.White, 0.7));
        _timer.FontFamily = Tokens.Fonts.Mono;

        _live = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Base,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _dot, _waveform, _timer },
        };

        // Rotated by animating the arc itself: Avalonia animates a visual's transform, not a
        // free-standing RotateTransform.
        _spinner = new Arc
        {
            Width = 14,
            Height = 14,
            StartAngle = 0,
            SweepAngle = 270,
            Stroke = new SolidColorBrush(Tokens.Colors.Spectrum[0]),
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            RenderTransform = new RotateTransform(),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _messageText = Ui.Text("Transcrevendo…", Tokens.Fonts.Label, weight: FontWeight.Medium);
        _messageText.MaxWidth = MaxPillWidth - 64;
        _messageText.TextTrimming = TextTrimming.CharacterEllipsis;
        _message = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            Children = { _messageIcon, _messageText },
        };

        foreach (var layer in new[] { _live, _message })
        {
            layer.Transitions =
            [
                new DoubleTransition { Property = OpacityProperty, Duration = Tokens.Motion.Quick, Easing = Tokens.Motion.Standard },
            ];
        }

        _content = new Grid { Children = { _live, _message }, Opacity = 0 };

        var radius = new CornerRadius(Tokens.Radius.Pill);
        _tint = new Border { CornerRadius = radius, Background = new SolidColorBrush(Tokens.Colors.Overlay, GlassTint) };

        Content = new Grid
        {
            Children =
            {
                _tint,

                // Sheen: light falling on the upper half of the glass.
                new Border
                {
                    CornerRadius = radius,
                    Background = VerticalGradient((0.13, 0), (0.04, 0.5), (0, 1)),
                },

                // Rim: a hairline that catches the light along the top and fades below.
                new Border
                {
                    CornerRadius = radius,
                    BorderThickness = new Thickness(1),
                    BorderBrush = VerticalGradient((0.34, 0), (0.1, 0.55), (0.06, 1)),
                },

                _content,
            },
        };

        _leaveTimer = new DispatcherTimer { Interval = DoneShown };
        _leaveTimer.Tick += (_, _) => { _leaveTimer.Stop(); Leave(); };

        _loopTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = Tokens.Motion.Frame };
        _loopTimer.Tick += (_, _) => StepLoops();

        _motion = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _motion.Tick += (_, _) => StepReveal();
    }

    /// <summary>Tint opacity over a real blur: enough to read text on any wallpaper.</summary>
    private const double GlassTint = 0.5;

    /// <summary>Tint opacity when the system gives no blur (transparency effects off).</summary>
    private const double SolidTint = 0.94;

    /// <summary>What the pill is showing now. Exposed for headless tests.</summary>
    public OverlayPhase Phase { get; private set; } = OverlayPhase.Hidden;

    /// <summary>The waveform. Exposed for headless tests.</summary>
    public Waveform Waveform => _waveform;

    /// <summary>The current message text. Exposed for headless tests.</summary>
    public string MessageText => _messageText.Text ?? string.Empty;

    /// <summary>How far the pill has grown, 0 (a dot) to 1. Exposed for headless tests.</summary>
    public double Reveal => _reveal;

    /// <summary>Text from the dictation that is finishing reached the user.</summary>
    public void NotifyCompleted() => _completed = true;

    /// <summary>The dictation that is finishing failed; <paramref name="message"/> says why.</summary>
    public void NotifyFailed(string message) => _failure = message;

    /// <summary>How long a failure stays: long enough to read a sentence.</summary>
    private static readonly TimeSpan FailureShown = TimeSpan.FromSeconds(5);

    /// <summary>How long "Texto inserido" stays.</summary>
    private static readonly TimeSpan DoneShown = TimeSpan.FromMilliseconds(900);

    /// <summary>
    /// Moves the pill to match the engine. Called every frame; cheap when nothing changed.
    /// </summary>
    /// <param name="state">The engine's state.</param>
    /// <param name="level">Input level, 0…1, for the waveform.</param>
    /// <param name="startedAt">When the recording started, for the timer.</param>
    /// <param name="status">What the engine is doing after recording — "Traduzindo para o inglês… 45%".</param>
    public void Apply(DictationState state, double level, DateTimeOffset startedAt, string status = "Transcrevendo…")
    {
        switch (state)
        {
            case DictationState.Recording:
                if (Phase is not OverlayPhase.Recording) Enter();
                _waveform.Push(level);
                var elapsed = DateTimeOffset.Now - startedAt;
                _timer.Text = string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes:0}:{elapsed.Seconds:00}");
                break;

            case DictationState.Transcribing:
                if (Phase is OverlayPhase.Recording) ShowMessage(status, spinner: true);
                else if (Phase is OverlayPhase.Transcribing && status != _messageText.Text) ShowMessage(status, spinner: true);
                break;

            default:
                if (Phase is OverlayPhase.Recording or OverlayPhase.Transcribing && _failure is { } failure)
                {
                    Phase = OverlayPhase.Failed;
                    ShowMessage(failure, spinner: false, failed: true);
                    _leaveTimer.Interval = FailureShown;
                    _leaveTimer.Start();
                }
                else if (Phase is OverlayPhase.Transcribing && _completed)
                {
                    Phase = OverlayPhase.Done;
                    ShowMessage("Texto inserido", spinner: false);
                    _leaveTimer.Interval = DoneShown;
                    _leaveTimer.Start();
                }
                else if (Phase is OverlayPhase.Recording or OverlayPhase.Transcribing)
                {
                    // Cancelled, or nothing was heard: there is nothing to confirm.
                    Leave();
                }

                break;
        }
    }

    private void Enter()
    {
        _leaveTimer.Stop();
        _completed = false;
        _failure = null;
        Phase = OverlayPhase.Recording;

        _waveform.Clear();
        _timer.Text = "0:00";
        Width = Tokens.Size.PillWidth;
        _live.Opacity = 1;
        _message.Opacity = 0;

        _restingPosition = BottomCentre();
        if (!IsVisible)
        {
            _reveal = 0;
            ApplyReveal();
            Show();
        }

        // Without a blur behind it (transparency effects off) a half-clear pill would be
        // unreadable, so it falls back to nearly solid.
        var glass = ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur
                    || ActualTransparencyLevel == WindowTransparencyLevel.Blur;
        _tint.Background = new SolidColorBrush(Tokens.Colors.Overlay, glass ? GlassTint : SolidTint);

        if (!_styled && TryGetPlatformHandle() is { } handle)
        {
            PlatformFactory.MakeOverlay(handle.Handle);
            _styled = true;

            // Once, and only for a real window — headless tests have no native handle.
            DiagnosticLog.Write($"overlay backdrop: {ActualTransparencyLevel}");
        }

        StartLoops();
        AnimateRevealTo(1);
    }

    private void ShowMessage(string text, bool spinner, bool failed = false)
    {
        if (spinner) Phase = OverlayPhase.Transcribing;

        _messageText.Text = text;
        _messageIcon.Content = spinner ? _spinner
            : failed ? Ui.Dot(Tokens.Colors.Warning, 9)
            : Icons.Make(Icons.Check, new SolidColorBrush(Tokens.Colors.Success), 14);

        _live.Opacity = 0;
        _message.Opacity = 1;
        FitMessage();
    }

    /// <summary>
    /// Widens the pill for a message that does not fit, and keeps it centred. It only grows
    /// during one dictation: shrinking back each time a percentage changes would make it twitch.
    /// </summary>
    private void FitMessage()
    {
        _message.Measure(Size.Infinity);
        var wanted = Math.Min(MaxPillWidth, Math.Ceiling(_message.DesiredSize.Width + (2 * Tokens.Space.Wide)));
        if (wanted <= Width) return;

        Width = wanted;
        _restingPosition = BottomCentre();
        ApplyReveal();
    }

    /// <summary>The widest the pill grows for a long message; the text is cut short beyond it.</summary>
    private const double MaxPillWidth = 420;

    private void Leave()
    {
        if (Phase is OverlayPhase.Hidden or OverlayPhase.Leaving) return;

        Phase = OverlayPhase.Leaving;
        AnimateRevealTo(0);
    }

    private void AnimateRevealTo(double target)
    {
        _revealFrom = _reveal;
        _revealTo = target;
        _revealStarted = DateTimeOffset.Now;
        _motion.Start();
    }

    private void StepReveal()
    {
        var t = Math.Clamp((DateTimeOffset.Now - _revealStarted) / Tokens.Motion.Overlay, 0, 1);
        _reveal = _revealFrom + ((_revealTo - _revealFrom) * Tokens.Motion.Standard.Ease(t));
        ApplyReveal();

        if (t < 1) return;
        _motion.Stop();

        if (_revealTo == 0 && Phase == OverlayPhase.Leaving)
        {
            _loopTimer.Stop();
            Phase = OverlayPhase.Hidden;
            Hide();
        }
    }

    /// <summary>
    /// Draws the current reveal: the region widens from a circle to the full pill, the window
    /// rises into place, and the contents fade in over the last part of the growth.
    /// </summary>
    private void ApplyReveal()
    {
        var scale = Screens?.Primary?.Scaling ?? 1;
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);

        var visibleWidth = (int)Math.Round(height + ((width - height) * _reveal));
        if (TryGetPlatformHandle() is { } handle)
        {
            PlatformFactory.SetRoundedRegion(
                handle.Handle, (width - visibleWidth) / 2, 0, Math.Max(1, visibleWidth), height, height / 2);
        }

        var rise = (int)Math.Round((1 - _reveal) * Tokens.Motion.OverlayRise * scale);
        Position = new PixelPoint(_restingPosition.X, _restingPosition.Y + rise);

        _content.Opacity = Math.Clamp((_reveal - 0.45) / 0.55, 0, 1);
    }

    /// <summary>Bottom centre of the primary screen's work area, above the taskbar.</summary>
    private PixelPoint BottomCentre()
    {
        if (Screens?.Primary is not { } screen) return Position;

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)(Width * scale);
        var height = (int)(Height * scale);

        return new PixelPoint(
            area.X + ((area.Width - width) / 2),
            area.Y + area.Height - height - (int)(Tokens.Size.PillBottomOffset * scale));
    }

    private static LinearGradientBrush VerticalGradient(params (double Alpha, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        };

        foreach (var (alpha, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * alpha), 255, 255, 255), offset));
        }

        return brush;
    }

    /// <summary>Starts the dot's breathing pulse and the spinner's rotation.</summary>
    /// <remarks>
    /// Driven by a frame timer, not an Avalonia <c>Animation</c>: an infinite animation
    /// started with <c>RunAsync</c> throws "Looping animations must not use the Run method"
    /// into a task nobody observes — the dot never pulsed and the spinner never turned.
    /// </remarks>
    private void StartLoops()
    {
        _loopStart = DateTime.UtcNow;
        _loopTimer.Start();
    }

    private void StepLoops()
    {
        var seconds = (DateTime.UtcNow - _loopStart).TotalSeconds;

        // A slow breath, 1.8 s per cycle, between full and 30 % opacity.
        _dot.Opacity = 0.65 + (0.35 * Math.Cos(2 * Math.PI * seconds / 1.8));

        if (_spinner.RenderTransform is RotateTransform rotate) rotate.Angle = seconds * 450 % 360;
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _loopTimer.Stop();
        _leaveTimer.Stop();
        _motion.Stop();
        base.OnClosed(e);
    }
}
