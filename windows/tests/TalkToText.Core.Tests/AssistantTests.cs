using TalkToText.Core;
using TalkToText.Testing;
using Shouldly;
using Xunit;

namespace TalkToText.CoreTests;

/// <summary>The Chat page's saved conversations.</summary>
public sealed class ChatStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ttt-chats-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Conversations_come_back_per_tab_most_recent_first()
    {
        var store = new ChatStore(_directory);
        var t0 = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

        store.Save(Conversation(recordings: false, "Daily", "O que foi decidido?", t0));
        store.Save(Conversation(recordings: false, "Todas as reuniões", "Quais prazos?", t0.AddHours(1)));
        store.Save(Conversation(recordings: true, "ligacao-sac", "Qual o lote?", t0.AddHours(2)));
        store.Save(new ChatConversation { UpdatedAt = t0.AddHours(3) }); // never asked anything

        var reopened = new ChatStore(_directory);
        reopened.All(aboutRecordings: false).Select(c => c.Title).ShouldBe(["Quais prazos?", "O que foi decidido?"]);

        var recording = reopened.All(aboutRecordings: true).ShouldHaveSingleItem();
        recording.ScopeTitle.ShouldBe("ligacao-sac");
        recording.Turns.ShouldHaveSingleItem().Answer.ShouldBe("Resposta.");
    }

    [Fact]
    public void A_deleted_conversation_is_gone()
    {
        var store = new ChatStore(_directory);
        var conversation = Conversation(recordings: false, "Daily", "Oi?", DateTimeOffset.UnixEpoch);
        store.Save(conversation);

        store.Delete(conversation.Id);

        store.All(aboutRecordings: false).ShouldBeEmpty();
    }

    private static ChatConversation Conversation(bool recordings, string scope, string question, DateTimeOffset at) => new()
    {
        AboutRecordings = recordings,
        ScopeTitle = scope,
        CreatedAt = at,
        UpdatedAt = at,
        Exchanges = [new ChatExchange { Question = question, Answer = "Resposta.", Sources = "Daily (2 nov.)" }],
    };
}

/// <summary>Questions about meetings and recordings, answered from the transcripts.</summary>
public sealed class TranscriptAssistantTests
{
    private static MeetingTranscript Meeting(string title, DateTimeOffset at, params string[] lines) => new()
    {
        Title = title,
        App = "Microsoft Teams",
        StartedAt = at,
        Speakers = [new MeetingSpeaker { Id = 1, Name = "Ana" }],
        Messages = [.. lines.Select((l, i) => new MeetingMessage { SpeakerId = 1, At = at.AddMinutes(i), Text = l })],
    };

    private static readonly DateTimeOffset Monday = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_meeting_that_talks_about_it_is_the_one_shown_to_the_model()
    {
        var model = new FakeLanguageModel();
        var budget = Meeting("Orçamento 2027", Monday.AddDays(-3), "O orçamento de marketing sobe para 40 mil.");
        var daily = Meeting("Daily", Monday, "Ontem terminei a tela de login.", "Hoje vou revisar o PR.");
        var assistant = new TranscriptAssistant(model);

        await foreach (var _ in assistant.AskAsync("Quanto ficou o orcamento de marketing?", [budget, daily], [], CancellationToken.None)) { }

        var (system, user) = model.Requests.ShouldHaveSingleItem();
        system.ShouldContain("não invente");
        user.ShouldContain("40 mil");
        user.ShouldContain("Reunião \"Orçamento 2027\"");
        user.ShouldContain("Pergunta: Quanto ficou o orcamento de marketing?");
        assistant.Sources[^1].ShouldBe(budget);
    }

    [Fact]
    public void Only_the_best_passages_that_fit_are_chosen()
    {
        var model = new FakeLanguageModel();
        var budget = Meeting("Orçamento", Monday.AddDays(-3), "O orçamento de marketing sobe para 40 mil.");
        var daily = Meeting("Daily", Monday, "Ontem terminei a tela de login.");

        // Room for one passage only.
        var cost = model.CountTokens(TranscriptAssistant.Passages(budget).Single().Text) + 5;
        var chosen = new TranscriptAssistant(model).Select("orçamento de marketing", [budget, daily], cost);

        chosen.ShouldHaveSingleItem().Transcript.ShouldBe(budget);
    }

    [Fact]
    public void Asking_about_the_last_meeting_favours_the_newest()
    {
        var model = new FakeLanguageModel();
        var older = Meeting("Planejamento", Monday.AddDays(-7), "Decidimos contratar duas pessoas.");
        var newest = Meeting("Revisão", Monday, "Decidimos adiar o lançamento.");

        var cost = model.CountTokens(TranscriptAssistant.Passages(newest).Single().Text) + 5;
        var chosen = new TranscriptAssistant(model).Select("O que decidimos na última reunião?", [older, newest], cost);

        chosen.ShouldHaveSingleItem().Transcript.ShouldBe(newest);
    }

    [Fact]
    public void A_name_in_the_question_picks_that_meeting_even_when_another_is_newer()
    {
        // "The last meeting with Melissa" once got the newest meeting of all — a test call with
        // nobody named Melissa — and the model answered "not found".
        var model = new FakeLanguageModel();
        var withMelissa = Meeting("Melissa Martinez", Monday.AddHours(-2), "Ficou decidido manter o preço.");
        withMelissa.Speakers.Add(new MeetingSpeaker { Id = 2, Name = "Melissa Martinez" });
        var newer = Meeting("Reunião iniciada manualmente", Monday, "Bom dia, Carla.");

        var chosen = new TranscriptAssistant(model).Select("O que foi decidido na última reunião com a Melissa?", [withMelissa, newer], 5000);

        chosen.ShouldNotBeEmpty();
        chosen.ShouldAllBe(p => p.Transcript == withMelissa);
    }

    [Fact]
    public void Across_meetings_passages_sharing_no_word_with_the_question_are_left_out()
    {
        var model = new FakeLanguageModel();
        var a = Meeting("Daily", Monday, "O servidor caiu de madrugada.");
        var b = Meeting("Retro", Monday.AddDays(-1), "Faltou café na copa.");

        var chosen = new TranscriptAssistant(model).Select("Por que o servidor caiu?", [a, b], 5000);

        chosen.ShouldHaveSingleItem().Transcript.ShouldBe(a);
    }

    [Fact]
    public void A_summary_is_offered_ahead_of_the_conversation_it_condenses()
    {
        var meeting = Meeting("Daily", Monday, "Bom dia.");
        meeting.Summary = "## Resumo\nAlinhamento rápido.";

        var passages = TranscriptAssistant.Passages(meeting).ToList();

        passages[0].IsSummary.ShouldBeTrue();
        passages[0].Text.ShouldContain("Alinhamento rápido.");
        passages[1].Text.ShouldContain("Ana: Bom dia.");
    }

    [Fact]
    public async Task A_follow_up_question_sees_the_earlier_turns()
    {
        var model = new FakeLanguageModel();
        var daily = Meeting("Daily", Monday, "O prazo é sexta.");

        await foreach (var _ in new TranscriptAssistant(model).AskAsync(
            "E quem é o responsável?", [daily], [new ChatTurn("Qual o prazo?", "Sexta-feira.")], CancellationToken.None)) { }

        var user = model.Requests.ShouldHaveSingleItem().User;
        user.ShouldContain("Usuário: Qual o prazo?");
        user.ShouldContain("Assistente: Sexta-feira.");
    }

    [Fact]
    public async Task What_was_found_is_reported_before_the_model_starts_reading()
    {
        var model = new FakeLanguageModel();
        var a = Meeting("Daily", Monday, "O servidor caiu.");
        var b = Meeting("Retro", Monday.AddDays(-1), "O servidor voltou.");
        var reports = new List<AssistantFound>();

        await foreach (var _ in new TranscriptAssistant(model).AskAsync(
            "O que houve com o servidor?", [a, b], [], CancellationToken.None, new Inline(reports.Add)))
        {
            // The report came first: the progress line can say what is being read while it is.
            reports.ShouldNotBeEmpty();
        }

        reports.ShouldHaveSingleItem().ShouldBe(new AssistantFound(2, 2));
    }

    private sealed class Inline(Action<AssistantFound> report) : IProgress<AssistantFound>
    {
        public void Report(AssistantFound value) => report(value);
    }

    [Fact]
    public void Words_match_without_accents_or_case_and_common_words_are_ignored()
    {
        TranscriptAssistant.Terms("Qual foi a DECISÃO sobre o Orçamento?").ShouldBe(["decisao", "orcamento"]);
    }
}
