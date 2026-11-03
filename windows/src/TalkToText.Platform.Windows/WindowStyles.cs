using System.Runtime.InteropServices;

namespace TalkToText.Platform.Windows;

/// <summary>Extended window styles the UI framework does not expose.</summary>
public static class WindowStyles
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    /// <summary>
    /// Clips <paramref name="window"/> to a rounded rectangle, in device pixels.
    /// </summary>
    /// <remarks>
    /// The acrylic blur behind a window is drawn by the system for the whole window
    /// rectangle; only a window region rounds it. Animating the region is also how the pill
    /// grows from a dot.
    /// </remarks>
    public static void SetRoundedRegion(IntPtr window, int left, int top, int width, int height, int radius)
    {
        if (window == IntPtr.Zero) return;

        var region = CreateRoundRectRgn(left, top, left + width + 1, top + height + 1, radius * 2, radius * 2);
        if (region == IntPtr.Zero) return;

        // On success the system owns the region; free it only if the hand-off failed.
        if (SetWindowRgn(window, region, redraw: true) == 0) _ = DeleteObject(region);
    }

    private const int SW_RESTORE = 9;
    private const uint ASFW_ANY = unchecked((uint)-1);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    /// <summary>
    /// Restores <paramref name="window"/> if minimised and makes it the foreground window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Activating a window only moves focus within its own app; when another app is in front,
    /// Windows needs <c>SetForegroundWindow</c>, which it allows to the process that just
    /// received the user's input — the tray click — or one granted permission by
    /// <see cref="AllowAnyToTakeForeground"/>.
    /// </para>
    /// <para>
    /// When that permission is missing, Windows only flashes the taskbar button. The fallback
    /// attaches this thread's input to the foreground window's thread for the duration of the
    /// call, which Windows treats as the same input context and lets through — the standard
    /// way round the foreground lock, with no synthetic key presses.
    /// </para>
    /// </remarks>
    public static void BringToFront(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        if (IsIconic(window)) _ = ShowWindow(window, SW_RESTORE);

        if (SetForegroundWindow(window) && GetForegroundWindow() == window) return;

        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var thisThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thisThread
                       && AttachThreadInput(thisThread, foregroundThread, attach: true);
        try
        {
            _ = BringWindowToTop(window);
            _ = SetForegroundWindow(window);
        }
        finally
        {
            if (attached) _ = AttachThreadInput(thisThread, foregroundThread, attach: false);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachThread, uint toThread, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    /// <summary>
    /// Lets another process take the foreground. Called by a second launch, which has the
    /// user's permission, just before it asks the running instance to show itself.
    /// </summary>
    public static void AllowAnyToTakeForeground() => _ = AllowSetForegroundWindow(ASFW_ANY);

    /// <summary>
    /// Makes <paramref name="window"/> a floating indicator: it never takes focus, even when
    /// clicked, and stays out of Alt+Tab and the taskbar.
    /// </summary>
    /// <remarks>
    /// <b>Focus is the whole point.</b> The recording pill appears while the user is
    /// dictating into another app; if it took focus, the transcript would be pasted into the
    /// pill instead of where the user was typing.
    /// </remarks>
    public static void MakeOverlay(IntPtr window)
    {
        if (window == IntPtr.Zero) return;

        var style = GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
        _ = SetWindowLongPtr(window, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }
}
