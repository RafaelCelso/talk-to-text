using TalkToText.Abstractions;
using TalkToText.Core;
using TalkToText.Testing;
using Shouldly;
using Xunit;

namespace TalkToText.CoreTests;

/// <summary>Which key combinations count as the shortcut.</summary>
public sealed class ShortcutDetectorTests
{
    private readonly ShortcutDetector _detector = new() { Shortcut = Shortcut.Default };
    private readonly List<string> _events = [];

    public ShortcutDetectorTests()
    {
        _detector.Activated += (_, _) => _events.Add("on");
        _detector.Deactivated += (_, _) => _events.Add("off");
        _detector.Interrupted += (_, _) => _events.Add("interrupted");
    }

    private void Down(int key) => _detector.Handle(new KeyTransition(key, IsDown: true));

    private void Up(int key) => _detector.Handle(new KeyTransition(key, IsDown: false));

    [Fact]
    public void Ctrl_win_fires_in_either_order_and_from_either_side()
    {
        Down(Keys.RightControl); Down(Keys.Windows);
        Up(Keys.Windows); Up(Keys.RightControl);

        Down(Keys.RightWindows); Down(Keys.LeftControl);
        Up(Keys.LeftControl); Up(Keys.RightWindows);

        _events.ShouldBe(["on", "off", "on", "off"]);
    }

    [Fact]
    public void One_key_of_the_pair_is_not_enough()
    {
        Down(Keys.LeftControl); Up(Keys.LeftControl);
        Down(Keys.Windows); Up(Keys.Windows);

        _events.ShouldBeEmpty();
    }

    [Fact]
    public void An_extra_key_already_held_means_a_different_chord()
    {
        Down(Keys.LeftShift); Down(Keys.LeftControl); Down(Keys.Windows);

        _events.ShouldBeEmpty();
    }

    [Fact]
    public void Auto_repeat_does_not_fire_twice()
    {
        Down(Keys.LeftControl); Down(Keys.Windows); Down(Keys.Windows); Down(Keys.LeftControl);

        _events.ShouldBe(["on"]);
    }

    [Fact]
    public void A_third_key_interrupts_and_the_chord_stays_dead_until_all_keys_are_up()
    {
        // Ctrl + Win + D is "new virtual desktop": the user never meant to dictate.
        Down(Keys.LeftControl); Down(Keys.Windows); Down(0x44);
        Up(0x44);
        Down(0x44); Up(0x44);
        Up(Keys.Windows); Up(Keys.LeftControl);

        _events.ShouldBe(["on", "interrupted"]);

        Down(Keys.LeftControl); Down(Keys.Windows);
        _events.ShouldBe(["on", "interrupted", "on"]);
    }

    [Fact]
    public void A_single_side_specific_key_ignores_the_other_side()
    {
        _detector.Shortcut = new Shortcut(Keys.RightControl);

        Down(Keys.LeftControl); Up(Keys.LeftControl);
        _events.ShouldBeEmpty();

        Down(Keys.RightControl); Up(Keys.RightControl);
        _events.ShouldBe(["on", "off"]);
    }

    [Fact]
    public void Win_and_alt_shortcuts_ask_for_the_release_mask()
    {
        Shortcut.Default.NeedsReleaseMask.ShouldBeTrue();
        new Shortcut(Keys.Control, Keys.Alt).NeedsReleaseMask.ShouldBeTrue();
        new Shortcut(Keys.Control, Keys.Shift).NeedsReleaseMask.ShouldBeFalse();
    }

    [Fact]
    public void Names_read_naturally()
    {
        Shortcut.Default.ToString().ShouldBe("Ctrl + Win");
        new Shortcut(Keys.RightControl).ToString().ShouldBe("Ctrl direito");
        new Shortcut(Keys.Control, 0x7C).ToString().ShouldBe("Ctrl + F13");
    }
}

/// <summary>Capturing a new shortcut from the keyboard.</summary>
public sealed class ShortcutRecorderTests
{
    private readonly ShortcutRecorder _recorder = new();
    private Shortcut? _result;
    private bool _cancelled;

    public ShortcutRecorderTests()
    {
        _recorder.Completed += (_, s) => _result = s;
        _recorder.Cancelled += (_, _) => _cancelled = true;
    }

    private void Tap(params int[] keys)
    {
        foreach (var k in keys) _recorder.Handle(new KeyTransition(k, IsDown: true));
        foreach (var k in keys.Reverse()) _recorder.Handle(new KeyTransition(k, IsDown: false));
    }

    [Fact]
    public void Two_keys_record_side_agnostically()
    {
        Tap(Keys.RightControl, Keys.Windows);
        _result.ShouldBe(new Shortcut(Keys.Control, Keys.Windows));
    }

    [Fact]
    public void One_key_keeps_its_side()
    {
        Tap(Keys.RightControl);
        _result.ShouldBe(new Shortcut(Keys.RightControl));
    }

    [Fact]
    public void Only_the_first_two_keys_count()
    {
        Tap(Keys.LeftControl, Keys.LeftShift, 0x41);
        _result.ShouldBe(new Shortcut(Keys.Control, Keys.Shift));
    }

    [Fact]
    public void Nothing_completes_until_every_key_is_released()
    {
        _recorder.Handle(new KeyTransition(Keys.LeftControl, IsDown: true));
        _recorder.Handle(new KeyTransition(Keys.Windows, IsDown: true));
        _recorder.Handle(new KeyTransition(Keys.Windows, IsDown: false));
        _result.ShouldBeNull();

        _recorder.Handle(new KeyTransition(Keys.LeftControl, IsDown: false));
        _result.ShouldBe(Shortcut.Default);
    }

    [Fact]
    public void Escape_cancels()
    {
        Tap(Keys.Escape);
        _cancelled.ShouldBeTrue();
        _result.ShouldBeNull();
    }
}

/// <summary>Hold-to-talk versus press-to-toggle, end to end through the engine.</summary>
public sealed class ActivationModeTests
{
    /// <summary>
    /// Waits in real time, up to five seconds. Capture and transcription run on the thread
    /// pool, so a fixed number of yields can finish before they have done anything.
    /// </summary>
    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5);
    }

    [Fact]
    public async Task Hold_records_only_while_the_keys_are_down()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(2.0), hotkey, new FakeTranscriber("held"), injector, () => [], new FakeClock());

        hotkey.Press();
        await WaitFor(() => engine.Level > 0);
        engine.State.ShouldBe(DictationState.Recording);

        hotkey.Release();
        await WaitFor(() => injector.Injected.Count > 0 && engine.State == DictationState.Idle);

        injector.Injected.ShouldBe(["held"]);
    }

    [Fact]
    public async Task Toggle_starts_on_one_press_and_stops_on_the_next()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(5.0), hotkey, new FakeTranscriber("toggled"), injector, () => [], new FakeClock())
        {
            Mode = ActivationMode.Toggle,
        };

        hotkey.Press();
        hotkey.Release();
        await WaitFor(() => engine.Level > 0);
        engine.State.ShouldBe(DictationState.Recording, "releasing must not stop a toggled recording");

        hotkey.Press();
        hotkey.Release();
        await WaitFor(() => injector.Injected.Count > 0 && engine.State == DictationState.Idle);

        injector.Injected.ShouldBe(["toggled"]);
    }

    [Fact]
    public async Task An_interrupted_shortcut_throws_the_recording_away()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(2.0), hotkey, new FakeTranscriber("never typed"), injector, () => [], new FakeClock());

        hotkey.Press();
        await WaitFor(() => engine.Level > 0);
        hotkey.Down(0x44);   // Ctrl + Win + D
        await WaitFor(() => engine.State == DictationState.Idle);
        hotkey.Up(0x44);
        hotkey.Release();
        for (var i = 0; i < 2000; i++) await Task.Yield();

        engine.State.ShouldBe(DictationState.Idle);
        injector.Injected.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_windows_key_release_is_masked_so_the_start_menu_stays_shut()
    {
        var hotkey = new FakeHotkeySource();
        await using var engine = new DictationEngine(
            FakeAudioCapture.Silence(0.2), hotkey, new FakeTranscriber(""), new RecordingTextInjector(), () => [], new FakeClock());

        hotkey.Press();
        hotkey.MaskCount.ShouldBe(1);
        hotkey.Release();
        await WaitFor(() => engine.State == DictationState.Idle);
    }

    /// <summary>
    /// Windows silently removes a keyboard hook whose callback runs for about a second. The
    /// shortcut's press and release arrive on that callback, so neither may wait for the
    /// microphone to open or for transcription — on real hardware that once lost the release
    /// and left the app recording forever.
    /// </summary>
    [Fact]
    public async Task Press_and_release_return_at_once_even_when_the_work_is_slow()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = new DictationEngine(
            new SlowStartCapture(FakeAudioCapture.Tone(1.0)), hotkey, new SlowTranscriber("slow"), injector, () => [], new FakeClock());

        var press = System.Diagnostics.Stopwatch.StartNew();
        hotkey.Press();
        press.Stop();
        engine.State.ShouldBe(DictationState.Recording, "the state must change before the press returns");

        await WaitFor(() => engine.Level > 0);

        var release = System.Diagnostics.Stopwatch.StartNew();
        hotkey.Release();
        release.Stop();

        press.ElapsedMilliseconds.ShouldBeLessThan(150);
        release.ElapsedMilliseconds.ShouldBeLessThan(150);

        for (var i = 0; i < 200 && injector.Injected.Count == 0; i++) await Task.Delay(20);
        injector.Injected.ShouldBe(["slow"]);
    }

    /// <summary>
    /// The model can arrive while the app runs — downloaded from the app itself. A dictation
    /// before it types nothing; the first one after must load it, not reuse the failed load.
    /// </summary>
    [Fact]
    public async Task A_model_downloaded_while_running_is_loaded_by_the_next_dictation()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        var transcriber = new LateModelTranscriber("depois do download");
        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(1.0), hotkey, transcriber, injector, () => [], new FakeClock());
        engine.Start();

        hotkey.Press();
        await WaitFor(() => engine.Level > 0);
        hotkey.Release();
        await WaitFor(() => engine.State == DictationState.Idle);
        injector.Injected.ShouldBeEmpty();

        transcriber.Installed = true;

        hotkey.Press();
        await WaitFor(() => engine.Level > 0);
        hotkey.Release();
        await WaitFor(() => injector.Injected.Count > 0 && engine.State == DictationState.Idle);

        injector.Injected.ShouldBe(["depois do download"]);
    }

    /// <summary>Loads only once <see cref="Installed"/> is set, like a model not yet downloaded.</summary>
    private sealed class LateModelTranscriber(string text) : ITranscriber
    {
        public volatile bool Installed;

        public bool IsReady { get; private set; }

        public ValueTask<bool> LoadAsync(CancellationToken cancellationToken)
        {
            IsReady = Installed;
            return ValueTask.FromResult(IsReady);
        }

        public ValueTask<string> TranscribeAsync(
            ReadOnlyMemory<float> samples, IReadOnlyList<string> biasPhrases, CancellationToken cancellationToken) =>
            ValueTask.FromResult(IsReady ? text : string.Empty);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Blocks for half a second before the first chunk, like a real device opening.</summary>
    private sealed class SlowStartCapture(FakeAudioCapture inner) : IAudioCapture
    {
        public bool IsCapturing => inner.IsCapturing;

        public async IAsyncEnumerable<AudioChunk> CaptureAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Thread.Sleep(500);
            await foreach (var chunk in inner.CaptureAsync(cancellationToken).ConfigureAwait(false)) yield return chunk;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Transcribes synchronously and slowly, like Parakeet's Decode.</summary>
    private sealed class SlowTranscriber(string text) : ITranscriber
    {
        public bool IsReady => true;

        public ValueTask<bool> LoadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<string> TranscribeAsync(
            ReadOnlyMemory<float> samples, IReadOnlyList<string> biasPhrases, CancellationToken cancellationToken)
        {
            Thread.Sleep(500);
            return ValueTask.FromResult(text);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_suspended_engine_ignores_the_shortcut()
    {
        var hotkey = new FakeHotkeySource();
        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(1.0), hotkey, new FakeTranscriber("x"), new RecordingTextInjector(), () => [], new FakeClock())
        {
            IsSuspended = true,
        };

        hotkey.Press();
        for (var i = 0; i < 500; i++) await Task.Yield();

        engine.State.ShouldBe(DictationState.Idle);
    }
}
