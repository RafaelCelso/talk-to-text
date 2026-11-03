using System.Buffers;
using TalkToText.Abstractions;
using TalkToText.Dictionary;

namespace TalkToText.Core;

/// <summary>What the engine is doing right now.</summary>
public enum DictationState
{
    /// <summary>Waiting for the hotkey.</summary>
    Idle,

    /// <summary>The key is held; audio is being captured.</summary>
    Recording,

    /// <summary>The key is released; the utterance is being transcribed.</summary>
    Transcribing,
}

/// <summary>One completed dictation.</summary>
public sealed record DictationResult(
    DateTimeOffset At,
    TimeSpan AudioDuration,
    TimeSpan ProcessingTime,
    string Text,
    IReadOnlyList<AppliedCorrection> Corrections);

/// <summary>
/// The whole dictation flow: hotkey down, capture, hotkey up, transcribe, correct, inject.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately platform-neutral. It targets plain <c>net10.0</c>, so <c>CA1416</c> turns any
/// accidental Windows API call in here into a build error. Everything platform-specific
/// arrives through the four interfaces it is constructed with.
/// </para>
/// <para>
/// That is what makes the interesting behaviour testable without Windows: hand it fakes and
/// the entire path — including chunking, the correction pass and the "nothing was said"
/// case — runs on any machine, in milliseconds.
/// </para>
/// </remarks>
public sealed class DictationEngine : IAsyncDisposable
{
    private readonly IAudioCapture _capture;
    private readonly IHotkeySource _hotkey;
    private readonly ITranscriber _transcriber;
    private readonly ITextInjector _injector;
    private readonly IClock _clock;
    private readonly Func<IReadOnlyList<DictionaryEntry>> _dictionary;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ShortcutDetector _detector = new();
    private readonly ShortcutDetector _commandDetector = new();
    private bool _commandEnabled;
    private bool _isCommand;
    private Task<bool>? _loading;
    private CancellationTokenSource? _recording;
    private List<float>? _buffer;
    private DateTimeOffset _startedAt;
    private bool _startedByThisPress;

    /// <summary>Current state.</summary>
    public DictationState State { get; private set; } = DictationState.Idle;

    /// <summary>The keys that start dictation.</summary>
    public Shortcut Shortcut
    {
        get => _detector.Shortcut;
        set
        {
            _detector.Shortcut = value;
            _detector.Reset();
        }
    }

    /// <summary>
    /// The keys for a spoken command — "deixa mais formal" — carried out by <see cref="Rewriter"/>
    /// on the selected text. Null turns command mode off.
    /// </summary>
    public Shortcut? CommandShortcut
    {
        get => _commandEnabled ? _commandDetector.Shortcut : null;
        set
        {
            _commandEnabled = value is not null;
            if (value is { } shortcut) _commandDetector.Shortcut = shortcut;
            _commandDetector.Reset();
        }
    }

    /// <summary>Whether "éé", "hã", "hum" and stuttered words are taken out before typing.</summary>
    public bool RemoveHesitations { get; set; } = true;

    /// <summary>Whether <see cref="Rewriter"/> tidies every dictation before it is typed. Slower.</summary>
    public bool PolishWithAi { get; set; }

    /// <summary>The local AI, for <see cref="PolishWithAi"/> and command mode; null when not set up.</summary>
    public ITextRewriter? Rewriter { get; set; }

    /// <summary>Reads the selection a command applies to; null types the command's result with nothing selected.</summary>
    public ISelectionReader? SelectionReader { get; set; }

    /// <summary>How long the AI may take before the dictation is typed as it was.</summary>
    public TimeSpan PolishLimit { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>How long the AI may take over a command.</summary>
    public TimeSpan CommandLimit { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Hold to talk, or press to start and press again to stop.</summary>
    public ActivationMode Mode { get; set; } = ActivationMode.Hold;

    /// <summary>
    /// Whether finished text is typed into the focused app. When off, it only reaches the
    /// history.
    /// </summary>
    public bool InjectText { get; set; } = true;

    /// <summary>
    /// Ignore the keyboard — set while the user records a new shortcut, so pressing the
    /// current one to replace it does not also start a dictation.
    /// </summary>
    public bool IsSuspended
    {
        get;
        set
        {
            field = value;
            _detector.Reset();
            _commandDetector.Reset();
        }
    }

    /// <summary>When the current recording started. Drives the on-screen timer.</summary>
    public DateTimeOffset RecordingStartedAt => _startedAt;

    /// <summary>Most recent input level, 0…1. Drives the meter.</summary>
    public float Level { get; private set; }

    /// <summary>Raised when a dictation completes and produced text.</summary>
    public event EventHandler<DictationResult>? Completed;

    /// <summary>Raised whenever <see cref="State"/> or <see cref="Level"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>One line per dictation step, and every failure with its stack trace.</summary>
    /// <remarks>
    /// The hotkey starts recording and transcription without anyone awaiting them, so an
    /// exception there has nowhere to go but here. Without it, a failure looks exactly like
    /// "nothing was said".
    /// </remarks>
    public event EventHandler<string>? Diagnostic;

    private void Report(string message) => Diagnostic?.Invoke(this, message);

    /// <summary>Wires the engine to its platform implementations.</summary>
    /// <param name="capture">Microphone source.</param>
    /// <param name="hotkey">Push-to-talk source.</param>
    /// <param name="transcriber">Speech engine.</param>
    /// <param name="injector">Where finished text goes.</param>
    /// <param name="dictionary">
    /// Read fresh on every utterance rather than captured once, so edits take effect without
    /// a restart.
    /// </param>
    /// <param name="clock">Time source; defaults to the system clock.</param>
    public DictationEngine(
        IAudioCapture capture,
        IHotkeySource hotkey,
        ITranscriber transcriber,
        ITextInjector injector,
        Func<IReadOnlyList<DictionaryEntry>> dictionary,
        IClock? clock = null)
    {
        _capture = capture;
        _hotkey = hotkey;
        _transcriber = transcriber;
        _injector = injector;
        _dictionary = dictionary;
        _clock = clock ?? SystemClock.Instance;

        _hotkey.KeyChanged += OnKeyChanged;
        _detector.Activated += OnActivated;
        _detector.Deactivated += OnDeactivated;
        _detector.Interrupted += OnInterrupted;
        _commandDetector.Activated += OnCommandActivated;
        _commandDetector.Deactivated += OnCommandDeactivated;
        _commandDetector.Interrupted += OnInterrupted;
    }

    /// <summary>Arms the hotkey and starts loading the speech model in the background.</summary>
    /// <returns>False if the hook could not be installed.</returns>
    /// <remarks>
    /// Loading starts here rather than on first press because Parakeet takes seconds to load,
    /// and that wait would otherwise land on the user's first dictation.
    /// </remarks>
    public bool Start()
    {
        _ = EnsureLoadedAsync();
        return _hotkey.Start();
    }

    /// <summary>Loads the transcriber once, however many callers ask.</summary>
    /// <remarks>
    /// A transcriber that was never loaded returns empty text rather than failing, which the
    /// engine reads as "nothing was said" — so a missing load is silent. Every transcription
    /// awaits this. <c>Task.Run</c> because loading is synchronous and CPU-heavy, and
    /// <see cref="Start"/> is called from the UI thread.
    /// </remarks>
    private Task<bool> EnsureLoadedAsync()
    {
        if (_loading is { } existing) return existing;

        var loading = Task.Run(async () =>
            await _transcriber.LoadAsync(CancellationToken.None).ConfigureAwait(false));
        return Interlocked.CompareExchange(ref _loading, loading, null) ?? loading;
    }

    /// <summary>
    /// Starts or stops recording from a button rather than the hotkey.
    /// </summary>
    /// <remarks>
    /// Routed through the same state machine as the hotkey, deliberately. Two independent
    /// paths into recording would eventually disagree about whether it is running.
    /// </remarks>
    public void TogglePushToTalk()
    {
        if (State == DictationState.Idle) Begin(command: false);
        else if (State == DictationState.Recording) Background(EndAsync, "transcription");
    }

    private void OnKeyChanged(object? sender, KeyTransition transition)
    {
        if (IsSuspended) return;
        _detector.Handle(transition);
        if (_commandEnabled) _commandDetector.Handle(transition);
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        // While the keys are still down: masking after release would be too late to stop a
        // lone Windows key opening the Start menu.
        if (Shortcut.NeedsReleaseMask) _hotkey.MaskModifierRelease();

        _startedByThisPress = State == DictationState.Idle;
        Report($"shortcut {Shortcut} pressed ({Mode}, {State})");

        // A command being recorded is not turned into a dictation half-way.
        if (_isCommand && State != DictationState.Idle) return;

        if (Mode == ActivationMode.Hold) Begin(command: false);
        else TogglePushToTalk();
    }

    /// <summary>The command shortcut: the same as dictation, but what is said is an instruction.</summary>
    private void OnCommandActivated(object? sender, EventArgs e)
    {
        if (CommandShortcut is { NeedsReleaseMask: true }) _hotkey.MaskModifierRelease();
        if (State != DictationState.Idle && !_isCommand) return;

        _startedByThisPress = State == DictationState.Idle;
        Report($"command shortcut {CommandShortcut} pressed ({Mode}, {State})");

        if (State == DictationState.Idle) Begin(command: true);
        else if (Mode == ActivationMode.Toggle && State == DictationState.Recording) Background(EndAsync, "command");
    }

    private void OnCommandDeactivated(object? sender, EventArgs e)
    {
        _startedByThisPress = false;
        if (Mode == ActivationMode.Hold && _isCommand) Background(EndAsync, "command");
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        _startedByThisPress = false;
        Report($"shortcut {Shortcut} released ({Mode}, {State})");
        if (Mode == ActivationMode.Hold && !_isCommand) Background(EndAsync, "transcription");
    }

    /// <summary>
    /// Another key joined the shortcut — Ctrl + Win + D, say. The user wanted a different
    /// Windows shortcut, so the recording this press started is thrown away, not typed.
    /// </summary>
    private void OnInterrupted(object? sender, EventArgs e)
    {
        Report($"shortcut {Shortcut} interrupted by another key ({State})");
        if (!_startedByThisPress) return;
        _startedByThisPress = false;
        Background(CancelAsync, "cancel");
    }

    private async Task CancelAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != DictationState.Recording) return;

            await _recording!.CancelAsync().ConfigureAwait(false);
            _recording.Dispose();
            _recording = null;
            _buffer = null;
            Level = 0;
            SetState(DictationState.Idle);
            Report("cancelled: another key joined the shortcut");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="step"/> on the thread pool, never on the caller's thread.
    /// </summary>
    /// <remarks>
    /// <b>The keyboard hook calls in on its own thread, and Windows silently removes a hook
    /// whose callback takes longer than about a second.</b> Opening the microphone takes
    /// hundreds of milliseconds and transcription longer, and both once ran inside that
    /// callback: on real hardware the release of the shortcut was then never seen and the app
    /// recorded forever. Only cheap bookkeeping may happen on the hook thread.
    /// </remarks>
    private void Background(Func<Task> step, string name) => _ = Task.Run(() => Guarded(step, name));

    private async Task Guarded(Func<Task> step, string name)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report($"{name} failed: {ex}");
        }
    }

    /// <summary>
    /// Moves to Recording on the caller's thread — cheap, so a release that follows at once
    /// is never processed before it — and opens the microphone in the background.
    /// </summary>
    private void Begin(bool command)
    {
        CancellationToken token;

        if (!_gate.Wait(TimeSpan.FromMilliseconds(250))) return;
        try
        {
            if (State != DictationState.Idle) return;

            _buffer = [];
            _isCommand = command;
            _startedAt = _clock.Now;
            _recording = new CancellationTokenSource();
            token = _recording.Token;
            SetState(DictationState.Recording);
        }
        finally
        {
            _gate.Release();
        }

        Background(() => CaptureAsync(token), "recording");
    }

    private async Task CaptureAsync(CancellationToken token)
    {
        try
        {
            await foreach (var chunk in _capture.CaptureAsync(token).ConfigureAwait(false))
            {
                // Stop consuming the moment recording ends. Cancellation is cooperative, so
                // chunks already queued still arrive after EndAsync has moved on — and
                // without this guard one of them sets Level back to a reading that has
                // already been zeroed.
                if (State != DictationState.Recording) break;

                // Copied, not referenced: capture implementations are entitled to reuse
                // their buffer the moment this returns.
                _buffer?.AddRange(chunk.Samples.Span);
                Level = chunk.Rms();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal: the key was released.
        }
        finally
        {
            // Authoritative: this runs only once the capture loop has genuinely finished, so
            // nothing can raise the level afterwards and leave the meter stuck.
            Level = 0;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task EndAsync()
    {
        List<float>? samples;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != DictationState.Recording) return;

            await _recording!.CancelAsync().ConfigureAwait(false);
            samples = _buffer;
            _buffer = null;
            Level = 0;
            SetState(DictationState.Transcribing);
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await ProcessAsync(samples).ConfigureAwait(false);
        }
        finally
        {
            _recording?.Dispose();
            _recording = null;
            SetState(DictationState.Idle);
        }
    }

    private async Task ProcessAsync(List<float>? samples)
    {
        Report($"captured {samples?.Count ?? 0} samples ({(samples?.Count ?? 0) / (double)AudioChunk.SampleRate:F1}s)");
        if (samples is null || samples.Count == 0) return;

        // Measured from key release, because that is the wait the user actually feels — and
        // it is the only figure on which a streaming and a batch engine compare honestly.
        var releasedAt = _clock.Now;
        var audio = new ReadOnlyMemory<float>(samples.ToArray());

        if (!await EnsureLoadedAsync().ConfigureAwait(false))
        {
            Report("speech model is not loaded; nothing transcribed");
            return;
        }

        var entries = _dictionary();
        var bias = DictionaryCorrector.BiasPhrases(entries);

        var pieces = AudioSegmenter.Split(audio);
        var transcripts = new List<string>(pieces.Count);

        foreach (var piece in pieces)
        {
            var text = await _transcriber
                .TranscribeAsync(piece, bias, CancellationToken.None)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(text)) transcripts.Add(text.Trim());
        }

        var raw = string.Join(' ', transcripts);
        Report($"transcribed {pieces.Count} segment(s): \"{raw}\"");
        if (string.IsNullOrWhiteSpace(raw)) return;

        if (_isCommand)
        {
            await CommandAsync(new DictionaryCorrector(entries).Apply(raw).Text, releasedAt, audio.Length).ConfigureAwait(false);
            return;
        }

        if (RemoveHesitations) raw = SpeechCleanup.Clean(raw);

        // The dictionary runs last and unconditionally. Biasing only raises the odds of the
        // right word; this is the pass that guarantees it.
        var (corrected, applied) = new DictionaryCorrector(entries).Apply(raw);

        if (PolishWithAi && Rewriter is not null)
        {
            // Never at the cost of the dictation: too slow or failing, it is typed as it was.
            using var limit = new CancellationTokenSource(PolishLimit);
            try
            {
                var polished = (await Rewriter.PolishAsync(corrected, limit.Token).ConfigureAwait(false)).Trim();
                if (polished.Length > 0)
                {
                    // Again: the AI may have "corrected" a name back to how it sounds.
                    (corrected, applied) = new DictionaryCorrector(entries).Apply(polished);
                    Report($"polished by the AI: \"{corrected}\"");
                }
            }
            catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
            {
                Report($"AI polish skipped: {e.Message}");
            }
        }

        var result = new DictationResult(
            At: releasedAt,
            AudioDuration: TimeSpan.FromSeconds((double)audio.Length / AudioChunk.SampleRate),
            ProcessingTime: _clock.Now - releasedAt,
            Text: corrected,
            Corrections: applied);

        // Typed first: the text is what the user is waiting for, and a failing Completed
        // subscriber (history, UI) must never be able to stop it from arriving.
        if (InjectText)
        {
            var typed = await _injector.InjectAsync(corrected, CancellationToken.None).ConfigureAwait(false);
            Report(typed ? "typed into the focused app" : "typing was refused by the system");
        }
        Completed?.Invoke(this, result);
    }

    /// <summary>
    /// Carries out a spoken instruction on the selected text and types the result over it —
    /// or, with nothing selected, types what the instruction asked for.
    /// </summary>
    private async Task CommandAsync(string instruction, DateTimeOffset releasedAt, int samples)
    {
        if (Rewriter is null)
        {
            Report("command mode needs the local AI, which is not set up");
            return;
        }

        var selection = SelectionReader is null
            ? null
            : await SelectionReader.CopySelectionAsync(CancellationToken.None).ConfigureAwait(false);
        Report($"command \"{instruction}\" on {selection?.Length ?? 0} selected characters");

        string result;
        using (var limit = new CancellationTokenSource(CommandLimit))
        {
            try
            {
                result = (await Rewriter.CommandAsync(instruction, selection, limit.Token).ConfigureAwait(false)).Trim();
            }
            catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
            {
                Report($"command failed: {e.Message}");
                return;
            }
        }

        if (result.Length == 0) return;

        var done = new DictationResult(
            At: releasedAt,
            AudioDuration: TimeSpan.FromSeconds((double)samples / AudioChunk.SampleRate),
            ProcessingTime: _clock.Now - releasedAt,
            Text: result,
            Corrections: []);

        if (InjectText)
        {
            var typed = await _injector.InjectAsync(result, CancellationToken.None).ConfigureAwait(false);
            Report(typed ? "command result typed into the focused app" : "typing was refused by the system");
        }

        Completed?.Invoke(this, done);
    }

    private void SetState(DictationState state)
    {
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _hotkey.KeyChanged -= OnKeyChanged;
        _hotkey.Dispose();

        if (_recording is not null)
        {
            await _recording.CancelAsync().ConfigureAwait(false);
            _recording.Dispose();
        }

        await _capture.DisposeAsync().ConfigureAwait(false);
        await _transcriber.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
