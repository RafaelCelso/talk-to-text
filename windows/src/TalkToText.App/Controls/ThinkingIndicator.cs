using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TalkToText.App.Design;

namespace TalkToText.App.Controls;

/// <summary>
/// What the chat shows while the local model works on an answer: three dots in the waveform's
/// colours rising in turn, skeleton lines with a sheen sweeping across, and a line saying which
/// step it is on and for how long.
/// </summary>
/// <remarks>
/// A CPU model can take most of a minute before its first word. A still "…" for that long reads
/// as frozen; motion and a running account of the steps make the same wait feel shorter. The
/// step's message gives way to the next ones as time passes, since reading the passages is one
/// long silent stretch. Driven by a frame timer, as the recording pill is: looping Avalonia
/// animations cannot be started with <c>RunAsync</c>.
/// </remarks>
public sealed class ThinkingIndicator : UserControl
{
    private readonly Border[] _dots = new Border[3];
    private readonly Border[] _lines;
    private readonly LinearGradientBrush[] _sheens;
    private readonly TextBlock _status;
    private readonly TextBlock _elapsed;
    private readonly DispatcherTimer _frame;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _stepClock = Stopwatch.StartNew();
    private string _step = "Preparando…";

    /// <summary>Messages for the long reading step, in order, with when each takes over.</summary>
    private static readonly (double After, string Text)[] ReadingMoments =
    [
        (12, "Analisando o que foi dito…"),
        (24, "Ligando os pontos entre as falas…"),
        (38, "Organizando a resposta…"),
    ];

    private bool _reading;

    /// <summary>Builds the indicator; it animates while on screen.</summary>
    public ThinkingIndicator()
    {
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < _dots.Length; i++)
        {
            _dots[i] = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(Tokens.Radius.Pill),
                Background = new SolidColorBrush(Tokens.Colors.SpectrumAt(i / 2.0)),
                RenderTransform = new TranslateTransform(),
            };
            dots.Children.Add(_dots[i]);
        }

        _status = Ui.Text(_step, Tokens.Fonts.Body, Tokens.Brushes.Foreground, FontWeight.Medium);
        _elapsed = Ui.Text(string.Empty, Tokens.Fonts.Caption, Tokens.Brushes.Muted);

        // Skeleton lines: the shape of the answer to come, a light sweeping across them.
        double[] widths = [1, 0.86, 0.62];
        _lines = new Border[widths.Length];
        _sheens = new LinearGradientBrush[widths.Length];
        var skeleton = new StackPanel { Spacing = 7, Margin = new Thickness(0, Tokens.Space.Tight, 0, 0) };
        for (var i = 0; i < widths.Length; i++)
        {
            _sheens[i] = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            };
            _lines[i] = new Border
            {
                Height = 9,
                CornerRadius = new CornerRadius(Tokens.Radius.Pill),
                Background = _sheens[i],
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = widths[i],
            };
            skeleton.Children.Add(_lines[i]);
        }

        Content = new StackPanel
        {
            MinWidth = 300,
            Spacing = Tokens.Space.Snug,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = Tokens.Space.Base,
                    Children = { dots, _status },
                },
                skeleton,
                _elapsed,
            },
        };

        _frame = new DispatcherTimer(DispatcherPriority.Render) { Interval = Tokens.Motion.Frame };
        _frame.Tick += (_, _) => Animate();
        AttachedToVisualTree += (_, _) => _frame.Start();
        DetachedFromVisualTree += (_, _) => _frame.Stop();
        SizeChanged += (_, _) => SizeLines();
        Animate();
    }

    /// <summary>The step being shown. Exposed for tests.</summary>
    public string Step => _status.Text ?? string.Empty;

    /// <summary>Shows a new step; "reading" steps move on to further messages as time passes.</summary>
    /// <param name="text">What the assistant is doing now.</param>
    /// <param name="reading">True for the long, silent stretch while the model reads.</param>
    public void SetStep(string text, bool reading = false)
    {
        _step = text;
        _reading = reading;
        _stepClock.Restart();
        _status.Text = text;
    }

    private void SizeLines()
    {
        var width = Math.Max(200, Bounds.Width);
        foreach (var line in _lines) line.Width = width * (double)line.Tag!;
    }

    private void Animate()
    {
        var t = _clock.Elapsed.TotalSeconds;

        // Dots rise and brighten one after another, like a wave passing through.
        for (var i = 0; i < _dots.Length; i++)
        {
            var wave = Math.Max(0, Math.Sin((t * 5.5) - (i * 0.9)));
            ((TranslateTransform)_dots[i].RenderTransform!).Y = -5 * wave;
            _dots[i].Opacity = 0.45 + (0.55 * wave);
        }

        // A soft highlight travels left to right across each skeleton line, staggered.
        for (var i = 0; i < _sheens.Length; i++)
        {
            var at = (((t * 0.7) + (i * 0.15)) % 1.4) - 0.2;
            var sheen = _sheens[i];
            sheen.GradientStops.Clear();
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x16, 255, 255, 255), 0));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x16, 255, 255, 255), Math.Clamp(at - 0.15, 0, 1)));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x3A, 255, 255, 255), Math.Clamp(at, 0, 1)));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x16, 255, 255, 255), Math.Clamp(at + 0.15, 0, 1)));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x16, 255, 255, 255), 1));
        }

        if (_reading)
        {
            var since = _stepClock.Elapsed.TotalSeconds;
            var text = _step;
            foreach (var (after, message) in ReadingMoments)
            {
                if (since >= after) text = message;
            }

            if (!Equals(_status.Text, text)) _status.Text = text;
        }

        _elapsed.Text = $"{(int)_clock.Elapsed.TotalSeconds} s · a IA roda no seu computador";
    }
}
