using System.Runtime.InteropServices;
using TalkToText.Core;
using TalkToText.Dictionary;
using TalkToText.Speech;

namespace TalkToText.App;

/// <summary>
/// A headless check that the published binary actually works.
/// </summary>
/// <remarks>
/// Run in CI against the real single-file executable. It cannot show a window on a runner, so
/// it verifies the things that only break <i>after</i> publishing: assemblies resolving out
/// of the bundle, native libraries extracting, and paths resolving against
/// <c>AppContext.BaseDirectory</c> rather than the current directory.
/// </remarks>
public static class SelfTest
{
    /// <summary>Runs the checks.</summary>
    /// <returns>0 if everything passed.</returns>
    public static int Run()
    {
        var failures = 0;

        failures += CheckDictionary();
        failures += CheckStorage();
        failures += CheckModelDiscovery();
        failures += CheckPlatformLayer();

        Console.WriteLine(failures == 0 ? "self-test: PASS" : $"self-test: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static int CheckDictionary()
    {
        var failures = 0;

        var corrector = new DictionaryCorrector([DictionaryEntry.Correction("cloud code", "Claude Code")]);
        var (text, applied) = corrector.Apply("I use CloudCode daily.");

        failures += Check("dictionary rewrites glued words",
            text == "I use Claude Code daily." && applied.Count == 1);

        failures += Check("dictionary leaves similar words alone",
            corrector.Apply("Cloudflare is fine.").Text == "Cloudflare is fine.");

        return failures;
    }

    private static int CheckStorage()
    {
        // Exercises the source-generated JSON, which is the part most likely to have been
        // silently broken by trimming or single-file publishing.
        var directory = Path.Combine(Path.GetTempPath(), $"talktotext-selftest-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(directory);

            var store = new TranscriptStore(Path.Combine(directory, "transcripts.jsonl"));
            store.Add(new TranscriptRecord
            {
                At = DateTimeOffset.UtcNow,
                Text = "Claude Code",
                Corrections = [new AppliedCorrection("cloud code", "Claude Code", 1)],
            });

            var reopened = new TranscriptStore(Path.Combine(directory, "transcripts.jsonl"));
            var ok = reopened.Records.Count == 1
                     && reopened.Records[0].Corrections?[0].To == "Claude Code";

            var settings = new AppSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Data with { Shortcut = new Shortcut(0x7C) });
            var settingsOk = new AppSettings(Path.Combine(directory, "settings.json")).Data.Shortcut == new Shortcut(0x7C);

            return Check("history round-trips through source-generated JSON", ok)
                 + Check("settings round-trip", settingsOk);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private static int CheckModelDiscovery()
    {
        // A fresh machine has no model, and that must be a friendly message rather than a
        // crash on launch.
        var located = ParakeetTranscriber.Locate();
        Console.WriteLine($"  model: {located ?? "(not installed — expected on a clean runner)"}");

        return Check("model search paths are absolute",
            ParakeetTranscriber.DefaultSearchPaths().All(Path.IsPathRooted));
    }

    /// <summary>
    /// Verifies the platform layer resolves from inside the published bundle.
    /// </summary>
    /// <remarks>
    /// This is the riskiest thing in the app's structure. <c>TalkToText.App</c> targets plain
    /// <c>net10.0</c> and loads <c>TalkToText.Platform.Windows</c> by reflection, which is what
    /// lets the UI be built and headless-tested on macOS. Reflection into a single-file
    /// bundle is exactly where that arrangement would fail — and it would fail at the moment
    /// the user first pressed the hotkey, not at startup. So it is checked here, on Windows,
    /// against the real published executable.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "TalkToText.Platform.Windows ships whole beside the executable and is "
                      + "never trimmed, so its reference list is intact.")]
    private static int CheckPlatformLayer()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.WriteLine("  [skip] platform layer (not Windows)");
            return 0;
        }

        var failures = Check("Windows platform assembly loads from the bundle", PlatformFactory.IsAvailable);
        if (!PlatformFactory.IsAvailable) return failures;

        var capture = PlatformFactory.CreateAudioCapture();
        failures += Check("audio capture constructs", capture is not null);

        // Constructing is not enough: NAudio is only loaded when the microphone is first
        // opened, so a missing NAudio.Wasapi.dll once passed every check here and then failed
        // on the user's first key press. Load every dependency now instead.
        if (capture is not null)
        {
            foreach (var reference in capture.GetType().Assembly.GetReferencedAssemblies())
            {
                failures += Check($"platform dependency {reference.Name} loads", CanLoad(reference));
            }
        }
        failures += Check("text injector constructs", PlatformFactory.CreateTextInjector() is not null);

        // The cloud AI's API keys: encrypted and read back, or a saved key would be lost.
        var protector = PlatformFactory.CreateSecretProtector();
        failures += Check(
            "API keys are encrypted with DPAPI",
            protector is not null
            && System.Text.Encoding.UTF8.GetString(protector.Unprotect(protector.Protect(System.Text.Encoding.UTF8.GetBytes("sk-test")))) == "sk-test");

        // Summaries: llama.cpp's native library must load out of the published bundle.
        var llama = Speech.SummaryModel.CheckNativeLibrary();
        if (llama is not null) Console.WriteLine($"         {llama}");
        failures += Check("summary engine (llama.cpp) loads", llama is null);
        Console.WriteLine($"  summary model: {(Speech.SummaryModel.IsInstalled() ? Speech.SummaryModel.PathOnDisk : "(not downloaded — fetched on first summary)")}");

        // Audio files: NAudio's readers load from the bundle and decode a real WAV to 16 kHz.
        failures += Check("audio file decoder reads a WAV", CanDecodeWav());

        // Meetings: each piece constructs, and a real snapshot of running apps can be taken.
        failures += Check("speaker capture constructs", PlatformFactory.CreateLoopbackCapture() is not null);
        failures += Check("Teams speaker reader constructs", PlatformFactory.CreateActiveSpeakerSource() is not null);

        var signals = PlatformFactory.CreateMeetingSignals();
        failures += Check("meeting signals construct", signals is not null);
        if (signals is not null)
        {
            var apps = signals.Snapshot();
            Console.WriteLine($"  apps visible: {apps.Count}; with microphone open: {apps.Count(a => a.UsingMicrophone)}");
            failures += Check("meeting signals snapshot", apps.Count > 0);
        }

        // Constructed, not started: installing a real low-level keyboard hook on a CI runner
        // is neither useful nor polite.
        var hotkey = PlatformFactory.CreateHotkeySource();
        failures += Check("keyboard hook constructs", hotkey is not null);
        hotkey?.Dispose();

        return failures;
    }

    /// <summary>Writes one second of a 44.1 kHz stereo tone and reads it back as 16 kHz mono.</summary>
    private static bool CanDecodeWav()
    {
        var decoder = PlatformFactory.CreateAudioFileDecoder();
        if (decoder is null) return false;

        var path = Path.Combine(Path.GetTempPath(), $"talktotext-selftest-{Guid.NewGuid():N}.wav");
        try
        {
            const int Rate = 44100;
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                var data = Rate * 4;
                writer.Write("RIFF"u8); writer.Write(36 + data); writer.Write("WAVE"u8);
                writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)2);
                writer.Write(Rate); writer.Write(Rate * 4); writer.Write((short)4); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(data);
                for (var i = 0; i < Rate; i++)
                {
                    var sample = (short)(8000 * Math.Sin(i * 2 * Math.PI * 440 / Rate));
                    writer.Write(sample);
                    writer.Write(sample);
                }
            }

            using var audio = decoder.Open(path);
            var buffer = new float[32000];
            var total = 0;
            int read;
            while ((read = audio.Read(buffer, 0, buffer.Length)) > 0) total += read;

            // One second at 16 kHz, give or take the resampler's edges.
            return Math.Abs(total - 16000) < 200 && Math.Abs(audio.Duration.TotalSeconds - 1) < 0.01;
        }
        catch (Exception e)
        {
            Console.WriteLine($"         {e.Message}");
            return false;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool CanLoad(System.Reflection.AssemblyName name)
    {
        try
        {
            System.Reflection.Assembly.Load(name);
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            Console.WriteLine($"         {e.Message}");
            return false;
        }
    }

    private static int Check(string name, bool passed)
    {
        Console.WriteLine($"  [{(passed ? "ok" : "FAIL")}] {name}");
        return passed ? 0 : 1;
    }
}
