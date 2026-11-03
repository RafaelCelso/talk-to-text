using TalkToText.Abstractions;
using TalkToText.Core;
using TalkToText.Dictionary;
using TalkToText.Speech;

namespace TalkToText.App;

/// <summary>
/// Wires the app together: settings, storage, the platform layer, and the engine.
/// </summary>
/// <remarks>
/// <para>
/// The only place that knows about every layer at once. Views take what they need; nothing
/// else reaches across.
/// </para>
/// <para>
/// The platform implementations are resolved by reflection rather than referenced directly,
/// so <c>TalkToText.App</c> can target plain <c>net10.0</c> and therefore be built, run and
/// headless-tested on macOS — which is the entire reason Avalonia was chosen over WPF. On any
/// non-Windows machine the lookup simply finds nothing and the app runs with inert stand-ins,
/// which is exactly what a UI test wants.
/// </para>
/// </remarks>
public sealed class Composition : IAsyncDisposable
{
    private Composition(
        AppSettings settings,
        DictionaryFile dictionary,
        TranscriptStore transcripts,
        DictationEngine? engine,
        IHotkeySource? hotkey,
        ITranscriber? transcriber,
        LocalAiRewriter? rewriter,
        bool platformAvailable)
    {
        _rewriter = rewriter;
        Settings = settings;
        Dictionary = dictionary;
        Transcripts = transcripts;
        Engine = engine;
        Hotkey = hotkey;
        Transcriber = transcriber;
        IsPlatformAvailable = platformAvailable;
    }

    private readonly LocalAiRewriter? _rewriter;

    /// <summary>The local AI, kept loaded between uses; shared by dictation and the Chat. Null off Windows.</summary>
    internal LocalAiRewriter? LocalAi => _rewriter;

    /// <summary>
    /// The speech recognizer, shared by dictation and meeting transcription — one copy of the
    /// ~2 GB model in memory, not two. Null when no platform layer is available.
    /// </summary>
    public ITranscriber? Transcriber { get; }

    /// <summary>Saved meeting transcripts.</summary>
    public MeetingStore Meetings { get; } = new(MeetingStore.DefaultDirectory);

    /// <summary>Transcribed audio files.</summary>
    public MeetingStore AudioFiles { get; } = new(MeetingStore.AudioFilesDirectory);

    /// <summary>Conversations in the Chat page.</summary>
    public ChatStore Chats { get; } = new(ChatStore.DefaultDirectory);

    /// <summary>Voices the user has named, recognised in later meetings.</summary>
    public KnownVoices Voices { get; } = new(KnownVoices.DefaultPath);

    /// <summary>User preferences.</summary>
    public AppSettings Settings { get; }

    /// <summary>The correction dictionary.</summary>
    public DictionaryFile Dictionary { get; }

    /// <summary>Transcript history.</summary>
    public TranscriptStore Transcripts { get; }

    /// <summary>The dictation engine, or null when no platform layer is available.</summary>
    public DictationEngine? Engine { get; }

    /// <summary>
    /// The system-wide keyboard, for recording a new shortcut in Settings. Null off Windows.
    /// </summary>
    public IHotkeySource? Hotkey { get; }

    /// <summary>Whether real audio and hotkey support were found.</summary>
    public bool IsPlatformAvailable { get; }

    /// <summary>Whether the speech model the engine uses is present on disk.</summary>
    /// <remarks>Resolved exactly as <see cref="Create"/> resolves it, settings first.</remarks>
    public bool IsModelInstalled =>
        (Settings.Data.ModelDirectory ?? ParakeetTranscriber.Locate()) is { } directory
        && ParakeetTranscriber.IsComplete(directory);

    /// <summary>Builds the object graph.</summary>
    public static Composition Create()
    {
        var settings = new AppSettings(AppSettings.DefaultPath);
        var dictionary = new DictionaryFile(DictionaryFile.DefaultPath);
        var transcripts = new TranscriptStore(TranscriptStore.DefaultPath);

        var capture = PlatformFactory.CreateAudioCapture();
        var hotkey = PlatformFactory.CreateHotkeySource();
        var injector = PlatformFactory.CreateTextInjector();

        DictationEngine? engine = null;
        ITranscriber? transcriber = null;
        LocalAiRewriter? rewriter = null;
        var available = capture is not null && hotkey is not null && injector is not null;

        if (available)
        {
            var modelDirectory = settings.Data.ModelDirectory ?? ParakeetTranscriber.Locate();

            DiagnosticLog.Write($"started; model: {modelDirectory ?? "none found"}");

            transcriber = modelDirectory is not null
                ? new ParakeetTranscriber(modelDirectory)
                : new UnavailableTranscriber();

            engine = new DictationEngine(
                capture!, hotkey!, transcriber, injector!,
                () => dictionary.Entries);

            rewriter = new LocalAiRewriter();
            engine.Rewriter = rewriter;
            engine.SelectionReader = PlatformFactory.CreateSelectionReader();

            // Queued, not written inline: some messages are raised on the keyboard hook's thread,
            // which must never wait on disk I/O.
            engine.Diagnostic += (_, message) =>
            {
                var at = DateTimeOffset.Now;
                ThreadPool.QueueUserWorkItem(_ => DiagnosticLog.Write(message, at));
            };

            // Applied live: changing a setting must not need a restart. The shortcut is only
            // reassigned when it actually changed, because assigning it resets the detector
            // and would drop a chord the user is holding at that moment.
            void ApplySettings()
            {
                if (engine.Shortcut != settings.Data.Shortcut) engine.Shortcut = settings.Data.Shortcut;
                engine.Mode = settings.Data.Mode;
                engine.InjectText = settings.Data.InjectText;
                engine.RemoveHesitations = settings.Data.RemoveHesitations;
                engine.PolishWithAi = settings.Data.PolishWithAi;

                // The same keys for both would make every dictation a command.
                var command = settings.Data.CommandMode && settings.Data.CommandShortcut != settings.Data.Shortcut
                    ? settings.Data.CommandShortcut
                    : (Shortcut?)null;
                if (engine.CommandShortcut != command) engine.CommandShortcut = command;
            }

            ApplySettings();
            settings.Changed += (_, _) => ApplySettings();

            engine.Completed += (_, result) =>
            {
                if (!settings.Data.KeepHistory) return;

                transcripts.Add(new TranscriptRecord
                {
                    At = result.At,
                    AudioSeconds = result.AudioDuration.TotalSeconds,
                    ProcessingSeconds = result.ProcessingTime.TotalSeconds,
                    Text = result.Text,
                    Corrections = result.Corrections.Count > 0 ? result.Corrections : null,
                });
            };
        }

        if (!available) DiagnosticLog.Write("started without the Windows platform layer; dictation is off");

        return new Composition(settings, dictionary, transcripts, engine, hotkey, transcriber, rewriter, available);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Engine is not null) await Engine.DisposeAsync().ConfigureAwait(false);
        _rewriter?.Dispose();
    }
}

/// <summary>
/// Appends to <c>%LOCALAPPDATA%\TalkToText\talktotext.log</c>.
/// </summary>
/// <remarks>
/// The only window into a dictation that went wrong on a machine nobody can attach a
/// debugger to. Logging must never itself break dictation, so I/O failures are ignored.
/// </remarks>
internal static class DiagnosticLog
{
    private static readonly Lock Gate = new();

    /// <summary>Where the log lives.</summary>
    public static string Path { get; } = System.IO.Path.Combine(DataFolder.Path, "talktotext.log");

    /// <summary>Appends one timestamped line.</summary>
    public static void Write(string message) => Write(message, DateTimeOffset.Now);

    /// <summary>Appends one line stamped with <paramref name="at"/>, when it happened.</summary>
    public static void Write(string message, DateTimeOffset at)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, $"{at:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Never let the log take dictation down with it.
        }
    }
}

/// <summary>
/// A transcriber that reports the model is missing rather than throwing.
/// </summary>
/// <remarks>
/// A fresh Windows install has no model, and that has to be a readable message in Settings
/// rather than a crash on first press.
/// </remarks>
internal sealed class UnavailableTranscriber : ITranscriber
{
    public bool IsReady => false;

    public ValueTask<bool> LoadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);

    public ValueTask<string> TranscribeAsync(
        ReadOnlyMemory<float> samples,
        IReadOnlyList<string> biasPhrases,
        CancellationToken cancellationToken) => ValueTask.FromResult(string.Empty);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
