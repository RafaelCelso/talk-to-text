using TalkToText.Core;
using TalkToText.Testing;
using Shouldly;
using Xunit;

namespace TalkToText.CoreTests;

/// <summary>Meeting summaries with a local model.</summary>
public sealed class MeetingSummarizerTests
{
    private static MeetingTranscript Meeting(int messages)
    {
        var start = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        return new MeetingTranscript
        {
            App = "Microsoft Teams",
            Title = "Planejamento",
            StartedAt = start,
            Speakers =
            [
                new MeetingSpeaker { Id = 0, Name = "Rafael", IsLocal = true, IsNamed = true },
                new MeetingSpeaker { Id = 1, Name = "Ana Silva", IsNamed = true },
                new MeetingSpeaker { Id = 2, Name = "Bruno", IsNamed = true },
            ],
            Messages = Enumerable.Range(0, messages).Select(i => new MeetingMessage
            {
                SpeakerId = i % 2,
                At = start.AddSeconds(i * 10),
                Text = $"Frase número {i} sobre o lançamento do aplicativo na quarta-feira.",
            }).ToList(),
        };
    }

    private static async Task<string> Run(MeetingSummarizer summarizer, MeetingTranscript meeting, List<SummaryProgress>? steps = null)
    {
        var text = new System.Text.StringBuilder();
        var progress = new SynchronousProgress(p => steps?.Add(p));
        await foreach (var piece in summarizer.SummarizeAsync(meeting, progress, CancellationToken.None)) text.Append(piece);
        return text.ToString();
    }

    [Fact]
    public async Task A_short_meeting_is_summarised_in_one_pass_with_the_transcript_and_people()
    {
        var model = new FakeLanguageModel();
        var text = await Run(new MeetingSummarizer(model), Meeting(6));

        model.Requests.ShouldHaveSingleItem();
        var (system, user) = model.Requests[0];
        system.ShouldContain("não invente");
        user.ShouldContain("Planejamento");
        user.ShouldContain("Rafael, Ana Silva");
        user.ShouldNotContain("Bruno", Case.Sensitive, "someone who never spoke is not a participant");
        user.ShouldContain("Ana Silva: Frase número 1");
        user.ShouldContain("## Próximos passos");
        text.ShouldContain("Tudo certo.");
    }

    [Fact]
    public async Task A_long_meeting_is_condensed_into_notes_first()
    {
        // A tiny context forces several chunks.
        var model = new FakeLanguageModel(contextTokens: 2200);
        var steps = new List<SummaryProgress>();
        await Run(new MeetingSummarizer(model), Meeting(200), steps);

        model.Requests.Count.ShouldBeGreaterThan(2);
        model.Requests[0].User.ShouldContain("parte 1 de");
        model.Requests[^1].User.ShouldContain("Notas de cada parte");
        steps[^1].Writing.ShouldBeTrue();
        steps[^1].Step.ShouldBe(steps[^1].Steps);
    }

    [Fact]
    public async Task Every_message_reaches_the_model_exactly_once()
    {
        var model = new FakeLanguageModel(contextTokens: 2200);
        var meeting = Meeting(120);
        await Run(new MeetingSummarizer(model), meeting);

        var chunkRequests = model.Requests.Take(model.Requests.Count - 1).Select(r => r.User).ToList();
        for (var i = 0; i < meeting.Messages.Count; i++)
        {
            chunkRequests.Count(r => r.Contains($"Frase número {i} ", StringComparison.Ordinal)).ShouldBe(1, $"message {i}");
        }
    }

    /// <summary>Progress&lt;T&gt; posts to a sync context; tests want the callback inline.</summary>
    private sealed class SynchronousProgress(Action<SummaryProgress> report) : IProgress<SummaryProgress>
    {
        public void Report(SummaryProgress value) => report(value);
    }
}
