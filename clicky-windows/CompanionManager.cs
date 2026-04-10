using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows.Threading;
using WpfApplication = System.Windows.Application;
using ClickyWindows.Services;

namespace ClickyWindows;

/// <summary>
/// The voice state of the companion. Drives UI in the overlay and tray panel.
/// </summary>
public enum CompanionVoiceState
{
    Idle,
    Listening,
    Processing,
    Responding
}

/// <summary>
/// Central state machine. Owns the full push-to-talk pipeline:
/// Ctrl+Alt press → mic → AssemblyAI → transcript + screenshot → Claude → TTS → cursor pointing
/// </summary>
public sealed class CompanionManager : INotifyPropertyChanged, IDisposable
{
    public const string WorkerBaseUrl = "https://clicky-proxy.sabbi-vikas.workers.dev";

    private const string SystemPrompt =
        "You are Clicky, a friendly AI learning companion that lives next to the user's cursor on Windows. " +
        "You can see their screen and help them understand what they're looking at. " +
        "Be concise — your responses will be spoken aloud via text-to-speech. " +
        "IMPORTANT: Always respond in the same language the user speaks. If they speak Telugu, respond in Telugu. " +
        "If Spanish, respond in Spanish. If Hindi, respond in Hindi. Match their language exactly. " +
        "When you want to point at specific UI elements in the screenshots, embed tags like: " +
        "[POINT:x,y:description:Screen 1 (Primary)] where x,y are pixel coordinates in that screenshot " +
        "and the screen name matches the label shown with each image. " +
        "You can include multiple POINT tags to walk the user through several UI elements in sequence. " +
        "Keep responses under 3 sentences when possible.";

    private CompanionVoiceState _voiceState = CompanionVoiceState.Idle;
    public CompanionVoiceState VoiceState
    {
        get => _voiceState;
        private set { _voiceState = value; OnPropertyChanged(); OnPropertyChanged(nameof(VoiceStateDisplayText)); }
    }

    private string _streamingResponseText = "";
    public string StreamingResponseText
    {
        get => _streamingResponseText;
        private set { _streamingResponseText = value; OnPropertyChanged(); }
    }

    private string _lastTranscript = "";
    public string LastTranscript
    {
        get => _lastTranscript;
        private set { _lastTranscript = value; OnPropertyChanged(); }
    }

    private float _audioPowerLevel = 0f;
    public float AudioPowerLevel
    {
        get => _audioPowerLevel;
        private set { _audioPowerLevel = value; OnPropertyChanged(); }
    }

    private string _selectedModel = "claude-haiku-4-5-20251001";
    public string SelectedModel
    {
        get => _selectedModel;
        set
        {
            _selectedModel = value;
            _claudeApiClient.Model = value;
            OnPropertyChanged();
        }
    }

    private bool _isCursorEnabled = true;
    public bool IsCursorEnabled
    {
        get => _isCursorEnabled;
        set { _isCursorEnabled = value; OnPropertyChanged(); CursorEnabledChanged?.Invoke(value); }
    }

    public string VoiceStateDisplayText => VoiceState switch
    {
        CompanionVoiceState.Idle => "Ready — hold Ctrl+Alt to talk",
        CompanionVoiceState.Listening => "Listening...",
        CompanionVoiceState.Processing => "Thinking...",
        CompanionVoiceState.Responding => "Responding...",
        _ => ""
    };

    public event Action<bool>? CursorEnabledChanged;
    public event Action<List<PointingTarget>>? PointingTargetsDetected;

    private readonly GlobalHotkeyMonitor _globalHotkeyMonitor;
    private readonly AudioCaptureService _audioCaptureService;
    private readonly AssemblyAiTranscriptionService _assemblyAiTranscriptionService;
    private readonly ClaudeApiClient _claudeApiClient;
    private readonly ElevenLabsTtsClient _elevenLabsTtsClient;

    private readonly List<(string UserMessage, string AssistantResponse)> _conversationHistory = new();
    private CancellationTokenSource? _currentResponseCancellationTokenSource;

    private readonly Dispatcher _uiDispatcher;
    private bool _disposed = false;

    private static readonly Regex PointTagRegex = new(
        @"\[POINT:(\d+(?:\.\d+)?)[:,](\d+(?:\.\d+)?):(?:([^:\]]+):)?([^\]]+)\]",
        RegexOptions.Compiled
    );

    public CompanionManager()
    {
        _uiDispatcher = WpfApplication.Current.Dispatcher;

        _globalHotkeyMonitor = new GlobalHotkeyMonitor();
        _audioCaptureService = new AudioCaptureService();
        _assemblyAiTranscriptionService = new AssemblyAiTranscriptionService(WorkerBaseUrl);
        _claudeApiClient = new ClaudeApiClient(WorkerBaseUrl, _selectedModel);
        _elevenLabsTtsClient = new ElevenLabsTtsClient(WorkerBaseUrl);
    }

    public void Start()
    {
        _globalHotkeyMonitor.HotkeyPressed += OnPushToTalkPressed;
        _globalHotkeyMonitor.HotkeyReleased += OnPushToTalkReleased;

        _audioCaptureService.AudioPowerLevelChanged += level =>
            _uiDispatcher.InvokeAsync(() => AudioPowerLevel = level);

        Console.WriteLine("🎯 Clicky: CompanionManager started");
    }

    public void Stop()
    {
        _globalHotkeyMonitor.HotkeyPressed -= OnPushToTalkPressed;
        _globalHotkeyMonitor.HotkeyReleased -= OnPushToTalkReleased;
        _audioCaptureService.StopCapture();
    }

    private void OnPushToTalkPressed()
    {
        _uiDispatcher.InvokeAsync(async () =>
        {
            _currentResponseCancellationTokenSource?.Cancel();
            _currentResponseCancellationTokenSource = null;

            _elevenLabsTtsClient.StopPlayback();

            StreamingResponseText = "";
            VoiceState = CompanionVoiceState.Listening;

            await _assemblyAiTranscriptionService.StartSessionAsync(
                onPartialTranscript: partialText =>
                    _uiDispatcher.InvokeAsync(() => LastTranscript = partialText),
                onFinalTranscript: finalText =>
                    _uiDispatcher.InvokeAsync(() => LastTranscript = finalText),
                onError: error =>
                    Console.WriteLine($"❌ AssemblyAI error: {error.Message}")
            );

            _audioCaptureService.AudioChunkAvailable += OnAudioChunkAvailable;
            _audioCaptureService.StartCapture();

            Console.WriteLine("🎙️ Push-to-talk: started listening");
        });
    }

    private void OnPushToTalkReleased()
    {
        _uiDispatcher.InvokeAsync(async () =>
        {
            _audioCaptureService.StopCapture();
            _audioCaptureService.AudioChunkAvailable -= OnAudioChunkAvailable;

            VoiceState = CompanionVoiceState.Processing;

            string finalTranscript = await _assemblyAiTranscriptionService.CloseSessionAndWaitForFinalTranscriptAsync();
            LastTranscript = finalTranscript;
            Console.WriteLine($"🎙️ Final transcript: \"{finalTranscript}\"");

            if (string.IsNullOrWhiteSpace(finalTranscript))
            {
                VoiceState = CompanionVoiceState.Idle;
                return;
            }

            await ProcessTranscriptAndRespondAsync(finalTranscript);
        });
    }

    private async void OnAudioChunkAvailable(byte[] pcm16Chunk)
    {
        await _assemblyAiTranscriptionService.SendAudioChunkAsync(pcm16Chunk);
    }

    private async Task ProcessTranscriptAndRespondAsync(string userTranscript)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        _currentResponseCancellationTokenSource = cancellationTokenSource;
        var ct = cancellationTokenSource.Token;

        try
        {
            var capturedScreens = await Task.Run(ScreenCaptureUtility.CaptureAllScreens);
            var screenshots = capturedScreens
                .Select(s => (s.JpegData, s.Label))
                .ToList();

            VoiceState = CompanionVoiceState.Responding;
            StreamingResponseText = "";

            // Sentence-streaming TTS: speak each sentence as Claude generates it
            var sentenceChannel = Channel.CreateUnbounded<string>();
            int lastSentenceEnd = 0;

            var ttsTask = Task.Run(async () =>
            {
                await foreach (var sentence in sentenceChannel.Reader.ReadAllAsync(ct))
                {
                    var audioData = await _elevenLabsTtsClient.DownloadTtsAudioAsync(sentence, ct);
                    await _elevenLabsTtsClient.PlayAndWaitAsync(audioData, ct);
                }
            }, ct);

            string fullResponse = await _claudeApiClient.AnalyzeScreensAndAskAsync(
                screenshots,
                SystemPrompt,
                _conversationHistory,
                userTranscript,
                onTextChunk: responseText =>
                {
                    _uiDispatcher.InvokeAsync(() =>
                        StreamingResponseText = StripPointTagsForDisplay(responseText)
                    );

                    // Detect complete sentences and queue them for TTS
                    string cleanText = StripPointTagsForDisplay(responseText);
                    while (lastSentenceEnd < cleanText.Length)
                    {
                        int nextEnd = FindSentenceEnd(cleanText, lastSentenceEnd);
                        if (nextEnd < 0) break;
                        string sentence = cleanText[lastSentenceEnd..nextEnd].Trim();
                        lastSentenceEnd = nextEnd;
                        if (sentence.Length > 0)
                            sentenceChannel.Writer.TryWrite(sentence);
                    }
                },
                cancellationToken: ct
            );

            // Flush any remaining text as the final sentence
            string cleanFull = StripPointTagsForDisplay(fullResponse);
            string remaining = lastSentenceEnd < cleanFull.Length
                ? cleanFull[lastSentenceEnd..].Trim()
                : "";
            if (remaining.Length > 0)
                sentenceChannel.Writer.TryWrite(remaining);
            sentenceChannel.Writer.Complete();

            _conversationHistory.Add((userTranscript, fullResponse));
            while (_conversationHistory.Count > 10)
                _conversationHistory.RemoveAt(0);

            // Handle pointing targets (multiple supported)
            var pointingTargets = ParseAllPointingTargets(fullResponse, capturedScreens);
            if (pointingTargets.Count > 0)
            {
                await _uiDispatcher.InvokeAsync(() =>
                    PointingTargetsDetected?.Invoke(pointingTargets)
                );
            }

            await ttsTask;
            await _uiDispatcher.InvokeAsync(() => VoiceState = CompanionVoiceState.Idle);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("🌐 Claude response cancelled (user spoke again)");
            await _uiDispatcher.InvokeAsync(() => VoiceState = CompanionVoiceState.Idle);
        }
        catch (Exception responseException)
        {
            Console.WriteLine($"❌ Response pipeline error: {responseException}");
            await _uiDispatcher.InvokeAsync(() =>
            {
                StreamingResponseText = $"Error: {responseException.Message}";
                VoiceState = CompanionVoiceState.Idle;
            });
        }
    }

    private static int FindSentenceEnd(string text, int startIndex)
    {
        for (int i = startIndex; i < text.Length; i++)
        {
            if (text[i] == '.' || text[i] == '!' || text[i] == '?')
            {
                if (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]))
                    return i + 1;
            }
        }
        return -1;
    }

    private List<PointingTarget> ParseAllPointingTargets(
        string claudeResponse,
        IReadOnlyList<ScreenCaptureUtility.CapturedScreen> capturedScreens
    )
    {
        var targets = new List<PointingTarget>();
        var matches = PointTagRegex.Matches(claudeResponse);

        foreach (Match match in matches)
        {
            if (!double.TryParse(match.Groups[1].Value, out double screenshotX)) continue;
            if (!double.TryParse(match.Groups[2].Value, out double screenshotY)) continue;

            string screenName = match.Groups[4].Value.Trim();
            string description = match.Groups[3].Success && match.Groups[3].Value.Trim().Length > 0
                ? match.Groups[3].Value.Trim()
                : screenName;

            var targetScreen = capturedScreens.FirstOrDefault(s =>
                s.Label.Equals(screenName, StringComparison.OrdinalIgnoreCase)
            );

            if (targetScreen == null)
            {
                Console.WriteLine($"⚠️ POINT tag references unknown screen '{screenName}'");
                continue;
            }

            // Apply scale factor to convert from downscaled coordinates to actual screen coordinates
            double actualScreenX = targetScreen.Bounds.X + (screenshotX * targetScreen.ScaleFactor);
            double actualScreenY = targetScreen.Bounds.Y + (screenshotY * targetScreen.ScaleFactor);

            Console.WriteLine(
                $"🎯 POINT: ({screenshotX}, {screenshotY}) ×{targetScreen.ScaleFactor:F2} on '{screenName}' → " +
                $"screen coords ({actualScreenX}, {actualScreenY}) — \"{description}\""
            );

            targets.Add(new PointingTarget(actualScreenX, actualScreenY, description, screenName));
        }

        return targets;
    }

    private static string StripPointTagsForDisplay(string text)
    {
        return PointTagRegex.Replace(text, "").Trim();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _globalHotkeyMonitor.Dispose();
        _audioCaptureService.Dispose();
        _assemblyAiTranscriptionService.Dispose();
        _elevenLabsTtsClient.Dispose();
    }
}

public record PointingTarget(
    double ScreenX,
    double ScreenY,
    string Description,
    string ScreenName
);
