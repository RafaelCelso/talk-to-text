using System.Text;

namespace TalkToText.App;

/// <summary>A summary being written: what it is for, how far along, and the text so far.</summary>
public sealed class SummaryJob : IDisposable
{
    private readonly StringBuilder _text = new();
    private readonly Lock _gate = new();

    /// <summary>Starts a job for <paramref name="meetingId"/>.</summary>
    public SummaryJob(Guid meetingId) => MeetingId = meetingId;

    /// <summary>The meeting being summarised.</summary>
    public Guid MeetingId { get; }

    /// <summary>A one-line description of the current step.</summary>
    public string Status { get; set; } = "Preparando…";

    /// <summary>Cancelled by the user.</summary>
    internal CancellationTokenSource Cancellation { get; } = new();

    /// <summary>Adds generated text. Called from the generating thread.</summary>
    internal void Append(string piece)
    {
        lock (_gate) _text.Append(piece);
    }

    /// <summary>A copy of the text so far.</summary>
    public string CurrentText()
    {
        lock (_gate) return _text.ToString();
    }

    /// <summary>Asks the job to stop.</summary>
    public void Cancel() => Cancellation.Cancel();

    /// <inheritdoc />
    public void Dispose() => Cancellation.Dispose();
}
