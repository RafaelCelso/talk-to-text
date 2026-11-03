using System.Runtime.CompilerServices;
using TalkToText.Abstractions;

namespace TalkToText.Testing;

/// <summary>
/// Test doubles for the four platform interfaces.
/// </summary>
/// <remarks>
/// These are what make the Windows app's behaviour verifiable from a machine that is not
/// Windows. Everything interesting — the state machine, chunking, the correction pass, the
/// silence case — runs against these in milliseconds, on any platform, in CI.
/// </remarks>
public sealed class FakeAudioCapture : IAudioCapture
{
    private readonly float[] _samples;
    private readonly int _chunkSize;

    /// <summary>Replays a fixed buffer as a series of chunks.</summary>
    /// <param name="samples">16 kHz mono float.</param>
    /// <param name="chunkSize">Samples per chunk; defaults to 20 ms.</param>
    public FakeAudioCapture(float[] samples, int chunkSize = AudioChunk.SampleRate / 50)
    {
        _samples = samples;
        _chunkSize = Math.Max(1, chunkSize);
    }

    /// <summary>Generates <paramref name="seconds"/> of a steady tone at <paramref name="amplitude"/>.</summary>
    public static FakeAudioCapture Tone(double seconds, float amplitude = 0.5f)
    {
        var count = (int)(seconds * AudioChunk.SampleRate);
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = amplitude * MathF.Sin(2 * MathF.PI * 440 * i / AudioChunk.SampleRate);
        }
        return new FakeAudioCapture(samples);
    }

    /// <summary>Generates <paramref name="seconds"/> of digital silence.</summary>
    public static FakeAudioCapture Silence(double seconds) =>
        new(new float[(int)(seconds * AudioChunk.SampleRate)]);

    /// <inheritdoc />
    public bool IsCapturing { get; private set; }

    /// <inheritdoc />
    public async IAsyncEnumerable<AudioChunk> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IsCapturing = true;
        try
        {
            for (var offset = 0; offset < _samples.Length; offset += _chunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(_chunkSize, _samples.Length - offset);

                // A fresh array per chunk, deliberately: a fake that reuses one buffer would
                // hide the very aliasing bug real capture implementations are prone to.
                var chunk = new float[length];
                Array.Copy(_samples, offset, chunk, 0, length);
                yield return new AudioChunk(chunk);

                await Task.Yield();
            }
        }
        finally
        {
            IsCapturing = false;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A keyboard you press from a test.</summary>
public sealed class FakeHotkeySource : IHotkeySource
{
    /// <inheritdoc />
    public event EventHandler<KeyTransition>? KeyChanged;

    /// <summary>
    /// The physical keys <see cref="Press"/> holds down. Defaults to Left Ctrl + Left Win,
    /// which is what the default Ctrl + Win shortcut sees from a real keyboard.
    /// </summary>
    public IReadOnlyList<int> Chord { get; set; } = [0xA2, 0x5B];

    /// <summary>Whether <see cref="Start"/> has been called.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>How many times <see cref="MaskModifierRelease"/> was called.</summary>
    public int MaskCount { get; private set; }

    /// <inheritdoc />
    public bool Start() { IsRunning = true; return true; }

    /// <inheritdoc />
    public void StopListening() => IsRunning = false;

    /// <inheritdoc />
    public void MaskModifierRelease() => MaskCount++;

    /// <summary>Presses every key of <see cref="Chord"/>, in order.</summary>
    public void Press()
    {
        foreach (var key in Chord) Down(key);
    }

    /// <summary>Releases every key of <see cref="Chord"/>, last first.</summary>
    public void Release()
    {
        foreach (var key in Chord.Reverse()) Up(key);
    }

    /// <summary>One key down.</summary>
    public void Down(int key) => KeyChanged?.Invoke(this, new KeyTransition(key, IsDown: true));

    /// <summary>One key up.</summary>
    public void Up(int key) => KeyChanged?.Invoke(this, new KeyTransition(key, IsDown: false));

    /// <inheritdoc />
    public void Dispose() => StopListening();
}

/// <summary>Returns canned transcripts and records what it was asked to transcribe.</summary>
public sealed class FakeTranscriber : ITranscriber
{
    private readonly Queue<string> _responses;

    /// <summary>Each call returns the next response; the last repeats once exhausted.</summary>
    public FakeTranscriber(params string[] responses) => _responses = new Queue<string>(responses);

    /// <summary>How many segments were submitted. Reveals chunking behaviour.</summary>
    public List<int> SegmentLengths { get; } = [];

    /// <summary>The bias list handed over on the most recent call.</summary>
    public IReadOnlyList<string> LastBias { get; private set; } = [];

    /// <inheritdoc />
    public bool IsReady { get; private set; }

    /// <inheritdoc />
    public ValueTask<bool> LoadAsync(CancellationToken cancellationToken)
    {
        IsReady = true;
        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public ValueTask<string> TranscribeAsync(
        ReadOnlyMemory<float> samples,
        IReadOnlyList<string> biasPhrases,
        CancellationToken cancellationToken)
    {
        // Mirrors ParakeetTranscriber, which returns nothing until LoadAsync has run. A fake
        // that transcribed regardless is how an engine that never loaded the model passed.
        if (!IsReady) return ValueTask.FromResult(string.Empty);

        SegmentLengths.Add(samples.Length);
        LastBias = biasPhrases;
        var text = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
        return ValueTask.FromResult(text);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Records what would have been typed.
/// </summary>
/// <remarks>
/// Asserting on intent rather than on an OS effect is deliberate: whether a keystroke really
/// lands in another application's text field is the one thing CI cannot observe, because
/// runners cannot take the foreground.
/// </remarks>
public sealed class RecordingTextInjector : ITextInjector
{
    /// <summary>Everything injected, in order.</summary>
    public List<string> Injected { get; } = [];

    /// <inheritdoc />
    public ValueTask<bool> InjectAsync(string text, CancellationToken cancellationToken)
    {
        Injected.Add(text);
        return ValueTask.FromResult(true);
    }
}

/// <summary>A clock you advance by hand.</summary>
public sealed class FakeClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Moves time forward.</summary>
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// Voice activity without a model: everything above silence since the last cut is one
/// utterance, cut when the stream ends.
/// </summary>
public sealed class FakeSpeechSegmenter : ISpeechSegmenter
{
    private readonly List<float> _speech = [];
    private long _offset;

    /// <inheritdoc />
    public IReadOnlyList<SpeechSpan> Accept(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            if (sample != 0) _speech.Add(sample);
        }

        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<SpeechSpan> Flush()
    {
        if (_speech.Count == 0) return [];

        var span = new SpeechSpan(_offset, [.. _speech]);
        _offset += _speech.Count;
        _speech.Clear();
        return [span];
    }

    /// <inheritdoc />
    public void Dispose() { }
}

/// <summary>
/// Voice prints from loudness: utterances at the same amplitude are "the same voice",
/// different amplitudes are different people.
/// </summary>
public sealed class FakeSpeakerEmbedder : ISpeakerEmbedder
{
    /// <inheritdoc />
    public float[] Embed(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));

        // Orthogonal-ish directions per loudness band.
        return peak switch
        {
            > 0.4f => [1, 0, 0],
            > 0.15f => [0, 1, 0],
            _ => [0, 0, 1],
        };
    }

    /// <inheritdoc />
    public void Dispose() { }
}

/// <summary>A meeting app that always says the same person is talking.</summary>
public sealed class FakeActiveSpeaker(string? name) : IActiveSpeakerSource
{
    /// <inheritdoc />
    public string? CurrentSpeaker(string processName) => name;
}

/// <summary>
/// A language model that records what it was asked and answers with canned text, split into
/// pieces the way a real model streams.
/// </summary>
public sealed class FakeLanguageModel(int contextTokens = 8192, string answer = "## Resumo\nTudo certo.") : ILanguageModel
{
    /// <summary>Every (system, user) pair received, in order.</summary>
    public List<(string System, string User)> Requests { get; } = [];

    /// <inheritdoc />
    public int ContextTokens => contextTokens;

    /// <inheritdoc />
    public int CountTokens(string text) => Math.Max(1, text.Length / 4);

    /// <inheritdoc />
    public async IAsyncEnumerable<string> CompleteAsync(
        string system, string user, int maxTokens,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Requests.Add((system, user));
        foreach (var word in answer.Split(' '))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return word + " ";
        }
    }

    /// <inheritdoc />
    public void Dispose() { }
}

/// <summary>
/// Voice activity without a model that keeps turns apart: each run of non-zero samples is
/// one utterance, ended by the first silent sample.
/// </summary>
public sealed class SilenceSplitSegmenter : ISpeechSegmenter
{
    private readonly List<float> _speech = [];
    private long _fed;
    private long _start;

    /// <inheritdoc />
    public IReadOnlyList<SpeechSpan> Accept(ReadOnlySpan<float> samples)
    {
        var spans = new List<SpeechSpan>();
        foreach (var sample in samples)
        {
            if (sample != 0)
            {
                if (_speech.Count == 0) _start = _fed;
                _speech.Add(sample);
            }
            else if (_speech.Count > 0)
            {
                spans.Add(new SpeechSpan(_start, [.. _speech]));
                _speech.Clear();
            }

            _fed++;
        }

        return spans;
    }

    /// <inheritdoc />
    public IReadOnlyList<SpeechSpan> Flush()
    {
        if (_speech.Count == 0) return [];
        var span = new SpeechSpan(_start, [.. _speech]);
        _speech.Clear();
        return [span];
    }

    /// <inheritdoc />
    public void Dispose() { }
}

/// <summary>An "audio file" that is a buffer in memory.</summary>
public sealed class FakeDecodedAudio(float[] samples) : IDecodedAudio
{
    private int _position;

    /// <summary>Called before each read with the position; lets a test act part-way through.</summary>
    public Action<int>? BeforeRead { get; set; }

    /// <inheritdoc />
    public TimeSpan Duration => TimeSpan.FromSeconds((double)samples.Length / AudioChunk.SampleRate);

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        BeforeRead?.Invoke(_position);
        var n = Math.Min(count, samples.Length - _position);
        Array.Copy(samples, _position, buffer, offset, n);
        _position += n;
        return n;
    }

    /// <inheritdoc />
    public void Dispose() { }

    /// <summary>Concatenates (seconds, amplitude) stretches; amplitude 0 is silence.</summary>
    public static FakeDecodedAudio Of(params (double Seconds, float Amplitude)[] parts)
    {
        var all = new List<float>();
        foreach (var (seconds, amplitude) in parts)
        {
            all.AddRange(Enumerable.Repeat(amplitude, (int)(seconds * AudioChunk.SampleRate)));
        }

        return new FakeDecodedAudio([.. all]);
    }
}
