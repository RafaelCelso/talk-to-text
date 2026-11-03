using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using TalkToText.Abstractions;

namespace TalkToText.Core;

/// <summary>One question and its answer, for follow-up questions.</summary>
/// <param name="Question">What the user asked.</param>
/// <param name="Answer">What the assistant said.</param>
public sealed record ChatTurn(string Question, string Answer);

/// <summary>What the search found, before the model starts reading.</summary>
/// <param name="Passages">Passages handed to the model.</param>
/// <param name="Transcripts">How many meetings and recordings they come from.</param>
public readonly record struct AssistantFound(int Passages, int Transcripts);

/// <summary>A stretch of one transcript that can be shown to the model.</summary>
/// <param name="Transcript">Where it comes from.</param>
/// <param name="Text">Header line plus the messages, or the transcript's summary.</param>
/// <param name="IsSummary">True for a saved summary rather than raw conversation.</param>
public sealed record TranscriptPassage(MeetingTranscript Transcript, string Text, bool IsSummary);

/// <summary>
/// Answers questions about the user's meetings and recordings with the local model.
/// </summary>
/// <remarks>
/// <para>
/// The model reads 8k tokens; the transcripts together are far more. So the question first
/// picks passages by the words it shares with them (accents and case ignored, common words
/// skipped, rare words worth more), saved summaries counting a little extra since they condense
/// a whole meeting. "Última", "recente", "hoje" and "ontem" favour the newest transcripts.
/// Only those passages go to the model, with their titles and dates, and the answer names them.
/// </para>
/// <para>
/// The prompt holds the model to the passages: a small model asked about a meeting it cannot
/// see will otherwise describe one that never happened.
/// </para>
/// <para>
/// It is still a chat. A greeting, a thank-you or a question about the assistant itself is
/// answered as such, with no transcript read — "Oi" used to get "Não encontrei isso nas
/// transcrições." A question whose words appear in no meeting gets no passages either, rather
/// than a prompt padded with unrelated ones; a short follow-up ("e quem ficou responsável?")
/// is looked up together with the question before it.
/// </para>
/// </remarks>
public sealed class TranscriptAssistant(ILanguageModel model)
{
    /// <summary>Tokens kept for the instructions, the question, earlier turns and the answer.</summary>
    private const int Reserve = 2200;

    /// <summary>Most transcript text handed to the model, in tokens: what keeps answers quick.</summary>
    private const int MaxPassageTokens = 2800;

    /// <summary>Longest answer, in tokens.</summary>
    public const int AnswerTokens = 700;

    /// <summary>Messages per passage: small enough to pick precisely, big enough to keep context.</summary>
    private const int MessagesPerPassage = 12;

    internal const string SystemPrompt =
        "Você é o assistente do Talk-to-Text, um app que transcreve ditados, reuniões e gravações no "
        + "computador do usuário. Converse de forma natural e simpática, em português do Brasil, como um "
        + "assistente de chat: responda a cumprimentos, agradecimentos, perguntas sobre você e assuntos "
        + "gerais normalmente e com brevidade. Quando a mensagem for sobre as reuniões ou gravações, use "
        + "somente os trechos fornecidos e diga de qual reunião ou gravação vem a informação; se a resposta "
        + "não estiver neles, diga que não encontrou isso nas transcrições e sugira citar a pessoa, o título "
        + "ou o assunto — não invente nomes, datas, números nem decisões. Use a conversa até aqui para "
        + "entender perguntas de acompanhamento. Seja direto; quando ajudar, use tópicos.";

    /// <summary>Openings that make a message small talk when it is short.</summary>
    private static readonly string[] Greetings =
    [
        "oi", "ola", "opa", "eai", "e ai", "bom dia", "boa tarde", "boa noite", "obrigado", "obrigada",
        "brigado", "valeu", "tudo bem", "tudo bom", "como vai", "tchau", "ate mais", "ate logo", "hello",
        "hi", "hey", "beleza", "blz", "ok", "certo", "legal", "show", "perfeito", "entendi", "otimo", "massa",
    ];

    /// <summary>Questions about the assistant itself, wherever they appear.</summary>
    private static readonly string[] AboutItself =
    [
        "quem e voce", "quem eh voce", "o que voce faz", "o que voce pode fazer", "o que voce sabe fazer",
        "como voce funciona", "como funciona voce", "voce consegue", "qual seu nome", "qual o seu nome",
        "como te chamo", "me ajuda", "pode me ajudar", "como usar", "como eu uso",
    ];

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "que", "qual", "quais", "com", "para", "por", "uma", "umas", "uns", "dos", "das", "nos",
        "nas", "foi", "foram", "ser", "sao", "esta", "este", "isso", "isto", "essa", "esse", "como",
        "onde", "quando", "quem", "mais", "menos", "muito", "sobre", "entre", "tem", "ter", "teve",
        "voce", "voces", "meu", "minha", "seu", "sua", "ele", "ela", "eles", "elas", "nao", "sim",
        "mas", "pela", "pelo", "aos", "num", "numa", "foi", "era", "estava", "falou", "disse",
        "reuniao", "reunioes", "gravacao", "gravacoes", "conversa", "transcricao", "the", "and",
        "resuma", "resumir", "resume", "fale", "diga", "conte", "explique", "liste", "mostre", "faca",
    };

    private static readonly string[] RecentWords = ["ultima", "ultimo", "recente", "hoje", "ontem", "agora"];

    /// <summary>The transcripts the last answer drew on, newest first.</summary>
    public IReadOnlyList<MeetingTranscript> Sources { get; private set; } = [];

    /// <summary>
    /// Answers <paramref name="question"/> from <paramref name="transcripts"/>, a piece at a time.
    /// </summary>
    /// <param name="question">What the user asked.</param>
    /// <param name="transcripts">The meetings and recordings to look in.</param>
    /// <param name="history">Earlier turns of this conversation, oldest first.</param>
    /// <param name="cancellationToken">Stops the answer.</param>
    /// <param name="found">Told what was found, as soon as it is — for a progress line.</param>
    public async IAsyncEnumerable<string> AskAsync(
        string question,
        IReadOnlyList<MeetingTranscript> transcripts,
        IReadOnlyList<ChatTurn> history,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        IProgress<AssistantFound>? found = null)
    {
        var earlier = History(history);
        // Capped well below what the model could take: a 3B model on a CPU reads about 40
        // tokens a second, and a full context made the first answers take three minutes.
        var budget = Math.Clamp(model.ContextTokens - Reserve - model.CountTokens(earlier) - model.CountTokens(question), 800, MaxPassageTokens);

        var smallTalk = IsSmallTalk(question);
        var lookup = question;
        if (!smallTalk && history.Count > 0 && Terms(question).Distinct().Count() < 3)
        {
            // "E quem ficou responsável?" is about whatever the previous question was about.
            lookup = $"{question} {history[^1].Question}";
        }

        var passages = smallTalk ? [] : Select(lookup, transcripts, budget);

        Sources = passages.Select(p => p.Transcript).Distinct().OrderByDescending(t => t.StartedAt).ToList();
        found?.Report(new AssistantFound(passages.Count, Sources.Count));

        var user = new StringBuilder();
        var meetings = transcripts.Count(t => t.SourceFile is null);
        var recordings = transcripts.Count - meetings;
        if (passages.Count > 0)
        {
            user.AppendLine("Trechos das reuniões e gravações:");
            foreach (var passage in passages) user.AppendLine().AppendLine(passage.Text);
        }
        else if (transcripts.Count == 0)
        {
            user.AppendLine("O usuário ainda não tem reuniões nem gravações transcritas.");
        }
        else if (smallTalk)
        {
            // Only what this tab holds: "0 gravações" on the Meetings tab read as if there were none at all.
            var holds = (meetings, recordings) switch
            {
                (> 0, > 0) => $"{meetings} reuniões e {recordings} gravações transcritas",
                (> 0, _) => $"{meetings} {(meetings == 1 ? "reunião transcrita" : "reuniões transcritas")}",
                _ => $"{recordings} {(recordings == 1 ? "gravação transcrita" : "gravações transcritas")}",
            };
            user.AppendLine(CultureInfo.InvariantCulture, $"(Mensagem de conversa: responda naturalmente. Aqui você pode consultar {holds} quando o usuário perguntar sobre elas.)");
        }
        else
        {
            user.AppendLine(CultureInfo.InvariantCulture, $"(Nenhum trecho das {transcripts.Count} reuniões e gravações transcritas menciona o que foi perguntado. Se for uma pergunta geral, responda normalmente; se for sobre as reuniões, diga que não encontrou.)");
        }

        if (earlier.Length > 0) user.AppendLine().AppendLine("Conversa até aqui:").AppendLine(earlier);
        user.AppendLine().Append(passages.Count > 0 ? "Pergunta: " : "Mensagem: ").AppendLine(question.Trim());

        await foreach (var piece in model.CompleteAsync(SystemPrompt, user.ToString(), AnswerTokens, cancellationToken).ConfigureAwait(false))
        {
            yield return piece;
        }
    }

    /// <summary>The best passages for <paramref name="question"/> that fit in <paramref name="budget"/> tokens.</summary>
    /// <remarks>
    /// Narrowed in steps. A name or title in the question ("…com a Melissa") picks the meetings
    /// that have it as a participant or in their title. "Última" then keeps only the newest of
    /// the meetings left that mention the rest of the question. Within what is left, passages
    /// sharing the most (and rarest) words with the question go first; when several meetings are
    /// left, passages sharing nothing are dropped rather than padding the prompt — every token
    /// read costs time on a CPU.
    /// </remarks>
    public List<TranscriptPassage> Select(string question, IReadOnlyList<MeetingTranscript> transcripts, int budget)
    {
        if (transcripts.Count == 0) return [];

        var terms = Terms(question).Distinct().ToList();
        var wantsRecent = terms.Any(t => RecentWords.Contains(t));
        var content = terms.Where(t => !RecentWords.Contains(t)).ToList();

        // Who was in it, and what it was called.
        var named = transcripts.ToDictionary(
            t => t,
            t =>
            {
                var names = Terms($"{t.Title} {string.Join(' ', t.Speakers.Select(s => s.Name))}").ToHashSet(StringComparer.Ordinal);
                return content.Count(names.Contains);
            });
        var bestName = named.Values.Max();
        var candidates = bestName > 0 ? transcripts.Where(t => named[t] == bestName).ToList() : transcripts.ToList();

        if (wantsRecent)
        {
            var mentioning = candidates.Where(t => Passages(t).Any(p => Terms(p.Text).Any(content.Contains))).ToList();
            var pool = mentioning.Count > 0 ? mentioning : candidates;
            candidates = [pool.MaxBy(t => t.StartedAt)!];
        }

        var passages = candidates.SelectMany(Passages).ToList();
        var documents = passages.Select(p => Terms(p.Text).ToList()).ToList();
        var newest = candidates.Max(t => t.StartedAt);

        // Rarer words say more about where the answer is.
        var weight = content.ToDictionary(
            t => t,
            t => Math.Log(1 + ((double)passages.Count / (1 + documents.Count(d => d.Contains(t))))));

        var scored = passages.Select((p, i) =>
        {
            var score = content.Sum(t => Math.Min(3, documents[i].Count(w => w == t)) * weight[t]);
            if (p.IsSummary) score = (score * 1.5) + 0.5;
            return (Passage: p, Score: score, Index: i);
        }).ToList();

        // Several meetings and the question names none, asks for nothing recent, and shares no
        // word with any of them: it is about something else, and padding the prompt with
        // unrelated passages would only cost time and invite a made-up answer.
        var nothingMatches = scored.All(x => x.Score <= 0 || (x.Passage.IsSummary && x.Score <= 0.5));
        if (candidates.Count > 1 && bestName == 0 && !wantsRecent && content.Count > 0 && nothingMatches) return [];

        // One meeting: all of it is relevant, best parts first. Several: only what matches.
        var keep = candidates.Count == 1 || scored.All(x => x.Score <= 0)
            ? scored
            : [.. scored.Where(x => x.Score > 0)];

        var chosen = new List<TranscriptPassage>();
        var used = 0;
        foreach (var (passage, _, _) in keep
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Passage.Transcript.StartedAt == newest)
            .ThenBy(x => x.Index))
        {
            var cost = model.CountTokens(passage.Text) + 2;
            if (used + cost > budget) continue;
            chosen.Add(passage);
            used += cost;
        }

        // Read in order: by meeting, then summary first, then the conversation as it went.
        return chosen
            .OrderByDescending(p => p.Transcript.StartedAt)
            .ThenBy(p => p.IsSummary ? 0 : 1)
            .ThenBy(p => passages.IndexOf(p))
            .ToList();
    }

    /// <summary>
    /// Whether <paramref name="message"/> is conversation rather than a question about the
    /// transcripts: a short greeting or thank-you, or a question about the assistant itself.
    /// </summary>
    public static bool IsSmallTalk(string message)
    {
        var plain = Plain(message);
        if (plain.Length == 0) return true;
        if (AboutItself.Any(phrase => $" {plain} ".Contains($" {phrase} ", StringComparison.Ordinal))) return true;

        var words = plain.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= 6 && Greetings.Any(g => plain == g || plain.StartsWith(g + " ", StringComparison.Ordinal));
    }

    /// <summary>Lower case, no accents, letters and single spaces only.</summary>
    private static string Plain(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            plain.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return string.Join(' ', plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The summary, if any, then the conversation in groups of messages.</summary>
    public static IEnumerable<TranscriptPassage> Passages(MeetingTranscript transcript)
    {
        var header = Header(transcript);

        if (transcript.Summary is { Length: > 0 } summary)
        {
            yield return new TranscriptPassage(transcript, $"### Resumo — {header}\n{summary.Trim()}", IsSummary: true);
        }

        for (var i = 0; i < transcript.Messages.Count; i += MessagesPerPassage)
        {
            var lines = transcript.Messages
                .Skip(i)
                .Take(MessagesPerPassage)
                .Select(m => $"[{transcript.TimeOf(m)}] {transcript.Speaker(m.SpeakerId)?.Name ?? "?"}: {m.Text}");
            yield return new TranscriptPassage(transcript, $"### {header}\n{string.Join('\n', lines)}", IsSummary: false);
        }
    }

    /// <summary>"Reunião "Daily" (Microsoft Teams, 02/11/2026 10:54)".</summary>
    public static string Header(MeetingTranscript transcript)
    {
        var kind = transcript.SourceFile is null ? "Reunião" : "Gravação";
        var when = transcript.StartedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        return $"{kind} \"{transcript.Title}\" ({transcript.App}, {when})";
    }

    /// <summary>Lower case, accents dropped, common and short words skipped.</summary>
    public static IEnumerable<string> Terms(string text)
    {
        var word = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToLowerInvariant(c));
                continue;
            }

            if (Accept(word)) yield return word.ToString();
            word.Clear();
        }

        if (Accept(word)) yield return word.ToString();
    }

    private static bool Accept(StringBuilder word) =>
        (word.Length >= 3 || (word.Length > 0 && char.IsDigit(word[0]))) && !Stopwords.Contains(word.ToString());

    /// <summary>The last few turns, answers shortened, so a follow-up has its context.</summary>
    private static string History(IReadOnlyList<ChatTurn> history) => string.Join(
        "\n",
        history.TakeLast(3).Select(t =>
            $"Usuário: {t.Question}\nAssistente: {(t.Answer.Length > 500 ? t.Answer[..500] + "…" : t.Answer)}"));
}
