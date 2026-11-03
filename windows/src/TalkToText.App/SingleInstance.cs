namespace TalkToText.App;

/// <summary>
/// Keeps one copy of the app running: a second launch asks the first to show itself, then
/// exits.
/// </summary>
/// <remarks>
/// <para>
/// Two copies is not just untidy. Both install a keyboard hook, so one shortcut starts two
/// recordings, and a copy started before the model was installed transcribes nothing — on
/// real hardware that looked exactly like dictation being broken.
/// </para>
/// <para>
/// The named event only exists on Windows; elsewhere every launch simply runs.
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _showRequest;
    private readonly string _name;
    private volatile bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle? showRequest, string name)
    {
        _mutex = mutex;
        _showRequest = showRequest;
        _name = name;
    }

    /// <summary>
    /// Claims the instance, or returns null when another copy already holds it.
    /// </summary>
    /// <param name="name">Unique per app; tests pass their own so they never meet the real app.</param>
    public static SingleInstance? TryClaim(string name = "TalkToText")
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{name}.Instance", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        var showRequest = OperatingSystem.IsWindows()
            ? new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Show")
            : null;

        return new SingleInstance(mutex, showRequest, name);
    }

    /// <summary>
    /// Asks the running copy to show its window. Returns false if it could not be reached.
    /// </summary>
    public static bool SignalRunning(string name = "TalkToText")
    {
        if (!OperatingSystem.IsWindows()) return false;

        // This process was just started by the user, so it may hand its right to take the
        // foreground to the copy that will actually show a window.
        PlatformFactory.AllowAnyToTakeForeground();

        if (!EventWaitHandle.TryOpenExisting($@"Local\{name}.Show", out var showRequest)) return false;
        using (showRequest)
        {
            return showRequest.Set();
        }
    }

    /// <summary>
    /// Calls <paramref name="onShowRequested"/> whenever a second launch asks this copy to show
    /// itself. Runs on a background thread; the callback must marshal to the UI itself.
    /// </summary>
    public void Listen(Action onShowRequested)
    {
        if (_showRequest is null) return;

        var thread = new Thread(() =>
        {
            while (!_disposed)
            {
                try
                {
                    if (_showRequest.WaitOne(TimeSpan.FromSeconds(1)) && !_disposed) onShowRequested();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = $"{_name} show-request listener",
        };
        thread.Start();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _showRequest?.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned by this thread */ }
        _mutex.Dispose();
    }
}
