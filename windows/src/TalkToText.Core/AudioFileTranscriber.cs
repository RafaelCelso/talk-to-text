using TalkToText.Abstractions;
using TalkToText.Dictionary;

namespace TalkToText.Core;

/// <summary>How far a file transcription has got.</summary>
/// <param name="Done">Audio read so far.</param>
/// <param name="Total">Length of the file.</param>
/// <param name="Messages">Messages transcribed so far.</param>
public readonly record struct FileTranscriptionProgress(TimeSpan Done, TimeSpan Total, int Messages)
{
    /// <summary>0…1.</summary>
    public double Fraction => Total <= TimeSpan.Zero ? 0 : Math.Clamp(Done / Total, 0, 1);
}

/// <summary>
/// Transcribes a recorded audio file into the same chat-shaped transcript a meeting produces:
/// cut into turns by voice activity, each turn given to a speaker by voice print, and each
/// one transcribed and run through the dictionary.
/// </summary>
/// <remarks>
/// The file is read a second at a time and never held whole: an hour of 16 kHz float is
/// 230 MB. Cancellation is checked between blocks and between turns, so stopping is prompt
/// and what was already transcribed stays in <see cref="MeetingTranscript.Messages"/>.
/// </remarks>
public sealed class AudioFileTranscriber
{
    private const int Block = AudioChunk.SampleRate;

    private readonly ITranscriber _transcriber;
    private readonly ISpeechSegmenter _segmenter;
    private readonly ISpeakerEmbedder? _embedder;
    private readonly SpeakerClusterer _clusterer;
    private readonly IReadOnlyList<DictionaryEntry> _dictionary;

    /// <summary>Sets up one transcription; the segmenter is used for this file only.</summary>
    /// <param name="transcriber">The speech model.</param>
    /// <param name="segmenter">Voice activity, fresh for this file.</param>
    /// <param name="embedder">Voice prints, to tell speakers apart; null puts every turn under one speaker.</param>
    /// <param name="knownVoices">Voices named in earlier meetings, recognised by name here too.</param>
    /// <param name="dictionary">Terms to bias toward and corrections to apply.</param>
    public AudioFileTranscriber(
        ITranscriber transcriber,
        ISpeechSegmenter segmenter,
        ISpeakerEmbedder? embedder,
        IReadOnlyList<KnownVoice> knownVoices,
        IReadOnlyList<DictionaryEntry> dictionary)
    {
        _transcriber = transcriber;
        _segmenter = segmenter;
        _embedder = embedder;
        _clusterer = new SpeakerClusterer(knownVoices);
        _dictionary = dictionary;
    }

    /// <summary>A transcript ready to be filled from the file at <paramref name="path"/>.</summary>
    public static MeetingTranscript NewTranscript(string path, TimeSpan duration, DateTimeOffset now) => new()
    {
        App = "Arquivo de áudio",
        Title = Path.GetFileNameWithoutExtension(path),
        SourceFile = path,
        StartedAt = now,
        EndedAt = now + duration,
    };

    /// <summary>
    /// Reads <paramref name="audio"/> to the end, adding a message to
    /// <paramref name="transcript"/> for every turn. Throws
    /// <see cref="OperationCanceledException"/> when stopped, with the messages so far kept.
    /// </summary>
    public async Task TranscribeAsync(
        IDecodedAudio audio,
        MeetingTranscript transcript,
        IProgress<FileTranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!_transcriber.IsReady) await _transcriber.LoadAsync(cancellationToken).ConfigureAwait(false);

        var bias = DictionaryCorrector.BiasPhrases(_dictionary);
        var corrector = new DictionaryCorrector(_dictionary);
        var speakerByCluster = new Dictionary<int, int>();
        var buffer = new float[Block];
        long read = 0;

        async Task HandleAsync(IReadOnlyList<SpeechSpan> spans)
        {
            foreach (var span in spans)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var raw = (await _transcriber.TranscribeAsync(span.Samples, bias, cancellationToken).ConfigureAwait(false)).Trim();
                if (raw.Length == 0 || SpeechNoise.IsNoise(raw, span.Duration.TotalSeconds)) continue;

                var speakerId = SpeakerFor(span, transcript, speakerByCluster);
                lock (transcript)
                {
                    transcript.Messages.Add(new MeetingMessage
                    {
                        SpeakerId = speakerId,
                        At = transcript.StartedAt + span.Start,
                        Text = corrector.Apply(raw).Text,
                    });
                }
            }
        }

        int count;
        while ((count = audio.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            read += count;

            await HandleAsync(_segmenter.Accept(buffer.AsSpan(0, count))).ConfigureAwait(false);
            progress?.Report(new FileTranscriptionProgress(Seconds(read), audio.Duration, transcript.Messages.Count));
        }

        await HandleAsync(_segmenter.Flush()).ConfigureAwait(false);
        progress?.Report(new FileTranscriptionProgress(audio.Duration, audio.Duration, transcript.Messages.Count));
    }

    /// <summary>The speaker for <paramref name="span"/>, adding "Pessoa N" the first time a voice is heard.</summary>
    private int SpeakerFor(SpeechSpan span, MeetingTranscript transcript, Dictionary<int, int> speakerByCluster)
    {
        var (cluster, knownName) = _embedder is null ? (0, null) : _clusterer.Assign(_embedder.Embed(span.Samples), span.Duration.TotalSeconds);

        lock (transcript)
        {
            if (!speakerByCluster.TryGetValue(cluster, out var id))
            {
                id = transcript.Speakers.Count == 0 ? 1 : transcript.Speakers.Max(s => s.Id) + 1;
                transcript.Speakers.Add(new MeetingSpeaker
                {
                    Id = id,
                    Name = knownName ?? $"Pessoa {transcript.Speakers.Count + 1}",
                    IsNamed = knownName is not null,
                });
                speakerByCluster[cluster] = id;
            }

            // Kept with the transcript: naming this person later can still teach the app their voice.
            if (_embedder is not null && transcript.Speaker(id) is { } speaker)
            {
                speaker.Voice = _clusterer.Centroid(cluster);
                speaker.VoiceCount = _clusterer.Count(cluster);
            }

            return id;
        }
    }

    private static TimeSpan Seconds(long samples) => TimeSpan.FromSeconds((double)samples / AudioChunk.SampleRate);
}
