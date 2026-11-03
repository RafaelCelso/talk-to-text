using TalkToText.Abstractions;
using TalkToText.Core;
using TalkToText.Testing;
using Shouldly;
using Xunit;

namespace TalkToText.CoreTests;

/// <summary>Cleaning up dictations, and spoken commands on the selected text.</summary>
public sealed class DictationAiTests
{
    private static readonly int[] ShiftWin = [0xA0, 0x5B];

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5);
    }

    private static async Task SayAsync(FakeHotkeySource hotkey, DictationEngine engine, IReadOnlyList<int>? chord = null)
    {
        hotkey.Chord = chord ?? [0xA2, 0x5B];
        hotkey.Press();
        await WaitFor(() => engine.State == DictationState.Recording);
        await WaitFor(() => engine.Level != 0);
        hotkey.Release();
        await WaitFor(() => engine.State == DictationState.Idle);
    }

    private static DictationEngine Build(FakeHotkeySource hotkey, string heard, RecordingTextInjector injector) =>
        new(FakeAudioCapture.Tone(1.0), hotkey, new FakeTranscriber(heard), injector, () => [], new FakeClock());

    [Fact]
    public async Task Hesitations_are_taken_out_unless_turned_off()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = Build(hotkey, "Éé, eu queria que que ligar.", injector);

        await SayAsync(hotkey, engine);
        injector.Injected[^1].ShouldBe("Eu queria que ligar.");

        engine.RemoveHesitations = false;
        await SayAsync(hotkey, engine);
        injector.Injected[^1].ShouldBe("Éé, eu queria que que ligar.");
    }

    [Fact]
    public async Task The_ai_tidies_a_dictation_when_asked()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = Build(hotkey, "eu vou amanha", injector);
        engine.Rewriter = new FakeRewriter { Polish = t => "Eu vou amanhã." };

        await SayAsync(hotkey, engine);
        injector.Injected[^1].ShouldBe("eu vou amanha"); // off by default

        engine.PolishWithAi = true;
        await SayAsync(hotkey, engine);
        injector.Injected[^1].ShouldBe("Eu vou amanhã.");
    }

    [Fact]
    public async Task A_failing_ai_still_types_what_was_said()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = Build(hotkey, "eu vou amanha", injector);
        engine.Rewriter = new FakeRewriter { Polish = _ => throw new InvalidOperationException("a IA local ainda não foi baixada") };
        engine.PolishWithAi = true;

        await SayAsync(hotkey, engine);
        injector.Injected[^1].ShouldBe("eu vou amanha");
    }

    [Fact]
    public async Task A_command_rewrites_the_selection_and_types_the_result()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = Build(hotkey, "deixa mais formal", injector);
        var rewriter = new FakeRewriter { Command = (i, s) => $"[{i}] {s?.ToUpperInvariant()}" };
        engine.Rewriter = rewriter;
        engine.SelectionReader = new FakeSelection("oi, tudo bem?");
        engine.CommandShortcut = new Shortcut(Keys.Shift, Keys.Windows);

        await SayAsync(hotkey, engine, ShiftWin);

        injector.Injected.ShouldBe(["[deixa mais formal] OI, TUDO BEM?"]);
    }

    [Fact]
    public async Task A_command_shows_what_the_ai_is_doing_and_how_far_it_got()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = Build(hotkey, "traduz para inglês", injector);
        DictationActivity? during = null;
        engine.Rewriter = new FakeRewriter { Command = (_, _) => "Hello", During = () => during = engine.Activity };
        engine.SelectionReader = new FakeSelection("Olá, tudo bem?"); // 14 characters; the fake has written 5
        engine.CommandShortcut = new Shortcut(Keys.Shift, Keys.Windows);

        await SayAsync(hotkey, engine, ShiftWin);

        during.ShouldNotBeNull();
        during.Step.ShouldBe(DictationStep.AiWriting);
        during.Instruction.ShouldBe("traduz para inglês");
        during.HasSelection.ShouldBeTrue();
        during.Progress!.Value.ShouldBe(5 / 14.0, 0.001);
        engine.Activity.ShouldBeNull(); // done
    }

    [Fact]
    public async Task Polishing_shows_as_such_without_an_instruction()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = Build(hotkey, "eu vou amanha", injector);
        DictationActivity? during = null;
        engine.Rewriter = new FakeRewriter { Polish = t => "Eu vou amanhã.", During = () => during = engine.Activity };
        engine.PolishWithAi = true;

        await SayAsync(hotkey, engine);

        during.ShouldNotBeNull();
        during.Polishing.ShouldBeTrue();
        during.Instruction.ShouldBeNull();
    }

    [Fact]
    public async Task With_nothing_selected_a_command_writes_what_it_asks_for()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = Build(hotkey, "escreve um agradecimento", injector);
        engine.Rewriter = new FakeRewriter { Command = (i, s) => s is null ? "Muito obrigado!" : "?" };
        engine.SelectionReader = new FakeSelection(null);
        engine.CommandShortcut = new Shortcut(Keys.Shift, Keys.Windows);

        await SayAsync(hotkey, engine, ShiftWin);

        injector.Injected.ShouldBe(["Muito obrigado!"]);
    }

    [Fact]
    public async Task A_refused_command_tells_the_user_why()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = Build(hotkey, "traduza para o inglês", injector);
        const string refusal = "O provedor recusou o pedido (400): Developer instruction is not enabled for this model";
        engine.Rewriter = new FakeRewriter { Command = (_, _) => throw new InvalidOperationException(refusal) };
        engine.SelectionReader = new FakeSelection("Olá, tudo bem?");
        engine.CommandShortcut = new Shortcut(Keys.Shift, Keys.Windows);
        string? failure = null;
        engine.Failed += (_, message) => failure = message;

        await SayAsync(hotkey, engine, ShiftWin);

        injector.Injected.ShouldBeEmpty();
        failure.ShouldBe(refusal);
    }

    [Fact]
    public async Task The_command_shortcut_does_nothing_while_command_mode_is_off()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = Build(hotkey, "deixa mais formal", injector);
        engine.Rewriter = new FakeRewriter { Command = (_, _) => "x" };

        hotkey.Chord = ShiftWin;
        hotkey.Press();
        await Task.Delay(100);
        hotkey.Release();
        await Task.Delay(100);

        engine.State.ShouldBe(DictationState.Idle);
        injector.Injected.ShouldBeEmpty();
    }

    private sealed class FakeRewriter : ITextRewriter
    {
        public Func<string, string> Polish { get; init; } = t => t;

        public Func<string, string?, string> Command { get; init; } = (i, _) => i;

        /// <summary>Called while the "AI" works, to look at the engine mid-rewrite.</summary>
        public Action? During { get; init; }

        public Task<string> PolishAsync(string text, CancellationToken cancellationToken, IProgress<RewriteProgress>? progress = null)
        {
            Work(progress);
            return Task.FromResult(Polish(text));
        }

        public Task<string> CommandAsync(
            string instruction, string? selection, CancellationToken cancellationToken, IProgress<RewriteProgress>? progress = null)
        {
            Work(progress);
            return Task.FromResult(Command(instruction, selection));
        }

        private void Work(IProgress<RewriteProgress>? progress)
        {
            progress?.Report(new RewriteProgress(RewriteStage.Writing, 5));
            During?.Invoke();
        }
    }

    private sealed class FakeSelection(string? selection) : ISelectionReader
    {
        public ValueTask<string?> CopySelectionAsync(CancellationToken cancellationToken) => ValueTask.FromResult(selection);
    }
}
