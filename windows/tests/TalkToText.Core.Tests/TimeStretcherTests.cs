using TalkToText.Abstractions;
using Shouldly;
using Xunit;

namespace TalkToText.CoreTests;

/// <summary>Faster playback that keeps the pitch.</summary>
public sealed class TimeStretcherTests
{
    private const int Rate = 44100;

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Length_shrinks_by_the_speed(double speed)
    {
        var output = Stretch(Sine(220, seconds: 4), speed);

        var expected = 4 * Rate / speed;
        output.Length.ShouldBeInRange((int)(expected * 0.97), (int)(expected * 1.03));
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Pitch_stays_the_same(double speed)
    {
        var output = Stretch(Sine(220, seconds: 4), speed);

        // Away from the edges, where the stream starts and ends.
        Frequency(output.AsSpan(Rate / 2, Rate)).ShouldBeInRange(214, 226);
    }

    [Fact]
    public void Steady_tone_keeps_its_loudness()
    {
        var output = Stretch(Sine(220, seconds: 4), 1.5);

        // A warbling overlap-add would cancel itself out in places and dip well below this.
        Rms(output.AsSpan(Rate / 2, Rate)).ShouldBeInRange(0.32, 0.39);
    }

    private static float[] Stretch(float[] input, double speed)
    {
        var stretcher = new TimeStretcher(Rate) { Speed = speed };
        var output = new List<float>();
        var buffer = new float[4096];

        // Fed in uneven blocks, the way an audio device pulls.
        for (var at = 0; at < input.Length; at += 3001)
        {
            stretcher.Write(input.AsSpan(at, Math.Min(3001, input.Length - at)));
            int n;
            while ((n = stretcher.Read(buffer)) > 0) output.AddRange(buffer[..n]);
        }

        stretcher.Flush();
        int rest;
        while ((rest = stretcher.Read(buffer)) > 0) output.AddRange(buffer[..rest]);
        return [.. output];
    }

    private static float[] Sine(double hz, double seconds) =>
        Enumerable.Range(0, (int)(seconds * Rate)).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / Rate))).ToArray();

    /// <summary>Rising zero crossings per second.</summary>
    private static double Frequency(ReadOnlySpan<float> samples)
    {
        var crossings = 0;
        for (var i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0) crossings++;
        }

        return crossings * (double)Rate / samples.Length;
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        return Math.Sqrt(sum / samples.Length);
    }
}
