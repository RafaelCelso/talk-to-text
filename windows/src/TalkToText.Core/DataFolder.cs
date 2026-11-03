namespace TalkToText.Core;

/// <summary>Where the app keeps its settings, history, dictionary, model and log.</summary>
public static class DataFolder
{
    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary><c>%LOCALAPPDATA%\TalkToText</c>.</summary>
    public static string Path { get; } = System.IO.Path.Combine(LocalAppData, "TalkToText");
}
