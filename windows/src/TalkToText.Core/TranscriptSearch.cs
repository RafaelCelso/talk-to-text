using System.Globalization;

namespace TalkToText.Core;

/// <summary>
/// Finds meetings and recordings by what was said in them: every word of the query must appear
/// somewhere — title, names, messages or summary — ignoring case and accents, so "reuniao"
/// finds "Reunião".
/// </summary>
public static class TranscriptSearch
{
    private static readonly CompareInfo Compare = CultureInfo.GetCultureInfo("pt-BR").CompareInfo;
    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
    private const int SnippetLength = 90;

    /// <summary>Whether <paramref name="transcript"/> matches <paramref name="query"/>, and where.</summary>
    /// <returns>
    /// Null when it does not match. Otherwise the line to show under it: the first message that
    /// mentions the query, as "Name: …text…", or empty when only the title, names or summary did.
    /// </returns>
    public static string? Find(MeetingTranscript transcript, string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return string.Empty;

        var everything = string.Join('\n',
            new[] { transcript.Title, transcript.Summary ?? string.Empty }
                .Concat(transcript.Speakers.Select(s => s.Name))
                .Concat(transcript.Messages.Select(m => m.Text)));
        if (!words.All(w => Contains(everything, w))) return null;

        // The message that holds the most words; the earliest on a tie.
        MeetingMessage? best = null;
        var bestCount = 0;
        foreach (var message in transcript.Messages)
        {
            var count = words.Count(w => Contains(message.Text, w));
            if (count > bestCount)
            {
                best = message;
                bestCount = count;
                if (count == words.Length) break;
            }
        }

        if (best is null) return string.Empty;
        var name = transcript.Speaker(best.SpeakerId)?.Name ?? "?";
        return $"{name}: {Around(best.Text, words.First(w => Contains(best.Text, w)))}";
    }

    private static bool Contains(string text, string word) => Compare.IndexOf(text, word, Loose) >= 0;

    /// <summary>About <see cref="SnippetLength"/> characters of <paramref name="text"/> around <paramref name="word"/>.</summary>
    private static string Around(string text, string word)
    {
        if (text.Length <= SnippetLength) return text;

        var at = Math.Max(0, Compare.IndexOf(text, word, Loose));
        var start = Math.Max(0, at - SnippetLength / 3);
        if (start > 0)
        {
            // Start on a word.
            var space = text.IndexOf(' ', start);
            if (space >= 0 && space < at) start = space + 1;
        }

        var length = Math.Min(SnippetLength, text.Length - start);
        var end = start + length;
        if (end < text.Length)
        {
            var space = text.LastIndexOf(' ', end - 1, length);
            if (space > at) end = space;
        }

        return (start > 0 ? "…" : string.Empty) + text[start..end].Trim() + (end < text.Length ? "…" : string.Empty);
    }
}
