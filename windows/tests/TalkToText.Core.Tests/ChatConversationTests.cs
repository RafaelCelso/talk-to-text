using TalkToText.Core;
using TalkToText.Testing;
using Shouldly;
using Xunit;

namespace TalkToText.CoreTests;

/// <summary>The Chat as a conversation, not only a search.</summary>
public sealed class ChatConversationTests
{
    private static readonly DateTimeOffset Monday = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    private static MeetingTranscript Meeting(string title, DateTimeOffset at, params string[] lines) => new()
    {
        App = "Microsoft Teams",
        Title = title,
        StartedAt = at,
        EndedAt = at.AddMinutes(lines.Length),
        Speakers = [new MeetingSpeaker { Id = 1, Name = "Ana", IsNamed = true }],
        Messages = [.. lines.Select((l, i) => new MeetingMessage { SpeakerId = 1, At = at.AddMinutes(i), Text = l })],
    };

    [Theory]
    [InlineData("Oi")]
    [InlineData("oi, tudo bem?")]
    [InlineData("Bom dia!")]
    [InlineData("Obrigado, ajudou muito")]
    [InlineData("Valeu!")]
    [InlineData("O que você pode fazer?")]
    [InlineData("Quem é você?")]
    public void Greetings_thanks_and_questions_about_itself_are_small_talk(string message) =>
        TranscriptAssistant.IsSmallTalk(message).ShouldBeTrue();

    [Theory]
    [InlineData("O que foi decidido na reunião com a Ana?")]
    [InlineData("Oi, qual foi o prazo combinado com o fornecedor na reunião de ontem?")]
    [InlineData("Quanto ficou o orçamento?")]
    public void Questions_about_meetings_are_not(string message) =>
        TranscriptAssistant.IsSmallTalk(message).ShouldBeFalse();

    [Fact]
    public async Task A_greeting_reads_no_transcript_and_is_answered_as_conversation()
    {
        var model = new FakeLanguageModel();
        var daily = Meeting("Daily", Monday, "O servidor caiu de madrugada.");
        var assistant = new TranscriptAssistant(model);

        await foreach (var _ in assistant.AskAsync("Oi", [daily], [], CancellationToken.None)) { }

        var (system, user) = model.Requests.ShouldHaveSingleItem();
        system.ShouldContain("Converse de forma natural");
        user.ShouldNotContain("servidor");
        user.ShouldContain("Mensagem: Oi");
        assistant.Sources.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_general_question_gets_no_unrelated_passages()
    {
        var model = new FakeLanguageModel();
        var a = Meeting("Daily", Monday, "O servidor caiu de madrugada.");
        var b = Meeting("Retro", Monday.AddDays(-1), "Faltou café na copa.");

        await foreach (var _ in new TranscriptAssistant(model).AskAsync("Qual a capital da França?", [a, b], [], CancellationToken.None)) { }

        var user = model.Requests.ShouldHaveSingleItem().User;
        user.ShouldNotContain("servidor");
        user.ShouldNotContain("café");
        user.ShouldContain("Nenhum trecho");
    }

    [Fact]
    public async Task A_short_follow_up_is_looked_up_with_the_question_before_it()
    {
        var model = new FakeLanguageModel();
        var a = Meeting("Daily", Monday, "O servidor caiu de madrugada.", "O Bruno vai investigar.");
        var b = Meeting("Retro", Monday.AddDays(-1), "Faltou café na copa.");

        await foreach (var _ in new TranscriptAssistant(model).AskAsync(
            "E quem resolve?", [a, b], [new ChatTurn("Por que o servidor caiu?", "Não se sabe ainda.")], CancellationToken.None)) { }

        var user = model.Requests.ShouldHaveSingleItem().User;
        user.ShouldContain("Bruno vai investigar");
        user.ShouldNotContain("café");
    }
}
