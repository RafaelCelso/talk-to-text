using TalkToText.Abstractions;
using TalkToText.Core;
using TalkToText.Testing;
using Shouldly;
using Xunit;

namespace TalkToText.CoreTests;

/// <summary>Recognising a meeting from running apps.</summary>
public sealed class MeetingDetectorTests
{
    private static AppActivity App(string process, bool mic, params string[] titles) => new(process, titles, mic);

    [Fact]
    public void Teams_with_the_microphone_open_is_a_meeting_and_its_title_is_cleaned()
    {
        var meeting = MeetingDetector.Detect(
        [
            App("ms-teams", mic: true, "Chat | Microsoft Teams", "Reunião semanal | Microsoft Teams"),
        ]);

        meeting.ShouldNotBeNull();
        meeting.App.ShouldBe("Microsoft Teams");
        meeting.Title.ShouldBe("Reunião semanal");
    }

    [Fact]
    public void Teams_open_without_the_microphone_is_just_Teams()
    {
        MeetingDetector.Detect([App("ms-teams", mic: false, "Chat | Microsoft Teams")]).ShouldBeNull();
    }

    [Fact]
    public void Packaged_teams_reported_by_package_name_counts_too()
    {
        MeetingDetector.Detect([App("MSTeams", mic: true)])!.App.ShouldBe("Microsoft Teams");
    }

    [Fact]
    public void A_browser_needs_a_meeting_title_as_well_as_the_microphone()
    {
        MeetingDetector.Detect([App("chrome", mic: true, "YouTube - Google Chrome")]).ShouldBeNull();

        var meet = MeetingDetector.Detect([App("chrome", mic: true, "Meet - abc-defg-hij - Google Chrome")]);
        meet.ShouldNotBeNull();
        meet.App.ShouldBe("Google Meet");
        meet.Title.ShouldBe("abc-defg-hij");
    }

    [Fact]
    public void This_apps_own_microphone_is_never_a_meeting()
    {
        MeetingDetector.Detect([App("TalkToText", mic: true, "Talk-to-Text")]).ShouldBeNull();
    }
}

/// <summary>Start and end, with hysteresis.</summary>
public sealed class MeetingWatcherTests
{
    private static readonly DetectedMeeting Teams = new("Microsoft Teams", "ms-teams", "Daily");

    [Fact]
    public void Starts_after_two_sightings_and_ends_after_four_misses()
    {
        var watcher = new MeetingWatcher();
        var events = new List<string>();
        watcher.Started += (_, m) => events.Add("start " + m.Title);
        watcher.Ended += (_, m) => events.Add("end " + m.Title);

        watcher.Observe(Teams);
        events.ShouldBeEmpty("one sighting could be a microphone opened for a second");
        watcher.Observe(Teams);
        events.ShouldBe(["start Daily"]);

        // A mute or a glitch must not cut the transcript in two.
        watcher.Observe(null);
        watcher.Observe(null);
        watcher.Observe(Teams);
        watcher.Observe(null);
        watcher.Observe(null);
        watcher.Observe(null);
        events.ShouldBe(["start Daily"]);

        watcher.Observe(null);
        events.ShouldBe(["start Daily", "end Daily"]);
        watcher.Current.ShouldBeNull();
    }
}

/// <summary>Grouping utterances by voice.</summary>
public sealed class SpeakerClustererTests
{
    [Fact]
    public void Similar_prints_join_one_speaker_and_different_ones_start_another()
    {
        var clusterer = new SpeakerClusterer([]);

        clusterer.Assign([1, 0.1f, 0]).Cluster.ShouldBe(0);
        clusterer.Assign([0.95f, 0.15f, 0]).Cluster.ShouldBe(0);
        clusterer.Assign([0, 1, 0]).Cluster.ShouldBe(1);
        clusterer.Count(0).ShouldBe(2);
    }

    [Fact]
    public void A_remembered_voice_comes_back_with_its_name()
    {
        var known = new KnownVoice { Name = "Ana", Embedding = Vectors.Normalized([0, 0, 1]), Count = 3 };
        var clusterer = new SpeakerClusterer([known]);

        clusterer.Assign([1, 0, 0]).KnownName.ShouldBeNull();
        clusterer.Assign([0, 0.1f, 1]).KnownName.ShouldBe("Ana");
    }

    [Fact]
    public void Similarity_threshold_matches_the_measured_bands()
    {
        // WeSpeaker scored one voice 0.86–0.94 against itself and two voices 0.50–0.55.
        SpeakerClusterer.DefaultThreshold.ShouldBeGreaterThan(0.55);
        SpeakerClusterer.DefaultThreshold.ShouldBeLessThan(0.86);
    }
}

/// <summary>Remembered voices on disk.</summary>
public sealed class KnownVoicesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ttt-voices-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Naming_someone_twice_refines_one_voice_and_survives_a_restart()
    {
        var voices = new KnownVoices(_path);
        voices.Remember("Ana", [1, 0, 0]);
        voices.Remember("ana", [0.8f, 0.6f, 0]);

        var reopened = new KnownVoices(_path).All;
        reopened.ShouldHaveSingleItem();
        reopened[0].Count.ShouldBe(2);
        reopened[0].Embedding[1].ShouldBeGreaterThan(0);
    }
}

/// <summary>Parsing "X is speaking" labels from a meeting app.</summary>
public sealed class SpeakingLabelTests
{
    [Theory]
    [InlineData("Ana Silva está falando", "Ana Silva")]
    [InlineData("Ana Silva, câmera ligada, está falando", "Ana Silva")]
    [InlineData("John Smith is speaking", "John Smith")]
    [InlineData("Maria, speaking", "Maria")]
    public void Names_the_speaker(string label, string name) => SpeakingLabel.NameIn(label).ShouldBe(name);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ana Silva, câmera ligada")]
    [InlineData("Speaking")]
    public void Says_nothing_when_nobody_is_speaking(string? label) => SpeakingLabel.NameIn(label).ShouldBeNull();
}

/// <summary>The whole meeting pipeline, with fakes.</summary>
public sealed class MeetingSessionTests : IDisposable
{
    private static readonly DetectedMeeting Teams = new("Microsoft Teams", "ms-teams", "Daily");

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ttt-meeting-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private MeetingSession Session(
        IAudioCapture microphone,
        IAudioCapture speakers,
        FakeTranscriber transcriber,
        IActiveSpeakerSource? activeSpeaker = null,
        KnownVoices? voices = null,
        IClock? clock = null,
        TimeSpan? captureStopLimit = null)
    {
        return new MeetingSession(
            Teams, "Rafael", microphone, speakers,
            new FakeSpeechSegmenter(), new FakeSpeechSegmenter(), new FakeSpeakerEmbedder(),
            transcriber, voices ?? new KnownVoices(Path.Combine(_root, "voices.json")),
            new MeetingStore(Path.Combine(_root, "meetings")), activeSpeaker, clock ?? new FakeClock(), captureStopLimit);
    }

    [Fact]
    public async Task The_microphone_is_the_user()
    {
        await using var session = Session(
            FakeAudioCapture.Tone(1.0, 0.5f), FakeAudioCapture.Silence(1.0), new FakeTranscriber("Bom dia"));

        session.Start();
        await Task.Delay(200);
        await session.StopAsync();

        var transcript = session.Snapshot();
        transcript.Messages.ShouldHaveSingleItem();
        transcript.Messages[0].Text.ShouldBe("Bom dia");
        transcript.Speaker(transcript.Messages[0].SpeakerId)!.Name.ShouldBe("Rafael");
        transcript.EndedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Different_voices_from_the_speakers_become_different_participants()
    {
        await using var session = Session(
            FakeAudioCapture.Silence(1.0), FakeAudioCapture.Tone(1.0, 0.5f), new FakeTranscriber("Olá"));

        session.Start();
        await Task.Delay(200);
        await session.StopAsync();

        var transcript = session.Snapshot();
        transcript.Messages.ShouldHaveSingleItem();
        var speaker = transcript.Speaker(transcript.Messages[0].SpeakerId)!;
        speaker.IsLocal.ShouldBeFalse();
        speaker.Name.ShouldBe("Participante 1");
        speaker.IsNamed.ShouldBeFalse();
    }

    [Fact]
    public async Task The_microphone_hearing_the_speakers_is_echo_and_is_dropped()
    {
        await using var session = Session(
            FakeAudioCapture.Tone(1.0, 0.5f), FakeAudioCapture.Tone(1.0, 0.5f), new FakeTranscriber("eco"));

        session.Start();
        await Task.Delay(200);
        await session.StopAsync();

        var transcript = session.Snapshot();
        transcript.Messages.ShouldHaveSingleItem("only the remote copy should remain");
        transcript.Speaker(transcript.Messages[0].SpeakerId)!.IsLocal.ShouldBeFalse();
    }

    [Fact]
    public async Task The_meeting_app_saying_who_talks_names_the_participant()
    {
        // Paced like a real call: hints are sampled while the speech is playing, and only
        // count for the utterance they overlap.
        await using var session = Session(
            FakeAudioCapture.Silence(1.0), new PacedCapture(FakeAudioCapture.Tone(1.0, 0.5f)), new FakeTranscriber("Oi"),
            new FakeActiveSpeaker("Ana Silva"), clock: SystemClock.Instance);

        session.Start();
        await Task.Delay(1800);
        await session.StopAsync();

        var transcript = session.Snapshot();
        transcript.Speaker(transcript.Messages[0].SpeakerId)!.Name.ShouldBe("Ana Silva");
    }

    [Fact]
    public async Task A_source_that_never_runs_dry_still_stops_on_time()
    {
        // The speakers' loopback once delivered padded silence without end. Waiting for the
        // next chunk was the only place the stop was checked, so the capture never stopped,
        // and the speech detector was then disposed under it: the app crashed after a call.
        var diagnostics = new List<string>();
        await using var session = Session(
            FakeAudioCapture.Silence(1.0), new EndlessCapture(), new FakeTranscriber("x"),
            captureStopLimit: TimeSpan.FromSeconds(3));
        session.Diagnostic += (_, line) => { lock (diagnostics) diagnostics.Add(line); };

        session.Start();
        await Task.Delay(200);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await session.StopAsync();

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        lock (diagnostics) diagnostics.ShouldNotContain(d => d.Contains("did not stop", StringComparison.Ordinal));
    }

    /// <summary>Hands out silent chunks as fast as they are taken, never waiting.</summary>
    private sealed class EndlessCapture : IAudioCapture
    {
        public bool IsCapturing => true;

        public async IAsyncEnumerable<AudioChunk> CaptureAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            var silence = new float[320];
            while (true) yield return new AudioChunk(silence);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_source_that_never_stops_cannot_keep_the_meeting_live()
    {
        // A Teams call once ended and stopping never returned, so the meeting stayed "live"
        // and its duration counted on for hours.
        using var release = new ManualResetEventSlim();
        await using var session = Session(
            FakeAudioCapture.Silence(1.0), FakeAudioCapture.Silence(1.0), new FakeTranscriber("x"),
            new HangingActiveSpeaker(release), captureStopLimit: TimeSpan.FromMilliseconds(200));

        session.Start();
        await Task.Delay(100);
        var stopping = session.StopAsync();
        var finished = await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(10)));
        release.Set();

        finished.ShouldBe(stopping);
        session.Snapshot().EndedAt.ShouldNotBeNull();
        new MeetingStore(Path.Combine(_root, "meetings")).All().ShouldHaveSingleItem().EndedAt.ShouldNotBeNull();
    }

    /// <summary>Blocks inside the lookup, as a UI Automation call into a closing window can.</summary>
    private sealed class HangingActiveSpeaker(ManualResetEventSlim release) : IActiveSpeakerSource
    {
        public string? CurrentSpeaker(string processName)
        {
            release.Wait();
            return null;
        }
    }

    [Fact]
    public void Meetings_cut_off_by_the_app_closing_end_at_their_last_message()
    {
        var store = new MeetingStore(Path.Combine(_root, "meetings"));
        var start = new DateTimeOffset(2026, 11, 1, 9, 38, 0, TimeSpan.Zero);
        store.Save(new MeetingTranscript
        {
            StartedAt = start,
            Messages = [new MeetingMessage { At = start.AddSeconds(20) }, new MeetingMessage { At = start.AddSeconds(45) }],
        });
        store.Save(new MeetingTranscript { StartedAt = start.AddHours(-1) });
        store.Save(new MeetingTranscript { StartedAt = start.AddHours(-2), EndedAt = start.AddHours(-1) });

        store.CloseUnfinished().ShouldBe(2);

        var all = store.All();
        all[0].EndedAt.ShouldBe(start.AddSeconds(45));
        all[1].EndedAt.ShouldBe(start.AddHours(-1), "nothing said: it ends where it began");
        all[2].EndedAt.ShouldBe(start.AddHours(-1), "already ended: left alone");
        store.CloseUnfinished().ShouldBe(0);
    }

    /// <summary>Delivers each 20 ms chunk 20 ms apart, as a live source does.</summary>
    private sealed class PacedCapture(FakeAudioCapture inner) : IAudioCapture
    {
        public bool IsCapturing => inner.IsCapturing;

        public async IAsyncEnumerable<AudioChunk> CaptureAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var chunk in inner.CaptureAsync(cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(chunk.Duration, cancellationToken).ConfigureAwait(false);
                yield return chunk;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Renaming_with_remember_is_recognised_in_the_next_meeting()
    {
        var voices = new KnownVoices(Path.Combine(_root, "voices.json"));

        await using (var first = Session(
            FakeAudioCapture.Silence(1.0), FakeAudioCapture.Tone(1.0, 0.5f), new FakeTranscriber("Oi"), voices: voices))
        {
            first.Start();
            await Task.Delay(200);
            await first.StopAsync();

            var speaker = first.Snapshot().Speakers.Single(s => !s.IsLocal);
            first.Rename(speaker.Id, "Bruno", rememberVoice: true);
            first.Snapshot().Speaker(speaker.Id)!.Name.ShouldBe("Bruno");
        }

        await using var second = Session(
            FakeAudioCapture.Silence(1.0), FakeAudioCapture.Tone(1.0, 0.5f), new FakeTranscriber("De novo"), voices: voices);
        second.Start();
        await Task.Delay(200);
        await second.StopAsync();

        var again = second.Snapshot();
        again.Speaker(again.Messages[0].SpeakerId)!.Name.ShouldBe("Bruno");
    }

    [Fact]
    public async Task The_transcript_is_saved_and_reads_back_as_plain_text()
    {
        var store = new MeetingStore(Path.Combine(_root, "meetings"));
        await using var session = Session(
            FakeAudioCapture.Tone(1.0, 0.5f), FakeAudioCapture.Silence(1.0), new FakeTranscriber("Salvo"));

        session.Start();
        await Task.Delay(200);
        await session.StopAsync();

        var saved = store.All().ShouldHaveSingleItem();
        saved.Title.ShouldBe("Daily");
        saved.ToPlainText().ShouldContain("Rafael: Salvo");
    }
}
