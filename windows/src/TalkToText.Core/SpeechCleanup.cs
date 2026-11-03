using System.Text.RegularExpressions;

namespace TalkToText.Core;

/// <summary>
/// Takes the hesitations out of dictated text, instantly and without the AI: "éé", "hã",
/// "hum", and a short word said twice in a row ("que que", "eu eu").
/// </summary>
/// <remarks>
/// <b>Never a bare "é".</b> It is the verb — "isso é bom" — so only drawn-out or breathy forms
/// count ("éé", "éh"). For the same reason only words of up to four letters are taken as
/// stutters when repeated: "muito muito" is more often emphasis than a slip.
/// </remarks>
public static partial class SpeechCleanup
{
    /// <summary><paramref name="text"/> without hesitations; unchanged when there were none.</summary>
    public static string Clean(string text)
    {
        var cleaned = Hesitation().Replace(text, string.Empty);
        cleaned = Repeated().Replace(cleaned, "$1");

        // Tidy what a removed word leaves behind: doubled spaces and commas, a comma opening
        // the text or sitting before a full stop.
        cleaned = Spaces().Replace(cleaned, " ");
        cleaned = DoubleComma().Replace(cleaned, ",");
        cleaned = SpaceBeforePunctuation().Replace(cleaned, "$1");
        cleaned = CommaBeforeStop().Replace(cleaned, "$1");
        cleaned = LeadingPunctuation().Replace(cleaned, string.Empty).Trim();

        if (!cleaned.Any(char.IsLetterOrDigit)) return text.Trim();

        // A sentence that started with the hesitation still starts with a capital.
        if (char.IsUpper(text.TrimStart()[0]) && char.IsLower(cleaned[0]))
        {
            cleaned = char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
        }

        return cleaned;
    }

    [GeneratedRegex(@"(?<![\p{L}\d])(?:é{2,}h*|éh+|eh+|ãh+|hã+|ã{2,}|ahn+|hum+|hm+|uhm+|uh+)(?![\p{L}\d])(?:\.{2,}|…)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Hesitation();

    [GeneratedRegex(@"(?<![\p{L}\d])(\p{L}{1,4})(?:,?\s+\1)+(?![\p{L}\d])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Repeated();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@",(\s*,)+")]
    private static partial Regex DoubleComma();

    [GeneratedRegex(@"\s+([,.;:!?…])")]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@",([.!?…])")]
    private static partial Regex CommaBeforeStop();

    [GeneratedRegex(@"^[\s,;:]+")]
    private static partial Regex LeadingPunctuation();
}
