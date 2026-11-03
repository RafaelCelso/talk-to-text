using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TalkToText.App.Views;

namespace TalkToText.App;

/// <summary>The application.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The application's lifetime ends in ShutdownRequested, which disposes them.")]
public partial class App : Application
{
    private Composition? _composition;
    private MeetingCoordinator? _meetings;
    private MainWindow? _main;
    private MeetingPromptWindow? _prompt;
    private RecordingOverlay? _overlay;
    private PlayerPillWindow? _playerPill;
    private LiveTranscriptWindow? _liveTranscript;

    /// <summary>
    /// Asks whether to transcribe a detected meeting. Accepting downloads the meeting models
    /// first if needed, starts the transcript and opens the Meetings page on it.
    /// </summary>
    private void PromptForMeeting(Core.DetectedMeeting meeting)
    {
        if (_meetings is null || _prompt is not null) return;

        var prompt = new MeetingPromptWindow(meeting, needsDownload: !Speech.MeetingModels.IsInstalled());
        prompt.Closed += (_, _) => _prompt = null;
        prompt.Accepted = async window =>
        {
            if (!Speech.MeetingModels.IsInstalled())
            {
                try
                {
                    var progress = new Progress<double>(p => window.SetStatus($"Baixando modelos… {p:P0}"));
                    await MeetingCoordinator.DownloadModelsAsync(progress, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
                {
                    DiagnosticLog.Write($"meeting model download failed: {e.Message}");
                    window.SetStatus("Não foi possível baixar os modelos. Verifique a internet.");
                    window.AllowRetry();
                    return;
                }
            }

            if (_meetings.StartTranscription(meeting))
            {
                // Not brought to the front: that would pull the user out of their meeting.
                // The chat is waiting on the Meetings page whenever they open the window.
                window.Close();
                _main?.ShowPage(Page.Meetings);
            }
            else
            {
                window.SetStatus("Não foi possível iniciar a transcrição.");
                window.AllowRetry();
            }
        };

        _prompt = prompt;
        prompt.ShowInCorner();
    }

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _composition = Composition.Create();

            // Before anything loads llama.cpp: its build — graphics card or CPU — is picked once.
            var useGpu = _composition.Settings.Data.UseGpuForAi;
            Speech.SummaryModel.ConfigureBackend(useGpu);
            _ = Task.Run(() => DiagnosticLog.Write(
                $"local AI: {(Speech.SummaryModel.UsesGpu ? "graphics card (Vulkan)" : "CPU")}{(useGpu ? string.Empty : ", graphics card turned off in Settings")}"));
            _meetings = new MeetingCoordinator(_composition);
            _main = new MainWindow(_composition, _meetings);
            desktop.MainWindow = _main;
            _overlay = StartOverlay(_composition);
            StartPlayerPill(_main);
            StartLiveTranscript(_main, _composition, _meetings);
            OfferOnboarding(_main, _composition);

            // No speech model at all — the installer's download failed or was skipped — and the
            // app cannot transcribe: fetch it now, with progress on the Home page, rather than
            // leave the user to find a way. An English-only v2 still works, so v3 is offered there
            // instead of forced.
            if (_composition.IsPlatformAvailable && !_composition.IsModelInstalled)
            {
                _ = SpeechModelDownload.StartAsync(_composition);
            }

            _meetings.MeetingDetected += (_, meeting) => PromptForMeeting(meeting);
            _meetings.Start();

            // A second launch of the app lands here, as a request to show this window.
            Program.Instance?.Listen(() => Avalonia.Threading.Dispatcher.UIThread.Post(ShowMain));

            // Closing the window leaves the app running in the tray — the shortcut still works,
            // which is the whole point of a dictation app. Quit is explicit, from the tray
            // menu or the app menu.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Disposing tears down the keyboard hook and releases the audio device. Leaving
            // a low-level hook installed after exit is the kind of thing that makes a
            // machine feel broken until it is rebooted.
            desktop.ShutdownRequested += (_, _) =>
            {
                // Meetings first: a live transcript is stopped and saved before the shared
                // recognizer it uses is disposed with the engine.
                _main?.Playback.Dispose();
                _meetings?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _meetings = null;
                _composition?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _composition = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The first time the app opens after installing, the setup comes up over the main window.
    /// Finished, skipped or closed, it is marked done and never shows again by itself.
    /// </summary>
    private static void OfferOnboarding(MainWindow main, Composition composition)
    {
        if (composition.Settings.Data.OnboardingDone) return;

        void Show(object? sender, EventArgs e)
        {
            main.Opened -= Show;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = new OnboardingWindow(composition).ShowDialog(main));
        }

        main.Opened += Show;
    }

    /// <summary>
    /// Drives the recording pill from the engine, at frame rate, for as long as the app runs —
    /// it has to work when the main window is closed to the tray.
    /// </summary>
    private static RecordingOverlay? StartOverlay(Composition composition)
    {
        if (composition.Engine is not { } engine) return null;

        var overlay = new RecordingOverlay();
        engine.Completed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(overlay.NotifyCompleted);

        var frame = new Avalonia.Threading.DispatcherTimer(Avalonia.Threading.DispatcherPriority.Render)
        {
            Interval = Design.Tokens.Motion.Frame,
        };
        frame.Tick += (_, _) =>
        {
            // Turning the pill off mid-recording lets it finish what it was showing.
            if (!composition.Settings.Data.ShowOverlay && overlay.Phase == OverlayPhase.Hidden) return;
            overlay.Apply(engine.State, engine.Level, engine.RecordingStartedAt, ActivityText.Describe(engine.Activity));
        };
        frame.Start();
        return overlay;
    }

    /// <summary>
    /// While a meeting is being transcribed and the window is minimised or closed to the tray,
    /// the live chat floats in the bottom-right corner — unless turned off in Settings.
    /// </summary>
    private void StartLiveTranscript(MainWindow main, Composition composition, MeetingCoordinator meetings)
    {
        var window = new LiveTranscriptWindow(composition.Meetings);
        window.OpenRequested += (_, _) =>
        {
            ShowMain();
            main.ShowPage(Page.Meetings);
        };
        _liveTranscript = window;

        var poll = new Avalonia.Threading.DispatcherTimer(Avalonia.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        poll.Tick += (_, _) =>
        {
            var away = !main.IsVisible || main.WindowState == WindowState.Minimized;
            if (composition.Settings.Data.ShowLiveTranscriptWindow && meetings.Session is { } session && away)
            {
                window.ShowSession(session);
                window.ShowInCorner();
            }
            else if (window.IsVisible)
            {
                window.Hide();
            }
        };
        poll.Start();
    }

    /// <summary>
    /// While audio plays and the window is minimised or closed to the tray, its controls sit at
    /// the bottom centre of the screen, where the recording pill appears — above it when both
    /// are up. Polled: the window's state, the playback and the recording pill all change it.
    /// </summary>
    private void StartPlayerPill(MainWindow main)
    {
        var pill = new PlayerPillWindow(main.Playback);
        pill.Player.OpenRequested += (_, id) =>
        {
            ShowMain();
            main.OpenAudioTranscript(id);
        };
        _playerPill = pill;

        var poll = new Avalonia.Threading.DispatcherTimer(Avalonia.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        poll.Tick += (_, _) =>
        {
            var away = !main.IsVisible || main.WindowState == WindowState.Minimized;
            if (main.Playback.IsActive && away)
            {
                pill.ShowAtBottom(recordingPillVisible: _overlay is { Phase: not OverlayPhase.Hidden });
            }
            else if (pill.IsVisible)
            {
                pill.Hide();
            }
        };
        poll.Start();
    }

    /// <summary>A single click on the tray icon opens the window, or brings it forward.</summary>
    private void OnTrayClicked(object? sender, EventArgs e) => ShowMain();

    private void OnTrayShow(object? sender, EventArgs e) => ShowMain();

    private void OnTraySettings(object? sender, EventArgs e)
    {
        ShowMain();
        _main?.ShowPage(Page.Settings);
    }

    private void OnTrayQuit(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    /// <summary>
    /// Shows the window from the tray, restores it if minimised, and brings it in front of
    /// whatever app is active — not merely activating it inside this app.
    /// </summary>
    private void ShowMain()
    {
        if (_main is null) return;

        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();

        if (_main.TryGetPlatformHandle() is { } handle) PlatformFactory.BringToFront(handle.Handle);
    }
}
