using System.Text.Json;
using System.Text.Json.Serialization;

namespace TalkToText.Core;

/// <summary>A voice the user has named, remembered across meetings.</summary>
public sealed class KnownVoice
{
    /// <summary>The person's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The average voice print of everything attributed to them.</summary>
    public float[] Embedding { get; set; } = [];

    /// <summary>How many prints were averaged into <see cref="Embedding"/>.</summary>
    public int Count { get; set; }
}

/// <summary>Named voices, persisted as JSON in the data folder.</summary>
public sealed class KnownVoices
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private List<KnownVoice> _voices;

    /// <summary>Loads <paramref name="path"/>, or starts empty.</summary>
    public KnownVoices(string path)
    {
        _path = path;
        _voices = Load(path);
    }

    /// <summary><c>%LOCALAPPDATA%\TalkToText\voices.json</c>.</summary>
    public static string DefaultPath => Path.Combine(DataFolder.Path, "voices.json");

    /// <summary>A copy of every remembered voice.</summary>
    public IReadOnlyList<KnownVoice> All
    {
        get { lock (_gate) return [.. _voices]; }
    }

    /// <summary>Remembers <paramref name="embedding"/> as <paramref name="name"/>'s voice.</summary>
    /// <remarks>
    /// Naming the same person again refines their print rather than replacing it: voices
    /// sound different over different microphones and connections, and the average of several
    /// meetings matches better than any one of them.
    /// </remarks>
    public void Remember(string name, float[] embedding, int count = 1)
    {
        lock (_gate)
        {
            var existing = _voices.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                _voices.Add(new KnownVoice { Name = name, Embedding = Vectors.Normalized(embedding), Count = count });
            }
            else
            {
                var total = existing.Count + count;
                var merged = new float[embedding.Length];
                var normalized = Vectors.Normalized(embedding);
                for (var i = 0; i < merged.Length; i++)
                {
                    merged[i] = ((existing.Embedding[i] * existing.Count) + (normalized[i] * count)) / total;
                }

                existing.Embedding = Vectors.Normalized(merged);
                existing.Count = total;
            }

            Save();
        }
    }

    /// <summary>Forgets <paramref name="name"/>.</summary>
    public void Forget(string name)
    {
        lock (_gate)
        {
            _voices.RemoveAll(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_voices, MeetingJsonContext.Default.ListKnownVoice));
    }

    private static List<KnownVoice> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), MeetingJsonContext.Default.ListKnownVoice) ?? []
                : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>Small vector helpers for voice prints.</summary>
public static class Vectors
{
    /// <summary>Cosine similarity: 1 for the same direction, 0 for unrelated.</summary>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>A unit-length copy of <paramref name="v"/>.</summary>
    public static float[] Normalized(ReadOnlySpan<float> v)
    {
        double norm = 0;
        foreach (var x in v) norm += x * x;
        norm = Math.Sqrt(norm);

        var result = new float[v.Length];
        if (norm == 0) return result;
        for (var i = 0; i < v.Length; i++) result[i] = (float)(v[i] / norm);
        return result;
    }
}

/// <summary>
/// Groups utterances by voice as they arrive, recognising voices named in past meetings.
/// </summary>
/// <remarks>
/// <para>
/// Online, not offline: the chat is live, so each utterance must be attributed when it
/// arrives rather than after the meeting. Each speaker is a running mean of their prints.
/// </para>
/// <para>
/// <b>Threshold 0.65</b>, measured with WeSpeaker ResNet34: one voice scored 0.86–0.94
/// against itself and two different voices 0.50–0.55. Call audio is compressed, which pulls
/// same-voice scores down, hence the threshold sits nearer the lower band than the midpoint.
/// </para>
/// <para>
/// <b>Only clear, long prints shape a speaker.</b> A real Teams call merged two people into
/// one: once a print of the second voice joined the first speaker's mean, the mean drifted
/// towards both and pulled in more of the second. A print now only moves the mean when it is
/// long enough to be reliable and clearly matches; a borderline one is attributed but leaves
/// the mean alone. And a short utterance — "uhum", a cough, half a word — never starts a new
/// speaker: the same call grew seven phantom participants out of those.
/// </para>
/// </remarks>
public sealed class SpeakerClusterer
{
    /// <summary>Similarity above which two prints are taken to be one person.</summary>
    public const double DefaultThreshold = 0.65;

    /// <summary>Utterances shorter than this never start a new speaker.</summary>
    public const double MinNewSpeakerSeconds = 1.5;

    /// <summary>A print shorter than this is too noisy to move a speaker's mean.</summary>
    private const double ReliableSeconds = 2.0;

    /// <summary>How far above the threshold a print must score to move a settled mean.</summary>
    private const double ClearMargin = 0.1;

    /// <summary>Prints a speaker takes unconditionally before the mean counts as settled.</summary>
    private const int Settling = 3;

    private readonly List<Cluster> _clusters = [];
    private readonly IReadOnlyList<KnownVoice> _known;
    private readonly double _threshold;

    /// <summary>Creates a clusterer that recognises <paramref name="known"/> voices.</summary>
    public SpeakerClusterer(IReadOnlyList<KnownVoice> known, double threshold = DefaultThreshold)
    {
        _known = known;
        _threshold = threshold;
    }

    /// <summary>Attributes one utterance's print to a speaker, creating one if it is new.</summary>
    /// <param name="embedding">The utterance's voice print.</param>
    /// <param name="seconds">How long the utterance is; short ones are trusted less.</param>
    /// <returns>The speaker's cluster id and, if recognised, the remembered name.</returns>
    public (int Cluster, string? KnownName) Assign(float[] embedding, double seconds = ReliableSeconds)
    {
        var print = Vectors.Normalized(embedding);
        var best = Nearest(print);

        if (best.Cluster is not null && best.Score >= _threshold)
        {
            Learn(best.Cluster, print, seconds, best.Score);
            return (best.Cluster.Id, best.Cluster.KnownName);
        }

        var known = _known
            .Select(v => (Voice: v, Score: Vectors.Cosine(v.Embedding, print)))
            .Where(x => x.Score >= _threshold)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Voice)
            .FirstOrDefault();

        // A remembered person already in this meeting under a slightly different print.
        if (known is not null && _clusters.FirstOrDefault(c => c.KnownName == known.Name) is { } same)
        {
            Learn(same, print, seconds, _threshold);
            return (same.Id, same.KnownName);
        }

        // Too short to be sure it is someone new: the closest voice said it.
        if (known is null && seconds < MinNewSpeakerSeconds && best.Cluster is not null)
        {
            return (best.Cluster.Id, best.Cluster.KnownName);
        }

        return StartNew(print, known?.Name);
    }

    /// <summary>
    /// Attributes a print to <paramref name="cluster"/> without asking the voice — the meeting
    /// app said who was talking.
    /// </summary>
    public void AssignTo(int cluster, float[] embedding, double seconds)
    {
        var print = Vectors.Normalized(embedding);
        var target = _clusters[cluster];
        Learn(target, print, seconds, Vectors.Cosine(target.Centroid, print));
    }

    /// <summary>Starts a new speaker from <paramref name="embedding"/>, whatever it resembles.</summary>
    public int StartNew(float[] embedding) => StartNew(Vectors.Normalized(embedding), null).Cluster;

    /// <summary>The current average print of <paramref name="cluster"/>.</summary>
    public float[] Centroid(int cluster) => _clusters[cluster].Centroid;

    /// <summary>How many prints were averaged into <paramref name="cluster"/>.</summary>
    public int Count(int cluster) => _clusters[cluster].Count;

    private (Cluster? Cluster, double Score) Nearest(float[] print) => _clusters
        .Select(c => (Cluster: (Cluster?)c, Score: Vectors.Cosine(c.Centroid, print)))
        .OrderByDescending(x => x.Score)
        .FirstOrDefault();

    private (int Cluster, string? KnownName) StartNew(float[] print, string? knownName)
    {
        var cluster = new Cluster(_clusters.Count, print, knownName);
        _clusters.Add(cluster);
        return (cluster.Id, cluster.KnownName);
    }

    private void Learn(Cluster cluster, float[] print, double seconds, double score)
    {
        if (seconds < ReliableSeconds) return;
        if (cluster.Count >= Settling && score < _threshold + ClearMargin) return;
        cluster.Add(print);
    }

    private sealed class Cluster(int id, float[] first, string? knownName)
    {
        private readonly float[] _sum = (float[])first.Clone();

        public int Id { get; } = id;

        public string? KnownName { get; } = knownName;

        public int Count { get; private set; } = 1;

        public float[] Centroid => Vectors.Normalized(_sum);

        public void Add(float[] print)
        {
            for (var i = 0; i < _sum.Length; i++) _sum[i] += print[i];
            Count++;
        }
    }
}
