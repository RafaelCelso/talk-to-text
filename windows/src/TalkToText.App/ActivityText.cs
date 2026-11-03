using System.Globalization;
using TalkToText.Core;

namespace TalkToText.App;

/// <summary>
/// Says what a dictation is doing, in Portuguese — "Traduzindo para o inglês… 45%" rather than
/// a bare "Transcrevendo…" for the whole of a command that takes several seconds.
/// </summary>
public static class ActivityText
{
    /// <summary>Longest instruction quoted in full; the pill is small.</summary>
    private const int InstructionRoom = 28;

    private static readonly Dictionary<string, string> Portuguese = new(StringComparer.Ordinal)
    {
        ["English"] = "inglês",
        ["Spanish"] = "espanhol",
        ["French"] = "francês",
        ["German"] = "alemão",
        ["Italian"] = "italiano",
        ["Portuguese"] = "português",
        ["Japanese"] = "japonês",
        ["Chinese"] = "chinês",
        ["Korean"] = "coreano",
        ["Russian"] = "russo",
        ["Arabic"] = "árabe",
        ["Dutch"] = "holandês",
    };

    /// <summary>The line to show for <paramref name="activity"/>; null reads as plain transcription.</summary>
    public static string Describe(DictationActivity? activity) => activity?.Step switch
    {
        null or DictationStep.Transcribing => "Transcrevendo…",
        DictationStep.LoadingSpeechModel => "Carregando o modelo de voz…",
        DictationStep.ReadingSelection => "Lendo o texto selecionado…",
        DictationStep.LoadingAi => "Preparando a IA…",
        DictationStep.Typing => "Inserindo o texto…",
        _ => Working(activity) + (activity.Progress is { } p ? string.Create(CultureInfo.InvariantCulture, $"… {p * 100:0}%") : "…"),
    };

    /// <summary>What the AI is doing, without the ellipsis.</summary>
    private static string Working(DictationActivity activity)
    {
        if (activity.Polishing || activity.Instruction is not { } instruction) return "Revisando o texto";

        if (activity.HasSelection && CommandPrompt.TranslationTarget(instruction) is { } language)
        {
            return $"Traduzindo para o {Portuguese.GetValueOrDefault(language, language)}";
        }

        return $"{(activity.HasSelection ? "Aplicando" : "Executando")} “{Quoted(instruction)}”";
    }

    /// <summary>"Deixa mais formal." → "deixa mais formal", cut short when long.</summary>
    private static string Quoted(string instruction)
    {
        var text = instruction.Trim().TrimEnd('.', '!', '?', ',', ';', ':');
        if (text.Length > 1 && !char.IsUpper(text[1])) text = char.ToLower(text[0], CultureInfo.CurrentCulture) + text[1..];
        return text.Length <= InstructionRoom ? text : text[..(InstructionRoom - 1)].TrimEnd() + "…";
    }
}
