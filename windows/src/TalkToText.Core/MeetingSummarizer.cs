using System.Runtime.CompilerServices;
using System.Text;
using TalkToText.Abstractions;

namespace TalkToText.Core;

/// <summary>What the summarizer is doing, for a progress line.</summary>
/// <param name="Step">1-based step.</param>
/// <param name="Steps">Total steps: one per chunk of a long meeting, plus the final summary.</param>
/// <param name="Writing">True once the final summary itself is being written.</param>
public readonly record struct SummaryProgress(int Step, int Steps, bool Writing);

/// <summary>
/// Summarises a meeting transcript with a local language model — no API key, no network.
/// </summary>
/// <remarks>
/// <para>
/// <b>Long meetings are summarised in two passes.</b> A 3B model on a CPU gets an 8k-token
/// context; an hour of talk is several times that. Chunks of the conversation are first
/// condensed into notes, then the notes into the summary — "map-reduce". A short meeting goes
/// straight to the final pass.
/// </para>
/// <para>
/// The prompt insists on using only what is in the transcript: small models fill gaps with
/// plausible inventions, and a meeting summary that invents a decision is worse than none.
/// </para>
/// <para>
/// <b>It also insists on specifics.</b> A real 30-minute Teams call about a refund process
/// came back as "the meeting discussed the flows" and "Nada foi combinado." over a call full
/// of agreements — who would send which report, which client to start with, what the new
/// system must allow. The prompt now names what counts as an agreement, asks for the names,
/// systems, companies and numbers as said, and the answer and notes got room to hold them.
/// </para>
/// </remarks>
public sealed class MeetingSummarizer
{
    /// <summary>Tokens kept back for the instructions and the quoted number lines.</summary>
    private const int Instructions = 1500;

    /// <summary>A model with at least this much context is a cloud one, with room to spare.</summary>
    private const int LargeContext = 16_000;

    internal const string SystemPrompt =
        "Você resume conversas — reuniões e gravações — em português do Brasil. Use apenas o que "
        + "está na transcrição: não invente nomes, datas, números nem decisões, e não torne nada "
        + "mais preciso do que foi dito — \"hoje à tarde\" não vira um horário. Se algo não foi "
        + "dito, não escreva. Seja específico: cite as pessoas, empresas, clientes, sistemas, "
        + "produtos, quantidades e prazos com os nomes e números que foram ditos. Nunca escreva "
        + "frases vagas como \"foi discutido o processo\" — diga o que foi dito sobre ele. A "
        + "transcrição vem de reconhecimento de voz: nomes próprios podem vir escritos de jeitos "
        + "diferentes, e às vezes a fala de uma pessoa aparece com o nome de outra.";

    /// <summary>Longest summary, in tokens: a local 3B model writes ~9 tokens a second.</summary>
    public static int SummaryTokens(ILanguageModel model) => model.ContextTokens >= LargeContext ? 4000 : 1100;

    /// <summary>Longest set of notes per chunk, in tokens.</summary>
    private static int NoteTokens(ILanguageModel model) => model.ContextTokens >= LargeContext ? 1500 : 550;

    private readonly ILanguageModel _model;
    private readonly string _system;

    /// <summary>Uses <paramref name="model"/> for every pass.</summary>
    /// <param name="model">The language model.</param>
    /// <param name="instruction">
    /// The user's own context for the AI — who they are, what the recordings are about, what
    /// to look for — from Settings. Followed in every pass, but never above the rule against
    /// inventing.
    /// </param>
    public MeetingSummarizer(ILanguageModel model, string? instruction = null)
    {
        _model = model;
        _system = string.IsNullOrWhiteSpace(instruction)
            ? SystemPrompt
            : $"""
              {SystemPrompt}

              Contexto e instruções do usuário — siga-os ao interpretar e resumir a conversa, sem deixar de usar só o que foi dito:
              {instruction.Trim()}
              """;
    }

    /// <summary>
    /// Writes the summary of <paramref name="meeting"/>, yielding it as it is generated so the
    /// user can watch it appear.
    /// </summary>
    public async IAsyncEnumerable<string> SummarizeAsync(
        MeetingTranscript meeting,
        IProgress<SummaryProgress>? progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var lines = Lines(meeting);
        // A long instruction from the user takes room from the transcript, not from the answer.
        var reserve = Instructions + Math.Max(SummaryTokens(_model), NoteTokens(_model));
        var budget = Math.Max(1000, _model.ContextTokens - reserve - _model.CountTokens(_system));
        var chunks = Chunk(lines, budget);
        var steps = chunks.Count == 1 ? 1 : chunks.Count + 1;

        string material;
        if (chunks.Count == 1)
        {
            material = chunks[0];
        }
        else
        {
            var notes = new StringBuilder();
            for (var i = 0; i < chunks.Count; i++)
            {
                progress?.Report(new SummaryProgress(i + 1, steps, Writing: false));
                var note = new StringBuilder();
                await foreach (var piece in _model.CompleteAsync(_system, NotesRequest(chunks[i], i + 1, chunks.Count), NoteTokens(_model), cancellationToken).ConfigureAwait(false))
                {
                    note.Append(piece);
                }

                notes.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"### Parte {i + 1}").AppendLine(note.ToString().Trim()).AppendLine();
            }

            material = notes.ToString();
        }

        progress?.Report(new SummaryProgress(steps, steps, Writing: true));
        await foreach (var piece in _model.CompleteAsync(_system, SummaryRequest(meeting, material, fromNotes: chunks.Count > 1), SummaryTokens(_model), cancellationToken).ConfigureAwait(false))
        {
            yield return piece;
        }

        yield return DataSection(meeting);
    }

    /// <summary>
    /// "Dados citados": every line that gave a number, date or deadline, word for word with
    /// its time. Written here rather than by the model — asked for it, the 3B model kept
    /// answering "none" over a call that gave a lot number. Quoting cannot invent anything.
    /// </summary>
    internal static string DataSection(MeetingTranscript meeting)
    {
        var lines = NumberLines(meeting)
            .Select(m => $"- [{meeting.TimeOf(m)}] {meeting.Speaker(m.SpeakerId)?.Name ?? "?"}: {m.Text}")
            .ToList();

        return lines.Count == 0 ? string.Empty : $"\n\n## Dados citados\n{string.Join('\n', lines)}\n";
    }

    private static IEnumerable<MeetingMessage> NumberLines(MeetingTranscript meeting) => meeting.Messages
        .Where(m => m.Text.Any(char.IsDigit) || NumberWords.Any(w => $" {m.Text} ".Contains(w, StringComparison.OrdinalIgnoreCase)))
        .Take(20);

    /// <summary>"[09:01] Ana Silva: text" per message.</summary>
    internal static List<string> Lines(MeetingTranscript meeting) => meeting.Messages
        .Select(m => $"[{meeting.TimeOf(m)}] {meeting.Speaker(m.SpeakerId)?.Name ?? "?"}: {m.Text}")
        .ToList();

    /// <summary>Groups whole lines into chunks that each fit <paramref name="budget"/> tokens.</summary>
    internal List<string> Chunk(IReadOnlyList<string> lines, int budget)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        var tokens = 0;

        foreach (var line in lines)
        {
            var cost = _model.CountTokens(line) + 1;
            if (tokens > 0 && tokens + cost > budget)
            {
                chunks.Add(current.ToString());
                current.Clear();
                tokens = 0;
            }

            current.AppendLine(line);
            tokens += cost;
        }

        if (current.Length > 0 || chunks.Count == 0) chunks.Add(current.ToString());
        return chunks;
    }

    internal static string SummaryRequest(MeetingTranscript meeting, string material, bool fromNotes)
    {
        var people = string.Join(", ", meeting.Speakers.Where(s => meeting.Messages.Any(m => m.SpeakerId == s.Id)).Select(s => s.Name));

        return $"""
            Resuma {(meeting.SourceFile is null ? "a reunião" : "a gravação")} "{meeting.Title}" ({meeting.App}). Participantes: {people}.
            Responda exatamente neste formato, em Markdown, sem nada antes ou depois:

            ## Resumo
            (3 a 5 frases: o objetivo da conversa, o que foi apresentado ou levantado e a que se chegou, com os nomes de pessoas, empresas, sistemas e produtos citados)

            ## Principais pontos
            - Assunto: o que foi dito sobre ele — como funciona hoje, problemas, exemplos, quantidades e quem disse
            (um item por fato importante, na ordem da conversa; vários itens sobre o mesmo assunto são bem-vindos)

            ## Decisões e combinados
            - (cada coisa decidida, combinada, pedida e aceita, sugerida e aprovada, registrado, aberto ou prometido, dizendo quem e o quê — um registro aberto ou um reembolso aceito também conta. Contam frases como "eu te mando", "vou pedir para ela mandar", "vamos começar por", "ela vai levantar", "tem que ser parametrizável", "não vai migrar agora". Escreva "Nada foi combinado." só se de fato nada foi)

            ## Próximos passos
            - Quem vai fazer: o que vai fazer (prazo, se foi dito) — só tarefas que alguém ficou de fazer depois da conversa, como mandar, levantar, verificar ou pedir algo; não repita aqui os itens de "Decisões e combinados"

            ## Pontos em aberto
            - (perguntas que ficaram sem resposta, coisas a confirmar ou verificar; se não houver nenhuma, não escreva esta seção)

            Se o contexto do usuário pedir outras informações, acrescente uma seção para cada uma depois destas.
            {NumberHints(meeting)}
            {(fromNotes ? "Notas de cada parte da conversa:" : "Transcrição:")}
            {material}
            """;
    }

    /// <summary>
    /// The lines of the conversation that contain a number — lots, codes, dates, deadlines,
    /// amounts — handed to the model separately. A 3B model asked to list "data mentioned" in
    /// a long transcript answered "none" over a call that gave a lot number; pointing at the
    /// lines fixes that without letting it invent any.
    /// </summary>
    internal static string NumberHints(MeetingTranscript meeting)
    {
        // Capped per line: a long monologue with one number in it would eat the context.
        var lines = NumberLines(meeting).Select(m => $"- {Clip(m.Text, 160)}").ToList();

        return lines.Count == 0
            ? string.Empty
            : $"\nFalas com números, datas ou prazos (números, lotes, datas e prazos que os resumos costumam perder; use-os sem mudar o que foi dito):\n{string.Join('\n', lines)}\n";
    }

    // Spoken numbers the recogniser sometimes leaves as words.
    private static readonly string[] NumberWords = [" dois ", " três ", " quatro ", " cinco ", " dez ", " quinze ", " trinta ", " cem ", " mil ", " dias úteis", " horas"];

    internal static string NotesRequest(string chunk, int part, int parts) => $"""
        Esta é a parte {part} de {parts} de uma conversa. Anote em tópicos, com detalhes, o que
        foi dito nesta parte: cada assunto e os fatos concretos sobre ele (como funciona,
        problemas, exemplos, quantidades, nomes de empresas, clientes e sistemas), quem disse
        cada coisa importante, e tudo o que foi decidido, combinado, pedido, prometido ou
        deixado em aberto. Não generalize: estas notas são tudo o que vai sobrar desta parte.

        Transcrição:
        {chunk}
        """;

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length].TrimEnd() + "…";
}
