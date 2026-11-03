using Avalonia;

namespace TalkToText.App;

/// <summary>Entry point.</summary>
public static class Program
{
    /// <summary>Starts the app, or runs a headless self-test.</summary>
    /// <param name="args">Command line. <c>--selftest</c> exits without showing UI.</param>
    /// <returns>0 on success.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        PlatformFactory.InstallResolver();

        // The published single-file exe is the only artifact CI can run end to end, and a
        // GitHub runner cannot show a window. This branch exercises startup — assembly
        // loading, native library resolution out of the self-extracted bundle, model
        // discovery — and exits, which is the class of failure that only appears after
        // publishing.
        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            return SelfTest.Run();
        }

        // Times the local AI on this machine: --benchmark-ai cpu|gpu, one per process, since
        // llama.cpp's build is chosen once.
        if (args.Length > 0 && args[0].Equals("--try-ai", StringComparison.OrdinalIgnoreCase))
        {
            return AiBenchmark.TryDictationAi(gpu: !(args.Length > 1 && args[1].Equals("cpu", StringComparison.OrdinalIgnoreCase)));
        }

        if (args.Length > 1 && args[0].Equals("--export-pdf", StringComparison.OrdinalIgnoreCase))
        {
            return AiBenchmark.ExportNewestPdf(args[1]);
        }

        if (args.Length > 0 && args[0].Equals("--read-selection", StringComparison.OrdinalIgnoreCase))
        {
            return AiBenchmark.ReadSelection();
        }

        if (args.Length > 0 && args[0].Equals("--benchmark-ai", StringComparison.OrdinalIgnoreCase))
        {
            return AiBenchmark.Run(
                args.Length > 1 && args[1].Equals("gpu", StringComparison.OrdinalIgnoreCase),
                args.Length > 2 && uint.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out var batch) ? batch : 512);
        }

        // One copy only. A second launch — the Start Menu entry clicked again, say — brings
        // the running window forward instead of starting a rival that also hooks the keyboard.
        using var instance = SingleInstance.TryClaim();
        if (instance is null)
        {
            SingleInstance.SignalRunning();
            return 0;
        }

        Instance = instance;

        AppDomain.CurrentDomain.UnhandledException +=
            (_, e) => DiagnosticLog.Write($"unhandled: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException +=
            (_, e) => DiagnosticLog.Write($"unobserved task: {e.Exception}");

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>This process's claim on being the only copy; null under tests.</summary>
    internal static SingleInstance? Instance { get; private set; }

    /// <summary>Configures Avalonia. Also used by the headless test host.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
