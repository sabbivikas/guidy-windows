using NAudio.Wave;

namespace ClickyWindows.Services;

/// <summary>
/// Captures microphone audio using NAudio's WaveInEvent and delivers
/// raw PCM16 mono 16kHz chunks to a callback. This matches exactly what
/// AssemblyAI's streaming WebSocket expects.
///
/// Also reports a normalized audio power level (0.0–1.0) for the waveform
/// visualizer in the overlay UI.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    // AssemblyAI streaming requires: 16kHz sample rate, mono, 16-bit PCM
    private const int SampleRateHz = 16000;
    private const int ChannelCount = 1;
    private const int BitsPerSample = 16;

    private WaveInEvent? _waveIn;
    private bool _isCapturing = false;
    private bool _disposed = false;

    /// Fired each time a chunk of PCM16 audio is ready to send over the websocket.
    public event Action<byte[]>? AudioChunkAvailable;

    /// Fired with a normalized power level (0.0–1.0) for the waveform visualizer.
    public event Action<float>? AudioPowerLevelChanged;

    public bool IsCapturing => _isCapturing;

    /// <summary>
    /// Starts capturing from the default microphone.
    /// Throws if microphone access is denied or no input device is available.
    /// </summary>
    public void StartCapture()
    {
        if (_isCapturing) return;

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRateHz, BitsPerSample, ChannelCount),
            // 100ms buffer — small enough for low latency, large enough to avoid
            // overwhelming the WebSocket with tiny packets
            BufferMilliseconds = 100
        };

        _waveIn.DataAvailable += OnAudioDataAvailable;
        _waveIn.StartRecording();
        _isCapturing = true;

        Console.WriteLine("🎙️ Clicky: Audio capture started (16kHz mono PCM16)");
    }

    public void StopCapture()
    {
        if (!_isCapturing) return;

        _waveIn?.StopRecording();
        _isCapturing = false;

        Console.WriteLine("🎙️ Clicky: Audio capture stopped");
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        // Extract only the recorded bytes (buffer may be partially filled)
        var audioChunk = new byte[e.BytesRecorded];
        Array.Copy(e.Buffer, audioChunk, e.BytesRecorded);

        AudioChunkAvailable?.Invoke(audioChunk);

        // Compute RMS power level for the waveform visualizer.
        // PCM16 samples are 2 bytes each, range -32768 to 32767.
        float powerLevel = ComputeNormalizedRmsPowerLevel(audioChunk);
        AudioPowerLevelChanged?.Invoke(powerLevel);
    }

    private static float ComputeNormalizedRmsPowerLevel(byte[] pcm16Bytes)
    {
        int sampleCount = pcm16Bytes.Length / 2;
        if (sampleCount == 0) return 0f;

        double sumOfSquares = 0;
        for (int i = 0; i < pcm16Bytes.Length - 1; i += 2)
        {
            short sample = BitConverter.ToInt16(pcm16Bytes, i);
            double normalizedSample = sample / 32768.0;
            sumOfSquares += normalizedSample * normalizedSample;
        }

        double rms = Math.Sqrt(sumOfSquares / sampleCount);

        // Clamp to 0–1 range
        return (float)Math.Min(1.0, rms * 5.0); // Multiply by 5 to amplify quiet speech
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopCapture();
        _waveIn?.Dispose();
        _waveIn = null;
    }
}
