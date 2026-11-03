using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TalkToText.Abstractions;

namespace TalkToText.Platform.Windows;

/// <summary>
/// Reports visible windows and which apps have a microphone open — the raw material for
/// recognising a meeting. Deciding what counts as a meeting is not done here.
/// </summary>
/// <remarks>
/// <para>
/// "Microphone in use" comes from the same registry data Windows shows under Settings →
/// Privacy → Microphone: each app that used the microphone has <c>LastUsedTimeStart</c> and
/// <c>LastUsedTimeStop</c>, and a stop time of zero means it is recording right now.
/// Packaged apps (the new Teams) are keyed by package family, desktop apps by their path.
/// </para>
/// </remarks>
public sealed class WindowsMeetingSignals : IMeetingSignals
{
    private const string ConsentStore =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, [Out] char[] text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    /// <inheritdoc />
    public IReadOnlyList<AppActivity> Snapshot()
    {
        var titles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<uint, string>();

        EnumWindows((window, lParam) =>
        {
            if (!IsWindowVisible(window)) return true;

            var length = GetWindowTextLength(window);
            if (length == 0) return true;

            var buffer = new char[length + 1];
            var copied = GetWindowText(window, buffer, buffer.Length);
            if (copied == 0) return true;

            _ = GetWindowThreadProcessId(window, out var pid);
            if (!names.TryGetValue(pid, out var name))
            {
                name = ProcessName(pid);
                names[pid] = name;
            }

            if (name.Length == 0) return true;
            if (!titles.TryGetValue(name, out var list)) titles[name] = list = [];
            list.Add(new string(buffer, 0, copied));
            return true;
        }, IntPtr.Zero);

        var microphone = AppsUsingMicrophone();

        return titles.Keys
            .Union(microphone, StringComparer.OrdinalIgnoreCase)
            .Select(name => new AppActivity(
                name,
                titles.TryGetValue(name, out var list) ? list : [],
                microphone.Contains(name)))
            .ToList();
    }

    private static string ProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Exited between enumeration and lookup.
            return string.Empty;
        }
    }

    /// <summary>Process names (or package names) with a microphone open right now.</summary>
    private static HashSet<string> AppsUsingMicrophone()
    {
        var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddActiveCaptureSessions(inUse);
        AddFromConsentStore(inUse);
        return inUse;
    }

    /// <summary>
    /// Apps with an active WASAPI capture session on any microphone — the primary signal.
    /// </summary>
    /// <remarks>
    /// On Windows 11 build 26300 the registry's <c>LastUsedTime</c> values had stopped being
    /// updated: a Teams call in progress showed nothing there, so meetings went undetected.
    /// The audio session list is what the audio engine itself knows, and it named
    /// <c>ms-teams</c> as actively capturing during that same call.
    /// </remarks>
    private static void AddActiveCaptureSessions(HashSet<string> inUse)
    {
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(
                         NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active))
            {
                using (device)
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;

                    for (var i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.IsSystemSoundsSession
                            || session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive)
                        {
                            continue;
                        }

                        var name = ProcessName(session.GetProcessID);
                        if (name.Length > 0) inUse.Add(name);
                    }
                }
            }
        }
        catch (COMException)
        {
            // A device vanished mid-enumeration; the next poll sees a settled list.
        }
    }

    /// <summary>The registry's view of who is recording — kept for Windows builds where it is current.</summary>
    private static void AddFromConsentStore(HashSet<string> inUse)
    {
        using var root = Registry.CurrentUser.OpenSubKey(ConsentStore);
        if (root is null) return;

        foreach (var key in root.GetSubKeyNames())
        {
            if (key.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                using var desktop = root.OpenSubKey(key);
                if (desktop is null) continue;

                foreach (var app in desktop.GetSubKeyNames())
                {
                    using var entry = desktop.OpenSubKey(app);
                    if (entry is not null && IsRecording(entry))
                    {
                        // "C:#Program Files#Google#Chrome#Application#chrome.exe" → "chrome"
                        inUse.Add(Path.GetFileNameWithoutExtension(app.Replace('#', '\\')));
                    }
                }

                continue;
            }

            using var packaged = root.OpenSubKey(key);
            if (packaged is not null && IsRecording(packaged))
            {
                // "MSTeams_8wekyb3d8bbwe" → "MSTeams"
                var underscore = key.IndexOf('_', StringComparison.Ordinal);
                inUse.Add(underscore > 0 ? key[..underscore] : key);
            }
        }
    }

    private static bool IsRecording(RegistryKey entry) =>
        entry.GetValue("LastUsedTimeStop") is long stop && stop == 0
        && entry.GetValue("LastUsedTimeStart") is long start && start != 0;
}
