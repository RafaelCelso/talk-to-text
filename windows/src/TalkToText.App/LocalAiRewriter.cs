using System.Text;
using TalkToText.Abstractions;
using TalkToText.Speech;

namespace TalkToText.App;

/// <summary>
/// The local Qwen model as dictation uses it: tidying a dictation, and carrying out spoken
/// commands on the selected text.
/// </summary>
/// <remarks>
/// Loading takes a few seconds, which would land on every dictation, so the model stays loaded
/// for <see cref="KeepWarm"/> after the last use and is then let go — it holds about 2.5 GB.
/// The Chat borrows the same loaded model (<see cref="AcquireAsync"/>), so a conversation
/// answers without the wait and two copies are never in memory at once.
/// </remarks>
internal sealed class LocalAiRewriter : ITextRewriter, IDisposable
{
    private static readonly TimeSpan KeepWarm = TimeSpan.FromMinutes(5);

    private const string PolishInstruction =
        "Você revisa textos ditados por voz. Corrija pontuação, acentuação, maiúsculas e concordância, "
        + "e tire hesitações e palavras repetidas. Mantenha as mesmas palavras e o mesmo sentido: não resuma, "
        + "não acrescente nada e não responda ao que o texto diz. Responda somente com o texto revisado.";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private QwenLanguageModel? _model;
    private Timer? _release;

    /// <inheritdoc />
    public async Task<string> PolishAsync(string text, CancellationToken cancellationToken)
    {
        var polished = await RunAsync(PolishInstruction, text, (text.Length / 2) + 64, cancellationToken).ConfigureAwait(false);

        // A "revision" much shorter or longer than what was said is the model answering it
        // or summarising it instead: type what the user said.
        return polished.Length < text.Length * 0.6 || polished.Length > (text.Length * 1.5) + 20 ? text : polished;
    }

    /// <inheritdoc />
    public Task<string> CommandAsync(string instruction, string? selection, CancellationToken cancellationToken)
    {
        var request = CommandPrompt.Build(instruction, selection);
        return RunAsync(request.System, request.User, request.MaxTokens, cancellationToken);
    }

    /// <summary>
    /// The loaded model, loading it if it is not, for a caller that drives it itself. Nobody
    /// else uses it until the lease is disposed; then it stays loaded for <see cref="KeepWarm"/>.
    /// </summary>
    public async Task<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        if (!SummaryModel.IsInstalled()) throw new InvalidOperationException("a IA local ainda não foi baixada");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _release?.Dispose();
            _release = null;

            if (_model is null)
            {
                _model = await Task.Run(() => new QwenLanguageModel(), cancellationToken).ConfigureAwait(false);
                DiagnosticLog.Write($"local AI loaded on the {(_model.OnGpu ? "graphics card" : "CPU")}");
            }

            return new Lease(_model, Return);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private void Return()
    {
        _release = new Timer(_ => Release(), null, KeepWarm, Timeout.InfiniteTimeSpan);
        _gate.Release();
    }

    private async Task<string> RunAsync(string system, string user, int maxTokens, CancellationToken cancellationToken)
    {
        try
        {
            using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            var answer = new StringBuilder();
            await foreach (var piece in lease.Model.CompleteAsync(system, user, maxTokens, cancellationToken).ConfigureAwait(false))
            {
                answer.Append(piece);
            }

            return Unquoted(answer.ToString().Trim());
        }
        catch (Exception e) when (e is LLama.Exceptions.RuntimeError or LLama.Exceptions.LLamaDecodeError or LLama.Exceptions.LoadWeightsFailedException)
        {
            throw new InvalidOperationException(e.Message, e);
        }
    }

    /// <summary>The model, lent until disposed.</summary>
    public sealed class Lease(QwenLanguageModel model, Action giveBack) : IDisposable
    {
        private int _returned;

        /// <summary>The loaded model.</summary>
        public QwenLanguageModel Model => model;

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0) giveBack();
        }
    }

    /// <summary>Lets go of the model once it has sat unused; never while it is working.</summary>
    private void Release()
    {
        if (!_gate.Wait(0)) return;
        try
        {
            _model?.Dispose();
            _model = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Models like to wrap the answer in quotes it was told not to use.</summary>
    private static string Unquoted(string text) =>
        text.Length >= 2 && (text[0], text[^1]) is ('"', '"') or ('“', '”') ? text[1..^1].Trim() : text;

    /// <inheritdoc />
    public void Dispose()
    {
        _release?.Dispose();
        _model?.Dispose();
        _gate.Dispose();
    }
}
