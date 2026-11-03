using NAudio.Wave;
using TalkToText.Abstractions;

namespace TalkToText.Platform.Windows;

/// <summary>
/// Plays an audio file through the default output device, mono, with volume and pitch-keeping
/// speed. NAudio reads and plays; <see cref="TimeStretcher"/> does the speed.
/// </summary>
public sealed class AudioFilePlayer : IAudioPlayer
{
    private readonly AudioFileReader _reader;
    private readonly Pipeline _pipeline;
    private readonly WaveOutEvent _output = new() { DesiredLatency = 150 };
    private readonly Lock _gate = new();
    private volatile bool _stoppedByUs;

    /// <summary>Opens <paramref name="path"/>; throws when it cannot be read.</summary>
    public AudioFilePlayer(string path)
    {
        _reader = new AudioFileReader(path);
        try
        {
            _pipeline = new Pipeline(_reader, _gate);
            _output.Init(_pipeline);
        }
        catch
        {
            _output.Dispose();
            _reader.Dispose();
            throw;
        }

        _output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is { } error)
            {
                // The device failed; keep the position so Play can carry on from here.
                Failed?.Invoke(this, error);
                return;
            }

            if (_stoppedByUs) return;

            // Stopped on its own well before the end: not an ending. Report it, keep the place.
            var at = Position;
            if (at < Duration - TimeSpan.FromSeconds(1))
            {
                Failed?.Invoke(this, new InvalidOperationException($"a reprodução parou sozinha em {at.TotalSeconds:0} s de {Duration.TotalSeconds:0} s"));
                return;
            }

            // Played to the end: rewind, so Play starts over.
            lock (_gate) _pipeline.Seek(TimeSpan.Zero);
            Ended?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <inheritdoc />
    public event EventHandler? Ended;

    /// <inheritdoc />
    public event EventHandler<Exception>? Failed;

    /// <inheritdoc />
    public TimeSpan Duration => _reader.TotalTime;

    /// <inheritdoc />
    public TimeSpan Position
    {
        get
        {
            lock (_gate) return _pipeline.Position;
        }
    }

    /// <inheritdoc />
    public bool IsPlaying => _output.PlaybackState == PlaybackState.Playing;

    /// <inheritdoc />
    public float Volume
    {
        get => _pipeline.Volume;
        set => _pipeline.Volume = Math.Clamp(value, 0, 1);
    }

    /// <inheritdoc />
    public double Speed
    {
        get => _pipeline.Speed;
        set
        {
            lock (_gate) _pipeline.Speed = value;
        }
    }

    /// <inheritdoc />
    public void Play()
    {
        _stoppedByUs = false;
        _output.Play();
    }

    /// <inheritdoc />
    public void Pause()
    {
        _stoppedByUs = true;
        _output.Pause();
    }

    /// <inheritdoc />
    public void StopAndRewind()
    {
        _stoppedByUs = true;
        _output.Stop();
        lock (_gate) _pipeline.Seek(TimeSpan.Zero);
    }

    /// <inheritdoc />
    public void Seek(TimeSpan position)
    {
        var target = position < TimeSpan.Zero ? TimeSpan.Zero : position > Duration ? Duration : position;
        lock (_gate) _pipeline.Seek(target);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stoppedByUs = true;
        _output.Dispose();
        _reader.Dispose();
    }

    /// <summary>File → mono → speed → volume, pulled by the output device on its own thread.</summary>
    private sealed class Pipeline : ISampleProvider
    {
        private readonly AudioFileReader _reader;
        private readonly ISampleProvider _mono;
        private readonly Lock _gate;
        private readonly TimeStretcher _stretcher;
        private float[] _input = [];
        private long _consumed;
        private bool _ended;

        public Pipeline(AudioFileReader reader, Lock gate)
        {
            _reader = reader;
            _gate = gate;
            _mono = reader.WaveFormat.Channels > 1 ? new DownmixSampleProvider(reader) : reader;
            _stretcher = new TimeStretcher(reader.WaveFormat.SampleRate);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(reader.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public float Volume { get; set; } = 1;

        public double Speed { get; set; } = 1;

        /// <summary>
        /// The position heard: input consumed, less what still waits in the stretcher. Ignores
        /// the device's own ~150 ms buffer, which is close enough for a progress bar.
        /// </summary>
        public TimeSpan Position =>
            TimeSpan.FromSeconds(Math.Max(0, _consumed - (_stretcher.Available * Speed)) / WaveFormat.SampleRate);

        public void Seek(TimeSpan position)
        {
            _reader.CurrentTime = position;
            _consumed = (long)(position.TotalSeconds * WaveFormat.SampleRate);
            _stretcher.Reset();
            _ended = false;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                var written = Math.Abs(Speed - 1) < 0.01 ? ReadStraight(buffer, offset, count) : ReadStretched(buffer, offset, count);

                var volume = Volume;
                if (volume < 0.999f)
                {
                    for (var i = 0; i < written; i++) buffer[offset + i] *= volume;
                }

                return written;
            }
        }

        private int ReadStraight(float[] buffer, int offset, int count)
        {
            // Whatever the stretcher still held from a faster speed goes out first.
            var written = _stretcher.Read(buffer.AsSpan(offset, count));
            if (written < count)
            {
                var read = _mono.Read(buffer, offset + written, count - written);
                _consumed += read;
                written += read;
            }

            return written;
        }

        private int ReadStretched(float[] buffer, int offset, int count)
        {
            _stretcher.Speed = Speed;
            var need = (int)(count * Speed) + 1;
            if (_input.Length < need) _input = new float[need];

            while (_stretcher.Available < count && !_ended)
            {
                var read = _mono.Read(_input, 0, need);
                if (read == 0)
                {
                    _stretcher.Flush();
                    _ended = true;
                    break;
                }

                _consumed += read;
                _stretcher.Write(_input.AsSpan(0, read));
            }

            return _stretcher.Read(buffer.AsSpan(offset, count));
        }
    }
}
