using TalkToText.Abstractions;

namespace TalkToText.Core;

/// <summary>A meeting in progress in some app.</summary>
/// <param name="App">Display name, e.g. "Microsoft Teams".</param>
/// <param name="ProcessName">The process holding the microphone.</param>
/// <param name="Title">The meeting's window title, cleaned of the app's suffix.</param>
public sealed record DetectedMeeting(string App, string ProcessName, string Title);

/// <summary>
/// Recognises a meeting from running apps: a meeting app <b>with the microphone open</b>.
/// </summary>
/// <remarks>
/// <para>
/// The open microphone is what separates "in a call" from "has Teams open", which is most
/// of the working day. Browsers need the window title as well, because a browser with the
/// microphone open could be anything; desktop meeting apps do not.
/// </para>
/// <para>Pure function of its input, so every rule is tested without Windows.</para>
/// </remarks>
public static class MeetingDetector
{
    private static readonly (string[] Processes, string App)[] DesktopApps =
    [
        (["ms-teams", "MSTeams", "Teams"], "Microsoft Teams"),
        (["Zoom"], "Zoom"),
        (["slack"], "Slack"),
        (["Discord"], "Discord"),
        (["CiscoCollabHost", "webexmta", "Webex"], "Webex"),
    ];

    private static readonly string[] Browsers =
        ["chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc"];

    private static readonly (string Marker, string App)[] WebMeetings =
    [
        ("Meet -", "Google Meet"),
        ("Google Meet", "Google Meet"),
        ("Microsoft Teams", "Microsoft Teams"),
        ("Zoom", "Zoom"),
        ("Jitsi", "Jitsi Meet"),
        ("Whereby", "Whereby"),
        ("Webex", "Webex"),
    ];

    /// <summary>The meeting in progress, or null.</summary>
    /// <param name="apps">A snapshot of running apps.</param>
    /// <param name="ownProcess">This app's own process, which opens the microphone too.</param>
    public static DetectedMeeting? Detect(IReadOnlyList<AppActivity> apps, string ownProcess = "TalkToText")
    {
        foreach (var app in apps)
        {
            if (!app.UsingMicrophone || Same(app.ProcessName, ownProcess)) continue;

            foreach (var (processes, name) in DesktopApps)
            {
                if (processes.Any(p => Same(p, app.ProcessName)))
                {
                    return new DetectedMeeting(name, app.ProcessName, BestTitle(apps, processes, name));
                }
            }

            if (Browsers.Any(b => Same(b, app.ProcessName)))
            {
                foreach (var title in app.WindowTitles)
                {
                    foreach (var (marker, name) in WebMeetings)
                    {
                        if (title.Contains(marker, StringComparison.OrdinalIgnoreCase))
                        {
                            return new DetectedMeeting(name, app.ProcessName, Clean(title, name));
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The window most likely to be the call: the new Teams keeps its main window open too, so
    /// a title naming a meeting wins over the generic one.
    /// </summary>
    private static string BestTitle(IReadOnlyList<AppActivity> apps, string[] processes, string appName)
    {
        var titles = apps
            .Where(a => processes.Any(p => Same(p, a.ProcessName)))
            .SelectMany(a => a.WindowTitles)
            .ToList();

        var meeting = titles.FirstOrDefault(t =>
            t.Contains("Meeting", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Reunião", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Call", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Chamada", StringComparison.OrdinalIgnoreCase));

        return Clean(meeting ?? titles.FirstOrDefault() ?? appName, appName);
    }

    /// <summary>Drops the " | Microsoft Teams" or " - Google Chrome" style suffixes.</summary>
    private static string Clean(string title, string appName)
    {
        var cleaned = title;
        foreach (var suffix in new[] { " | " + appName, " - " + appName, " - Google Chrome", " - Microsoft Edge", " — Mozilla Firefox", " - Brave" })
        {
            var at = cleaned.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (at > 0) cleaned = cleaned[..at];
        }

        if (cleaned.StartsWith("Meet - ", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned["Meet - ".Length..];
        cleaned = cleaned.Trim();
        return cleaned.Length == 0 ? appName : cleaned;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Turns repeated snapshots into "meeting started" and "meeting ended", with hysteresis.
/// </summary>
/// <remarks>
/// Two sightings in a row before announcing a start, so a microphone opened for a second
/// does not raise a prompt; several misses before announcing the end, so a mute or a glitch
/// does not cut a transcript in two.
/// </remarks>
public sealed class MeetingWatcher
{
    private readonly int _startAfter;
    private readonly int _endAfter;
    private int _seen;
    private int _missed;

    /// <summary>Creates the watcher.</summary>
    /// <param name="startAfter">Consecutive sightings before <see cref="Started"/>.</param>
    /// <param name="endAfter">Consecutive misses before <see cref="Ended"/>.</param>
    public MeetingWatcher(int startAfter = 2, int endAfter = 4)
    {
        _startAfter = startAfter;
        _endAfter = endAfter;
    }

    /// <summary>The meeting in progress, once announced.</summary>
    public DetectedMeeting? Current { get; private set; }

    /// <summary>A meeting has been seen long enough to count.</summary>
    public event EventHandler<DetectedMeeting>? Started;

    /// <summary>The announced meeting has been gone long enough to count.</summary>
    public event EventHandler<DetectedMeeting>? Ended;

    /// <summary>Feeds one detection result (null when no meeting was seen).</summary>
    public void Observe(DetectedMeeting? detected)
    {
        if (detected is not null)
        {
            _missed = 0;
            if (Current is not null) return;

            if (++_seen >= _startAfter)
            {
                Current = detected;
                _seen = 0;
                Started?.Invoke(this, detected);
            }

            return;
        }

        _seen = 0;
        if (Current is null) return;

        if (++_missed >= _endAfter)
        {
            var ended = Current;
            Current = null;
            _missed = 0;
            Ended?.Invoke(this, ended);
        }
    }
}
