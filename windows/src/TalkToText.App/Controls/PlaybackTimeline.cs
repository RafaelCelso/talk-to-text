using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TalkToText.App.Design;

namespace TalkToText.App.Controls;

/// <summary>
/// The recording as a row of bars — the recording pill's look — doubling as the seek bar.
/// </summary>
/// <remarks>
/// <para>
/// Each bar is as tall as the audio is loud at that point. What has been played is lit in the
/// waveform spectrum with the pill's glow; what is still to come is a faint grey. While playing,
/// the bars around the playhead dance and the played ones shimmer, driven by
/// <see cref="Phase"/>, which the owner advances once a frame.
/// </para>
/// <para>
/// Click or drag anywhere to seek: <see cref="SeekRequested"/> carries the fraction, 0…1.
/// </para>
/// </remarks>
public sealed class PlaybackTimeline : Control
{
    private const double BarWidth = 3;
    private const double Gap = 2;

    /// <summary>Builds the timeline.</summary>
    public PlaybackTimeline()
    {
        Height = 34;
        Cursor = new Cursor(StandardCursorType.Hand);
        ClipToBounds = false;
    }

    /// <summary>The user clicked or dragged to this fraction of the recording.</summary>
    public event EventHandler<double>? SeekRequested;

    /// <summary>Loudness along the recording, 0…1; null draws an even row until it is known.</summary>
    public IReadOnlyList<double>? Envelope { get; set; }

    /// <summary>How much has been played, 0…1.</summary>
    public double Fraction { get; set; }

    /// <summary>Whether to animate.</summary>
    public bool IsPlaying { get; set; }

    /// <summary>Animation clock, in seconds.</summary>
    public double Phase { get; set; }

    /// <summary>How many bars fit. Exposed for tests.</summary>
    public int BarCount => Math.Max(1, (int)((Bounds.Width + Gap) / (BarWidth + Gap)));

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var count = BarCount;
        var mid = Bounds.Height / 2;
        var playhead = Fraction * Bounds.Width;

        for (var i = 0; i < count; i++)
        {
            var x = i * (BarWidth + Gap);
            var t = count == 1 ? 0 : (double)i / (count - 1);
            var level = LevelAt(t, count);
            var played = x + (BarWidth / 2) <= playhead;
            var height = Math.Max(3, (0.16 + (0.84 * level)) * Bounds.Height * Motion(i, x, playhead, played));
            var rect = new Rect(x, mid - (height / 2), BarWidth, height);

            if (!played)
            {
                context.DrawRectangle(new SolidColorBrush(Colors.White, 0.16), null, new RoundedRect(rect, BarWidth / 2));
                continue;
            }

            var color = Tokens.Colors.SpectrumAt(t);
            var glow = Math.Clamp(level, 0.25, 1);

            // The pill's halo, strongest right behind the playhead.
            var near = Math.Max(0, 1 - (Math.Abs(playhead - x) / 60));
            if (IsPlaying && near > 0)
            {
                var spread = 1.5 + (near * 2.5);
                context.DrawRectangle(
                    new SolidColorBrush(color, 0.25 * near),
                    null,
                    new RoundedRect(rect.Inflate(new Thickness(spread, spread * 0.8)), (BarWidth / 2) + spread));
            }

            context.DrawRectangle(Waveform.BarFill(color, glow), null, new RoundedRect(rect, BarWidth / 2));
        }

        // The playhead: a thin white line with a soft glow.
        if (Fraction > 0)
        {
            var line = new Rect(Math.Clamp(playhead - 1, 0, Bounds.Width - 2), 0, 2, Bounds.Height);
            context.DrawRectangle(new SolidColorBrush(Colors.White, 0.18), null, new RoundedRect(line.Inflate(new Thickness(2, 0)), 3));
            context.DrawRectangle(new SolidColorBrush(Colors.White, 0.9), null, new RoundedRect(line, 1));
        }
    }

    /// <summary>The loudest stretch of the envelope under bar <paramref name="t"/>.</summary>
    private double LevelAt(double t, int count)
    {
        if (Envelope is not { Count: > 0 } envelope) return 0.35;

        var from = (int)(t * (envelope.Count - 1));
        var to = Math.Min(envelope.Count - 1, from + Math.Max(0, (envelope.Count / count) - 1));
        var level = 0.0;
        for (var i = from; i <= to; i++) level = Math.Max(level, envelope[i]);
        return level;
    }

    /// <summary>
    /// Height multiplier for the animation: bars near the playhead bounce like the pill's,
    /// played ones breathe gently, the rest stay still.
    /// </summary>
    private double Motion(int i, double x, double playhead, bool played)
    {
        if (!IsPlaying) return 1;

        var near = Math.Max(0, 1 - (Math.Abs(playhead - x) / 40));
        var bounce = near * 0.45 * Math.Abs(Math.Sin((Phase * 9) + (i * 0.9)));
        var shimmer = played ? 0.06 * Math.Sin((Phase * 3) + (i * 0.35)) : 0;
        return 1 + bounce + shimmer;
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Pointer.Capture(this);
        Seek(e.GetPosition(this).X);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (ReferenceEquals(e.Pointer.Captured, this)) Seek(e.GetPosition(this).X);
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
    }

    private void Seek(double x)
    {
        if (Bounds.Width <= 0) return;
        Fraction = Math.Clamp(x / Bounds.Width, 0, 1);
        InvalidateVisual();
        SeekRequested?.Invoke(this, Fraction);
    }
}
