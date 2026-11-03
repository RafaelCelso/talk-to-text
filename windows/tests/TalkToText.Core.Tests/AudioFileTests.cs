using TalkToText.Abstractions;
using TalkToText.Core;
using TalkToText.Dictionary;
using TalkToText.Testing;
using Shouldly;
using Xunit;

namespace TalkToText.CoreTests;

/// <summary>Transcribing a recorded audio file.</summary>
public sealed class AudioFileTranscriberTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    private static AudioFileTranscriber Transcriber(FakeTranscriber speech, IReadOnlyList<DictionaryEntry>? dictionary = null) =>
        new(speech, new SilenceSplitSegmenter(), new FakeSpeakerEmbedder(), [], dictionary ?? []);

    [Fact]
    public async Task Each_turn_becomes_a_message_from_its_voice_at_its_place_in_the_recording()
    {
        // Loud, quiet, loud: two voices, the first one coming back.
        var audio = FakeDecodedAudio.Of((1, 0), (2, 0.5f), (1, 0), (2, 0.2f), (1, 0), (1, 0.5f), (0.5, 0));
        var transcript = AudioFileTranscriber.NewTranscript("ligacao.mp3", audio.Duration, Now);

        await Transcriber(new FakeTranscriber("Bom dia", "Oi, tudo bem?", "Pode falar"))
            .TranscribeAsync(audio, transcript, null, CancellationToken.None);

        transcript.Title.ShouldBe("ligacao");
        transcript.Messages.Select(m => m.Text).ShouldBe(["Bom dia", "Oi, tudo bem?", "Pode falar"]);
        transcript.Speakers.Select(s => s.Name).ShouldBe(["Pessoa 1", "Pessoa 2"]);
        transcript.Messages[2].SpeakerId.ShouldBe(transcript.Messages[0].SpeakerId);
        transcript.TimeOf(transcript.Messages[1]).ShouldBe("00:04");
        transcript.EndedAt.ShouldBe(Now + audio.Duration);
    }

    [Fact]
    public async Task The_dictionary_corrects_what_was_heard()
    {
        var audio = FakeDecodedAudio.Of((1, 0.5f), (0.5, 0));
        var transcript = AudioFileTranscriber.NewTranscript("sac.wav", audio.Duration, Now);

        await Transcriber(new FakeTranscriber("Produto da besins"), [DictionaryEntry.Correction("besins", "Besins")])
            .TranscribeAsync(audio, transcript, null, CancellationToken.None);

        transcript.Messages.ShouldHaveSingleItem().Text.ShouldBe("Produto da Besins");
    }

    [Fact]
    public async Task Stopping_keeps_what_was_already_transcribed()
    {
        var audio = FakeDecodedAudio.Of((1, 0.5f), (1, 0), (1, 0.2f), (1, 0), (1, 0.5f), (1, 0));
        var transcript = AudioFileTranscriber.NewTranscript("longa.mp3", audio.Duration, Now);
        using var stop = new CancellationTokenSource();
        audio.BeforeRead = position => { if (position >= 4 * AudioChunk.SampleRate) stop.Cancel(); };

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Transcriber(new FakeTranscriber("um", "dois", "três")).TranscribeAsync(audio, transcript, null, stop.Token));

        transcript.Messages.Select(m => m.Text).ShouldBe(["um", "dois"]);
    }

    [Fact]
    public async Task Progress_reaches_the_end_of_the_file()
    {
        var audio = FakeDecodedAudio.Of((2.5, 0.5f), (0.5, 0));
        var transcript = AudioFileTranscriber.NewTranscript("a.wav", audio.Duration, Now);
        var reports = new List<FileTranscriptionProgress>();

        await Transcriber(new FakeTranscriber("x"))
            .TranscribeAsync(audio, transcript, new SyncProgress(reports.Add), CancellationToken.None);

        reports.Select(r => r.Fraction).ShouldBeInOrder();
        reports[^1].Fraction.ShouldBe(1);
        reports[^1].Messages.ShouldBe(1);
    }

    /// <summary>Reports inline: <see cref="Progress{T}"/> posts later, after the test ends.</summary>
    private sealed class SyncProgress(Action<FileTranscriptionProgress> report) : IProgress<FileTranscriptionProgress>
    {
        public void Report(FileTranscriptionProgress value) => report(value);
    }
}

/// <summary>A recording's loudness along its length, for the player's bars.</summary>
public sealed class AudioEnvelopeTests
{
    [Fact]
    public void Loud_stretches_stand_tall_and_silence_stays_flat()
    {
        // One second each: silence, loud, quiet.
        var audio = FakeDecodedAudio.Of((1, 0), (1, 0.5f), (1, 0.05f));

        var levels = AudioEnvelope.Compute(audio, 3);

        levels.Length.ShouldBe(3);
        levels[0].ShouldBe(0);
        levels[1].ShouldBe(1);
        levels[2].ShouldBeInRange(0.2, 0.6);
    }
}

/// <summary>The user's instruction for the AI, from Settings.</summary>
public sealed class SummaryInstructionTests
{
    [Fact]
    public async Task The_users_instruction_reaches_the_model_beside_the_rule_against_inventing()
    {
        var model = new FakeLanguageModel();
        var transcript = new MeetingTranscript
        {
            Title = "Ligação 42",
            App = "Arquivo de áudio",
            SourceFile = "ligacao-42.mp3",
            StartedAt = DateTimeOffset.UnixEpoch,
            Speakers = [new MeetingSpeaker { Id = 1, Name = "Pessoa 1" }],
            Messages = [new MeetingMessage { SpeakerId = 1, At = DateTimeOffset.UnixEpoch.AddSeconds(75), Text = "Quero registrar uma queixa técnica." }],
        };

        var summarizer = new MeetingSummarizer(model, "Você é um atendente de SAC do laboratório Besins.");
        await foreach (var _ in summarizer.SummarizeAsync(transcript, null, CancellationToken.None)) { }

        var (system, user) = model.Requests.ShouldHaveSingleItem();
        system.ShouldContain("atendente de SAC do laboratório Besins");
        system.ShouldContain("não invente");
        user.ShouldContain("a gravação \"Ligação 42\"");
        user.ShouldContain("[01:15] Pessoa 1: Quero registrar uma queixa técnica.");

        // Registering something counts as agreed: the model once wrote "no decisions" over a
        // call that opened two records and promised a refund.
        user.ShouldContain("## Decisões e combinados");
        user.ShouldContain("registrado, aberto ou prometido");
        user.ShouldNotContain("## Dados citados", customMessage: "written by the app, not the model");
    }

    [Fact]
    public async Task Lines_with_numbers_are_pointed_out_for_the_data_section()
    {
        var model = new FakeLanguageModel();
        var transcript = new MeetingTranscript
        {
            Speakers = [new MeetingSpeaker { Id = 1, Name = "Pessoa 1" }],
            Messages =
            [
                new MeetingMessage { SpeakerId = 1, Text = "Bom dia." },
                new MeetingMessage { SpeakerId = 1, Text = "O lote é 4721." },
                new MeetingMessage { SpeakerId = 1, Text = "Retornamos em até cinco dias úteis." },
            ],
        };

        var text = string.Concat(await new MeetingSummarizer(model).SummarizeAsync(transcript, null, CancellationToken.None).ToListAsync());

        var user = model.Requests.ShouldHaveSingleItem().User;
        user.ShouldContain("Falas com números, datas ou prazos");

        // Quoted word for word by the app, after whatever the model wrote.
        text.ShouldContain("## Dados citados");
        text.ShouldContain("Pessoa 1: O lote é 4721.");
        text.ShouldContain("Pessoa 1: Retornamos em até cinco dias úteis.");
        text.ShouldNotContain(": Bom dia.");
        user.ShouldContain("- O lote é 4721.");
        user.ShouldContain("- Retornamos em até cinco dias úteis.");
        user.ShouldNotContain("- Bom dia.");
    }

    [Fact]
    public async Task Without_an_instruction_the_prompt_is_the_plain_one()
    {
        var model = new FakeLanguageModel();
        var transcript = new MeetingTranscript
        {
            Speakers = [new MeetingSpeaker { Id = 1, Name = "Ana" }],
            Messages = [new MeetingMessage { SpeakerId = 1, Text = "Oi" }],
        };

        await foreach (var _ in new MeetingSummarizer(model, "   ").SummarizeAsync(transcript, null, CancellationToken.None)) { }

        model.Requests.ShouldHaveSingleItem().System.ShouldNotContain("Contexto e instruções do usuário");
    }
}
