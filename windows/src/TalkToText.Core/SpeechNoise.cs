namespace TalkToText.Core;

/// <summary>
/// Tells a real reply from what the recogniser makes of a cough, a click or a murmur.
/// </summary>
/// <remarks>
/// Parakeet v3 answers a fraction of a second of noise with a stock English word. A real
/// Teams call in Portuguese came back peppered with "Okay.", "Thank you.", "Think.", "Wish."
/// and "Mm-hmm." — each one a chat bubble, and each one a chance to start a phantom
/// participant. Only short utterances are judged: said at length, "okay" is a real word.
/// </remarks>
public static class SpeechNoise
{
    /// <summary>Utterances at least this long are always kept.</summary>
    public const double MaxNoiseSeconds = 1.5;

    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        // What the recogniser invents on noise.
        "okay", "ok", "thank you", "thanks", "yeah", "yes", "oh", "so", "you", "the", "i",
        "bye", "right", "well", "think", "wish", "product", "really", "t", "uh huh",
        // Murmurs, in either language.
        "mm", "mmm", "mm hmm", "mhm", "hmm", "hm", "uh", "ah", "eh",
        "uhum", "aham", "hum", "ahn", "ã", "hã",

        // Not "um" or "a": in Portuguese those are words.
    };

    /// <summary>Whether <paramref name="text"/>, heard in <paramref name="seconds"/>, is noise.</summary>
    public static bool IsNoise(string text, double seconds)
    {
        if (seconds >= MaxNoiseSeconds) return false;

        var words = text
            .ToLowerInvariant()
            .Split([' ', '-', '.', ',', '!', '?', '…', ';', ':'], StringSplitOptions.RemoveEmptyEntries);

        return words.Length == 0 || Fillers.Contains(string.Join(' ', words));
    }
}
