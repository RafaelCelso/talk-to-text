using System.Globalization;
using Avalonia.Media;
using Avalonia.Platform;
using SkiaSharp;
using TalkToText.App.Design;
using TalkToText.Core;

namespace TalkToText.App.Views;

/// <summary>
/// A meeting or recording as a PDF that looks like the app: the dark shell, Inter, the glass
/// summary card with its spectrum-coloured topics, and the conversation as chat bubbles — the
/// user's in blue on the right, everyone else's on the left beside their initials.
/// </summary>
/// <remarks>
/// <para>
/// Drawn with SkiaSharp, which Avalonia itself paints with, into Skia's PDF backend: text stays
/// text (selectable, searchable) and shapes stay vectors, rather than a screenshot of the
/// window cut into pages.
/// </para>
/// <para>
/// Sizes are the app's own tokens, in its pixels, scaled to points (96 → 72 per inch), so the
/// page reads at the size the window does. Every block — the header, a summary card, a bubble —
/// is laid out first and then placed on pages whole; a summary longer than a page continues
/// in a second card, a very long message in a second bubble.
/// </para>
/// </remarks>
public static class TranscriptPdf
{
    private const float PageWidth = 595;   // A4, points
    private const float PageHeight = 842;
    private const float Margin = 36;
    private const float FooterHeight = 26;
    private const float ContentWidth = PageWidth - (2 * Margin);
    private const float ContentBottom = PageHeight - Margin - FooterHeight;

    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>App pixels to PDF points.</summary>
    private static float S(double pixels) => (float)(pixels * 0.75);

    /// <summary>Writes <paramref name="transcript"/> as a PDF to <paramref name="output"/>.</summary>
    public static void Write(MeetingTranscript transcript, Stream output)
    {
        using var fonts = new Fonts();
        var blocks = Layout(transcript, fonts);

        // Place whole blocks on pages.
        var pages = new List<List<(Block Block, float Y)>> { new() };
        var y = Margin;
        foreach (var block in blocks)
        {
            if (y + block.Height > ContentBottom && pages[^1].Count > 0)
            {
                pages.Add([]);
                y = Margin;
            }

            pages[^1].Add((block, y));
            y += block.Height + block.After;
        }

        using var buffer = new MemoryStream();
        using (var document = SKDocument.CreatePdf(buffer, new SKDocumentPdfMetadata
        {
            Title = transcript.Title,
            Creator = "Talk-to-Text",
            Producer = "Talk-to-Text",
            Creation = DateTime.Now,
            Modified = DateTime.Now,
            RasterDpi = 144,
        }))
        {
            for (var i = 0; i < pages.Count; i++)
            {
                var canvas = document.BeginPage(PageWidth, PageHeight);
                using (var background = new SKPaint { Color = Sk(Tokens.Colors.Surface) })
                {
                    canvas.DrawRect(0, 0, PageWidth, PageHeight, background);
                }

                foreach (var (block, top) in pages[i]) block.Draw(canvas, top);
                Footer(canvas, fonts, i + 1, pages.Count);
                document.EndPage();
            }

            document.Close();
        }

        buffer.Position = 0;
        buffer.CopyTo(output);
    }

    private sealed record Block(float Height, float After, Action<SKCanvas, float> Draw);

    private static List<Block> Layout(MeetingTranscript transcript, Fonts fonts)
    {
        var blocks = new List<Block> { Header(transcript, fonts) };

        if (!string.IsNullOrWhiteSpace(transcript.Summary)) blocks.AddRange(SummaryCards(transcript, fonts));

        if (transcript.Messages.Count > 0)
        {
            var label = fonts.Font(fonts.SemiBold, Tokens.Fonts.Heading);
            blocks.Add(new Block(S(22), S(10), (c, y) => Text(c, "Conversa", label, Margin, y + S(16), Tokens.Colors.Foreground)));
        }

        MeetingMessage? previous = null;
        foreach (var message in transcript.Messages)
        {
            var showHeader = previous is null
                || previous.SpeakerId != message.SpeakerId
                || message.At - previous.At > TimeSpan.FromMinutes(2);
            blocks.AddRange(Bubbles(transcript, message, showHeader, fonts));
            previous = message;
        }

        return blocks;
    }

    /// <summary>The brand line, the title, when and how long, and who took part.</summary>
    private static Block Header(MeetingTranscript t, Fonts fonts)
    {
        var brand = fonts.Font(fonts.SemiBold, Tokens.Fonts.Label);
        var title = fonts.Font(fonts.SemiBold, 24);
        var detail = fonts.Font(fonts.Regular, Tokens.Fonts.Label);

        var titleLines = Wrap(t.Title, title, ContentWidth);
        var length = (t.EndedAt ?? t.StartedAt) - t.StartedAt;
        var duration = length.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)length.TotalHours}h{length.Minutes:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)Math.Round(length.TotalMinutes))} min");
        var kind = t.SourceFile is not null
            ? $"Gravação · {Path.GetFileName(t.SourceFile)}"
            : string.IsNullOrWhiteSpace(t.App) || t.App == "Reunião" ? "Reunião" : $"Reunião · {t.App}";
        var when = t.StartedAt.ToLocalTime().ToString("dddd, d 'de' MMMM 'de' yyyy, HH:mm", Portuguese);
        var people = string.Join(", ", t.Speakers.Where(s => t.Messages.Any(m => m.SpeakerId == s.Id)).Select(s => s.Name));
        var details = Wrap($"{kind} · {when} · {duration}", detail, ContentWidth);
        var participants = people.Length > 0 ? Wrap($"Participantes: {people}", detail, ContentWidth) : [];

        var titleLine = S(30);
        var detailLine = S(18);
        var height = S(22) + S(14) + (titleLines.Count * titleLine) + S(6) + ((details.Count + participants.Count) * detailLine) + S(6);

        return new Block(height, S(18), (c, y) =>
        {
            // The app icon and name, as in the sidebar.
            if (fonts.Icon is { } icon) c.DrawBitmap(icon, new SKRect(Margin, y, Margin + S(22), y + S(22)));
            Text(c, "Talk-to-Text", brand, Margin + S(30), y + S(15), Tokens.Colors.Foreground);

            var line = y + S(22) + S(14);
            foreach (var text in titleLines)
            {
                Text(c, text, title, Margin, line + S(24), Tokens.Colors.Foreground);
                line += titleLine;
            }

            line += S(6);
            foreach (var text in details.Concat(participants))
            {
                Text(c, text, detail, Margin, line + S(13), Tokens.Colors.Muted);
                line += detailLine;
            }

            using var rule = new SKPaint { IsAntialias = true, Shader = HorizontalFade(Margin, Margin + ContentWidth, 0.16f) };
            c.DrawRect(Margin, y + height - 1, ContentWidth, 0.75f, rule);
        });
    }

    /// <summary>The summary in the app's glass card, split into more cards when it runs past a page.</summary>
    private static IEnumerable<Block> SummaryCards(MeetingTranscript t, Fonts fonts)
    {
        var padX = S(22);
        var padY = S(20);
        var inner = ContentWidth - (2 * padX);
        var heading = fonts.Font(fonts.Bold, Tokens.Fonts.Body);
        var body = fonts.Font(fonts.Regular, Tokens.Fonts.Body);
        var title = fonts.Font(fonts.SemiBold, Tokens.Fonts.Heading);
        var caption = fonts.Font(fonts.Regular, Tokens.Fonts.Caption);
        var lineHeight = S(19);

        // Lines of the body, each one drawn on its own: a heading, a bullet line or a paragraph line.
        var rows = new List<(float Height, Action<SKCanvas, float, float> Draw)>();
        var lines = t.Summary!.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')
            .Select(l => l.Trim().Replace("**", string.Empty, StringComparison.Ordinal))
            .Where(l => l.Length > 0)
            .ToList();
        var sections = Math.Max(1, lines.Count(l => l.StartsWith('#')));
        var section = -1;
        var accent = Tokens.Colors.Spectrum[0];

        foreach (var line in lines)
        {
            if (line.StartsWith('#'))
            {
                section++;
                accent = Tokens.Colors.SpectrumAt(sections == 1 ? 0 : (double)section / (sections - 1));
                var text = line.TrimStart('#').Trim().ToUpper(Portuguese);
                var gap = rows.Count == 0 ? 0 : S(12);
                rows.Add((gap + S(20), (c, x, y) => Spaced(c, text, heading, x, y + gap + S(14), Colors.White, S(0.8))));
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                var dot = accent;
                var wrapped = Wrap(line[2..].Trim(), body, inner - S(14));
                for (var i = 0; i < wrapped.Count; i++)
                {
                    var first = i == 0;
                    var text = wrapped[i];
                    rows.Add((lineHeight, (c, x, y) =>
                    {
                        if (first)
                        {
                            using var glow = new SKPaint { IsAntialias = true, Color = Sk(dot, 0.45), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, S(3)) };
                            c.DrawCircle(x + S(4.5), y + S(9.5), S(3), glow);
                            using var fill = new SKPaint { IsAntialias = true, Color = Sk(dot) };
                            c.DrawCircle(x + S(4.5), y + S(9.5), S(2.5), fill);
                        }

                        Text(c, text, body, x + S(14), y + S(14), Tokens.Colors.Foreground);
                    }));
                }
            }
            else
            {
                foreach (var text in Wrap(line, body, inner))
                {
                    rows.Add((lineHeight, (c, x, y) => Text(c, text, body, x, y + S(14), Tokens.Colors.Foreground)));
                }
            }
        }

        var headerHeight = S(32) + S(16) + 1 + S(14);
        var room = ContentBottom - Margin - (2 * padY);
        var start = 0;
        var part = 0;
        while (start < rows.Count || part == 0)
        {
            var available = room - (part == 0 ? headerHeight : 0);
            var count = 0;
            var used = 0f;
            while (start + count < rows.Count && (used + rows[start + count].Height <= available || count == 0))
            {
                used += rows[start + count].Height;
                count++;
            }

            var segment = rows.GetRange(start, count);
            var withHeader = part == 0;
            var height = (2 * padY) + (withHeader ? headerHeight : 0) + used;
            var subtitle = t.SummarizedAt is { } at
                ? $"Gerado no seu computador · {at.ToLocalTime().ToString("d 'de' MMM, HH:mm", Portuguese)}"
                : "Gerado no seu computador";
            var name = t.SourceFile is null ? "Resumo da reunião" : "Resumo da gravação";

            yield return new Block(height, S(18), (c, y) =>
            {
                GlassCard(c, new SKRect(Margin, y, Margin + ContentWidth, y + height));
                var x = Margin + padX;
                var line = y + padY;

                if (withHeader)
                {
                    SparkleChip(c, x, line);
                    Text(c, name, title, x + S(32) + S(12), line + S(14), Tokens.Colors.Foreground);
                    Text(c, subtitle, caption, x + S(32) + S(12), line + S(29), Tokens.Colors.Muted);
                    line += S(32) + S(16);
                    using var rule = new SKPaint { IsAntialias = true, Shader = HorizontalFade(x, x + inner, 0.16f) };
                    c.DrawRect(x, line, inner, 0.75f, rule);
                    line += 1 + S(14);
                }

                foreach (var (rowHeight, draw) in segment)
                {
                    draw(c, x, line);
                    line += rowHeight;
                }
            });

            start += count;
            part++;
            if (count == 0) break;
        }
    }

    /// <summary>One message as the app shows it, in more than one bubble if it is longer than a page.</summary>
    private static IEnumerable<Block> Bubbles(MeetingTranscript t, MeetingMessage message, bool showHeader, Fonts fonts)
    {
        var speaker = t.Speaker(message.SpeakerId) ?? new MeetingSpeaker { Name = "?" };
        var mine = speaker.IsLocal;
        var color = ChatView.ColorFor(speaker);

        var body = fonts.Font(fonts.Regular, Tokens.Fonts.Body);
        var nameFont = fonts.Font(fonts.SemiBold, Tokens.Fonts.Label);
        var timeFont = fonts.Font(fonts.Regular, Tokens.Fonts.Caption);

        var avatar = S(32);
        var gap = S(8);
        var padX = S(12);
        var padY = S(8);
        var lineHeight = S(18);
        var maxBubble = Math.Min(S(520), ContentWidth - avatar - gap);
        var lines = Wrap(message.Text, body, maxBubble - (2 * padX));
        if (lines.Count == 0) lines.Add(string.Empty);

        var perBubble = (int)((ContentBottom - Margin - S(40)) / lineHeight);
        var time = t.TimeOf(message);

        for (var start = 0; start < lines.Count; start += perBubble)
        {
            var chunk = lines.GetRange(start, Math.Min(perBubble, lines.Count - start));
            var header = showHeader && start == 0;
            var headerHeight = header ? S(8) + S(18) : 0;
            var textWidth = chunk.Max(l => Width(body, l));
            var bubbleWidth = Math.Min(maxBubble, textWidth + (2 * padX));
            var bubbleHeight = (chunk.Count * lineHeight) + (2 * padY);
            var height = headerHeight + bubbleHeight;

            yield return new Block(height, S(4), (c, y) =>
            {
                var left = mine ? Margin + ContentWidth - bubbleWidth : Margin + avatar + gap;
                if (header)
                {
                    var nameWidth = Width(nameFont, speaker.Name);
                    var timeWidth = Width(timeFont, time);
                    var nameX = mine ? Margin + ContentWidth - nameWidth - S(8) - timeWidth : left;
                    Text(c, speaker.Name, nameFont, nameX, y + S(8) + S(13), color);
                    Text(c, time, timeFont, nameX + nameWidth + S(8), y + S(8) + S(13), Tokens.Colors.Muted);
                }

                var top = y + headerHeight;
                var rect = new SKRect(left, top, left + bubbleWidth, top + bubbleHeight);

                // The corner by the name is tucked in, as in the app.
                var tucked = S(header ? Tokens.Radius.Small : Tokens.Radius.Shell);
                var round = S(Tokens.Radius.Shell);
                var radii = mine
                    ? new[] { new SKPoint(round, round), new SKPoint(tucked, tucked), new SKPoint(round, round), new SKPoint(round, round) }
                    : new[] { new SKPoint(tucked, tucked), new SKPoint(round, round), new SKPoint(round, round), new SKPoint(round, round) };
                using var shape = new SKRoundRect();
                shape.SetRectRadii(rect, radii);

                using (var fill = new SKPaint { IsAntialias = true })
                {
                    fill.Shader = mine
                        ? SKShader.CreateLinearGradient(new SKPoint(0, rect.Top), new SKPoint(0, rect.Bottom), [Sk(Tokens.Colors.PrimaryTop), Sk(Tokens.Colors.PrimaryBottom)], SKShaderTileMode.Clamp)
                        : SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Bottom), [White(0.12), White(0.065), White(0.04)], [0, 0.5f, 1], SKShaderTileMode.Clamp);
                    c.DrawRoundRect(shape, fill);
                }

                using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.75f })
                {
                    rim.Shader = SKShader.CreateLinearGradient(
                        new SKPoint(0, rect.Top), new SKPoint(0, rect.Bottom),
                        mine ? [White(0.4), White(0.08), White(0)] : [White(0.16), White(0.06), White(0.04)],
                        [0, 0.5f, 1], SKShaderTileMode.Clamp);
                    c.DrawRoundRect(shape, rim);
                }

                var line = top + padY;
                foreach (var text in chunk)
                {
                    Text(c, text, body, left + padX, line + S(13.5), mine ? Tokens.Colors.OnPrimary : Tokens.Colors.Foreground);
                    line += lineHeight;
                }

                if (header && !mine)
                {
                    // Initials in a tinted circle beside the first bubble, level with its bottom.
                    var cx = Margin + (avatar / 2);
                    var cy = rect.Bottom - (avatar / 2);
                    using var disc = new SKPaint { IsAntialias = true, Color = Sk(color, 0.22) };
                    c.DrawCircle(cx, cy, avatar / 2, disc);
                    var initials = ChatView.Initials(speaker.Name);
                    var font = fonts.Font(fonts.SemiBold, Tokens.Fonts.Label);
                    Text(c, initials, font, cx - (Width(font, initials) / 2), cy + S(4.5), color);
                }
            });
        }
    }

    private static void Footer(SKCanvas c, Fonts fonts, int page, int pages)
    {
        var font = fonts.Font(fonts.Regular, Tokens.Fonts.Caption);
        var y = PageHeight - Margin + S(4);
        using (var rule = new SKPaint { IsAntialias = true, Shader = HorizontalFade(Margin, Margin + ContentWidth, 0.1f) })
        {
            c.DrawRect(Margin, y - S(16), ContentWidth, 0.75f, rule);
        }

        Text(c, $"Talk-to-Text · exportado em {DateTime.Now.ToString("d 'de' MMMM 'de' yyyy", Portuguese)}", font, Margin, y, Tokens.Colors.Muted);
        var number = $"Página {page} de {pages}";
        Text(c, number, font, Margin + ContentWidth - Width(font, number), y, Tokens.Colors.Muted);
    }

    /// <summary>The summary card's frosted glass: a dark tint, a faint frost, a diagonal sheen and a rim lit from the top.</summary>
    private static void GlassCard(SKCanvas c, SKRect rect)
    {
        var radius = S(14);
        using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 120), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, S(14)) })
        {
            c.DrawRoundRect(new SKRect(rect.Left + S(8), rect.Top + S(14), rect.Right - S(8), rect.Bottom + S(6)), radius, radius, shadow);
        }

        using (var tint = new SKPaint { IsAntialias = true, Color = Sk(Tokens.Colors.Overlay, 0.72) })
        {
            c.DrawRoundRect(rect, radius, radius, tint);
        }

        using (var frost = new SKPaint { IsAntialias = true, Color = White(0.03) })
        {
            c.DrawRoundRect(rect, radius, radius, frost);
        }

        using (var sheen = new SKPaint { IsAntialias = true })
        {
            sheen.Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Bottom), [White(0.09), White(0.025), White(0)], [0, 0.45f, 1], SKShaderTileMode.Clamp);
            c.DrawRoundRect(rect, radius, radius, sheen);
        }

        using var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.75f };
        rim.Shader = SKShader.CreateLinearGradient(new SKPoint(0, rect.Top), new SKPoint(0, rect.Bottom), [White(0.32), White(0.08), White(0.04)], [0, 0.45f, 1], SKShaderTileMode.Clamp);
        c.DrawRoundRect(rect, radius, radius, rim);
    }

    /// <summary>The summary header's icon chip: blue to violet glass with a white sparkle.</summary>
    private static void SparkleChip(SKCanvas c, float x, float y)
    {
        var size = S(32);
        var rect = new SKRect(x, y, x + size, y + size);
        using (var fill = new SKPaint { IsAntialias = true })
        {
            fill.Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Bottom),
                [Sk(Tokens.Colors.Spectrum[1], 0.55), Sk(Tokens.Colors.Spectrum[3], 0.4)], SKShaderTileMode.Clamp);
            c.DrawRoundRect(rect, S(10), S(10), fill);
        }

        using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.75f })
        {
            rim.Shader = SKShader.CreateLinearGradient(new SKPoint(0, rect.Top), new SKPoint(0, rect.Bottom), [White(0.35), White(0.05)], SKShaderTileMode.Clamp);
            c.DrawRoundRect(rect, S(10), S(10), rim);
        }

        // The Sparkles icon's path, in a 24-unit box, centred in the chip.
        using var path = SKPath.ParseSvgPathData(Controls.Icons.Sparkles);
        var scale = S(18) / 24f;
        c.Save();
        c.Translate(x + ((size - S(18)) / 2), y + ((size - S(18)) / 2));
        c.Scale(scale);
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = SKColors.White };
        c.DrawPath(path, stroke);
        c.Restore();
    }

    private static SKShader HorizontalFade(float left, float right, float alpha) =>
        SKShader.CreateLinearGradient(new SKPoint(left, 0), new SKPoint(right, 0),
            [White(0), White(alpha), White(alpha), White(0)], [0, 0.2f, 0.8f, 1], SKShaderTileMode.Clamp);

    private static void Text(SKCanvas c, string text, SKFont font, float x, float baseline, Color color)
    {
        using var paint = new SKPaint { IsAntialias = true, Color = Sk(color) };
        c.DrawText(text, x, baseline, font, paint);
    }

    /// <summary>Text with extra space between letters, like the summary's headings.</summary>
    private static void Spaced(SKCanvas c, string text, SKFont font, float x, float baseline, Color color, float spacing)
    {
        using var paint = new SKPaint { IsAntialias = true, Color = Sk(color) };
        foreach (var ch in text)
        {
            var s = ch.ToString();
            c.DrawText(s, x, baseline, font, paint);
            x += Width(font, s) + spacing;
        }
    }

    /// <summary>Greedy word wrap at <paramref name="width"/>; a word longer than a line is cut.</summary>
    public static List<string> Wrap(string text, SKFont font, float width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : $"{line} {word}";
                if (Width(font, candidate) <= width)
                {
                    line = candidate;
                    continue;
                }

                if (line.Length > 0) lines.Add(line);
                line = word;
                while (Width(font, line) > width && line.Length > 1)
                {
                    var fit = line.Length - 1;
                    while (fit > 1 && Width(font, line[..fit]) > width) fit--;
                    lines.Add(line[..fit]);
                    line = line[fit..];
                }
            }

            if (line.Length > 0 || paragraph.Length == 0) lines.Add(line);
        }

        return lines;
    }

    /// <summary>How wide <paramref name="text"/> is in <paramref name="font"/>, in points.</summary>
    /// <remarks>Through a paint: SkiaSharp 2.88's <c>SKFont</c> measures glyph ids, not text.</remarks>
    private static float Width(SKFont font, string text)
    {
        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size, SubpixelText = true };
        return paint.MeasureText(text);
    }

    private static SKColor Sk(Color color, double opacity = 1) =>
        new(color.R, color.G, color.B, (byte)Math.Round(color.A * opacity));

    private static SKColor White(double opacity) => new(255, 255, 255, (byte)Math.Round(255 * opacity));

    /// <summary>Inter, as the app bundles it, and the app icon.</summary>
    private sealed class Fonts : IDisposable
    {
        private readonly List<SKFont> _made = [];

        public Fonts()
        {
            Regular = Load("Inter-Regular.ttf");
            SemiBold = Load("Inter-SemiBold.ttf");
            Bold = Load("Inter-Bold.ttf");
            try
            {
                using var stream = AssetLoader.Open(new Uri("avares://TalkToText/Assets/app-icon.png"));
                Icon = SKBitmap.Decode(stream);
            }
            catch (Exception e) when (e is FileNotFoundException or InvalidOperationException or IOException)
            {
                Icon = null;
            }
        }

        public SKTypeface Regular { get; }

        public SKTypeface SemiBold { get; }

        public SKTypeface Bold { get; }

        public SKBitmap? Icon { get; }

        /// <summary>A font at the app's <paramref name="pixels"/> size, in points.</summary>
        public SKFont Font(SKTypeface face, double pixels)
        {
            var font = new SKFont(face, S(pixels)) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
            _made.Add(font);
            return font;
        }

        private static SKTypeface Load(string file)
        {
            try
            {
                using var stream = AssetLoader.Open(new Uri($"avares://Avalonia.Fonts.Inter/Assets/{file}"));
                return SKTypeface.FromStream(stream) ?? SKTypeface.Default;
            }
            catch (Exception e) when (e is FileNotFoundException or InvalidOperationException or IOException)
            {
                return SKTypeface.Default;
            }
        }

        public void Dispose()
        {
            foreach (var font in _made) font.Dispose();
            Regular.Dispose();
            SemiBold.Dispose();
            Bold.Dispose();
            Icon?.Dispose();
        }
    }
}
