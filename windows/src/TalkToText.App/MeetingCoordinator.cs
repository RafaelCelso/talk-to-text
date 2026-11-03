using Avalonia.Threading;
using TalkToText.Abstractions;
using TalkToText.Core;
using TalkToText.Speech;

namespace TalkToText.App;

/// <summary>
/// Watches for meetings, asks before transcribing one, and runs the live transcript.
/// </summary>
/// <remarks>
/// Every event is raised on the UI thread. Polling and the session's own work run in the
/// background; nothing here blocks the window.
/// </remarks>
public sealed class MeetingCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly Composition _composition;
    private readonly IMeetingSignals? _signals;
    private readonly MeetingWatcher _watcher = new();
    private readonly CancellationTokenSource _stop = new();
    private WeSpeakerEmbedder? _embedder;
    private Task _polling = Task.CompletedTask;

    /// <summary>Wires the coordinator; call <see cref="Start"/> to begin watching.</summary>
    public MeetingCoordinator(Composition composition)
    {
        _composition = composition;
        _signals = PlatformFactory.CreateMeetingSignals();

        _watcher.Started += (_, meeting) => Dispatcher.UIThread.Post(() => OnStarted(meeting));
        _watcher.Ended += (_, meeting) => Dispatcher.UIThread.Post(() => _ = OnEndedAsync(meeting));
    }

    /// <summary>The transcript being recorded now, if any.</summary>
    public MeetingSession? Session { get; private set; }

    /// <summary>The voices the user has named, recognised in later meetings and recordings.</summary>
    public KnownVoices Voices => _composition.Voices;

    /// <summary>The cloud model chosen in Settings, or null for the local AI.</summary>
    public CloudLanguageModel? CloudModel() => _composition.CloudModel();

    /// <summary>Whether meeting transcription can run here at all.</summary>
    public bool IsAvailable => _signals is not null && _composition.Transcriber is not null;

    /// <summary>A meeting was detected and is waiting for the user's yes or no.</summary>
    public event EventHandler<DetectedMeeting>? MeetingDetected;

    /// <summary>A session started or stopped.</summary>
    public event EventHandler? SessionChanged;

    /// <summary>Begins polling for meetings.</summary>
    public void Start()
    {
        // Nothing is live yet, so any transcript still marked live was cut off by the app
        // being closed mid-meeting.
        var closed = _composition.Meetings.CloseUnfinished();
        if (closed > 0) DiagnosticLog.Write($"closed {closed} meeting(s) left unfinished by an earlier run");

        if (_signals is null) return;
        _polling = Task.Run(() => PollAsync(_stop.Token));
    }

    private async Task PollAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                _watcher.Observe(MeetingDetector.Detect(_signals!.Snapshot()));
            }
            catch (Exception e)
            {
                DiagnosticLog.Write($"meeting detection failed: {e.Message}");
            }

            try
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnStarted(DetectedMeeting meeting)
    {
        DiagnosticLog.Write($"meeting detected: {meeting.App} — {meeting.Title}");
        if (!_composition.Settings.Data.DetectMeetings || Session is not null) return;
        MeetingDetected?.Invoke(this, meeting);
    }

    private async Task OnEndedAsync(DetectedMeeting meeting)
    {
        DiagnosticLog.Write($"meeting ended: {meeting.App} — {meeting.Title}");
        if (Session is not null) await StopAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Starts transcribing <paramref name="meeting"/>, or a meeting the user started by hand
    /// when it is null.
    /// </summary>
    /// <returns>False when the models or the platform layer are missing.</returns>
    public bool StartTranscription(DetectedMeeting? meeting = null)
    {
        if (Session is not null) return true;
        if (!IsAvailable || !MeetingModels.IsInstalled()) return false;

        var microphone = PlatformFactory.CreateAudioCapture();
        var loopback = PlatformFactory.CreateLoopbackCapture();
        if (microphone is null || loopback is null) return false;

        meeting ??= _watcher.Current ?? new DetectedMeeting("Reunião", string.Empty, "Reunião iniciada manualmente");
        _embedder ??= new WeSpeakerEmbedder();

        var session = new MeetingSession(
            meeting,
            LocalName(),
            microphone,
            loopback,
            new SileroSpeechSegmenter(),
            new SileroSpeechSegmenter(),
            _embedder,
            _composition.Transcriber!,
            _composition.Voices,
            _composition.Meetings,
            PlatformFactory.CreateActiveSpeakerSource());

        session.Diagnostic += (_, message) => DiagnosticLog.Write(message);
        session.Start();

        Session = session;
        SessionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Stops the live transcript, keeping everything said so far.</summary>
    public async Task StopAsync()
    {
        if (Session is not { } session) return;

        Session = null;
        SessionChanged?.Invoke(this, EventArgs.Empty);
        await session.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>What the user is called in transcripts.</summary>
    public string LocalName() =>
        string.IsNullOrWhiteSpace(_composition.Settings.Data.DisplayName) ? "Você" : _composition.Settings.Data.DisplayName!.Trim();

    /// <summary>
    /// Downloads the voice-activity and voice-print models (~27 MB), each checked against
    /// its exact size before it is moved into place.
    /// </summary>
    /// <param name="progress">0…1 across both files.</param>
    /// <param name="cancellationToken">Abandons the download.</param>
    public static async Task DownloadModelsAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var total = MeetingModels.Downloads.Sum(d => d.Size);
        long before = 0;

        foreach (var (file, url, size) in MeetingModels.Downloads)
        {
            var done = before;
            await DownloadFileAsync(
                url, Path.Combine(MeetingModels.Directory, file), size,
                new Progress<long>(bytes => progress?.Report((double)(done + bytes) / total)),
                cancellationToken).ConfigureAwait(false);
            before += size;
        }
    }

    /// <summary>
    /// Downloads one file to <paramref name="target"/> unless it is already there at
    /// <paramref name="size"/> bytes. Written to <c>.download</c> first and moved into place
    /// only once complete, so a cut connection never leaves a truncated model behind.
    /// </summary>
    internal static async Task DownloadFileAsync(
        string url, string target, long size, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (File.Exists(target) && new FileInfo(target).Length == size)
        {
            progress?.Report(size);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".download";

        using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
        using (var response = await http.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = File.Create(temp);

            var buffer = new byte[1 << 16];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                progress?.Report(written);
            }
        }

        if (new FileInfo(temp).Length != size)
        {
            File.Delete(temp);
            throw new IOException($"{Path.GetFileName(target)} downloaded incomplete; try again.");
        }

        File.Move(temp, target, overwrite: true);
    }

    /// <summary>The summary being written now, if any. One at a time: each needs ~2.5 GB.</summary>
    public SummaryJob? Summary { get; private set; }

    /// <summary>The summary job changed: progress, new text, finished or failed. UI thread.</summary>
    public event EventHandler? SummaryChanged;

    /// <summary>
    /// Writes a summary of transcript <paramref name="meetingId"/> in <paramref name="store"/> —
    /// a meeting or a transcribed audio file — downloading the model first if this is the first
    /// summary. Only ever on the user's request.
    /// </summary>
    /// <param name="store">Where the transcript is, and where the summary is saved.</param>
    /// <param name="meetingId">The transcript.</param>
    /// <param name="instruction">The user's context for the AI, from Settings; null for none.</param>
    /// <returns>An error message for the user, or null on success or cancellation.</returns>
    public async Task<string?> SummarizeAsync(MeetingStore store, Guid meetingId, string? instruction = null)
    {
        if (Summary is not null) return null;

        var meeting = store.All().FirstOrDefault(m => m.Id == meetingId);
        if (meeting is null || meeting.Messages.Count == 0) return null;

        var job = new SummaryJob(meetingId);
        Summary = job;
        Notify();

        try
        {
            var cloud = _composition.CloudModel();
            if (cloud is null && !SummaryModel.IsInstalled())
            {
                job.Status = "Baixando o modelo de resumo (2 GB)…";
                Notify();
                await DownloadFileAsync(
                    SummaryModel.Url, SummaryModel.PathOnDisk, SummaryModel.Size,
                    new Progress<long>(bytes =>
                    {
                        job.Status = $"Baixando o modelo de resumo… {(double)bytes / SummaryModel.Size:P0}";
                        Notify();
                    }),
                    job.Cancellation.Token).ConfigureAwait(true);
            }

            job.Status = cloud is null ? "Carregando o modelo…" : $"Enviando para {CloudAi.Info(cloud.Provider).Name}…";
            Notify();

            using var model = cloud ?? await Task.Run<ILanguageModel>(() => new QwenLanguageModel(), job.Cancellation.Token).ConfigureAwait(true);
            var summarizer = new MeetingSummarizer(model, instruction);
            var progress = new Progress<SummaryProgress>(p =>
            {
                job.Status = p.Writing
                    ? "Escrevendo o resumo…"
                    : $"Lendo a conversa, parte {p.Step} de {p.Steps - 1}…";
                Notify();
            });

            // Generated on the thread pool; each piece is handed to the UI as it arrives.
            await Task.Run(async () =>
            {
                await foreach (var piece in summarizer.SummarizeAsync(meeting, progress, job.Cancellation.Token).ConfigureAwait(false))
                {
                    job.Append(piece);
                    Dispatcher.UIThread.Post(Notify);
                }
            }, job.Cancellation.Token).ConfigureAwait(true);

            meeting.Summary = job.CurrentText().Trim();
            meeting.SummarizedAt = DateTimeOffset.Now;
            store.Save(meeting);
            DiagnosticLog.Write($"meeting summarised: {meeting.Title} ({meeting.Summary.Length} chars)");
            return null;
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Write("meeting summary cancelled");
            return null;
        }
        catch (Exception e)
        {
            DiagnosticLog.Write($"meeting summary failed: {e}");
            return e switch
            {
                // From the cloud provider, its reason: a bad key, no credit, no connection.
                HttpRequestException when _composition.UsesCloudAi => $"Não foi possível gerar o resumo: {e.Message}",
                HttpRequestException or IOException => "Não foi possível baixar o modelo. Verifique a internet e tente de novo.",
                _ => "Não foi possível gerar o resumo.",
            };
        }
        finally
        {
            job.Dispose();
            Summary = null;
            Notify();
        }
    }

    /// <summary>Stops the summary being written; nothing is saved.</summary>
    public void CancelSummary() => Summary?.Cancel();

    private void Notify() => SummaryChanged?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _polling.ConfigureAwait(false);
        if (Session is { } session) await session.DisposeAsync().ConfigureAwait(false);
        _embedder?.Dispose();
        _stop.Dispose();
    }
}
