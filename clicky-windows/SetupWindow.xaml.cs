using System.Diagnostics;
using System.Windows;
using ClickyWindows.Services;

namespace ClickyWindows;

public partial class SetupWindow : Window
{
    public bool SetupCompleted { get; private set; } = false;

    public SetupWindow()
    {
        InitializeComponent();

        // Pre-fill if settings already exist (user re-opening setup)
        if (SettingsManager.SettingsExist())
        {
            var s = SettingsManager.Load();
            AnthropicKeyBox.Password = s.AnthropicApiKey;
            AssemblyAiKeyBox.Password = s.AssemblyAiApiKey;
            ElevenLabsKeyBox.Password = s.ElevenLabsApiKey;
            VoiceIdBox.Text = s.ElevenLabsVoiceId;
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        string anthropic = AnthropicKeyBox.Password.Trim();
        string assemblyAi = AssemblyAiKeyBox.Password.Trim();
        string elevenLabs = ElevenLabsKeyBox.Password.Trim();
        string voiceId = VoiceIdBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(anthropic) ||
            string.IsNullOrWhiteSpace(assemblyAi) ||
            string.IsNullOrWhiteSpace(elevenLabs) ||
            string.IsNullOrWhiteSpace(voiceId))
        {
            ErrorText.Text = "All fields are required.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        SettingsManager.Save(new AppSettings
        {
            AnthropicApiKey = anthropic,
            AssemblyAiApiKey = assemblyAi,
            ElevenLabsApiKey = elevenLabs,
            ElevenLabsVoiceId = voiceId
        });

        SetupCompleted = true;
        Close();
    }

    private void OpenAnthropicConsole(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => Process.Start(new ProcessStartInfo("https://console.anthropic.com/settings/keys") { UseShellExecute = true });

    private void OpenAssemblyAiConsole(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => Process.Start(new ProcessStartInfo("https://www.assemblyai.com/app") { UseShellExecute = true });

    private void OpenElevenLabsConsole(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => Process.Start(new ProcessStartInfo("https://elevenlabs.io/app/settings/api-keys") { UseShellExecute = true });
}
