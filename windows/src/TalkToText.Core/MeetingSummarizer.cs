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
/// </remarks>
public sealed class MeetingSummarizer
{
    /// <summary>Tokens kept back for the instructions and the answer.</summary>
    private const int Reserve = 1800;

    /// <summary>Longest summary, in tokens.</summary>
    public const int SummaryTokens = 700;

    /// <summary>Longest set of notes per chunk, in tokens.</summary>
    private const int NoteTokens = 350;

    internal const string SystemPrompt =
        "Você resume conversas — reuniões e gravações — em português do Brasil. Use apenas o que "
        + "está na transcrição: não invente nomes, datas, números nem decisões, e não torne nada "
        + "mais preciso do que foi dito — \"hoje à tarde\" não vira um horário. Se algo não foi "
        + "dito, não escreva.";

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
        var budget = Math.Max(1000, _model.ContextTokens - Reserve - _model.CountTokens(_system));
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
                await foreach (var piece in _model.CompleteAsync(_system, NotesRequest(chunks[i], i + 1, chunks.Count), NoteTokens, cancellationToken).ConfigureAwait(false))
                {
                    note.Append(piece);
                }

                notes.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"### Parte {i + 1}").AppendLine(note.ToString().Trim()).AppendLine();
            }

            material = notes.ToString();
        }

        progress?.Report(new SummaryProgress(steps, steps, Writing: true));
        await foreach (var piece in _model.CompleteAsync(_system, SummaryRequest(meeting, material, fromNotes: chunks.Count > 1), SummaryTokens, cancellationToken).ConfigureAwait(false))
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
            (2 a 4 frases sobre o objetivo e o resultado da conversa)

            ## Principais pontos
            - (um item por assunto discutido)

            ## Decisões e combinados
            - (tudo o que ficou decidido, combinado, registrado, aberto ou prometido — um registro aberto ou um reembolso aceito também conta; escreva "Nada foi combinado." só se de fato nada foi)

            ## Próximos passos
            - Quem vai fazer: o que vai fazer (prazo, se foi dito)

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
        var lines = NumberLines(meeting).Select(m => $"- {m.Text}").ToList();

        return lines.Count == 0
            ? string.Empty
            : $"\nFalas com números, datas ou prazos (números, lotes, datas e prazos que os resumos costumam perder; use-os sem mudar o que foi dito):\n{string.Join('\n', lines)}\n";
    }

    // Spoken numbers the recogniser sometimes leaves as words.
    private static readonly string[] NumberWords = [" dois ", " três ", " quatro ", " cinco ", " dez ", " quinze ", " trinta ", " cem ", " mil ", " dias úteis", " horas"];

    internal static string NotesRequest(string chunk, int part, int parts) => $"""
        Esta é a parte {part} de {parts} de uma conversa. Liste em tópicos curtos o que foi
        discutido, decidido e combinado nesta parte, dizendo quem disse cada coisa importante.

        Transcrição:
        {chunk}
        """;
}
