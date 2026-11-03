using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using TalkToText.App.Controls;
using TalkToText.App.Design;
using TalkToText.App.Views;
using TalkToText.Core;
using Shouldly;

[assembly: AvaloniaTestApplication(typeof(TalkToText.AppTests.TestAppBuilder))]

namespace TalkToText.AppTests;

/// <summary>Hosts the app headlessly so the UI can be exercised without a display.</summary>
public static class TestAppBuilder
{
    /// <summary>Builds a headless Avalonia app for the test host.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>A minimal application shell for headless tests.</summary>
public sealed class TestApp : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(PointerCursors.Create());
    }
}

/// <summary>
/// Real UI tests, running with no display.
/// </summary>
/// <remarks>
/// This is the payoff for choosing Avalonia over WPF: these run on any machine in
/// milliseconds, so a broken layout or a control that fails to construct is caught while
/// writing it rather than after shipping.
/// </remarks>
public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void Window_opens_on_home_and_lays_out()
    {
        var window = new MainWindow();
        window.Show();

        window.CurrentPage.ShouldBe(Page.Home);
        window.Bounds.Width.ShouldBeGreaterThan(0);
        window.Title.ShouldBe("Talk-to-Text");
    }

    [AvaloniaFact]
    public void Sidebar_switches_between_every_page()
    {
        var window = new MainWindow();
        window.Show();

        foreach (var page in Enum.GetValues<Page>())
        {
            window.ShowPage(page);
            window.CurrentPage.ShouldBe(page);
        }
    }

    [AvaloniaFact]
    public void Microphone_button_moves_home_between_ready_and_recording()
    {
        var window = new MainWindow();
        window.Show();

        window.Home.IsRecording.ShouldBeFalse();
        window.Home.StatusText.ShouldBe("Pronto para ditar");
        window.Home.Waveform.IsVisible.ShouldBeFalse();

        window.ToggleRecording();
        window.Home.IsRecording.ShouldBeTrue();
        window.Home.StatusText.ShouldBe("Gravando");
        window.Home.Waveform.IsVisible.ShouldBeTrue();

        window.ToggleRecording();
        window.Home.IsRecording.ShouldBeFalse();
        window.Home.Waveform.IsVisible.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void Closing_from_the_title_bar_hides_so_the_tray_can_show_it_again()
    {
        var window = new MainWindow();
        window.Show();

        // A user close, not a programmatic one: that is what the X button sends. The event
        // args' constructor is internal to Avalonia, hence reflection.
        const System.Reflection.BindingFlags Internal =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var args = (WindowClosingEventArgs)typeof(WindowClosingEventArgs)
            .GetConstructors(Internal)
            .Single(c => c.GetParameters().Length == 2)
            .Invoke([WindowCloseReason.WindowClosing, false]);
        window.GetType()
            .GetMethod("OnClosing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, [args]);

        args.Cancel.ShouldBeTrue();
        window.IsVisible.ShouldBeFalse();

        window.Show();
        window.IsVisible.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Window_honours_its_minimum_size()
    {
        var window = new MainWindow();
        window.MinWidth.ShouldBe(760);
        window.MinHeight.ShouldBe(540);
    }
}

/// <summary>The settings page.</summary>
public sealed class SettingsViewTests
{
    [AvaloniaFact]
    public void Builds_without_an_engine_and_does_not_start_recording_a_shortcut()
    {
        var view = new SettingsView(null);
        var window = new Window { Content = view };
        window.Show();

        view.IsRecordingShortcut.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void The_ai_instruction_field_is_on_the_page()
    {
        var view = new SettingsView(null);
        var window = new Window { Content = view };
        window.Show();

        view.AiInstructionField.ShouldNotBeNull().AcceptsReturn.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Key_caps_show_each_key_of_the_shortcut()
    {
        var caps = Ui.KeyCaps(Shortcut.Default);

        // "Ctrl", "+", "Win".
        caps.Children.Count.ShouldBe(3);
    }
}

/// <summary>The audio file page.</summary>
public sealed class AudioFilesViewTests
{
    [AvaloniaFact]
    public void Attaching_a_file_never_starts_transcribing_by_itself()
    {
        var view = new AudioFilesView(null, null);
        var window = new Window { Content = view };
        window.Show();

        var path = Path.Combine(Path.GetTempPath(), $"ttt-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(path, SilentWav(seconds: 1));
            view.Attach(path);
            view.IsTranscribing.ShouldBeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A 16 kHz mono 16-bit PCM WAV of silence.</summary>
    private static byte[] SilentWav(int seconds)
    {
        const int Rate = 16000;
        var data = Rate * 2 * seconds;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data); w.Write(new byte[data]);
        return stream.ToArray();
    }
}

/// <summary>The animation shown while the chat's model works.</summary>
public sealed class ThinkingIndicatorTests
{
    [AvaloniaFact]
    public void Shows_the_step_it_is_told()
    {
        var indicator = new TalkToText.App.Controls.ThinkingIndicator();
        var window = new Window { Content = indicator };
        window.Show();

        indicator.SetStep("Lendo 3 trechos de 1 reunião ou gravação…", reading: true);
        indicator.Step.ShouldBe("Lendo 3 trechos de 1 reunião ou gravação…");
    }
}

/// <summary>The Chat page.</summary>
public sealed class ChatPageTests
{
    [AvaloniaFact]
    public async Task Builds_with_nothing_behind_it_and_does_not_start_answering()
    {
        var page = new ChatPage(null, null);
        var window = new Window { Content = page };
        window.Show();

        page.ScopeCount.ShouldBe(1, "\"all\" is always there");
        page.ScopeAllLabel.ShouldBe("Todas as reuniões");

        page.SelectTab(recordings: true);
        page.ScopeAllLabel.ShouldBe("Todas as gravações");
        await page.AskAsync("O que foi decidido?");
        page.IsAnswering.ShouldBeFalse();
    }
}

/// <summary>The meeting chat floating over other apps while the window is minimised.</summary>
public sealed class LiveTranscriptWindowTests
{
    [AvaloniaFact]
    public void Collapses_to_the_icon_and_expands_back_to_the_chat()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ttt-live-{Guid.NewGuid():N}");
        try
        {
            var window = new LiveTranscriptWindow(new MeetingStore(directory));
            window.Show();
            window.IsCollapsed.ShouldBeFalse();

            Click(window, "Recolher");
            window.IsCollapsed.ShouldBeTrue();
            window.Width.ShouldBe(window.Height, "only the round icon is left");

            Click(window, "Mostrar a transcrição");
            window.IsCollapsed.ShouldBeFalse();
            window.Width.ShouldBeGreaterThan(300);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Is_on_unless_turned_off_in_settings()
    {
        new SettingsData().ShowLiveTranscriptWindow.ShouldBeTrue();
    }

    private static void Click(Control root, string name)
    {
        var button = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<Button>()
            .Single(b => Equals(ToolTip.GetTip(b), name));
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }
}

/// <summary>The transcript following the audio as it plays.</summary>
public sealed class PlaybackFollowTests
{
    [AvaloniaFact]
    public void The_message_being_heard_is_the_one_highlighted()
    {
        var start = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var transcript = new MeetingTranscript
        {
            Title = "ligacao",
            StartedAt = start,
            Speakers = [new MeetingSpeaker { Id = 1, Name = "Pessoa 1" }, new MeetingSpeaker { Id = 2, Name = "Pessoa 2" }],
            Messages =
            [
                new MeetingMessage { SpeakerId = 1, At = start.AddSeconds(1), Text = "Bom dia" },
                new MeetingMessage { SpeakerId = 2, At = start.AddSeconds(4), Text = "Oi" },
                new MeetingMessage { SpeakerId = 1, At = start.AddSeconds(9), Text = "Pode falar" },
            ],
        };

        var directory = Path.Combine(Path.GetTempPath(), $"ttt-follow-{Guid.NewGuid():N}");
        try
        {
            var view = new ChatView(null, transcript, new MeetingStore(directory));
            var window = new Window { Content = view };
            window.Show();

            view.FollowPlayback(TimeSpan.FromSeconds(5), playing: true);
            view.PlayingMessage.ShouldBe(transcript.Messages[1]);

            view.FollowPlayback(TimeSpan.FromSeconds(9.5), playing: true);
            view.PlayingMessage.ShouldBe(transcript.Messages[2]);

            // Back to the start after Stop: nothing is being said.
            view.FollowPlayback(TimeSpan.Zero, playing: false);
            view.PlayingMessage.ShouldBeNull();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

/// <summary>The audio player above a transcribed recording.</summary>
public sealed class AudioPlayerBarTests
{
    private static AudioPlayerBar Bar(PlaybackHub hub, string path = "gravacao.wav") =>
        new(hub, path, Path.GetFileNameWithoutExtension(path), Guid.NewGuid());

    [AvaloniaFact]
    public void Every_control_drives_the_player()
    {
        var player = new FakePlayer();
        var hub = new PlaybackHub(_ => player);
        var bar = Bar(hub);
        var window = new Window { Content = bar };
        window.Show();

        Press(bar, "Reproduzir");
        player.IsPlaying.ShouldBeTrue();

        Press(bar, "Pausar");
        player.IsPlaying.ShouldBeFalse();

        // Speeds live in the gear's menu; the gear itself is the only thing in the row.
        bar.SpeedItems.Count.ShouldBe(3);
        Click(bar.SpeedItems[1]);
        player.Speed.ShouldBe(1.5);
        Click(bar.SpeedItems[2]);
        player.Speed.ShouldBe(2);
        Click(bar.SpeedItems[0]);
        player.Speed.ShouldBe(1);

        bar.PlayFrom(TimeSpan.FromSeconds(42));
        player.Position.ShouldBe(TimeSpan.FromSeconds(42));
        player.IsPlaying.ShouldBeTrue();

        Press(bar, "Parar");
        player.IsPlaying.ShouldBeFalse();
        player.Position.ShouldBe(TimeSpan.Zero);

        player.Seek(TimeSpan.FromSeconds(30));
        Press(bar, "Recomeçar");
        player.Position.ShouldBe(TimeSpan.Zero);
        player.IsPlaying.ShouldBeTrue();

        Press(bar, "Sem som");
        player.Volume.ShouldBe(0);
        Press(bar, "Sem som");
        player.Volume.ShouldBe(1);
    }

    [AvaloniaFact]
    public void Leaving_the_page_keeps_the_audio_playing_for_the_floating_player()
    {
        var player = new FakePlayer();
        var hub = new PlaybackHub(_ => player);
        var bar = Bar(hub);
        var window = new Window { Content = bar };
        window.Show();

        Press(bar, "Reproduzir");
        hub.IsShownInline.ShouldBeTrue("its own bar is on screen: no floating player");

        window.Content = null;

        player.IsPlaying.ShouldBeTrue();
        player.Disposed.ShouldBeFalse();
        hub.IsActive.ShouldBeTrue();
        hub.IsShownInline.ShouldBeFalse("the floating player takes over now");

        // A second control — the floating one — drives the same playback.
        var floating = new MiniPlayer(hub, PlayerLayout.Full);
        window.Content = floating;
        Press(floating, "Pausar");
        player.IsPlaying.ShouldBeFalse();

        Press(floating, "Fechar o player");
        player.Disposed.ShouldBeTrue();
        hub.IsActive.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void The_floating_mini_player_builds() => MiniPlayerBuilds(PlayerLayout.Full);

    [AvaloniaFact]
    public void The_pill_mini_player_builds() => MiniPlayerBuilds(PlayerLayout.Pill);

    private static void MiniPlayerBuilds(PlayerLayout layout)
    {
        // The pill layout once threw while building, and it is built at startup: the app
        // would not open at all.
        var hub = new PlaybackHub(_ => new FakePlayer());
        hub.Acquire("a.wav", "a", null)!.Play();

        var window = new Window { Content = new MiniPlayer(hub, layout) };
        window.Show();
    }

    [AvaloniaFact]
    public void The_bottom_pill_window_builds_at_startup()
    {
        var pill = new PlayerPillWindow(new PlaybackHub(_ => new FakePlayer()));
        pill.Player.ShouldNotBeNull();
    }

    [AvaloniaFact]
    public void Playing_another_recording_closes_the_first()
    {
        var players = new Dictionary<string, FakePlayer>();
        var hub = new PlaybackHub(path => players[path] = new FakePlayer());
        var window = new Window { Content = new StackPanel { Children = { Bar(hub, "a.wav"), Bar(hub, "b.wav") } } };
        window.Show();

        hub.Acquire("a.wav", "a", null)!.Play();
        hub.Acquire("b.wav", "b", null)!.Play();

        players["a.wav"].Disposed.ShouldBeTrue();
        players["b.wav"].IsPlaying.ShouldBeTrue();
        hub.Path.ShouldBe("b.wav");
    }

    [AvaloniaFact]
    public void Clicking_the_bars_seeks_and_the_played_part_follows_the_position()
    {
        var player = new FakePlayer();
        var bar = Bar(new PlaybackHub(_ => player));
        var window = new Window { Content = bar, Width = 500 };
        window.Show();

        var timeline = bar.Timeline;
        timeline.BarCount.ShouldBeGreaterThan(20);

        // Three quarters of the way along.
        timeline.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(
            timeline,
            new Avalonia.Input.Pointer(1, Avalonia.Input.PointerType.Mouse, true),
            timeline,
            new Point(timeline.Bounds.Width * 0.75, 10),
            0,
            new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed),
            Avalonia.Input.KeyModifiers.None));

        player.Position.TotalSeconds.ShouldBe(90, tolerance: 1);
        timeline.Fraction.ShouldBe(0.75, tolerance: 0.01);
    }

    private static void Press(Control root, string tooltip)
    {
        var button = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<Button>().Single(b => Equals(ToolTip.GetTip(b), tooltip));
        Click(button);
    }

    private static void Click(Button button) =>
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    private sealed class FakePlayer : TalkToText.Abstractions.IAudioPlayer
    {
        public TimeSpan Duration => TimeSpan.FromMinutes(2);

        public TimeSpan Position { get; private set; }

        public bool IsPlaying { get; private set; }

        public float Volume { get; set; } = 1;

        public double Speed { get; set; } = 1;

        public bool Disposed { get; private set; }

        public event EventHandler? Ended { add { } remove { } }

        public event EventHandler<Exception>? Failed { add { } remove { } }

        public void Play() => IsPlaying = true;

        public void Pause() => IsPlaying = false;

        public void StopAndRewind()
        {
            IsPlaying = false;
            Position = TimeSpan.Zero;
        }

        public void Seek(TimeSpan position) => Position = position;

        public void Dispose() => Disposed = true;
    }
}

/// <summary>Views that react to stores the engine writes from its own threads.</summary>
public sealed class CrossThreadTests
{
    /// <summary>
    /// The engine records a finished dictation on a background thread. If the view touches
    /// its controls on that thread, Avalonia throws, the exception escapes the engine's
    /// Completed event, and the text is never typed — silently.
    /// </summary>
    [AvaloniaFact]
    public async Task Transcript_added_off_the_UI_thread_does_not_throw()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ttt-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new TranscriptStore(path);
            var view = new TranscriptionsView(store);
            var window = new Window { Content = view };
            window.Show();

            await Task.Run(() => store.Add(new TranscriptRecord
            {
                At = DateTimeOffset.Now,
                Text = "hello",
            }));

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            store.Records.Count.ShouldBe(1);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>The floating recording pill.</summary>
public sealed class RecordingOverlayTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Now;

    [AvaloniaFact]
    public void Never_activates_so_the_transcript_lands_in_the_users_app()
    {
        var overlay = new RecordingOverlay();

        overlay.ShowActivated.ShouldBeFalse();
        overlay.ShowInTaskbar.ShouldBeFalse();
        overlay.Topmost.ShouldBeTrue();
        overlay.SystemDecorations.ShouldBe(SystemDecorations.None);
    }

    [AvaloniaFact]
    public void Asks_for_a_real_blur_first_and_is_exactly_pill_sized()
    {
        var overlay = new RecordingOverlay();

        // The window is the glass: any spare margin would be blurred too.
        overlay.TransparencyLevelHint[0].ShouldBe(WindowTransparencyLevel.AcrylicBlur);
        overlay.Width.ShouldBe(Tokens.Size.PillWidth);
        overlay.Height.ShouldBe(Tokens.Size.PillHeight);
    }

    [AvaloniaFact]
    public async Task Grows_from_a_dot_into_the_full_pill()
    {
        var overlay = new RecordingOverlay();
        overlay.Apply(DictationState.Recording, 0.1, Start);

        for (var i = 0; i < 100 && overlay.Reveal < 1; i++)
        {
            await Task.Delay(10);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        overlay.Reveal.ShouldBe(1, 0.001);
    }

    [AvaloniaFact]
    public void Appears_on_record_and_follows_the_level()
    {
        var overlay = new RecordingOverlay();
        overlay.Phase.ShouldBe(OverlayPhase.Hidden);

        overlay.Apply(DictationState.Recording, 0.2, Start);

        overlay.Phase.ShouldBe(OverlayPhase.Recording);
        overlay.IsVisible.ShouldBeTrue();
        overlay.Waveform.Levels[^1].ShouldBeGreaterThan(0);
    }

    [AvaloniaFact]
    public void Shows_transcribing_then_confirms_then_leaves()
    {
        var overlay = new RecordingOverlay();

        overlay.Apply(DictationState.Recording, 0.1, Start);
        overlay.Apply(DictationState.Transcribing, 0, Start);
        overlay.Phase.ShouldBe(OverlayPhase.Transcribing);
        overlay.MessageText.ShouldBe("Transcrevendo…");

        overlay.NotifyCompleted();
        overlay.Apply(DictationState.Idle, 0, Start);
        overlay.Phase.ShouldBe(OverlayPhase.Done);
        overlay.MessageText.ShouldBe("Texto inserido");
    }

    [AvaloniaFact]
    public void Follows_what_the_engine_is_doing_and_widens_for_a_long_message()
    {
        var overlay = new RecordingOverlay();

        overlay.Apply(DictationState.Recording, 0.1, Start);
        overlay.Apply(DictationState.Transcribing, 0, Start, "Transcrevendo…");
        overlay.Apply(DictationState.Transcribing, 0, Start, "Aplicando “deixa mais formal e mais curto”… 40%");

        overlay.MessageText.ShouldBe("Aplicando “deixa mais formal e mais curto”… 40%");
        overlay.Width.ShouldBeGreaterThan(Tokens.Size.PillWidth);

        // The next dictation starts from the usual size.
        overlay.Apply(DictationState.Idle, 0, Start);
        overlay.Apply(DictationState.Recording, 0.1, Start);
        overlay.Width.ShouldBe(Tokens.Size.PillWidth);
    }

    [AvaloniaFact]
    public void A_cancelled_recording_leaves_without_confirming_anything()
    {
        var overlay = new RecordingOverlay();

        overlay.Apply(DictationState.Recording, 0.1, Start);
        overlay.Apply(DictationState.Idle, 0, Start);

        overlay.Phase.ShouldBe(OverlayPhase.Leaving);
    }

    [AvaloniaFact]
    public void Nothing_heard_leaves_without_confirming_anything()
    {
        var overlay = new RecordingOverlay();

        overlay.Apply(DictationState.Recording, 0, Start);
        overlay.Apply(DictationState.Transcribing, 0, Start);
        overlay.Apply(DictationState.Idle, 0, Start);

        overlay.Phase.ShouldBe(OverlayPhase.Leaving);
    }

    [AvaloniaFact]
    public void A_new_recording_while_leaving_comes_straight_back()
    {
        var overlay = new RecordingOverlay();

        overlay.Apply(DictationState.Recording, 0.1, Start);
        overlay.Apply(DictationState.Idle, 0, Start);
        overlay.Apply(DictationState.Recording, 0.1, Start);

        overlay.Phase.ShouldBe(OverlayPhase.Recording);
    }
}

/// <summary>The live level bars.</summary>
public sealed class WaveformTests
{
    [AvaloniaFact]
    public void Newest_level_enters_on_the_right_and_stays_in_range()
    {
        var wave = new Waveform();

        wave.Push(0.05);
        wave.Push(10);

        wave.Levels[^1].ShouldBe(1);
        wave.Levels[^2].ShouldBeGreaterThan(0);
        wave.Levels[^2].ShouldBeLessThan(1);
        wave.Levels.Count.ShouldBe(Tokens.Size.WaveformBars);
    }

    [AvaloniaFact]
    public void Clear_returns_every_bar_to_rest()
    {
        var wave = new Waveform();
        wave.Push(0.3);
        wave.Clear();

        wave.Levels.ShouldAllBe(level => level == 0);
    }

    [AvaloniaFact]
    public void Renders_without_throwing()
    {
        var wave = new Waveform { Width = 120, Height = 24 };
        var window = new Window { Content = wave };
        window.Show();
        wave.Push(0.4);

        wave.Bounds.Width.ShouldBeGreaterThan(0);
    }
}

/// <summary>
/// Pins the design system to the OpenWhispr values it is modelled on.
/// </summary>
/// <remarks>
/// These are the sort of values that drift one reasonable-looking commit at a time.
/// </remarks>
public sealed class DesignSystemTests
{
    [AvaloniaFact]
    public void Primary_is_the_brand_blue()
    {
        Tokens.Colors.Primary.ShouldBe(Color.FromRgb(0x45, 0x77, 0xE9));
    }

    [AvaloniaFact]
    public void Radii_match_the_tight_rounding()
    {
        Tokens.Radius.Control.ShouldBe(6);
        Tokens.Radius.Card.ShouldBe(8);
        Tokens.Radius.Shell.ShouldBe(12);
    }

    [AvaloniaFact]
    public void Spacing_stays_on_the_grid()
    {
        double[] steps =
        [
            Tokens.Space.Hair, Tokens.Space.Tight, Tokens.Space.Snug,
            Tokens.Space.Base, Tokens.Space.Roomy, Tokens.Space.Wide, Tokens.Space.Page,
        ];

        foreach (var step in steps) (step % 2).ShouldBe(0, $"{step} is off the grid");
    }

    [AvaloniaFact]
    public void Surfaces_get_lighter_as_they_rise()
    {
        static double Lightness(Color c) => (c.R + c.G + c.B) / 3.0;

        Lightness(Tokens.Colors.Window).ShouldBeLessThan(Lightness(Tokens.Colors.Surface));
        Lightness(Tokens.Colors.Surface).ShouldBeLessThan(Lightness(Tokens.Colors.Card));
        Lightness(Tokens.Colors.Card).ShouldBeLessThan(Lightness(Tokens.Colors.Raised));
    }

    [AvaloniaFact]
    public void Waveform_spectrum_runs_from_sky_to_pink_through_the_brand_blue()
    {
        Tokens.Colors.SpectrumAt(0).ShouldBe(Tokens.Colors.Spectrum[0]);
        Tokens.Colors.SpectrumAt(1).ShouldBe(Tokens.Colors.Spectrum[^1]);
        Tokens.Colors.SpectrumAt(1.0 / 3).ShouldBe(Tokens.Colors.Primary);

        // Out-of-range positions clamp rather than throw.
        Tokens.Colors.SpectrumAt(-1).ShouldBe(Tokens.Colors.Spectrum[0]);
        Tokens.Colors.SpectrumAt(2).ShouldBe(Tokens.Colors.Spectrum[^1]);
    }

    [AvaloniaFact]
    public void Motion_uses_the_fast_start_curve_never_ease_in()
    {
        Tokens.Motion.Standard.Ease(0.25).ShouldBeGreaterThan(0.5);
        Tokens.Motion.Overlay.ShouldBe(TimeSpan.FromMilliseconds(280));
    }
}

/// <summary>Content the user reads can be selected and copied.</summary>
public sealed class SelectableTextTests
{
    [AvaloniaFact]
    public void A_summary_is_selectable_text_with_a_copy_menu()
    {
        var summary = Ui.Markdown("## Decisões\n- Lançar na segunda\nTexto corrido.");
        var window = new Window { Content = summary };
        window.Show();

        var blocks = summary.GetLogicalDescendants().OfType<TextBlock>().ToList();
        blocks.Count.ShouldBe(3);
        blocks.ShouldAllBe(b => b is SelectableTextBlock);

        var paragraph = (SelectableTextBlock)blocks[^1];
        paragraph.SelectAll();
        paragraph.SelectedText.ShouldBe("Texto corrido.");
        paragraph.ContextMenu.ShouldNotBeNull();
    }
}
