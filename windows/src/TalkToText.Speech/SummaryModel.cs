using System.Runtime.CompilerServices;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using TalkToText.Abstractions;

namespace TalkToText.Speech;

/// <summary>The local model meeting summaries are written with: Qwen2.5 3B Instruct, Q4_K_M.</summary>
/// <remarks>
/// Qwen2.5 because it writes good Portuguese at a size a CPU can run; 3B rather than 1.5B
/// because the smaller one blurs who said what. Downloaded only when the user first asks for
/// a summary — it is 2 GB, and most people may never want one.
/// </remarks>
public static class SummaryModel
{
    /// <summary>The model file.</summary>
    public const string File = "qwen2.5-3b-instruct-q4_k_m.gguf";

    /// <summary>Where it comes from.</summary>
    public const string Url = "https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf";

    /// <summary>Its exact size, for spotting a truncated download.</summary>
    public const long Size = 2104932768;

    /// <summary><c>%LOCALAPPDATA%\TalkToText\models\summary</c>.</summary>
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TalkToText", "models", "summary");

    /// <summary>The full path of the model file.</summary>
    public static string PathOnDisk => Path.Combine(Directory, File);

    /// <summary>Whether the model is present at its exact size.</summary>
    public static bool IsInstalled() =>
        System.IO.File.Exists(PathOnDisk) && new FileInfo(PathOnDisk).Length == Size;

    /// <summary>Whether the Vulkan build of llama.cpp was asked for; see <see cref="ConfigureBackend"/>.</summary>
    public static bool GpuRequested { get; private set; }

    /// <summary>
    /// Whether the loaded llama.cpp can put the model on a graphics card. Loads the native
    /// library if nothing has yet.
    /// </summary>
    public static bool UsesGpu
    {
        get
        {
            try
            {
                return GpuRequested && LLama.Native.NativeApi.llama_supports_gpu_offload();
            }
            catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or LLama.Exceptions.RuntimeError)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Picks llama.cpp's build before it first loads: Vulkan, which runs on AMD, NVIDIA and Intel
    /// graphics, integrated or not, or the plain CPU one. Without a Vulkan driver the Vulkan
    /// build fails to load and LLamaSharp falls back to the CPU on its own.
    /// </summary>
    /// <remarks>
    /// <b>AVX2 only.</b> The Vulkan build still does part of the work on the CPU, through a
    /// <c>ggml-cpu.dll</c> its package forgot to ship; the app's publish copies the AVX2 one in
    /// beside it. On a CPU without AVX2 that copy would crash, so those stay on the CPU build,
    /// which picks its own variant.
    /// </remarks>
    public static void ConfigureBackend(bool useGpu)
    {
        GpuRequested = useGpu && System.Runtime.Intrinsics.X86.Avx2.IsSupported;
        try
        {
            LLama.Native.NativeLibraryConfig.All.WithVulkan(GpuRequested).WithAutoFallback(true);
        }
        catch (InvalidOperationException)
        {
            // Already loaded: the first choice stands until the app restarts.
        }
    }

    /// <summary>
    /// Forces llama.cpp's native library to load, without a model. For the self-test: the
    /// native DLL lives under <c>runtimes/…</c> and a single-file publish is exactly where
    /// resolving it could break.
    /// </summary>
    /// <returns>Null on success, otherwise why it failed.</returns>
    public static string? CheckNativeLibrary()
    {
        try
        {
            LLama.Native.NativeApi.llama_empty_call();
            return null;
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or LLama.Exceptions.RuntimeError)
        {
            return e.GetBaseException().Message;
        }
    }
}

/// <summary>Qwen2.5 through llama.cpp (LLamaSharp), on the graphics card when there is one.</summary>
/// <remarks>
/// Loading takes a few seconds and ~2.5 GB of memory, so the model is loaded when a summary
/// is requested and released afterwards rather than held for the life of the app.
/// </remarks>
public sealed class QwenLanguageModel : ILanguageModel
{
    private const string EndOfTurn = "<|im_end|>";

    private readonly LLamaWeights _weights;
    private readonly ModelParams _parameters;

    /// <summary>Loads <see cref="SummaryModel.PathOnDisk"/>.</summary>
    /// <param name="batchSize">Prompt tokens read per step; exposed for the benchmark.</param>
    public QwenLanguageModel(uint batchSize = 512)
    {
        _batchSize = batchSize;
        OnGpu = SummaryModel.UsesGpu;
        _parameters = Parameters(OnGpu);
        try
        {
            _weights = LLamaWeights.LoadFromFile(_parameters);
        }
        catch (Exception e) when (OnGpu && e is LLama.Exceptions.LoadWeightsFailedException or LLama.Exceptions.RuntimeError or OutOfMemoryException)
        {
            // A card without room for the model must not cost the user the feature.
            OnGpu = false;
            _parameters = Parameters(onGpu: false);
            _weights = LLamaWeights.LoadFromFile(_parameters);
        }
    }

    /// <summary>Whether the model's layers are on the graphics card.</summary>
    public bool OnGpu { get; }

    private readonly uint _batchSize;

    private ModelParams Parameters(bool onGpu) => new(SummaryModel.PathOnDisk)
    {
        ContextSize = 8192,
        BatchSize = _batchSize,
        UBatchSize = _batchSize,

        // Half the logical cores: leaves room for dictation and the meeting app while a
        // summary is being written.
        Threads = Math.Max(1, Environment.ProcessorCount / 2),

        // Every layer: Qwen2.5 3B has 36, and a card either holds the whole 2 GB or the load
        // fails and it runs on the CPU.
        GpuLayerCount = onGpu ? 99 : 0,
    };

    /// <inheritdoc />
    public int ContextTokens => (int)(_parameters.ContextSize ?? 8192);

    /// <inheritdoc />
    public int CountTokens(string text) => _weights.Tokenize(text, false, false, Encoding.UTF8).Length;

    /// <inheritdoc />
    /// <remarks>Qwen's ChatML template, written out: the model was trained on exactly this.</remarks>
    public async IAsyncEnumerable<string> CompleteAsync(
        string system, string user, int maxTokens, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prompt = $"<|im_start|>system\n{system}{EndOfTurn}\n<|im_start|>user\n{user}{EndOfTurn}\n<|im_start|>assistant\n";

        // A fresh executor per request: each pass of a long meeting is independent.
        var executor = new StatelessExecutor(_weights, _parameters);
        var inference = new InferenceParams
        {
            MaxTokens = maxTokens,
            AntiPrompts = [EndOfTurn],
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.2f, TopP = 0.9f, RepeatPenalty = 1.1f },
        };

        await foreach (var piece in executor.InferAsync(prompt, inference, cancellationToken).ConfigureAwait(false))
        {
            // The stop marker can arrive as part of the last piece; it is never content.
            var text = piece.Replace(EndOfTurn, string.Empty, StringComparison.Ordinal);
            if (text.Length > 0) yield return text;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _weights.Dispose();
}
