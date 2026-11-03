using System.Text.Json;
using System.Text.Json.Serialization;

namespace TalkToText.Core;

/// <summary>Someone in a meeting transcript.</summary>
public sealed class MeetingSpeaker
{
    /// <summary>Stable within the transcript; 0 is always the user.</summary>
    public int Id { get; set; }

    /// <summary>What the chat shows.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The user, heard through their own microphone.</summary>
    public bool IsLocal { get; set; }

    /// <summary>
    /// Whether <see cref="Name"/> is a real name — given by the user, recognised from a past
    /// meeting, or read from the meeting app — rather than "Participante 2".
    /// </summary>
    public bool IsNamed { get; set; }

    /// <summary>
    /// The average voice print of everything this person said, so naming them after the meeting
    /// can still teach the app their voice. Null for the user and for older transcripts.
    /// </summary>
    public float[]? Voice { get; set; }

    /// <summary>How many utterances were averaged into <see cref="Voice"/>.</summary>
    public int VoiceCount { get; set; }
}

/// <summary>One chat bubble: what someone said.</summary>
public sealed class MeetingMessage
{
    /// <summary>The <see cref="MeetingSpeaker.Id"/> of whoever said it.</summary>
    public int SpeakerId { get; set; }

    /// <summary>When they started saying it.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>The transcribed text.</summary>
    public string Text { get; set; } = string.Empty;
}

/// <summary>A whole meeting: who was there and what was said, in order.</summary>
public sealed class MeetingTranscript
{
    /// <summary>Identity, and the file name.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>"Microsoft Teams", "Google Meet"…</summary>
    public string App { get; set; } = string.Empty;

    /// <summary>The meeting's title from its window.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>When transcription started.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When it stopped; null while live.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Everyone heard, the user first.</summary>
    public List<MeetingSpeaker> Speakers { get; set; } = [];

    /// <summary>Everything said, oldest first.</summary>
    public List<MeetingMessage> Messages { get; set; } = [];

    /// <summary>Full path of the audio file this was transcribed from, for playback; null for a live meeting.</summary>
    public string? SourceFile { get; set; }

    /// <summary>The summary, in Markdown, once the user has asked for one.</summary>
    public string? Summary { get; set; }

    /// <summary>When <see cref="Summary"/> was written.</summary>
    public DateTimeOffset? SummarizedAt { get; set; }

    /// <summary>The speaker with <paramref name="id"/>.</summary>
    public MeetingSpeaker? Speaker(int id) => Speakers.FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// When <paramref name="message"/> was said: the time of day in a meeting, the position in
    /// the recording ("03:25", "1:02:10") for an audio file.
    /// </summary>
    public string TimeOf(MeetingMessage message)
    {
        if (SourceFile is null) return message.At.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        var offset = message.At - StartedAt;
        if (offset < TimeSpan.Zero) offset = TimeSpan.Zero;
        return offset.ToString(offset.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Plain text, "Name: text" per line — for copying out.</summary>
    public string ToPlainText() => string.Join(
        Environment.NewLine,
        Messages.Select(m => $"[{TimeOf(m)}] {Speaker(m.SpeakerId)?.Name ?? "?"}: {m.Text}"));
}

/// <summary>Meeting transcripts, one JSON file each, in the data folder.</summary>
public sealed class MeetingStore
{
    private readonly string _directory;
    private readonly Lock _gate = new();

    /// <summary>Stores transcripts under <paramref name="directory"/>.</summary>
    public MeetingStore(string directory) => _directory = directory;

    /// <summary><c>%LOCALAPPDATA%\TalkToText\meetings</c>.</summary>
    public static string DefaultDirectory => Path.Combine(DataFolder.Path, "meetings");

    /// <summary><c>%LOCALAPPDATA%\TalkToText\audio-files</c>: transcribed recordings, kept apart from meetings.</summary>
    public static string AudioFilesDirectory => Path.Combine(DataFolder.Path, "audio-files");

    /// <summary>Raised after a save or delete. May fire on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Every transcript, newest first.</summary>
    public IReadOnlyList<MeetingTranscript> All()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory)) return [];

            return Directory.EnumerateFiles(_directory, "*.json")
                .Select(Read)
                .OfType<MeetingTranscript>()
                .OrderByDescending(m => m.StartedAt)
                .ToList();
        }
    }

    /// <summary>Writes <paramref name="transcript"/>, replacing any earlier version.</summary>
    /// <remarks>Written to a temporary file and moved into place, so a crash mid-meeting
    /// leaves the last complete version rather than half a file.</remarks>
    public void Save(MeetingTranscript transcript)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(transcript.Id);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(transcript, MeetingJsonContext.Default.MeetingTranscript));
            File.Move(temp, path, overwrite: true);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gives every transcript still marked live an end: its last message, or its start if
    /// nothing was said. Call at startup, before any meeting is live.
    /// </summary>
    /// <remarks>
    /// A meeting is only marked ended when transcription stops cleanly. When the app is killed
    /// mid-meeting — an update's installer closes it, Task Manager, a crash — the file stays
    /// "live" forever, and its duration was then counted up to the present: a 1-minute call
    /// read "14h02" that evening.
    /// </remarks>
    /// <returns>How many were closed.</returns>
    public int CloseUnfinished()
    {
        var closed = 0;
        foreach (var meeting in All().Where(m => m.EndedAt is null))
        {
            meeting.EndedAt = meeting.Messages.Count > 0 ? meeting.Messages.Max(m => m.At) : meeting.StartedAt;
            Save(meeting);
            closed++;
        }

        return closed;
    }

    /// <summary>Deletes the transcript with <paramref name="id"/>.</summary>
    public void Delete(Guid id)
    {
        lock (_gate)
        {
            var path = PathFor(id);
            if (File.Exists(path)) File.Delete(path);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string PathFor(Guid id) => Path.Combine(_directory, id.ToString("N") + ".json");

    private static MeetingTranscript? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), MeetingJsonContext.Default.MeetingTranscript);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Source-generated JSON for meetings and voices.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(MeetingTranscript))]
[JsonSerializable(typeof(List<KnownVoice>))]
public sealed partial class MeetingJsonContext : JsonSerializerContext;
