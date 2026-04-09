using System.Net.Http;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace ClickyWindows.Services;

/// <summary>
/// Sends text directly to the ElevenLabs TTS API and plays the resulting MP3.
/// </summary>
public sealed class ElevenLabsTtsClient : IDisposable
{
    private readonly string _apiKey;
    private readonly string _voiceId;
    private readonly HttpClient _httpClient;

    private WaveOutEvent? _waveOut;
    private Mp3FileReader? _mp3Reader;
    private bool _disposed = false;

    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;

    public ElevenLabsTtsClient(string apiKey, string voiceId)
    {
        _apiKey = apiKey;
        _voiceId = voiceId;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task SpeakTextAsync(string textToSpeak, CancellationToken cancellationToken = default)
    {
        StopPlayback();

        var requestBody = new
        {
            text = textToSpeak,
            model_id = "eleven_flash_v2_5",
            voice_settings = new { stability = 0.5, similarity_boost = 0.75, speed = 1.2 }
        };

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.elevenlabs.io/v1/text-to-speech/{_voiceId}"
        )
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("xi-api-key", _apiKey);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("audio/mpeg"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"ElevenLabs TTS error ({(int)response.StatusCode}): {errorBody}");
        }

        byte[] mp3AudioData = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        Console.WriteLine($"🔊 ElevenLabs TTS: playing {mp3AudioData.Length / 1024}KB audio");
        PlayMp3Audio(mp3AudioData);
    }

    private void PlayMp3Audio(byte[] mp3Data)
    {
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
