using System.IO.Compression;
using TalkToText.Core;
using TalkToText.Testing;
using Shouldly;
using Xunit;

namespace TalkToText.CoreTests;

/// <summary>Searching, exporting, and remembering voices after the fact.</summary>
public sealed class TranscriptToolsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 13, 0, 0, TimeSpan.Zero);

    private static MeetingTranscript Sample() => new()
    {
        App = "Microsoft Teams",
        Title = "Reunião de orçamento",
        StartedAt = Start,
        EndedAt = Start.AddMinutes(32),
        Summary = "## Decisões\n- Fechar o **orçamento** até sexta",
        Speakers =
        [
            new MeetingSpeaker { Id = 0, Name = "Rafael", IsLocal = true, IsNamed = true },
            new MeetingSpeaker { Id = 1, Name = "Ana Silva", IsNamed = true },
        ],
        Messages =
        [
            new MeetingMessage { SpeakerId = 1, At = Start.AddMinutes(1), Text = "Bom dia, vamos começar." },
            new MeetingMessage { SpeakerId = 0, At = Start.AddMinutes(2), Text = "O fornecedor ainda não mandou a proposta revisada & assinada." },
            new MeetingMessage { SpeakerId = 1, At = Start.AddMinutes(3), Text = "Então ligamos para o fornecedor amanhã cedo." },
        ],
    };

    [Fact]
    public void Search_ignores_accents_and_case_and_shows_where_it_was_said()
    {
        var hit = TranscriptSearch.Find(Sample(), "PROPOSTA revisada");
        hit.ShouldBe("Rafael: O fornecedor ainda não mandou a proposta revisada & assinada.");

        TranscriptSearch.Find(Sample(), "reuniao").ShouldBe(string.Empty); // the title only
        TranscriptSearch.Find(Sample(), "fornecedor amanha").ShouldStartWith("Ana Silva: Então ligamos");
        TranscriptSearch.Find(Sample(), "contrato").ShouldBeNull();
        TranscriptSearch.Find(Sample(), "   ").ShouldBe(string.Empty);
    }

    [Fact]
    public void A_long_message_is_cut_around_the_match()
    {
        var meeting = Sample();
        meeting.Messages.Add(new MeetingMessage
        {
            SpeakerId = 1,
            At = Start.AddMinutes(4),
            Text = string.Concat(Enumerable.Repeat("palavra comum de enchimento ", 12)) + "o cronograma final " + string.Concat(Enumerable.Repeat("mais texto depois ", 12)),
        });

        var hit = TranscriptSearch.Find(meeting, "cronograma")!;
        hit.ShouldContain("cronograma");
        hit.ShouldStartWith("Ana Silva: …");
        hit.Length.ShouldBeLessThan(120);
    }

    [Fact]
    public void Markdown_has_the_summary_and_every_message_with_its_time()
    {
        var md = TranscriptExport.ToMarkdown(Sample());
        md.ShouldStartWith("# Reunião de orçamento");
        md.ShouldContain("**Participantes:** Rafael, Ana Silva");
        md.ShouldContain("### Decisões"); // one level under "## Resumo"
        md.ShouldContain("**[");
        md.ShouldContain("Ana Silva:** Então ligamos para o fornecedor amanhã cedo.");
    }

    [Fact]
    public void Plain_text_drops_the_markdown()
    {
        var text = TranscriptExport.ToText(Sample());
        text.ShouldContain("DECISÕES");
        text.ShouldContain("- Fechar o orçamento até sexta");
        text.ShouldNotContain("**");
    }

    [Fact]
    public void The_word_document_is_a_valid_package_with_the_text_escaped()
    {
        using var file = new MemoryStream();
        TranscriptExport.WriteDocx(Sample(), file);

        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        zip.Entries.Select(e => e.FullName).ShouldBe(["[Content_Types].xml", "_rels/.rels", "word/document.xml"], ignoreOrder: true);

        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = reader.ReadToEnd();
        System.Xml.Linq.XDocument.Parse(xml); // well-formed
        xml.ShouldContain("revisada &amp; assinada");
        xml.ShouldContain("Fechar o ");
        xml.ShouldContain("<w:b/>");
    }

    [Fact]
    public void The_file_name_is_the_title_and_date_without_forbidden_characters()
    {
        var meeting = Sample();
        meeting.Title = "Q4: plano/metas?";
        TranscriptExport.FileName(meeting).ShouldBe("Q4 plano metas 2026-11-02");
    }

    [Fact]
    public async Task A_transcribed_recording_keeps_each_voice_so_it_can_be_named_later()
    {
        var audio = FakeDecodedAudio.Of((1, 0), (2, 0.5f), (1, 0), (2, 0.2f), (0.5, 0));
        var transcript = AudioFileTranscriber.NewTranscript("ligacao.mp3", audio.Duration, Start);

        await new AudioFileTranscriber(new FakeTranscriber("Bom dia", "Oi"), new SilenceSplitSegmenter(), new FakeSpeakerEmbedder(), [], [])
            .TranscribeAsync(audio, transcript, null, CancellationToken.None);

        transcript.Speakers.ShouldAllBe(s => s.Voice != null && s.Voice.Length > 0 && s.VoiceCount > 0);
    }
}
