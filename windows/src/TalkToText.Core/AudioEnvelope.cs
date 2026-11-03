using TalkToText.Abstractions;

namespace TalkToText.Core;

/// <summary>
/// How loud a recording is along its length, for drawing it as a row of bars.
/// </summary>
public static class AudioEnvelope
{
    /// <summary>
    /// Reads <paramref name="audio"/> to the end and returns <paramref name="buckets"/> levels,
    /// 0…1, each the loudness of its stretch relative to the loudest one.
    /// </summary>
    /// <remarks>
    /// RMS rather than peak, so a click does not dwarf speech; square-rooted, so quiet
    /// passages still show as more than a flat line.
    /// </remarks>
    public static double[] Compute(IDecodedAudio audio, int buckets, CancellationToken cancellationToken = default)
    {
        buckets = Math.Max(1, buckets);
        var total = Math.Max(1, (long)(audio.Duration.TotalSeconds * AudioChunk.SampleRate));
        var sums = new double[buckets];
        var counts = new long[buckets];
        var buffer = new float[AudioChunk.SampleRate];
        long position = 0;
        int read;

        while ((read = audio.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++, position++)
            {
                var bucket = (int)Math.Min(buckets - 1, position * buckets / total);
                sums[bucket] += buffer[i] * buffer[i];
                counts[bucket]++;
            }
        }

        var levels = new double[buckets];
        for (var b = 0; b < buckets; b++) levels[b] = counts[b] == 0 ? 0 : Math.Sqrt(Math.Sqrt(sums[b] / counts[b]));

        var loudest = levels.Max();
        if (loudest > 0)
        {
            for (var b = 0; b < buckets; b++) levels[b] /= loudest;
        }

        return levels;
    }
}
