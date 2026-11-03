namespace TalkToText.Abstractions;

/// <summary>
/// Plays mono audio faster or slower without changing its pitch — 1.5× speech still sounds like
/// the same person, not a chipmunk.
/// </summary>
/// <remarks>
/// <para>
/// WSOLA (waveform-similarity overlap-add): the output is built from 40 ms Hann-windowed
/// frames laid down every 20 ms, while the input is read <see cref="Speed"/> times faster. Each
/// frame is taken from within ±12 ms of where it nominally belongs, at the offset whose
/// waveform best continues the previous frame, so the overlaps line up in phase and do not
/// warble.
/// </para>
/// <para>
/// Lives here rather than in the Windows player so it is tested on any machine; the player only
/// pumps samples through it.
/// </para>
/// </remarks>
public sealed class TimeStretcher
{
    private readonly int _frame;
    private readonly int _hop;
    private readonly int _tolerance;
    private readonly float[] _window;
    private readonly List<float> _input = [];
    private readonly Queue<float> _output = new();
    private float[] _tail;
    private double _position;
    private int _previous = -1;

    /// <summary>A stretcher for mono audio at <paramref name="sampleRate"/>.</summary>
    public TimeStretcher(int sampleRate)
    {
        _frame = Math.Max(64, sampleRate / 25) & ~1;
        _hop = _frame / 2;
        _tolerance = sampleRate * 12 / 1000;
        _tail = new float[_hop];

        // Periodic Hann: at 50% overlap the windows sum to exactly one.
        _window = new float[_frame];
        for (var i = 0; i < _frame; i++) _window[i] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / _frame)));
    }

    /// <summary>Playback speed: 1 is unchanged, 2 is twice as fast. Takes effect from the next frame.</summary>
    public double Speed { get; set; } = 1;

    /// <summary>Samples ready to be read.</summary>
    public int Available => _output.Count;

    /// <summary>Adds input audio.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples) _input.Add(s);
        Process();
    }

    /// <summary>Takes up to <paramref name="destination"/>.Length stretched samples; returns how many.</summary>
    public int Read(Span<float> destination)
    {
        var n = Math.Min(destination.Length, _output.Count);
        for (var i = 0; i < n; i++) destination[i] = _output.Dequeue();
        return n;
    }

    /// <summary>
    /// Ends the stream: whatever input is left is laid down as it is, so the last words are not lost.
    /// </summary>
    public void Flush()
    {
        var start = Math.Min((int)_position, _input.Count);
        for (var i = 0; i < _hop; i++) _output.Enqueue(_tail[i] + (start + i < _input.Count ? _input[start + i] * _window[i] : 0));
        for (var i = start + _hop; i < _input.Count; i++) _output.Enqueue(_input[i]);
        Reset();
    }

    /// <summary>Forgets everything — after a seek.</summary>
    public void Reset()
    {
        _input.Clear();
        _output.Clear();
        _tail = new float[_hop];
        _position = 0;
        _previous = -1;
    }

    private void Process()
    {
        while (true)
        {
            var nominal = (int)_position;
            if (nominal + _tolerance + _frame + _hop > _input.Count) break;

            var start = _previous < 0 ? nominal : BestStart(nominal);

            for (var i = 0; i < _hop; i++) _output.Enqueue(_tail[i] + (_input[start + i] * _window[i]));
            for (var i = 0; i < _hop; i++) _tail[i] = _input[start + _hop + i] * _window[_hop + i];

            _previous = start;
            _position += _hop * Math.Clamp(Speed, 0.25, 4);
            Trim();
        }
    }

    /// <summary>
    /// The start near <paramref name="nominal"/> whose first half best matches how the previous
    /// frame would naturally have gone on.
    /// </summary>
    private int BestStart(int nominal)
    {
        var natural = _previous + _hop;
        var from = Math.Max(0, nominal - _tolerance);
        var to = nominal + _tolerance;
        var best = nominal;
        var bestScore = double.NegativeInfinity;

        // Every other sample and offset: a quarter of the work, and still far finer than a
        // speech period.
        for (var candidate = from; candidate <= to; candidate += 2)
        {
            double score = 0;
            for (var i = 0; i < _hop; i += 2) score += _input[candidate + i] * _input[natural + i];
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>Drops input no future frame can reach, shifting positions to match.</summary>
    private void Trim()
    {
        var keep = Math.Min(_previous + _hop, (int)_position - _tolerance);
        if (keep <= _frame * 4) return;

        _input.RemoveRange(0, keep);
        _position -= keep;
        _previous -= keep;
    }
}
