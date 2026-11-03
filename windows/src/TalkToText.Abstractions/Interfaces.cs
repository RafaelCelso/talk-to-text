namespace TalkToText.Abstractions;

/// <summary>
/// A block of captured audio: mono, 32-bit float, 16 kHz, samples in [-1, 1].
/// </summary>
/// <remarks>
/// The format is fixed at this boundary on purpose. Every speech model this app will use
/// wants 16 kHz mono float, and resampling is a device concern — so the platform layer deals
/// with whatever the hardware offers and nothing above it ever has to ask.
/// </remarks>
/// <param name="Samples">Owned by the receiver. The producer must not reuse the buffer.</param>
public readonly record struct AudioChunk(ReadOnlyMemory<float> Samples)
{
    /// <summary>The sample rate every implementation must deliver.</summary>
    public const int SampleRate = 16000;

    /// <summary>Duration of this chunk.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);

    /// <summary>
    /// Root-mean-square amplitude, 0…1. Drives the level meter.
    /// </summary>
    public float Rms()
    {
        var span = Samples.Span;
        if (span.Length == 0) return 0;

        double sum = 0;
        foreach (var sample in span) sum += (double)sample * sample;
        return (float)Math.Sqrt(sum / span.Length);
    }
}

/// <summary>Captures microphone audio.</summary>
/// <remarks>
/// Implementations must deliver <see cref="AudioChunk"/>s already converted to 16 kHz mono
/// float, and must hand over buffers they will not touch again — the single most common
/// audio bug on every platform is a capture callback reusing one array.
/// </remarks>
public interface IAudioCapture : IAsyncDisposable
{
    /// <summary>Whether capture is currently running.</summary>
    bool IsCapturing { get; }

    /// <summary>Starts capture and yields chunks until cancelled.</summary>
    IAsyncEnumerable<AudioChunk> CaptureAsync(CancellationToken cancellationToken);
}

/// <summary>One physical key going down or coming up, anywhere in the system.</summary>
/// <param name="VirtualKey">
/// Side-specific virtual-key code: Left and Right Ctrl arrive as 0xA2 and 0xA3, never as the
/// generic 0x11.
/// </param>
/// <param name="IsDown">True for a press (including auto-repeat), false for a release.</param>
public readonly record struct KeyTransition(int VirtualKey, bool IsDown);

/// <summary>Reports every key the user presses or releases, system-wide.</summary>
/// <remarks>
/// Deliberately raw. Deciding what counts as the shortcut — two keys, either side, nothing
/// else held — is logic, and logic belongs where CI can test it, not in the Win32 layer.
/// </remarks>
public interface IHotkeySource : IDisposable
{
    /// <summary>A key went down or up. Raised on the hook's own thread; keep handlers short.</summary>
    event EventHandler<KeyTransition>? KeyChanged;

    /// <summary>
    /// Stops modifiers held right now from triggering their own action when released — a
    /// lone Windows key opens the Start menu on release, a lone Alt focuses the menu bar.
    /// </summary>
    void MaskModifierRelease();

    /// <summary>Begins listening.</summary>
    /// <returns>False if the hook could not be installed.</returns>
    bool Start();

    /// <summary>Stops listening.</summary>
    /// <remarks>Not named <c>Stop</c>: that is a reserved word in VB, which CA1716 flags
    /// on any interface member.</remarks>
    void StopListening();
}

/// <summary>Types text into whatever application currently has focus.</summary>
public interface ITextInjector
{
    /// <summary>Inserts <paramref name="text"/> at the caret of the focused control.</summary>
    /// <returns>False if the text could not be delivered.</returns>
    ValueTask<bool> InjectAsync(string text, CancellationToken cancellationToken);
}

/// <summary>Reads the text selected in whatever application has focus.</summary>
public interface ISelectionReader
{
    /// <summary>
    /// Copies the selection — once the user has let go of their modifier keys — and puts back
    /// what the clipboard held before.
    /// </summary>
    /// <returns>The selected text, or null when nothing was selected or it could not be read.</returns>
    ValueTask<string?> CopySelectionAsync(CancellationToken cancellationToken);
}

/// <summary>The local AI, as dictation uses it: tidying what was said, and carrying out spoken commands.</summary>
public interface ITextRewriter
{
    /// <summary>
    /// <paramref name="text"/> with punctuation and grammar fixed and hesitations gone, saying
    /// the same thing in the same words.
    /// </summary>
    Task<string> PolishAsync(string text, CancellationToken cancellationToken);

    /// <summary>
    /// Carries out <paramref name="instruction"/> on <paramref name="selection"/> — "deixa mais
    /// formal" — or, with nothing selected, writes what it asks for.
    /// </summary>
    /// <returns>The text to type in place of the selection.</returns>
    Task<string> CommandAsync(string instruction, string? selection, CancellationToken cancellationToken);
}

/// <summary>Turns audio into text.</summary>
public interface ITranscriber : IAsyncDisposable
{
    /// <summary>Whether the model is loaded and ready.</summary>
    bool IsReady { get; }

    /// <summary>Loads the model. Slow — call once, at startup or first use.</summary>
    ValueTask<bool> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Transcribes one utterance.</summary>
    /// <param name="samples">16 kHz mono float, in [-1, 1].</param>
    /// <param name="biasPhrases">
    /// Dictionary terms to bias the recogniser toward. May be ignored by engines that don't
    /// support it — the correction pass is what actually guarantees spelling.
    /// </param>
    /// <param name="cancellationToken">Cancels a transcription in flight.</param>
    ValueTask<string> TranscribeAsync(
        ReadOnlyMemory<float> samples,
        IReadOnlyList<string> biasPhrases,
        CancellationToken cancellationToken);
}

/// <summary>
/// Wall-clock time, behind an interface so timing logic is testable.
/// </summary>
/// <remarks>
/// Anything that measures a duration takes one of these. A test that depends on the real
/// clock is a test that fails on a slow CI runner.
/// </remarks>
public interface IClock
{
    /// <summary>The current instant.</summary>
    DateTimeOffset Now { get; }
}

/// <inheritdoc cref="IClock"/>
public sealed class SystemClock : IClock
{
    /// <summary>The shared instance.</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset Now => DateTimeOffset.Now;
}
