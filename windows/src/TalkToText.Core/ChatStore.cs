using System.Text.Json;
using System.Text.Json.Serialization;

namespace TalkToText.Core;

/// <summary>One question and answer as saved, with where the answer came from.</summary>
public sealed class ChatExchange
{
    /// <summary>What the user asked.</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>What the assistant answered.</summary>
    public string Answer { get; set; } = string.Empty;

    /// <summary>"Daily (2 nov.) · Retro (1 nov.)": the meetings or recordings it drew on.</summary>
    public string? Sources { get; set; }
}

/// <summary>A conversation in the Chat page, kept so it can be reopened from the history.</summary>
public sealed class ChatConversation
{
    /// <summary>Identity, and the file name.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Asked about recordings (true) or meetings (false) — which tab it belongs to.</summary>
    public bool AboutRecordings { get; set; }

    /// <summary>The one meeting or recording it was about, or null for all of them.</summary>
    public Guid? ScopeId { get; set; }

    /// <summary>"Todas as reuniões", or the meeting's or recording's title, as it was then.</summary>
    public string ScopeTitle { get; set; } = string.Empty;

    /// <summary>When it was started.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it was last added to.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Questions and answers, oldest first.</summary>
    public List<ChatExchange> Exchanges { get; set; } = [];

    /// <summary>The first question: the conversation's name in the history.</summary>
    public string Title => Exchanges.Count == 0 ? "Nova conversa" : Exchanges[0].Question;

    /// <summary>The turns as the assistant takes them, for follow-up questions.</summary>
    public IReadOnlyList<ChatTurn> Turns => [.. Exchanges.Select(e => new ChatTurn(e.Question, e.Answer))];
}

/// <summary>Chat conversations, one JSON file each, in the data folder.</summary>
public sealed class ChatStore
{
    private readonly string _directory;
    private readonly Lock _gate = new();

    /// <summary>Stores conversations under <paramref name="directory"/>.</summary>
    public ChatStore(string directory) => _directory = directory;

    /// <summary><c>%LOCALAPPDATA%\TalkToText\chats</c>.</summary>
    public static string DefaultDirectory => Path.Combine(DataFolder.Path, "chats");

    /// <summary>The conversations about meetings, or about recordings, most recently used first.</summary>
    public IReadOnlyList<ChatConversation> All(bool aboutRecordings)
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory)) return [];

            return Directory.EnumerateFiles(_directory, "*.json")
                .Select(Read)
                .OfType<ChatConversation>()
                .Where(c => c.AboutRecordings == aboutRecordings && c.Exchanges.Count > 0)
                .OrderByDescending(c => c.UpdatedAt)
                .ToList();
        }
    }

    /// <summary>Writes <paramref name="conversation"/>, replacing any earlier version.</summary>
    public void Save(ChatConversation conversation)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(conversation.Id);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(conversation, ChatJsonContext.Default.ChatConversation));
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>Deletes the conversation with <paramref name="id"/>.</summary>
    public void Delete(Guid id)
    {
        lock (_gate)
        {
            var path = PathFor(id);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private string PathFor(Guid id) => Path.Combine(_directory, id.ToString("N") + ".json");

    private static ChatConversation? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), ChatJsonContext.Default.ChatConversation);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Source-generated JSON for chat conversations.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(ChatConversation))]
public sealed partial class ChatJsonContext : JsonSerializerContext;
