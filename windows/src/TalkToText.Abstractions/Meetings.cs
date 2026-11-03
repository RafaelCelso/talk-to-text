namespace TalkToText.Abstractions;

/// <summary>One stretch of continuous speech cut out of a longer stream.</summary>
/// <param name="StartSample">Offset from the start of the stream, in 16 kHz samples.</param>
/// <param name="Samples">16 kHz mono float. Owned by the receiver.</param>
public readonly record struct SpeechSpan(long StartSample, float[] Samples)
{
    /// <summary>Start, from the beginning of the stream.</summary>
    public TimeSpan Start => TimeSpan.FromSeconds((double)StartSample / AudioChunk.SampleRate);

    /// <summary>Length of the speech.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / AudioChunk.SampleRate);
}

/// <summary>
/// Voice activity detection: turns a continuous stream into separate utterances.
/// </summary>
public interface ISpeechSegmenter : IDisposable
{
    /// <summary>Feeds audio; returns any utterances that have just ended.</summary>
    IReadOnlyList<SpeechSpan> Accept(ReadOnlySpan<float> samples);

    /// <summary>Ends the stream; returns whatever speech was still open.</summary>
    IReadOnlyList<SpeechSpan> Flush();
}

/// <summary>Turns a stretch of speech into a voice print — similar voices, similar vectors.</summary>
public interface ISpeakerEmbedder : IDisposable
{
    /// <summary>The voice print of <paramref name="samples"/> (16 kHz mono float).</summary>
    float[] Embed(ReadOnlySpan<float> samples);
}

/// <summary>What one running app looks like from the outside.</summary>
/// <param name="ProcessName">Executable name without extension, e.g. <c>ms-teams</c>, <c>chrome</c>.</param>
/// <param name="WindowTitles">Titles of its visible top-level windows.</param>
/// <param name="UsingMicrophone">Whether Windows reports it as recording from a microphone right now.</param>
public sealed record AppActivity(string ProcessName, IReadOnlyList<string> WindowTitles, bool UsingMicrophone);

/// <summary>A snapshot of running apps, from which a meeting can be recognised.</summary>
public interface IMeetingSignals
{
    /// <summary>Apps with visible windows or an open microphone, right now.</summary>
    IReadOnlyList<AppActivity> Snapshot();
}

/// <summary>
/// Reads, from a meeting app's own window, the name of whoever is talking.
/// </summary>
/// <remarks>Best effort: meeting apps change their UI without notice.</remarks>
public interface IActiveSpeakerSource
{
    /// <summary>The name shown as currently speaking in <paramref name="processName"/>'s window, if any.</summary>
    string? CurrentSpeaker(string processName);
}

/// <summary>A local language model: given instructions, writes text, a piece at a time.</summary>
public interface ILanguageModel : IDisposable
{
    /// <summary>Roughly how many tokens fit in one request, prompt and answer together.</summary>
    int ContextTokens { get; }

    /// <summary>Approximate token count of <paramref name="text"/>.</summary>
    int CountTokens(string text);

    /// <summary>Writes a reply to <paramref name="user"/> under <paramref name="system"/> instructions.</summary>
    IAsyncEnumerable<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken cancellationToken);
}

/// <summary>
/// Pulls a person's name out of a meeting app's accessible "is speaking" label.
/// </summary>
/// <remarks>
/// Lives here, not in the Windows layer that reads the labels, so the parsing is tested on
/// any machine; the Windows layer only fetches strings.
/// </remarks>
public static class SpeakingLabel
{
    // Longest first, so "está falando" wins over the "falando" inside it.
    private static readonly string[] Markers =
        [" está falando", " is speaking", ", falando", ", speaking", " falando", " speaking"];

    /// <summary>
    /// The name in <paramref name="label"/> if it says someone is speaking, e.g.
    /// "Ana Silva, câmera ligada, está falando" → "Ana Silva"; otherwise null.
    /// </summary>
    public static string? NameIn(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;

        foreach (var marker in Markers)
        {
            var at = label.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at <= 0) continue;

            var name = label[..at];
            var comma = name.IndexOf(',', StringComparison.Ordinal);
            if (comma > 0) name = name[..comma];
            name = name.Trim(' ', ',', '.', ':');

            return name.Length is > 0 and <= 60 ? name : null;
        }

        return null;
    }
}

/// <summary>An audio file being read as 16 kHz mono float, start to finish.</summary>
public interface IDecodedAudio : IDisposable
{
    /// <summary>Length of the whole file.</summary>
    TimeSpan Duration { get; }

    /// <summary>Reads up to <paramref name="count"/> samples; 0 at the end of the file.</summary>
    int Read(float[] buffer, int offset, int count);
}

/// <summary>Opens audio files — MP3, WAV and whatever else the platform can decode.</summary>
public interface IAudioFileDecoder
{
    /// <summary>The extensions offered in the file picker, lower case with the dot.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>Opens <paramref name="path"/>; throws when it is not audio it can read.</summary>
    IDecodedAudio Open(string path);
}

/// <summary>Plays an audio file: play, pause, seek, volume and speed.</summary>
/// <remarks>Members may be called from the UI thread while audio plays on its own thread.</remarks>
public interface IAudioPlayer : IDisposable
{
    /// <summary>Length of the file.</summary>
    TimeSpan Duration { get; }

    /// <summary>Where playback is in the file.</summary>
    TimeSpan Position { get; }

    /// <summary>Whether sound is coming out now.</summary>
    bool IsPlaying { get; }

    /// <summary>0 (silent) to 1 (as recorded).</summary>
    float Volume { get; set; }

    /// <summary>1, 1.5, 2… Faster without raising the pitch.</summary>
    double Speed { get; set; }

    /// <summary>Starts, or resumes after a pause.</summary>
    void Play();

    /// <summary>Pauses where it is.</summary>
    void Pause();

    /// <summary>Stops and goes back to the start.</summary>
    void StopAndRewind();

    /// <summary>Moves to <paramref name="position"/>, playing or not.</summary>
    void Seek(TimeSpan position);

    /// <summary>Raised when the file plays to its end. Any thread.</summary>
    event EventHandler? Ended;

    /// <summary>Raised when playback stopped on an error rather than at the end. Any thread.</summary>
    event EventHandler<Exception>? Failed;
}
