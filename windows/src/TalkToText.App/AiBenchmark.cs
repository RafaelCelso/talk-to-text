using System.Diagnostics;
using System.Text;
using TalkToText.Speech;

namespace TalkToText.App;

/// <summary>
/// Times the local AI the way the Chat uses it: a long prompt read in, then a short answer
/// written out. Prints and logs load time, reading speed and writing speed.
/// </summary>
internal static class AiBenchmark
{
    public static int Run(bool gpu, uint batchSize)
    {
        if (!SummaryModel.IsInstalled())
        {
            Report("benchmark: the AI model is not downloaded");
            return 1;
        }

        SummaryModel.ConfigureBackend(gpu);
        var clock = Stopwatch.StartNew();
        using var model = new QwenLanguageModel(batchSize);
        var load = clock.Elapsed;

        // About the size of a Chat question with its passages (~2,800 tokens is the cap).
        var passage = new StringBuilder();
        while (model.CountTokens(passage.ToString()) < 2000)
        {
            passage.Append("[10:02] Ana: Precisamos fechar o orçamento do projeto até sexta-feira, e o fornecedor ainda não mandou a proposta revisada. ");
            passage.Append("[10:03] Rafael: Eu ligo para eles hoje à tarde e peço o prazo por escrito; se atrasar de novo, abrimos cotação com outra empresa. ");
        }

        var promptTokens = model.CountTokens(passage.ToString());
        clock.Restart();
        TimeSpan? firstToken = null;
        var pieces = 0;
        var answer = new StringBuilder();
        foreach (var piece in Collect(model.CompleteAsync(
            "Você responde em português, de forma curta.",
            passage + "\n\nQual é o prazo do orçamento e quem vai ligar para o fornecedor?",
            96,
            CancellationToken.None)))
        {
            firstToken ??= clock.Elapsed;
            pieces++;
            answer.Append(piece);
        }

        var total = clock.Elapsed;
        var read = firstToken ?? total;
        var write = total - read;
        var written = model.CountTokens(answer.ToString());

        Report($"benchmark: {(model.OnGpu ? "GPU (Vulkan)" : "CPU")}; requested {(gpu ? "gpu" : "cpu")}; batch {batchSize}");
        Report($"benchmark: load {load.TotalSeconds:F1} s");
        Report($"benchmark: read {promptTokens} tokens in {read.TotalSeconds:F1} s = {promptTokens / Math.Max(0.001, read.TotalSeconds):F0} tokens/s");
        Report($"benchmark: wrote {written} tokens in {write.TotalSeconds:F1} s = {Math.Max(0, written - 1) / Math.Max(0.001, write.TotalSeconds):F1} tokens/s");
        Report($"benchmark: total {total.TotalSeconds:F1} s; answer: {answer.ToString().Trim().Replace('\n', ' ')}");
        return 0;
    }

    /// <summary>
    /// Runs dictation's AI on sample sentences — the polish and two commands — and prints what
    /// came back and how long it took, the model kept loaded in between as dictation keeps it.
    /// </summary>
    public static int TryDictationAi(bool gpu)
    {
        SummaryModel.ConfigureBackend(gpu);
        using var ai = new AiRewriter(() => null);

        void Time(string label, Func<IProgress<Abstractions.RewriteProgress>, Task<string>> run)
        {
            var clock = Stopwatch.StartNew();
            var stages = new Stages();
            var result = run(stages).GetAwaiter().GetResult();
            Report($"try-ai {label} ({clock.Elapsed.TotalSeconds:F1} s; {stages}): {result.Replace('\n', ' ')}");
        }

        Time("polish 1", p => ai.PolishAsync("entao eu acho que a gente precisa fechar o orcamento ate sexta feira e mandar pro cliente", CancellationToken.None, p));
        Time("polish 2", p => ai.PolishAsync("o relatorio ficou pronto mas faltou os graficos do terceiro trimestre", CancellationToken.None, p));
        Time("formal", p => ai.CommandAsync("deixa mais formal", "oi, blz? manda o relatório até amanhã pfv", CancellationToken.None, p));
        Time("english", p => ai.CommandAsync("traduz para inglês", "Obrigado pela reunião de hoje, seguem os próximos passos.", CancellationToken.None, p));
        // A selection that is itself a request: it must be translated, not obeyed or half-translated.
        Time("english 2", p => ai.CommandAsync(
            "Traduza para inglês.",
            "Me explique melhor como que funciona o 'Modo Comando'. Atualmente quando eu seleciono um texto e aperto as teclas de atalho...",
            CancellationToken.None,
            p));
        Time("spanish", p => ai.CommandAsync(
            "Traduz isso pro espanhol",
            "Preciso que você revise o contrato até amanhã e me avise se tiver alguma cláusula estranha.",
            CancellationToken.None,
            p));
        Time("formal 2", p => ai.CommandAsync(
            "Deixa mais formal.",
            "Me explique melhor como que funciona o 'Modo Comando'. Atualmente quando eu seleciono um texto e aperto as teclas de atalho...",
            CancellationToken.None,
            p));
        Time("write", p => ai.CommandAsync("escreve uma frase curta agradecendo o cliente pela parceria", null, CancellationToken.None, p));
        return 0;
    }

    /// <summary>Counts the progress the AI reports, to check the pill has something to show.</summary>
    private sealed class Stages : IProgress<Abstractions.RewriteProgress>
    {
        private readonly List<string> _seen = [];
        private int _written;

        public void Report(Abstractions.RewriteProgress value)
        {
            var name = value.Stage.ToString();
            if (_seen.Count == 0 || _seen[^1] != name) _seen.Add(name);
            _written = value.CharactersWritten;
        }

        public override string ToString() => $"{string.Join(" → ", _seen)}, {_written} chars";
    }

    /// <summary>Reads the selection in the app in front after two seconds — to check command mode's copy on a real machine.</summary>
    public static int ReadSelection()
    {
        Thread.Sleep(2000);
        var reader = PlatformFactory.CreateSelectionReader();
        var selection = reader?.CopySelectionAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Report($"read-selection: {(selection is null ? "(nothing)" : $"\"{selection}\"")}");
        return selection is null ? 1 : 0;
    }

    /// <summary>
    /// Writes the newest meeting as a PDF to <paramref name="path"/> — the export button's own
    /// code, run from the published app, to check Skia's PDF backend and the bundled fonts.
    /// </summary>
    public static int ExportNewestPdf(string path)
    {
        Program.BuildAvaloniaApp().SetupWithoutStarting();
        var newest = new Core.MeetingStore(Core.MeetingStore.DefaultDirectory).All().MaxBy(m => m.StartedAt);
        if (newest is null)
        {
            Report("export-pdf: no meetings");
            return 1;
        }

        using (var file = File.Create(path)) Views.TranscriptPdf.Write(newest, file);
        Report($"export-pdf: \"{newest.Title}\" → {new FileInfo(path).Length} bytes");
        return 0;
    }

    private static IEnumerable<string> Collect(IAsyncEnumerable<string> pieces)
    {
        var e = pieces.GetAsyncEnumerator();
        try
        {
            while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult()) yield return e.Current;
        }
        finally
        {
            e.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void Report(string line)
    {
        Console.WriteLine(line);
        DiagnosticLog.Write(line);
    }
}
