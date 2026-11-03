using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using TalkToText.Abstractions;

namespace TalkToText.App;

/// <summary>
/// Loads the Windows platform layer, if it is present.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why reflection rather than a project reference:</b> <c>TalkToText.Platform.Windows</c>
/// targets <c>net10.0-windows</c>. Referencing it directly would force this project onto that
/// TFM too, and the app could then no longer be built or headless-tested on macOS — losing
/// the fast local loop that is the whole reason for choosing Avalonia.
/// </para>
/// <para>
/// The assembly is shipped alongside the app on Windows and simply absent elsewhere, so the
/// lookup failing is the normal, expected case on a developer's Mac.
/// </para>
/// </remarks>
internal static class PlatformFactory
{
    private const string AssemblyName = "TalkToText.Platform.Windows";
    private const string Namespace = "TalkToText.Platform.Windows";

    private static Assembly? _assembly;
    private static bool _attempted;

    /// <summary>Whether the Windows platform assembly could be loaded.</summary>
    public static bool IsAvailable => Load() is not null;

    /// <summary>
    /// Teaches the default load context to find the platform assembly beside the executable.
    /// </summary>
    /// <remarks>
    /// <c>PublishSingleFile</c> only bundles assemblies the compiler knows about, and this
    /// one is deliberately invisible to it — that is what keeps <c>TalkToText.App</c> on plain
    /// <c>net10.0</c>. It therefore ships as a loose file next to the exe. Default probing
    /// normally finds it, but a single-file host resolves differently enough that relying on
    /// that alone is a bet — and losing it means the app starts fine and then does nothing
    /// when the user presses the key. Explicit is cheaper than that failure.
    /// </remarks>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "TalkToText.Platform.Windows ships whole beside the executable and is "
                      + "never trimmed; nothing it depends on can have been removed.")]
    public static void InstallResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;

        // Its dependencies too, not just the layer itself. The default context only probes the
        // app folder for assemblies listed in TalkToText.App.deps.json, and NAudio is not listed —
        // the app never references it. Handling only the layer's own name once let the app
        // start, construct everything, and then fail to load NAudio.Wasapi on the first press.
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name is null) return null;
            if (!string.Equals(name.Name, AssemblyName, StringComparison.Ordinal)
                && !name.Name.StartsWith("NAudio", StringComparison.Ordinal)
                && !name.Name.StartsWith("Interop.", StringComparison.Ordinal)) return null;

            var candidate = System.IO.Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
    }

    private static bool _resolverInstalled;

    /// <summary>Creates the WASAPI capture, or null off Windows.</summary>
    public static IAudioCapture? CreateAudioCapture() =>
        Create<IAudioCapture>("WasapiAudioCapture", [null]);

    /// <summary>Captures what the speakers play — the other people in a call. Null off Windows.</summary>
    public static IAudioCapture? CreateLoopbackCapture() =>
        Create<IAudioCapture>("SpeakerAudioCapture", []);

    /// <summary>Running apps and open microphones, for spotting a meeting. Null off Windows.</summary>
    public static IMeetingSignals? CreateMeetingSignals() =>
        Create<IMeetingSignals>("WindowsMeetingSignals", []);

    /// <summary>Reads who Teams shows as speaking. Null off Windows.</summary>
    public static IActiveSpeakerSource? CreateActiveSpeakerSource() =>
        Create<IActiveSpeakerSource>("TeamsActiveSpeaker", []);

    /// <summary>Reads MP3, WAV and other audio files. Null off Windows.</summary>
    public static IAudioFileDecoder? CreateAudioFileDecoder() =>
        Create<IAudioFileDecoder>("AudioFileDecoder", []);

    /// <summary>Opens <paramref name="path"/> for playback. Null off Windows or when the file cannot be read.</summary>
    public static IAudioPlayer? CreateAudioPlayer(string path) =>
        Create<IAudioPlayer>("AudioFilePlayer", [path]);

    /// <summary>Creates the low-level keyboard hook, or null off Windows.</summary>
    public static IHotkeySource? CreateHotkeySource() =>
        Create<IHotkeySource>("PushToTalkHook", []);

    /// <summary>
    /// Marks a native window as a non-activating overlay. Does nothing off Windows.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:DynamicallyAccessedMembers",
        Justification = "TalkToText.Platform.Windows is published whole and never trimmed.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "TalkToText.Platform.Windows is published whole and never trimmed.")]
    public static void MakeOverlay(IntPtr window)
    {
        var method = Load()?.GetType($"{Namespace}.WindowStyles")?.GetMethod("MakeOverlay");
        method?.Invoke(null, [window]);
    }

    /// <summary>Clips a native window to a rounded rectangle. Does nothing off Windows.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:DynamicallyAccessedMembers",
        Justification = "TalkToText.Platform.Windows is published whole and never trimmed.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "TalkToText.Platform.Windows is published whole and never trimmed.")]
    public static void SetRoundedRegion(IntPtr window, int left, int top, int width, int height, int radius)
    {
        _setRegion ??= Load()?.GetType($"{Namespace}.WindowStyles")?.GetMethod("SetRoundedRegion");
        _setRegion?.Invoke(null, [window, left, top, width, height, radius]);
    }

    // Cached: the overlay calls this every animation frame.
    private static MethodInfo? _setRegion;

    /// <summary>Restores and foregrounds a native window. Does nothing off Windows.</summary>
    public static void BringToFront(IntPtr window) => InvokeWindowStyles("BringToFront", [window]);

    /// <summary>Lets the running instance take the foreground. Does nothing off Windows.</summary>
    public static void AllowAnyToTakeForeground() => InvokeWindowStyles("AllowAnyToTakeForeground", []);

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:DynamicallyAccessedMembers",
        Justification = "TalkToText.Platform.Windows is published whole and never trimmed.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "TalkToText.Platform.Windows is published whole and never trimmed.")]
    private static void InvokeWindowStyles(string method, object?[] arguments) =>
        Load()?.GetType($"{Namespace}.WindowStyles")?.GetMethod(method)?.Invoke(null, arguments);

    /// <summary>Reads the focused app's selection, for command mode. Null off Windows.</summary>
    public static ISelectionReader? CreateSelectionReader() =>
        Create<ISelectionReader>("ClipboardSelectionReader", []);

    /// <summary>Encrypts the cloud AI's API keys with DPAPI. Null off Windows.</summary>
    public static ISecretProtector? CreateSecretProtector() =>
        Create<ISecretProtector>("DpapiSecretProtector", []);

    /// <summary>Creates the SendInput injector, or null off Windows.</summary>
    public static ITextInjector? CreateTextInjector() =>
        Create<ITextInjector>("SendInputTextInjector", []);

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The platform assembly is published whole alongside the app and is "
                      + "never trimmed; its types are resolved by name at startup.")]
    [UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000:AssemblyLocation",
        Justification = "Assembly.Load resolves from the bundle, not from a file path.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072:DynamicallyAccessedMembers",
        Justification = "TalkToText.Platform.Windows is published whole and never trimmed.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:DynamicallyAccessedMembers",
        Justification = "TalkToText.Platform.Windows is published whole and never trimmed.")]
    private static T? Create<T>(string typeName, object?[] arguments) where T : class
    {
        var assembly = Load();
        var type = assembly?.GetType($"{Namespace}.{typeName}");
        if (type is null) return null;

        try
        {
            return Activator.CreateInstance(type, arguments) as T;
        }
        catch (Exception e) when (e is MissingMethodException or TargetInvocationException)
        {
            return null;
        }
    }

    private static Assembly? Load()
    {
        if (_attempted) return _assembly;
        _attempted = true;

        // Absent on macOS and Linux, which is expected and must stay silent — this runs on
        // every launch of the headless test host.
        try
        {
            _assembly = Assembly.Load(AssemblyName);
        }
        catch (Exception e) when (e is FileNotFoundException or BadImageFormatException)
        {
            _assembly = null;
        }

        return _assembly;
    }
}
