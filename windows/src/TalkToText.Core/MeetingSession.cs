using System.Threading.Channels;
using TalkToText.Abstractions;

namespace TalkToText.Core;

/// <summary>
/// Transcribes a meeting live, as a chat: who said what, as they say it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two sources, and that is how the user is told apart.</b> The microphone carries the
/// user; the speakers (WASAPI loopback) carry everyone else. Remote speech is then grouped by
/// voice print into participants, who are recognised from earlier meetings, named from the
/// meeting app when it says who is talking, or renamed by the user.
/// </para>
/// <para>
/// <b>Echo.</b> Without headphones the microphone also hears the speakers. A microphone
/// utterance that mostly overlaps remote speech is that echo, and is dropped rather than
/// attributed to the user.
/// </para>
/// <para>
/// Every heavy step — voice prints, transcription — runs on one worker in arrival order, so
/// messages appear in the order they were spoken and the shared recognizer is never asked
/// for two things at once by this session.
/// </para>
/// </remarks>
public sealed class MeetingSession : IAsyncDisposable
{
    /// <summary>The user's speaker id.</summary>
    public const int LocalSpeaker = 0;

    /// <summary>
    /// How long the VAD waits after speech before closing an utterance. An utterance therefore
    /// ended roughly this long before it was delivered.
    /// </summary>
    private static readonly TimeSpan SilenceTail = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a microphone utterance waits for remote speech it might be echoing.</summary>
    private static readonly TimeSpan EchoSettle = TimeSpan.FromMilliseconds(700);

    private readonly IAudioCapture _microphone;
    private readonly IAudioCapture _loopback;
    private readonly ISpeechSegmenter _microphoneVad;
    private readonly ISpeechSegmenter _loopbackVad;
    private readonly ISpeakerEmbedder _embedder;
    private readonly ITranscriber _transcriber;
    private readonly KnownVoices _voices;
    private readonly MeetingStore _store;
    private readonly IActiveSpeakerSource? _activeSpeaker;
    private readonly string _processName;
    private readonly IClock _clock;
    private readonly SpeakerClusterer _clusterer;
    private readonly TimeSpan _captureStopLimit;

    private readonly Lock _gate = new();
    private readonly Lock _vadGate = new();
    private bool _vadsDisposed;
    private readonly Dictionary<int, int> _speakerByCluster = [];
    private readonly List<(DateTimeOffset Start, DateTimeOffset End)> _remoteSpeech = [];
    private readonly List<(DateTimeOffset At, string Name)> _hints = [];
    private readonly Channel<Utterance> _queue = Channel.CreateUnbounded<Utterance>(
        new UnboundedChannelOptions { SingleReader = true });

    private CancellationTokenSource? _stop;
    private Task _captures = Task.CompletedTask;
    private Task _worker = Task.CompletedTask;

    /// <summary>Sets up a session; nothing is captured until <see cref="Start"/>.</summary>
    public MeetingSession(
        DetectedMeeting meeting,
        string localName,
        IAudioCapture microphone,
        IAudioCapture loopback,
        ISpeechSegmenter microphoneVad,
        ISpeechSegmenter loopbackVad,
        ISpeakerEmbedder embedder,
        ITranscriber transcriber,
        KnownVoices voices,
        MeetingStore store,
        IActiveSpeakerSource? activeSpeaker = null,
        IClock? clock = null,
        TimeSpan? captureStopLimit = null)
    {
        _captureStopLimit = captureStopLimit ?? TimeSpan.FromSeconds(5);
        _microphone = microphone;
        _loopback = loopback;
        _microphoneVad = microphoneVad;
        _loopbackVad = loopbackVad;
        _embedder = embedder;
        _transcriber = transcriber;
        _voices = voices;
        _store = store;
        _activeSpeaker = activeSpeaker;
        _processName = meeting.ProcessName;
        _clock = clock ?? SystemClock.Instance;
        _clusterer = new SpeakerClusterer(voices.All);

        Transcript = new MeetingTranscript
        {
            App = meeting.App,
            Title = meeting.Title,
            StartedAt = _clock.Now,
            Speakers = [new MeetingSpeaker { Id = LocalSpeaker, Name = localName, IsLocal = true, IsNamed = true }],
        };
    }

    /// <summary>
    /// The live transcript. Mutated by the worker: read it through <see cref="Snapshot"/>
    /// from any other thread.
    /// </summary>
    public MeetingTranscript Transcript { get; }

    /// <summary>Whether capture is running.</summary>
    public bool IsRunning => _stop is { IsCancellationRequested: false };

    /// <summary>A message was added. Raised on the worker thread.</summary>
    public event EventHandler<MeetingMessage>? MessageAdded;

    /// <summary>A speaker joined or was renamed. Raised on any thread.</summary>
    public event EventHandler? SpeakersChanged;

    /// <summary>One line per notable step, for the log.</summary>
    public event EventHandler<string>? Diagnostic;

    /// <summary>A consistent copy of the transcript, safe to read on the UI thread.</summary>
    public MeetingTranscript Snapshot()
    {
        lock (_gate)
        {
            return new MeetingTranscript
            {
                Id = Transcript.Id,
                App = Transcript.App,
                Title = Transcript.Title,
                StartedAt = Transcript.StartedAt,
                EndedAt = Transcript.EndedAt,
                Speakers = Transcript.Speakers.Select(s => new MeetingSpeaker
                {
                    Id = s.Id, Name = s.Name, IsLocal = s.IsLocal, IsNamed = s.IsNamed,
                }).ToList(),
                Messages = [.. Transcript.Messages],
            };
        }
    }

    /// <summary>Starts capturing both sources.</summary>
    public void Start()
    {
        if (_stop is not null) return;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;

        Save();
        Report($"meeting transcription started: {Transcript.App} — {Transcript.Title}");

        _worker = Task.Run(WorkAsync, CancellationToken.None);
        _captures = Task.WhenAll(
            Task.Run(() => CaptureAsync(_microphone, _microphoneVad, remote: false, token), CancellationToken.None),
            Task.Run(() => CaptureAsync(_loopback, _loopbackVad, remote: true, token), CancellationToken.None),
            Task.Run(() => WatchActiveSpeakerAsync(token), CancellationToken.None));
    }

    /// <summary>Stops capturing, transcribes whatever is still queued, and saves.</summary>
    /// <remarks>
    /// The end is recorded and saved <i>first</i>, and neither wait below can last forever. A
    /// Teams call once ended and this never returned — some source ignored cancellation, most
    /// likely a UI Automation call into the closing Teams window — so the meeting stayed "live"
    /// and its duration kept counting for hours.
    /// </remarks>
    public async Task StopAsync()
    {
        if (_stop is null || _stop.IsCancellationRequested) return;

        lock (_gate) Transcript.EndedAt = _clock.Now;
        Save();

        await _stop.CancelAsync().ConfigureAwait(false);
        if (!await Within(_captures, _captureStopLimit).ConfigureAwait(false))
        {
            Report($"meeting capture did not stop within {_captureStopLimit.TotalSeconds:0} s; finishing without it");
        }

        _queue.Writer.TryComplete();
        if (!await Within(_worker, DrainLimit).ConfigureAwait(false))
        {
            Report($"queued speech not transcribed within {DrainLimit.TotalSeconds:0} s; saving what there is");
        }

        Save();
        Report($"meeting transcription stopped: {Transcript.Messages.Count} messages");
    }

    /// <summary>How long transcribing what was still queued may take.</summary>
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(60);

    /// <summary>Whether <paramref name="task"/> finished within <paramref name="limit"/>.</summary>
    private static async Task<bool> Within(Task task, TimeSpan limit)
    {
        if (await Task.WhenAny(task, Task.Delay(limit)).ConfigureAwait(false) != task) return false;
        await task.ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Renames a speaker everywhere in the transcript, and optionally remembers their voice so
    /// they are recognised in later meetings.
    /// </summary>
    public void Rename(int speakerId, string name, bool rememberVoice)
    {
        name = name.Trim();
        if (name.Length == 0) return;

        lock (_gate)
        {
            var speaker = Transcript.Speaker(speakerId);
            if (speaker is null) return;

            speaker.Name = name;
            speaker.IsNamed = true;

            if (rememberVoice && !speaker.IsLocal)
            {
                var cluster = _speakerByCluster.FirstOrDefault(p => p.Value == speakerId).Key;
                if (_speakerByCluster.ContainsKey(cluster))
                {
                    _voices.Remember(name, _clusterer.Centroid(cluster), _clusterer.Count(cluster));
                }
            }
        }

        Save();
        SpeakersChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task CaptureAsync(IAudioCapture capture, ISpeechSegmenter vad, bool remote, CancellationToken token)
    {
        try
        {
            await foreach (var chunk in capture.CaptureAsync(token).ConfigureAwait(false))
            {
                // Checked per chunk, not only while waiting: a source that never runs dry would
                // otherwise ignore the stop for ever.
                token.ThrowIfCancellationRequested();

                IReadOnlyList<SpeechSpan> spans;
                lock (_vadGate)
                {
                    if (_vadsDisposed) return;
                    spans = vad.Accept(chunk.Samples.Span);
                }

                foreach (var span in spans) Enqueue(span, remote);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        catch (Exception e)
        {
            Report($"{(remote ? "speaker" : "microphone")} capture failed: {e}");
        }

        IReadOnlyList<SpeechSpan> rest;
        lock (_vadGate)
        {
            if (_vadsDisposed) return;
            rest = vad.Flush();
        }

        foreach (var span in rest) Enqueue(span, remote);
    }

    /// <summary>
    /// Timestamps an utterance from the wall clock rather than its sample offset: loopback
    /// delivers nothing while nothing plays, so its sample count stops tracking time.
    /// </summary>
    private void Enqueue(SpeechSpan span, bool remote)
    {
        var end = _clock.Now - SilenceTail;
        var start = end - span.Duration;

        if (remote)
        {
            lock (_gate)
            {
                _remoteSpeech.Add((start, end));
                _remoteSpeech.RemoveAll(r => r.End < end - TimeSpan.FromMinutes(2));
            }
        }

        _queue.Writer.TryWrite(new Utterance(remote, span.Samples, start, end));
    }

    private async Task WorkAsync()
    {
        await foreach (var utterance in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await ProcessAsync(utterance).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Report($"meeting utterance failed: {e}");
            }
        }
    }

    private async Task ProcessAsync(Utterance utterance)
    {
        int speakerId;

        if (utterance.Remote)
        {
            var (cluster, knownName) = _clusterer.Assign(_embedder.Embed(utterance.Samples));
            speakerId = SpeakerFor(cluster, knownName);
            lock (_gate)
            {
                if (Transcript.Speaker(speakerId) is { } speaker)
                {
                    speaker.Voice = _clusterer.Centroid(cluster);
                    speaker.VoiceCount = _clusterer.Count(cluster);
                }
            }

            NameFromMeetingApp(speakerId, utterance);
        }
        else
        {
            // Give the loopback side time to report the same audio before deciding.
            var settle = utterance.End + SilenceTail + EchoSettle - _clock.Now;
            if (settle > TimeSpan.Zero) await Task.Delay(settle).ConfigureAwait(false);

            if (IsEcho(utterance))
            {
                Report("dropped a microphone utterance that echoed the speakers");
                return;
            }

            speakerId = LocalSpeaker;
        }

        if (!_transcriber.IsReady) await _transcriber.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        var text = (await _transcriber.TranscribeAsync(utterance.Samples, [], CancellationToken.None).ConfigureAwait(false)).Trim();
        if (text.Length == 0) return;

        var message = new MeetingMessage { SpeakerId = speakerId, At = utterance.Start, Text = text };
        lock (_gate) Transcript.Messages.Add(message);

        Save();
        MessageAdded?.Invoke(this, message);
    }

    private bool IsEcho(Utterance local)
    {
        var length = (local.End - local.Start).TotalMilliseconds;
        if (length <= 0) return false;

        lock (_gate)
        {
            var overlap = _remoteSpeech
                .Select(r => Math.Max(0, (Min(local.End, r.End) - Max(local.Start, r.Start)).TotalMilliseconds))
                .Sum();
            return overlap / length > 0.5;
        }
    }

    private int SpeakerFor(int cluster, string? knownName)
    {
        bool added;
        int id;

        lock (_gate)
        {
            added = !_speakerByCluster.TryGetValue(cluster, out id);
            if (added)
            {
                id = Transcript.Speakers.Max(s => s.Id) + 1;
                var participants = Transcript.Speakers.Count(s => !s.IsLocal) + 1;
                Transcript.Speakers.Add(new MeetingSpeaker
                {
                    Id = id,
                    Name = knownName ?? $"Participante {participants}",
                    IsNamed = knownName is not null,
                });
                _speakerByCluster[cluster] = id;
            }
        }

        if (added) SpeakersChanged?.Invoke(this, EventArgs.Empty);
        return id;
    }

    /// <summary>
    /// Names an anonymous participant after whoever the meeting app showed as speaking for
    /// most of the utterance. Names the user gave are never overwritten.
    /// </summary>
    private void NameFromMeetingApp(int speakerId, Utterance utterance)
    {
        string? name;
        lock (_gate)
        {
            var speaker = Transcript.Speaker(speakerId);
            if (speaker is null || speaker.IsNamed) return;

            name = _hints
                .Where(h => h.At >= utterance.Start && h.At <= utterance.End)
                .GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();

            if (name is null || Transcript.Speakers.Any(s => s.IsNamed && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            speaker.Name = name;
            speaker.IsNamed = true;
        }

        Report($"named a participant from the meeting app: {name}");
        SpeakersChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task WatchActiveSpeakerAsync(CancellationToken token)
    {
        if (_activeSpeaker is null) return;

        try
        {
            while (!token.IsCancellationRequested)
            {
                string? name = null;
                try
                {
                    name = _activeSpeaker.CurrentSpeaker(_processName);
                }
                catch (Exception e)
                {
                    // Best effort by design: the meeting app's UI is not ours.
                    Report($"active speaker lookup failed: {e.Message}");
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    lock (_gate)
                    {
                        _hints.Add((_clock.Now, name.Trim()));
                        _hints.RemoveAll(h => h.At < _clock.Now - TimeSpan.FromMinutes(2));
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(300), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    private void Save()
    {
        try
        {
            lock (_gate) _store.Save(Transcript);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Report($"saving the meeting failed: {e.Message}");
        }
    }

    private void Report(string message) => Diagnostic?.Invoke(this, message);

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop?.Dispose();

        // A capture that missed the stop deadline may still be inside a VAD call; disposing the
        // native detector under it crashed the app (access violation in sherpa-onnx) when a
        // Teams call ended. The gate waits for that call, and the flag stops any later one.
        lock (_vadGate)
        {
            _vadsDisposed = true;
            _microphoneVad.Dispose();
            _loopbackVad.Dispose();
        }

        await _microphone.DisposeAsync().ConfigureAwait(false);
        await _loopback.DisposeAsync().ConfigureAwait(false);
    }

    private readonly record struct Utterance(bool Remote, float[] Samples, DateTimeOffset Start, DateTimeOffset End);
}
