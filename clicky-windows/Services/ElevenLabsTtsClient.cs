using System.Net.Http;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace ClickyWindows.Services;

/// <summary>
/// Sends text to ElevenLabs TTS via the Cloudflare Worker proxy and plays
/// the resulting MP3 audio through the default audio output device.
///
/// Mirrors ElevenLabsTTSClient.swift: POSTs to /tts, receives MP3 bytes,
/// plays back with NAudio's WaveOutEvent + Mp3FileReader.
/// </summary>
public sealed class ElevenLabsTtsClient : IDisposable
{
    private readonly string _ttsProxyUrl;
    private readonly HttpClient _httpClient;

    private WaveOutEvent? _waveOut;
    private Mp3FileReader? _mp3Reader;
    private bool _disposed = false;

    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;

    public ElevenLabsTtsClient(string workerBaseUrl)
    {
        _ttsProxyUrl = $"{workerBaseUrl}/tts";
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// Sends text to ElevenLabs and plays the audio. Stops any currently
    /// playing audio before starting new playback.
    /// </summary>
    public async Task SpeakTextAsync(string textToSpeak, CancellationToken cancellationToken = default)
    {
        StopPlayback();

        var requestBody = new
        {
            text = textToSpeak,
            model_id = "eleven_flash_v2_5",
            voice_settings = new
            {
                stability = 0.5,
                similarity_boost = 0.75,
                speed = 1.2
            }
        };

        string requestJson = JsonSerializer.Serialize(requestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, _ttsProxyUrl)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("audio/mpeg")
        );

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"ElevenLabs TTS error ({(int)response.StatusCode}): {errorBody}"
            );
        }

        byte[] mp3AudioData = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        Console.WriteLine(
            $"🔊 ElevenLabs TTS: playing {mp3AudioData.Length / 1024}KB audio"
        );

        PlayMp3Audio(mp3AudioData);
    }

    private void PlayMp3Audio(byte[] mp3Data)
    {
        // NAudio plays MP3 by wrapping a MemoryStream in Mp3FileReader,
        // then feeding it to WaveOutEvent (DirectSound output).
        var memoryStream = new System.IO.MemoryStream(mp3Data);
        _mp3Reader = new Mp3FileReader(memoryStream);
        _waveOut = new WaveOutEvent();
        _waveOut.Init(_mp3Reader);
        _waveOut.Play();
    }

    public void StopPlayback()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;

        _mp3Reader?.Dispose();
        _mp3Reader = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopPlayback();
        _httpClient.Dispose();
    }
}
