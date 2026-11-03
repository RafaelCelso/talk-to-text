using TalkToText.Speech;

namespace TalkToText.App;

/// <summary>
/// Downloads the speech model from inside the app — no terminal, no documentation to follow.
/// </summary>
/// <remarks>
/// <para>
/// One download for the whole app: the Home banner and Settings both show and start this same
/// one, so two buttons can never fetch 670 MB twice.
/// </para>
/// <para>
/// The installer downloads the model too, but that can fail — no connection, a proxy — and the
/// user then chose "continue without it". Before this, the only way back was a PowerShell
/// script from the docs, and the docs' example fetched the English-only v2.
/// </para>
/// <para>
/// Call from the UI thread: progress is reported through a <see cref="Progress{T}"/> made
/// there, so <see cref="Changed"/> is always raised on the UI thread.
/// </para>
/// </remarks>
internal static class SpeechModelDownload
{
    private static CancellationTokenSource? _cancel;

    /// <summary>Whether a download is in progress.</summary>
    public static bool IsRunning => _cancel is not null;

    /// <summary>0…1 of the whole download.</summary>
    public static double Fraction { get; private set; }

    /// <summary>Why the last download failed, for the user; null when it did not.</summary>
    public static string? Error { get; private set; }

    /// <summary>Started, advanced, finished or failed. UI thread.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Downloads every missing file of the model and switches the app over to it. Does nothing
    /// if a download is already running.
    /// </summary>
    public static async Task StartAsync(Composition composition)
    {
        if (_cancel is not null) return;

        var cancel = new CancellationTokenSource();
        _cancel = cancel;
        Fraction = 0;
        Error = null;
        Notify();

        try
        {
            long before = 0;
            foreach (var (file, url, size) in SpeechModel.Downloads)
            {
                var done = before;
                await MeetingCoordinator.DownloadFileAsync(
                    url, Path.Combine(SpeechModel.Directory, file), size,
                    new Progress<long>(bytes =>
                    {
                        Fraction = (double)(done + bytes) / SpeechModel.TotalSize;
                        Notify();
                    }),
                    cancel.Token).ConfigureAwait(true);
                before += size;
            }

            DiagnosticLog.Write("speech model downloaded from the app");
            await composition.ReloadSpeechModelAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Write("speech model download cancelled");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"speech model download failed: {e.Message}");
            Error = "Não foi possível baixar o modelo de voz. Verifique a internet e tente de novo.";
        }
        finally
        {
            _cancel = null;
            cancel.Dispose();
            Notify();
        }
    }

    /// <summary>Stops the download; what was fully downloaded is kept for next time.</summary>
    public static void Cancel() => _cancel?.Cancel();

    private static void Notify() => Changed?.Invoke(null, EventArgs.Empty);
}
