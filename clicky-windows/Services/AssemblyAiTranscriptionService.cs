using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ClickyWindows.Services;

/// <summary>
/// Streams audio to AssemblyAI's v3 real-time WebSocket API and returns
/// partial + final transcripts via callbacks.
/// </summary>
public sealed class AssemblyAiTranscriptionService : IDisposable
{
    private readonly string _apiKey;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCancellationTokenSource;
    private Task? _receiveLoopTask;
    private string _lastFinalTranscript = "";
    private bool _disposed = false;

    public AssemblyAiTranscriptionService(string apiKey)
    {
        _apiKey = apiKey;
    }

    public async Task StartSessionAsync(
        Action<string> onPartialTranscript,
        Action<string> onFinalTranscript,
        Action<Exception> onError
    )
    {
        _lastFinalTranscript = "";
        try
        {
            Console.WriteLine("🎙️ AssemblyAI: connecting...");

            _webSocket = new ClientWebSocket();
            _receiveCancellationTokenSource = new CancellationTokenSource();

            var webSocketUrl = new Uri(
                $"wss://streaming.assemblyai.com/v3/ws" +
                $"?speech_model=u3-rt-pro" +
                $"&sample_rate=16000" +
                $"&encoding=pcm_s16le" +
                $"&end_of_turn_confidence_threshold=0.3" +
                $"&token={Uri.EscapeDataString(_apiKey)}"
            );

            await _webSocket.ConnectAsync(webSocketUrl, _receiveCancellationTokenSource.Token);
            Console.WriteLine("🎙️ AssemblyAI: WebSocket connected");

            // Start receiving messages in the background
            _receiveLoopTask = ReceiveMessagesAsync(
                onPartialTranscript,
                onFinalTranscript,
                onError,
                _receiveCancellationTokenSource.Token
            );
        }
        catch (Exception sessionStartException)
        {
            Console.WriteLine(
                $"❌ AssemblyAI: Failed to start session: {sessionStartException.Message}"
            );
            onError(sessionStartException);
        }
    }

    public async Task SendAudioChunkAsync(byte[] pcm16AudioChunk)
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        try
        {
            await _webSocket.SendAsync(
                new ArraySegment<byte>(pcm16AudioChunk),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken: CancellationToken.None
            );
        }
        catch (Exception sendException)
        {
            Console.WriteLine(
                $"⚠️ AssemblyAI: Failed to send audio chunk: {sendException.Message}"
            );
        }
    }

    public async Task<string> CloseSessionAndWaitForFinalTranscriptAsync()
    {
        if (_webSocket?.State != WebSocketState.Open) return _lastFinalTranscript;

        try
        {
            var terminateMessage = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"Terminate\"}");
            await _webSocket.SendAsync(
                new ArraySegment<byte>(terminateMessage),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None
            );

            if (_receiveLoopTask != null)
            {
                await _receiveLoopTask.WaitAsync(TimeSpan.FromSeconds(8));
            }
        }
        catch (Exception closeException)
        {
            Console.WriteLine(
                $"⚠️ AssemblyAI: Error closing session: {closeException.Message}"
            );
        }

        return _lastFinalTranscript;
    }

    private async Task ReceiveMessagesAsync(
        Action<string> onPartialTranscript,
        Action<string> onFinalTranscript,
        Action<Exception> onError,
        CancellationToken cancellationToken
    )
    {
        var receiveBuffer = new byte[8192];

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var messageBuilder = new StringBuilder();
                WebSocketReceiveResult receiveResult;

                // Receive the full message (may span multiple frames)
                do
                {
                    receiveResult = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(receiveBuffer),
                        cancellationToken
                    );

                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        Console.WriteLine(
                            $"🎙️ AssemblyAI: WebSocket closed by server " +
                            $"— code: {_webSocket.CloseStatus}, reason: \"{_webSocket.CloseStatusDescription}\""
                        );
                        return;
                    }

                    messageBuilder.Append(
                        Encoding.UTF8.GetString(receiveBuffer, 0, receiveResult.Count)
                    );
                }
                while (!receiveResult.EndOfMessage);

                string jsonMessage = messageBuilder.ToString();
                ParseAndDispatchTranscriptMessage(jsonMessage, onPartialTranscript, onFinalTranscript);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation — not an error
        }
        catch (WebSocketException webSocketException)
        {
            Console.WriteLine(
                $"❌ AssemblyAI: WebSocket error: {webSocketException.Message}"
            );
            onError(webSocketException);
        }
    }

    private void ParseAndDispatchTranscriptMessage(
        string jsonMessage,
        Action<string> onPartialTranscript,
        Action<string> onFinalTranscript
    )
    {
        try
        {
            using var jsonDocument = JsonDocument.Parse(jsonMessage);
            var root = jsonDocument.RootElement;

            if (!root.TryGetProperty("type", out var typeElement)) return;
            string messageType = typeElement.GetString() ?? "";

            if (messageType == "Turn")
            {
                string transcript = root.TryGetProperty("transcript", out var t)
                    ? t.GetString() ?? "" : "";

                if (string.IsNullOrEmpty(transcript)) return;

                bool isEndOfTurn = root.TryGetProperty("end_of_turn", out var eot)
                    && eot.GetBoolean();

                if (isEndOfTurn)
                {
                    _lastFinalTranscript = transcript;
                    Console.WriteLine($"🎙️ AssemblyAI final: \"{transcript}\"");
                    onFinalTranscript(transcript);
                }
                else
                {
                    onPartialTranscript(transcript);
                }
            }
        }
        catch (JsonException parseException)
        {
            Console.WriteLine(
                $"⚠️ AssemblyAI: Failed to parse message: {parseException.Message}\nRaw: {jsonMessage}"
            );
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _receiveCancellationTokenSource?.Cancel();
        _webSocket?.Dispose();
    }
}
