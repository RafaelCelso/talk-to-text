using TalkToText.Abstractions;
using SherpaOnnx;

namespace TalkToText.Speech;

/// <summary>
/// NVIDIA Parakeet TDT, running locally through sherpa-onnx.
/// </summary>
/// <remarks>
/// <para>
/// Windows has no counterpart to Apple's <c>SpeechAnalyzer</c>, so unlike the macOS build —
/// where Parakeet is an optional upgrade — this is the only engine, and the app cannot
/// transcribe until the model files are downloaded. See <c>docs/PARAKEET-WINDOWS.md</c>.
/// </para>
/// <para>
/// <b>CPU only, deliberately.</b> sherpa-onnx ships no GPU package at all — setting a CUDA
/// provider silently falls back to CPU. DirectML is several releases behind, forbids parallel
/// inference, and wants fixed tensor shapes, which a variable-length audio model cannot
/// provide. With int8 weights on four threads this runs roughly 40× faster than real time,
/// so none of that is worth the dependency.
/// </para>
/// <para>
/// <b>Biasing is not supported by this engine.</b> sherpa-onnx's offline recogniser has no
/// contextual-strings equivalent to Apple's <c>AnalysisContext</c>, so the dictionary's
/// correction pass carries the whole job on Windows. That pass was always the guarantee and
/// biasing only ever the nudge, so behaviour matches — the Windows build simply has one fewer
/// chance to get a name right before correction.
/// </para>
/// </remarks>
public sealed class ParakeetTranscriber : ITranscriber
{
    /// <summary>Threads for inference. Four measured fastest; eight measured slower.</summary>
    private const int Threads = 4;

    /// <summary>
    /// Feature dimension. <b>Must be 128</b> — the library defaults to 80, which is wrong for
    /// this model.
    /// </summary>
    private const int FeatureDim = 128;

    /// <summary>
    /// Model family. <b>Required.</b> Without it the model fails to load.
    /// </summary>
    private const string ModelType = "nemo_transducer";

    private readonly string _modelDirectory;
    private readonly Lock _decodeGate = new();
    private OfflineRecognizer? _recognizer;

    /// <summary>Points the engine at a folder of model files.</summary>
    /// <param name="modelDirectory">
    /// Must contain <c>encoder.int8.onnx</c>, <c>decoder.int8.onnx</c>,
    /// <c>joiner.int8.onnx</c> and <c>tokens.txt</c>.
    /// </param>
    public ParakeetTranscriber(string modelDirectory) => _modelDirectory = modelDirectory;

    /// <summary>Where the model is looked for, in order.</summary>
    /// <remarks>
    /// <para>
    /// <c>%LOCALAPPDATA%</c> first: it needs no administrator rights, so the app can download
    /// and update the model itself even when installed under Program Files.
    /// </para>
    /// <para>
    /// v3 before v2 within each location: v3 covers 25 languages, Portuguese among them, and
    /// is what the installer and the app download. v2 only knows English — with both on disk,
    /// picking v2 turned every Portuguese dictation into nonsense. It is still searched, as a
    /// fallback for a machine set up by hand for English.
    /// </para>
    /// </remarks>
    public static IEnumerable<string> DefaultSearchPaths()
    {
        var localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TalkToText", "models");

        yield return Path.Combine(localAppData, "parakeet-v3");
        yield return Path.Combine(localAppData, "parakeet-v2");

        // AppContext.BaseDirectory, not Assembly.Location — the latter returns an empty
        // string in a single-file app, which silently resolves paths against the current
        // directory instead.
        yield return Path.Combine(AppContext.BaseDirectory, "models", "parakeet-v3");
        yield return Path.Combine(AppContext.BaseDirectory, "models", "parakeet-v2");
    }

    /// <summary>Finds a directory containing a complete model, or null.</summary>
    public static string? Locate() => DefaultSearchPaths().FirstOrDefault(IsComplete);

    /// <summary>Whether <paramref name="directory"/> holds every required file.</summary>
    /// <remarks>
    /// Worth checking before loading: a truncated download fails with an opaque protobuf
    /// parse error that reads like a corrupt build rather than a missing byte range.
    /// </remarks>
    public static bool IsComplete(string directory) =>
        RequiredFiles.All(f => File.Exists(Path.Combine(directory, f)));

    /// <summary>The files the engine needs.</summary>
    public static IReadOnlyList<string> RequiredFiles { get; } =
    [
        "encoder.int8.onnx",
        "decoder.int8.onnx",
        "joiner.int8.onnx",
        "tokens.txt",
    ];

    /// <inheritdoc />
    public bool IsReady => _recognizer is not null;

    /// <inheritdoc />
    public ValueTask<bool> LoadAsync(CancellationToken cancellationToken)
    {
        if (_recognizer is not null) return ValueTask.FromResult(true);
        if (!IsComplete(_modelDirectory)) return ValueTask.FromResult(false);

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = AudioChunk.SampleRate;
        config.FeatConfig.FeatureDim = FeatureDim;

        config.ModelConfig.Transducer.Encoder = Path.Combine(_modelDirectory, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(_modelDirectory, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(_modelDirectory, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(_modelDirectory, "tokens.txt");
        config.ModelConfig.ModelType = ModelType;
        config.ModelConfig.NumThreads = Threads;
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";

        _recognizer = new OfflineRecognizer(config);
        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="biasPhrases"/> is accepted and ignored — see the class remarks. Audio
    /// longer than the encoder can handle is the caller's problem; <c>AudioSegmenter</c>
    /// splits it before this is reached.
    /// </remarks>
    public ValueTask<string> TranscribeAsync(
        ReadOnlyMemory<float> samples,
        IReadOnlyList<string> biasPhrases,
        CancellationToken cancellationToken)
    {
        if (samples.Length == 0) return ValueTask.FromResult(string.Empty);

        cancellationToken.ThrowIfCancellationRequested();

        // One decode at a time: dictation and a meeting transcript share this recognizer, and
        // sherpa-onnx does not promise that concurrent decodes on one recognizer are safe.
        // Checked inside the lock, because the model may be disposed between check and use.
        lock (_decodeGate)
        {
            if (_recognizer is not { } recognizer) return ValueTask.FromResult(string.Empty);

            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(AudioChunk.SampleRate, samples.ToArray());
            recognizer.Decode(stream);

            return ValueTask.FromResult(stream.Result.Text?.Trim() ?? string.Empty);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Under the decode lock: the model can be swapped while the app runs, after a download,
    /// and that must not free the recognizer under a decode in progress.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        lock (_decodeGate)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }

        return ValueTask.CompletedTask;
    }
}
