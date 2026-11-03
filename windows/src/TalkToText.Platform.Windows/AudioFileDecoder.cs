using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TalkToText.Abstractions;

namespace TalkToText.Platform.Windows;

/// <summary>
/// Decodes audio files with NAudio: WAV and MP3 directly, M4A, AAC and WMA through Media
/// Foundation. Whatever the source format, the output is 16 kHz mono float.
/// </summary>
public sealed class AudioFileDecoder : IAudioFileDecoder
{
    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [".mp3", ".wav", ".m4a", ".aac", ".wma"];

    /// <inheritdoc />
    public IDecodedAudio Open(string path) => new DecodedAudio(path);

    private sealed class DecodedAudio : IDecodedAudio
    {
        private readonly AudioFileReader _reader;
        private readonly ISampleProvider _pipeline;

        public DecodedAudio(string path)
        {
            _reader = new AudioFileReader(path);
            try
            {
                Duration = _reader.TotalTime;
                ISampleProvider provider = _reader;
                if (provider.WaveFormat.Channels > 1) provider = new DownmixSampleProvider(provider);

                // WDL, as for the microphone: managed, so no COM apartment to worry about on
                // the thread pool.
                _pipeline = provider.WaveFormat.SampleRate == AudioChunk.SampleRate
                    ? provider
                    : new WdlResamplingSampleProvider(provider, AudioChunk.SampleRate);
            }
            catch
            {
                _reader.Dispose();
                throw;
            }
        }

        public TimeSpan Duration { get; }

        public int Read(float[] buffer, int offset, int count) => _pipeline.Read(buffer, offset, count);

        public void Dispose() => _reader.Dispose();
    }
}
