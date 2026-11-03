using System.Text.Json;
using System.Text.Json.Serialization;

namespace TalkToText.Core;

/// <summary>User preferences.</summary>
/// <remarks>
/// <b>Setters, not <c>init</c>.</b> The source-generated serializer assigns every
/// <c>init</c> property, writing <c>default</c> for any the file lacks — so a settings file
/// from an older version would load with no shortcut at all. With plain setters, a missing
/// property keeps the initializer's value.
/// </remarks>
public sealed record SettingsData
{
    /// <summary>The keys that start dictation. Defaults to Ctrl + Win.</summary>
    /// <remarks>
    /// <b>Not Right Alt alone.</b> On German, Polish, UK, Nordic and most Latin-American
    /// layouts Right Alt is AltGr — it is how those users type <c>@</c>, <c>€</c>, <c>\</c>
    /// and <c>|</c>.
    /// </remarks>
    public Shortcut Shortcut { get; set; } = Shortcut.Default;

    /// <summary>Hold the shortcut to talk, or press it to start and again to stop.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ActivationMode>))]
    public ActivationMode Mode { get; set; } = ActivationMode.Hold;

    /// <summary>Where the speech model lives, or null to search the default locations.</summary>
    public string? ModelDirectory { get; set; }

    /// <summary>Whether to type the transcript into the focused app.</summary>
    public bool InjectText { get; set; } = true;

    /// <summary>Whether to show the floating pill at the bottom of the screen while recording.</summary>
    public bool ShowOverlay { get; set; } = true;

    /// <summary>Whether to watch for meetings and offer to transcribe them.</summary>
    public bool DetectMeetings { get; set; } = true;

    /// <summary>
    /// Whether a live meeting transcript floats in the bottom-right corner of the screen while
    /// the app is minimised.
    /// </summary>
    public bool ShowLiveTranscriptWindow { get; set; } = true;

    /// <summary>How the user appears in meeting transcripts; null shows "Você".</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Whether the first-run setup has been finished or skipped. False on a fresh install — and
    /// on settings files from before it existed, which then see it once.
    /// </summary>
    public bool OnboardingDone { get; set; }

    /// <summary>Whether to keep a transcript history.</summary>
    public bool KeepHistory { get; set; } = true;

    /// <summary>
    /// The user's instruction for the AI when it summarises an audio file: who they are and what
    /// the recordings are about. Null or blank for none.
    /// </summary>
    public string? AiInstruction { get; set; }

    /// <summary>
    /// Whether the local AI runs on the graphics card when there is one. Read once at startup:
    /// llama.cpp's native library is chosen when it first loads.
    /// </summary>
    public bool UseGpuForAi { get; set; } = true;

    /// <summary>Whether "éé", "hã", "hum" and stuttered words are taken out of dictations.</summary>
    public bool RemoveHesitations { get; set; } = true;

    /// <summary>Whether the local AI tidies every dictation before it is typed. Adds a few seconds.</summary>
    public bool PolishWithAi { get; set; }

    /// <summary>
    /// Which AI writes summaries, answers the Chat and runs commands: the local Qwen, or a cloud
    /// provider with the user's own API key (kept apart, encrypted, in <see cref="ApiKeyStore"/>).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AiProvider>))]
    public AiProvider AiProvider { get; set; } = AiProvider.Local;

    /// <summary>The cloud model chosen from the provider's list — "gpt-4o-mini". Null with the local AI.</summary>
    public string? AiModel { get; set; }

    /// <summary>Whether the command shortcut is listened for.</summary>
    public bool CommandMode { get; set; }

    /// <summary>
    /// The keys for a spoken command on the selected text. Shift + Win by default: Ctrl + Alt
    /// is AltGr on many layouts.
    /// </summary>
    public Shortcut CommandShortcut { get; set; } = new(Keys.Shift, Keys.Windows);
}

/// <summary>Settings, persisted as JSON.</summary>
public sealed class AppSettings
{
    private readonly string _path;

    /// <summary>Loads settings from <paramref name="path"/>, or defaults if absent.</summary>
    public AppSettings(string path)
    {
        _path = path;
        Data = Load(path);
    }

    /// <summary>The default location.</summary>
    public static string DefaultPath => Path.Combine(DataFolder.Path, "settings.json");

    /// <summary>Current values.</summary>
    public SettingsData Data { get; private set; }

    /// <summary>Raised after a successful save.</summary>
    public event EventHandler? Changed;

    /// <summary>Replaces and persists the settings.</summary>
    public void Update(SettingsData data)
    {
        Data = data;

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(data, SettingsJsonContext.Default.SettingsData));

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static SettingsData Load(string path)
    {
        // Corrupt or unreadable settings must never stop the app launching — defaults are
        // always a working configuration.
        try
        {
            if (!File.Exists(path)) return new SettingsData();

            return JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.SettingsData)
                   ?? new SettingsData();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SettingsData();
        }
    }
}

/// <summary>Source-generated JSON for settings.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SettingsData))]
public sealed partial class SettingsJsonContext : JsonSerializerContext;
