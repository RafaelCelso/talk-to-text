using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using TalkToText.Abstractions;

namespace TalkToText.Platform.Windows;

/// <summary>
/// Reads, through UI Automation, the name Microsoft Teams shows as currently speaking.
/// </summary>
/// <remarks>
/// <para>
/// <b>Experimental, and knowingly fragile.</b> Teams does not document how it exposes the
/// active speaker; it surfaces as accessible text on participant tiles ("Ana Silva is
/// speaking", "Ana Silva está falando") that changes between versions. This looks for those
/// phrases and returns the name in front of them, or null. A null costs nothing: participants
/// simply stay "Participante N" until named.
/// </para>
/// <para>
/// Teams only. A browser's accessibility tree is the whole web page; walking it several
/// times a second would cost far more than the name is worth.
/// </para>
/// </remarks>
public sealed class TeamsActiveSpeaker : IActiveSpeakerSource
{
    private static readonly string[] TeamsProcesses = ["ms-teams", "MSTeams", "Teams"];

    /// <summary>Walking the tree is slow; one lookup serves this long.</summary>
    private static readonly TimeSpan Reuse = TimeSpan.FromMilliseconds(800);

    /// <summary>Upper bound on elements inspected per lookup, to bound the cost.</summary>
    private const int MaxElements = 4000;

    private CUIAutomation? _automation;
    private DateTime _lastLookup;
    private string? _last;

    /// <inheritdoc />
    public string? CurrentSpeaker(string processName)
    {
        if (!TeamsProcesses.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase))) return null;
        if (DateTime.UtcNow - _lastLookup < Reuse) return _last;

        _lastLookup = DateTime.UtcNow;
        _last = Lookup(processName);
        return _last;
    }

    private string? Lookup(string processName)
    {
        _automation ??= new CUIAutomation();
        var all = _automation.CreateTrueCondition();

        foreach (var window in WindowsOf(processName))
        {
            try
            {
                var root = _automation.ElementFromHandle(window);
                var elements = root?.FindAll(TreeScope.TreeScope_Descendants, all);
                if (elements is null) continue;

                var count = Math.Min(elements.Length, MaxElements);
                for (var i = 0; i < count; i++)
                {
                    var name = SpeakingLabel.NameIn(elements.GetElement(i).CurrentName);
                    if (name is not null) return name;
                }
            }
            catch (COMException)
            {
                // The window closed or changed under us; the next lookup tries again.
            }
        }

        return null;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private static List<IntPtr> WindowsOf(string processName)
    {
        var ids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process) ids.Add((uint)process.Id);
        }

        var windows = new List<IntPtr>();
        EnumWindows((window, lParam) =>
        {
            if (IsWindowVisible(window)
                && GetWindowThreadProcessId(window, out var pid) != 0
                && ids.Contains(pid))
            {
                windows.Add(window);
            }

            return true;
        }, IntPtr.Zero);

        return windows;
    }
}
