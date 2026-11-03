namespace TalkToText.Core;

/// <summary>Where the app keeps its settings, history, dictionary, model and log.</summary>
public static class DataFolder
{
    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary><c>%LOCALAPPDATA%\TalkToText</c>.</summary>
    public static string Path { get; } = System.IO.Path.Combine(LocalAppData, "TalkToText");

    /// <summary>The folder used before the app was renamed from Murmur.</summary>
    public static string LegacyPath { get; } = System.IO.Path.Combine(LocalAppData, "Murmur");

    /// <summary>
    /// Moves the pre-rename <c>%LOCALAPPDATA%\Murmur</c> folder into place, once.
    /// </summary>
    /// <remarks>
    /// A move, not a copy: the folder holds a ~670 MB model, and on one volume a move is a
    /// rename. Settings may carry an absolute <c>ModelDirectory</c> inside the old folder,
    /// so that path is rewritten too — otherwise the moved model would not be found.
    /// Never throws: a failed migration must not stop the app starting, it only means the
    /// user sees a fresh install.
    /// </remarks>
    /// <returns>True if a legacy folder was moved.</returns>
    public static bool MigrateLegacy() => MigrateLegacy(LegacyPath, Path);

    /// <summary>Testable core of <see cref="MigrateLegacy()"/>.</summary>
    public static bool MigrateLegacy(string legacy, string current)
    {
        try
        {
            if (!Directory.Exists(legacy) || Directory.Exists(current)) return false;

            Directory.Move(legacy, current);

            var settings = System.IO.Path.Combine(current, "settings.json");
            if (File.Exists(settings))
            {
                // JSON escapes backslashes, so the path appears doubled in the file.
                var escapedOld = legacy.Replace("\\", "\\\\", StringComparison.Ordinal);
                var escapedNew = current.Replace("\\", "\\\\", StringComparison.Ordinal);
                var text = File.ReadAllText(settings);
                File.WriteAllText(settings, text.Replace(escapedOld, escapedNew, StringComparison.OrdinalIgnoreCase));
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
