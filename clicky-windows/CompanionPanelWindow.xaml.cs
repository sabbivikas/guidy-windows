using System.Windows;
using System.Windows.Input;
using WpfSize = System.Windows.Size;

namespace ClickyWindows;

/// <summary>
/// The floating control panel that drops down when the user clicks the
/// system tray icon. Dark-themed, borderless, topmost.
///
/// Mirrors CompanionPanelView.swift: shows status, model picker, cursor toggle,
/// and quit button.
/// </summary>
public partial class CompanionPanelWindow : Window
{
    private CompanionManager? _companionManager;

    public CompanionPanelWindow()
    {
        InitializeComponent();

        // Allow dragging the panel by clicking anywhere on it
        MouseLeftButtonDown += (_, _) => DragMove();

        // Auto-dismiss when the window loses focus (clicked outside)
        Deactivated += (_, _) => Hide();
    }

    public void BindToCompanionManager(CompanionManager companionManager)
    {
        _companionManager = companionManager;
        companionManager.PropertyChanged += OnCompanionPropertyChanged;

        // Set initial state
        HaikuRadio.IsChecked = companionManager.SelectedModel == "claude-haiku-4-5-20251001";
        SonnetRadio.IsChecked = companionManager.SelectedModel == "claude-sonnet-4-6";
        OpusRadio.IsChecked = companionManager.SelectedModel == "claude-opus-4-6";
        CursorToggle.IsChecked = companionManager.IsCursorEnabled;
    }

    /// <summary>
    /// Shows the panel positioned near the system tray icon.
    /// The tray icon area is in the bottom-right of the screen, so we
    /// position just above the taskbar.
    /// </summary>
    public void ShowNearTrayIcon()
    {
        var workArea = SystemParameters.WorkArea;

        // Position in the bottom-right above the taskbar. Use a fixed estimate
        // for height on first show (before WPF has laid out the window).
        double estimatedHeight = ActualHeight > 0 ? ActualHeight : 320;
        Left = workArea.Right - Width - 12;
        Top = workArea.Bottom - estimatedHeight - 12;

        Show();
        Activate();
        Focus();
    }

    private void OnCompanionPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        if (e.PropertyName == nameof(CompanionManager.VoiceStateDisplayText))
            StatusLabel.Text = _companionManager!.VoiceStateDisplayText;

        if (e.PropertyName == nameof(CompanionManager.LastTranscript))
        {
            string transcript = _companionManager!.LastTranscript;
            if (!string.IsNullOrEmpty(transcript))
            {
                TranscriptLabel.Text = $"\"{transcript}\"";
                TranscriptLabel.Visibility = Visibility.Visible;
            }
        }
    }

    private void OnClosePanelClicked(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void OnModelSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_companionManager == null) return;

        if (HaikuRadio.IsChecked == true)
            _companionManager.SelectedModel = "claude-haiku-4-5-20251001";
        else if (SonnetRadio.IsChecked == true)
            _companionManager.SelectedModel = "claude-sonnet-4-6";
        else if (OpusRadio.IsChecked == true)
            _companionManager.SelectedModel = "claude-opus-4-6";
    }

    private void OnCursorToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_companionManager == null) return;
        _companionManager.IsCursorEnabled = CursorToggle.IsChecked == true;
    }

    private void OnQuitClicked(object sender, RoutedEventArgs e)
    {
        System.Windows.Application.Current.Shutdown();
    }
}
