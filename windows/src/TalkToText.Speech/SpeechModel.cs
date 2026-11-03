namespace TalkToText.Speech;

/// <summary>
/// The speech model the app downloads itself: Parakeet TDT 0.6B v3, int8 — 25 languages,
/// Portuguese among them.
/// </summary>
/// <remarks>
/// v3, never v2: v2 only knows English, and a Portuguese dictation through it comes out as
/// English-sounding nonsense rather than an error. The same files, sizes and folder as the
/// installer's, so either one finds what the other downloaded.
/// </remarks>
public static class SpeechModel
{
    private const string Repo = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/";

    /// <summary><c>%LOCALAPPDATA%\TalkToText\models\parakeet-v3</c>.</summary>
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TalkToText", "models", "parakeet-v3");

    /// <summary>Downloads, with the exact size each file must have.</summary>
    public static IReadOnlyList<(string File, string Url, long Size)> Downloads { get; } =
    [
        ("tokens.txt", Repo + "tokens.txt", 93939),
        ("joiner.int8.onnx", Repo + "joiner.int8.onnx", 6355277),
        ("decoder.int8.onnx", Repo + "decoder.int8.onnx", 11845275),
        ("encoder.int8.onnx", Repo + "encoder.int8.onnx", 652184281),
    ];

    /// <summary>The whole download, in bytes.</summary>
    public static long TotalSize { get; } = Downloads.Sum(d => d.Size);

    /// <summary>Whether every file is present at its exact size.</summary>
    public static bool IsInstalled() => Downloads.All(d =>
    {
        var path = Path.Combine(Directory, d.File);
        return File.Exists(path) && new FileInfo(path).Length == d.Size;
    });
}
