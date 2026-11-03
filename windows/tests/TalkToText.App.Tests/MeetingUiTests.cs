using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using TalkToText.App.Views;
using TalkToText.Core;
using Shouldly;

namespace TalkToText.AppTests;

/// <summary>The meeting chat.</summary>
public sealed class MeetingUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ttt-ui-meet-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static MeetingTranscript Sample()
    {
        var start = DateTimeOffset.Now.AddMinutes(-10);
        return new MeetingTranscript
        {
            App = "Microsoft Teams",
            Title = "Daily",
            StartedAt = start,
            EndedAt = start.AddMinutes(9),
            Speakers =
            [
                new MeetingSpeaker { Id = 0, Name = "Rafael", IsLocal = true, IsNamed = true },
                new MeetingSpeaker { Id = 1, Name = "Ana Silva", IsNamed = true },
                new MeetingSpeaker { Id = 2, Name = "Participante 2" },
            ],
            Messages =
            [
                new MeetingMessage { SpeakerId = 1, At = start.AddMinutes(1), Text = "Bom dia!" },
                new MeetingMessage { SpeakerId = 1, At = start.AddMinutes(1.2), Text = "Vamos começar?" },
                new MeetingMessage { SpeakerId = 0, At = start.AddMinutes(2), Text = "Vamos." },
                new MeetingMessage { SpeakerId = 2, At = start.AddMinutes(3), Text = "Eu tenho uma dúvida." },
            ],
        };
    }

    [AvaloniaFact]
    public void A_saved_meeting_renders_as_a_chat()
    {
        var view = new ChatView(null, Sample(), new MeetingStore(_root));
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();

        view.Bounds.Width.ShouldBeGreaterThan(0);
    }

    [AvaloniaFact]
    public void The_meetings_page_lists_saved_meetings()
    {
        var store = new MeetingStore(_root);
        store.Save(Sample());

        var view = new MeetingsView(store, null);
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        window.Show();

        store.All().ShouldHaveSingleItem();
    }

    [AvaloniaFact]
    public async Task The_prompt_counts_down_and_never_takes_focus()
    {
        var prompt = new MeetingPromptWindow(new DetectedMeeting("Microsoft Teams", "ms-teams", "Daily"), needsDownload: false);
        prompt.ShowActivated.ShouldBeFalse();
        prompt.Countdown.ShouldBe(1);

        prompt.ShowInCorner();
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(30);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        prompt.Countdown.ShouldBeLessThan(1);
        prompt.Countdown.ShouldBeGreaterThan(0.9);
        prompt.Close();
    }

    [AvaloniaFact]
    public void Avatars_use_initials_and_a_stable_colour_per_person()
    {
        ChatView.Initials("Ana Silva").ShouldBe("AS");
        ChatView.Initials("Participante 2").ShouldBe("P2");
        ChatView.Initials("rafael").ShouldBe("R");

        var ana = new MeetingSpeaker { Id = 1, Name = "Ana" };
        ChatView.ColorFor(ana).ShouldBe(ChatView.ColorFor(ana));
        ChatView.ColorFor(new MeetingSpeaker { Id = 0, IsLocal = true }).ShouldBe(TalkToText.App.Design.Tokens.Colors.Primary);
    }
}
