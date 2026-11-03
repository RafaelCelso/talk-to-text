using System.Text;
using TalkToText.Abstractions;
using TalkToText.Speech;

namespace TalkToText.App;

/// <summary>
/// The AI as dictation uses it — tidying a dictation, and carrying out spoken commands on the
/// selected text — whether that is the local Qwen or a cloud model the user chose.
/// </summary>
/// <remarks>
/// <para>
/// Loading the local model takes a few seconds, which would land on every dictation, so it stays
/// loaded for <see cref="KeepWarm"/> after the last use and is then let go — it holds about
/// 2.5 GB. The Chat borrows the same loaded model (<see cref="AcquireAsync"/>), so a
/// conversation answers without the wait and two copies are never in memory at once.
/// </para>
/// <para>
/// With a cloud provider chosen, <paramref name="cloud"/> hands back its model and nothing is
/// loaded or waited for: requests to a provider can run side by side.
/// </para>
/// </remarks>
/// <param name="cloud">The cloud model to use instead of the local one, read on every use; null for the local one.</param>
internal sealed class AiRewriter(Func<ILanguageModel?> cloud) : ITextRewriter, IDisposable
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
    public async Task<string> PolishAsync(string text, CancellationToken cancellationToken, IProgress<RewriteProgress>? progress = null)
    {
        var polished = await RunAsync(PolishInstruction, text, (text.Length / 2) + 64, progress, cancellationToken).ConfigureAwait(false);

        // A "revision" much shorter or longer than what was said is the model answering it
        // or summarising it instead: type what the user said.
        return polished.Length < text.Length * 0.6 || polished.Length > (text.Length * 1.5) + 20 ? text : polished;
    }

    /// <inheritdoc />
    public Task<string> CommandAsync(
        string instruction, string? selection, CancellationToken cancellationToken, IProgress<RewriteProgress>? progress = null)
    {
        var request = CommandPrompt.Build(instruction, selection);
        return RunAsync(request.System, request.User, request.MaxTokens, progress, cancellationToken);
    }

    /// <summary>
    /// The model to use, for a caller that drives it itself: the chosen cloud model, or the
    /// local one, loaded if it is not. Nobody else uses the local one until the lease is
    /// disposed; then it stays loaded for <see cref="KeepWarm"/>.
    /// </summary>
    /// <param name="cancellationToken">Gives up waiting.</param>
    /// <param name="loading">Called before the model is loaded, when it has to be.</param>
    public async Task<Lease> AcquireAsync(CancellationToken cancellationToken, Action? loading = null)
    {
        if (cloud() is { } remote) return new Lease(remote, static () => { });
        if (!SummaryModel.IsInstalled()) throw new InvalidOperationException("a IA local ainda não foi baixada");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _release?.Dispose();
            _release = null;

            if (_model is null)
            {
                loading?.Invoke();
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

    private async Task<string> RunAsync(
        string system, string user, int maxTokens, IProgress<RewriteProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            using var lease = await AcquireAsync(
                cancellationToken, () => progress?.Report(new RewriteProgress(RewriteStage.LoadingModel))).ConfigureAwait(false);
            progress?.Report(new RewriteProgress(RewriteStage.Reading));
            var answer = new StringBuilder();
            await foreach (var piece in lease.Model.CompleteAsync(system, user, maxTokens, cancellationToken).ConfigureAwait(false))
            {
                answer.Append(piece);
                progress?.Report(new RewriteProgress(RewriteStage.Writing, answer.Length));
            }

            return Unquoted(answer.ToString().Trim());
        }
        catch (Exception e) when (e is LLama.Exceptions.RuntimeError or LLama.Exceptions.LLamaDecodeError or LLama.Exceptions.LoadWeightsFailedException
                                       or HttpRequestException or System.Text.Json.JsonException)
        {
            throw new InvalidOperationException(e.Message, e);
        }
    }

    /// <summary>The model, lent until disposed.</summary>
    public sealed class Lease(ILanguageModel model, Action giveBack) : IDisposable
    {
        private int _returned;

        /// <summary>The model.</summary>
        public ILanguageModel Model => model;

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
