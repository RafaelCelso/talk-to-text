using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace TalkToText.Core;

/// <summary>
/// A meeting or recording as a document to keep or send: Word, Markdown or plain text. Each has
/// the title, when and how long, who took part, the summary when there is one, then everything
/// said with its time.
/// </summary>
public static partial class TranscriptExport
{
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>A file name for <paramref name="transcript"/>: its title and date, without characters Windows refuses.</summary>
    public static string FileName(MeetingTranscript transcript)
    {
        var title = string.Join(' ', string.Concat(transcript.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (title.Length == 0) title = "Transcrição";
        if (title.Length > 80) title = title[..80].Trim();
        return $"{title} {transcript.StartedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Markdown: the summary as it is, the conversation as a list.</summary>
    public static string ToMarkdown(MeetingTranscript transcript)
    {
        var md = new StringBuilder();
        md.Append("# ").AppendLine(transcript.Title).AppendLine();
        md.AppendLine(Details(transcript)).AppendLine();
        md.Append("**Participantes:** ").AppendLine(Participants(transcript)).AppendLine();

        if (!string.IsNullOrWhiteSpace(transcript.Summary))
        {
            md.AppendLine("## Resumo").AppendLine();
            md.AppendLine(Demoted(transcript.Summary.Trim())).AppendLine();
        }

        md.AppendLine("## Transcrição").AppendLine();
        foreach (var m in transcript.Messages)
        {
            md.Append("- **[").Append(transcript.TimeOf(m)).Append("] ").Append(transcript.Speaker(m.SpeakerId)?.Name ?? "?").Append(":** ").AppendLine(m.Text);
        }

        return md.ToString();
    }

    /// <summary>Plain text, readable anywhere.</summary>
    public static string ToText(MeetingTranscript transcript)
    {
        var text = new StringBuilder();
        text.AppendLine(transcript.Title).AppendLine(Details(transcript));
        text.Append("Participantes: ").AppendLine(Participants(transcript)).AppendLine();

        if (!string.IsNullOrWhiteSpace(transcript.Summary))
        {
            text.AppendLine("RESUMO").AppendLine();
            foreach (var line in transcript.Summary.Trim().Split('\n'))
            {
                var plain = Bold().Replace(line.TrimEnd(), "$1");
                text.AppendLine(plain.StartsWith('#') ? plain.TrimStart('#', ' ').ToUpper(Portuguese) : plain);
            }

            text.AppendLine();
        }

        text.AppendLine("TRANSCRIÇÃO").AppendLine();
        text.AppendLine(transcript.ToPlainText());
        return text.ToString();
    }

    /// <summary>A Word document, written as Office Open XML without a library.</summary>
    public static void WriteDocx(MeetingTranscript transcript, Stream output)
    {
        var body = new StringBuilder();
        body.Append(Paragraph(transcript.Title, size: 36, bold: true, after: 120));
        body.Append(Paragraph(Details(transcript), color: "666666", after: 60));
        body.Append(Paragraph($"**Participantes:** {Participants(transcript)}", after: 240));

        if (!string.IsNullOrWhiteSpace(transcript.Summary))
        {
            body.Append(Paragraph("Resumo", size: 28, bold: true, before: 240, after: 120));
            foreach (var raw in transcript.Summary.Trim().Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0) continue;
                if (line.StartsWith('#')) body.Append(Paragraph(line.TrimStart('#', ' '), size: 24, bold: true, before: 160, after: 60));
                else if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal) || line.TrimStart().StartsWith("* ", StringComparison.Ordinal))
                    body.Append(Paragraph("•  " + line.TrimStart()[2..], indent: 360, after: 40));
                else body.Append(Paragraph(line, after: 80));
            }
        }

        body.Append(Paragraph("Transcrição", size: 28, bold: true, before: 240, after: 120));
        foreach (var m in transcript.Messages)
        {
            body.Append(Paragraph($"**[{transcript.TimeOf(m)}] {transcript.Speaker(m.SpeakerId)?.Name ?? "?"}:** {m.Text}", after: 80));
        }

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Entry(zip, "[Content_Types].xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""");
        Entry(zip, "_rels/.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");
        Entry(zip, "word/document.xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>"""
            + body
            + """<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1418" w:right="1418" w:bottom="1418" w:left="1418" w:header="709" w:footer="709" w:gutter="0"/></w:sectPr></w:body></w:document>""");
    }

    private static string Details(MeetingTranscript t)
    {
        var length = (t.EndedAt ?? t.StartedAt) - t.StartedAt;
        var duration = length.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)length.TotalHours}h{length.Minutes:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)Math.Round(length.TotalMinutes))} min");
        var source = t.SourceFile is not null ? $"Arquivo {Path.GetFileName(t.SourceFile)}" : t.App;
        return $"{source} · {t.StartedAt.ToLocalTime().ToString("d 'de' MMMM 'de' yyyy, HH:mm", Portuguese)} · {duration}";
    }

    private static string Participants(MeetingTranscript t) =>
        string.Join(", ", t.Speakers.Where(s => t.Messages.Any(m => m.SpeakerId == s.Id)).Select(s => s.Name));

    /// <summary>The summary's headings one level down, under "## Resumo".</summary>
    private static string Demoted(string markdown) =>
        string.Join('\n', markdown.Split('\n').Select(l => l.StartsWith('#') ? "##" + l : l));

    /// <summary>One paragraph; <c>**bold**</c> spans become bold runs.</summary>
    private static string Paragraph(string text, int size = 22, bool bold = false, string? color = null, int before = 0, int after = 0, int indent = 0)
    {
        var p = new StringBuilder("<w:p><w:pPr>");
        p.Append(CultureInfo.InvariantCulture, $"<w:spacing w:before=\"{before}\" w:after=\"{after}\"/>");
        if (indent > 0) p.Append(CultureInfo.InvariantCulture, $"<w:ind w:left=\"{indent}\"/>");
        p.Append("</w:pPr>");

        var parts = text.Split("**");
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            p.Append("<w:r><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\"/>");
            if (bold || i % 2 == 1) p.Append("<w:b/>");
            if (color is not null) p.Append(CultureInfo.InvariantCulture, $"<w:color w:val=\"{color}\"/>");
            p.Append(CultureInfo.InvariantCulture, $"<w:sz w:val=\"{size}\"/></w:rPr><w:t xml:space=\"preserve\">");
            p.Append(SecurityElement.Escape(parts[i]));
            p.Append("</w:t></w:r>");
        }

        return p.Append("</w:p>").ToString();
    }

    private static void Entry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();
}
