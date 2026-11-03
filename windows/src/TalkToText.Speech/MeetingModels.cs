using SherpaOnnx;
using TalkToText.Abstractions;

namespace TalkToText.Speech;

/// <summary>
/// The two small models meeting transcription needs on top of Parakeet: Silero VAD to cut
/// speech into utterances, and WeSpeaker ResNet34 to tell voices apart.
/// </summary>
/// <remarks>
/// WeSpeaker, not 3D-Speaker CAM++: measured on the same clips, CAM++ scored two halves of
/// one sentence at 0.31 and two different voices at 0.82 — unusable — while WeSpeaker put the
/// same voice at 0.86–0.94 and different voices at 0.50–0.55.
/// </remarks>
public static class MeetingModels
{
    /// <summary>The voice-activity model's file name.</summary>
    public const string VadFile = "silero_vad.onnx";

    /// <summary>The speaker-embedding model's file name.</summary>
    public const string SpeakerFile = "speaker.onnx";

    /// <summary>Where both live: <c>%LOCALAPPDATA%\TalkToText\models\meeting</c>.</summary>
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TalkToText", "models", "meeting");

    /// <summary>Downloads, with the exact size each file must have.</summary>
    public static IReadOnlyList<(string File, string Url, long Size)> Downloads { get; } =
    [
        (VadFile, "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx", 643854),
        (SpeakerFile, "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34.onnx", 26534365),
    ];

    /// <summary>Whether both models are present at their exact sizes.</summary>
    public static bool IsInstalled() => Downloads.All(d =>
    {
        var path = Path.Combine(Directory, d.File);
        return File.Exists(path) && new FileInfo(path).Length == d.Size;
    });
}

/// <summary>Silero VAD: emits each utterance once the speaker pauses.</summary>
public sealed class SileroSpeechSegmenter : ISpeechSegmenter
{
    private const int Window = 512;

    private readonly VoiceActivityDetector _vad;
    private readonly List<float> _pending = new(Window * 2);
    private long _fed;
    private long _flushedOffset;

    /// <summary>Loads the VAD model from <see cref="MeetingModels.Directory"/>.</summary>
    /// <param name="minSilenceSeconds">How long a pause ends an utterance.</param>
    public SileroSpeechSegmenter(float minSilenceSeconds = 0.5f)
    {
        var config = new VadModelConfig { SampleRate = AudioChunk.SampleRate, NumThreads = 1, Provider = "cpu" };
        config.SileroVad.Model = Path.Combine(MeetingModels.Directory, MeetingModels.VadFile);
        config.SileroVad.Threshold = 0.5f;

        // Half a second of silence ends a turn by default: short enough that a reply becomes
        // its own message, long enough not to split a sentence at every breath.
        config.SileroVad.MinSilenceDuration = minSilenceSeconds;
        config.SileroVad.MinSpeechDuration = 0.3f;
        config.SileroVad.WindowSize = Window;

        // A monologue is still cut into ~20 s messages, so the chat keeps moving.
        config.SileroVad.MaxSpeechDuration = 20f;

        _vad = new VoiceActivityDetector(config, 60);
    }

    /// <inheritdoc />
    public IReadOnlyList<SpeechSpan> Accept(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples) _pending.Add(sample);

        // Silero works on fixed windows; anything short of one waits for the next call.
        var whole = _pending.Count / Window * Window;
        for (var offset = 0; offset < whole; offset += Window)
        {
            _vad.AcceptWaveform(_pending.GetRange(offset, Window).ToArray());
        }

        _pending.RemoveRange(0, whole);
        _fed += whole;
        return Drain();
    }

    /// <inheritdoc />
    public IReadOnlyList<SpeechSpan> Flush()
    {
        _vad.Flush();
        var spans = Drain();
        _vad.Reset();
        _flushedOffset = _fed;
        return spans;
    }

    private List<SpeechSpan> Drain()
    {
        var spans = new List<SpeechSpan>();
        while (!_vad.IsEmpty())
        {
            var segment = _vad.Front();
            spans.Add(new SpeechSpan(_flushedOffset + segment.Start, segment.Samples));
            _vad.Pop();
        }

        return spans;
    }

    /// <inheritdoc />
    public void Dispose() => _vad.Dispose();
}

/// <summary>WeSpeaker ResNet34 voice prints, 256 dimensions.</summary>
public sealed class WeSpeakerEmbedder : ISpeakerEmbedder
{
    private readonly SpeakerEmbeddingExtractor _extractor;
    private readonly Lock _gate = new();

    /// <summary>Loads the model from <see cref="MeetingModels.Directory"/>.</summary>
    public WeSpeakerEmbedder() => _extractor = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig
    {
        Model = Path.Combine(MeetingModels.Directory, MeetingModels.SpeakerFile),
        NumThreads = 2,
        Provider = "cpu",
    });

    /// <inheritdoc />
    public float[] Embed(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            using var stream = _extractor.CreateStream();
            stream.AcceptWaveform(AudioChunk.SampleRate, samples.ToArray());
            stream.InputFinished();
            return _extractor.Compute(stream);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _extractor.Dispose();
}
