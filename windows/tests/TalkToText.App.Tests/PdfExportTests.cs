using System.Text;
using Avalonia.Headless.XUnit;
using TalkToText.App.Views;
using TalkToText.Core;
using Shouldly;

namespace TalkToText.AppTests;

/// <summary>Exporting a transcript as a PDF that looks like the app.</summary>
public sealed class PdfExportTests
{
    /// <summary>A copy for looking at: the last sample written by this test.</summary>
    public static string SamplePath => Path.Combine(Path.GetTempPath(), "ttt-amostra.pdf");

    private static MeetingTranscript Sample()
    {
        var start = new DateTimeOffset(2026, 11, 2, 14, 0, 0, TimeSpan.FromHours(-3));
        var lines = new[]
        {
            (1, "Bom dia, pessoal. Vamos começar pelo orçamento do quarto trimestre?"),
            (0, "Bom dia! Pode ser. Eu trouxe os números atualizados."),
            (2, "Antes disso, o fornecedor ainda não mandou a proposta revisada."),
            (1, "Então precisamos cobrar hoje."),
            (0, "Eu ligo para eles à tarde e peço o prazo por escrito; se atrasar de novo, abrimos cotação com outra empresa, porque não dá para ficar esperando mais uma semana sem resposta e com o cliente pressionando pela data de entrega."),
        };

        var messages = new List<MeetingMessage>();
        for (var round = 0; round < 8; round++)
        {
            foreach (var (speaker, text) in lines)
            {
                messages.Add(new MeetingMessage { SpeakerId = speaker, At = start.AddMinutes(messages.Count), Text = text });
            }
        }

        return new MeetingTranscript
        {
            App = "Microsoft Teams",
            Title = "Planejamento do 4º trimestre",
            StartedAt = start,
            EndedAt = start.AddMinutes(42),
            SummarizedAt = start.AddMinutes(50),
            Summary = "## Decisões\n- Fechar o **orçamento** até sexta-feira.\n- Abrir cotação com outro fornecedor se a proposta atrasar.\n\n"
                + "## Próximos passos\n- Rafael liga para o fornecedor hoje à tarde.\n- Ana revisa os números do trimestre.\n\n"
                + "## Pendências\n- Proposta revisada do fornecedor.",
            Speakers =
            [
                new MeetingSpeaker { Id = 0, Name = "Rafael Moraes", IsLocal = true, IsNamed = true },
                new MeetingSpeaker { Id = 1, Name = "Ana Silva", IsNamed = true },
                new MeetingSpeaker { Id = 2, Name = "Participante 2" },
            ],
            Messages = messages,
        };
    }

    [AvaloniaFact]
    public void A_long_meeting_becomes_a_pdf_of_several_pages_with_its_text()
    {
        using var file = new MemoryStream();
        TranscriptPdf.Write(Sample(), file);
        var bytes = file.ToArray();
        File.WriteAllBytes(SamplePath, bytes);

        var raw = Encoding.Latin1.GetString(bytes);
        raw.ShouldStartWith("%PDF-");
        raw.TrimEnd().ShouldEndWith("%%EOF");

        // Pages are objects of type /Page; 40 messages and a summary run past one page.
        var pages = System.Text.RegularExpressions.Regex.Count(raw, @"/Type\s*/Page\b");
        pages.ShouldBeGreaterThan(1);
    }

    [AvaloniaFact]
    public void A_meeting_without_a_summary_or_messages_still_exports()
    {
        using var file = new MemoryStream();
        TranscriptPdf.Write(new MeetingTranscript { Title = "Vazia", App = "Zoom", StartedAt = DateTimeOffset.Now }, file);
        file.Length.ShouldBeGreaterThan(500);
    }

    [AvaloniaFact]
    public void Wrapping_keeps_every_word_and_respects_the_width()
    {
        using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, 10);
        var text = "Eu ligo para eles à tarde e peço o prazo por escrito; se atrasar de novo, abrimos cotação com outra empresa.";

        var lines = TranscriptPdf.Wrap(text, font, 120);

        lines.Count.ShouldBeGreaterThan(1);
        string.Join(' ', lines).ShouldBe(text);
    }
}
