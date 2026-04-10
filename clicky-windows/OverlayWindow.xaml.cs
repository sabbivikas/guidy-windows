using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClickyWindows;

/// <summary>
/// Full-screen transparent click-through overlay that hosts the animated cursor buddy,
/// response bubble, and waveform visualizer across all monitors.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private readonly DispatcherTimer _cursorFollowTimer;
    private double _currentCursorCanvasX = 0;
    private double _currentCursorCanvasY = 0;
    private double _buddyPositionX = 0;
    private double _buddyPositionY = 0;
    private const double FollowSmoothingFactor = 0.18;
    private POINT? _cursorPosAtPointEnd = null;
    private bool _waitingForUserDismiss = false;

    private CompanionManager? _companionManager;

    public OverlayWindow()
    {
        InitializeComponent();

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Loaded += OnWindowLoaded;

        _cursorFollowTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16) // ~60fps
        };
        _cursorFollowTimer.Tick += OnCursorFollowTick;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int currentStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            currentStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW);

        _cursorFollowTimer.Start();
    }

    public void BindToCompanionManager(CompanionManager companionManager)
    {
        _companionManager = companionManager;
        companionManager.PropertyChanged += OnCompanionManagerPropertyChanged;
        companionManager.PointingTargetDetected += OnPointingTargetDetected;
    }

    private void OnCursorFollowTick(object? sender, EventArgs e)
    {
        if (_waitingForUserDismiss)
        {
            if (IsUserInputDetected())
            {
                _waitingForUserDismiss = false;
                HideResponseBubble();
                CursorCanvas.BeginAnimation(System.Windows.Controls.Canvas.LeftProperty, null);
                CursorCanvas.BeginAnimation(System.Windows.Controls.Canvas.TopProperty, null);
            }
            return;
        }

        if (!GetCursorPos(out POINT screenCursorPos)) return;

        if (_cursorPosAtPointEnd.HasValue)
        {
            var anchor = _cursorPosAtPointEnd.Value;
            if (Math.Abs(screenCursorPos.X - anchor.X) < 4 &&
                Math.Abs(screenCursorPos.Y - anchor.Y) < 4)
                return;

            _cursorPosAtPointEnd = null;
            HideResponseBubble();
        }

        // GetCursorPos returns physical pixels; convert to WPF logical units for DPI correctness
        var presentationSource = PresentationSource.FromVisual(this);
        var transformFromDevice = presentationSource?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        var logicalCursorPos = transformFromDevice.Transform(
            new System.Windows.Point(screenCursorPos.X, screenCursorPos.Y)
        );

        _currentCursorCanvasX = logicalCursorPos.X - SystemParameters.VirtualScreenLeft;
        _currentCursorCanvasY = logicalCursorPos.Y - SystemParameters.VirtualScreenTop;

        _buddyPositionX += (_currentCursorCanvasX - _buddyPositionX) * FollowSmoothingFactor;
        _buddyPositionY += (_currentCursorCanvasY - _buddyPositionY) * FollowSmoothingFactor;

        double buddyLeft = _buddyPositionX - 10;
        double buddyTop = _buddyPositionY - 10;

        System.Windows.Controls.Canvas.SetLeft(CursorCanvas, buddyLeft);
        System.Windows.Controls.Canvas.SetTop(CursorCanvas, buddyTop);

        // Keep the response bubble near the cursor
        System.Windows.Controls.Canvas.SetLeft(ResponseBubble, buddyLeft + 70);
        System.Windows.Controls.Canvas.SetTop(ResponseBubble, buddyTop - 10);

        System.Windows.Controls.Canvas.SetLeft(StatusBubble, buddyLeft + 70);
        System.Windows.Controls.Canvas.SetTop(StatusBubble, buddyTop + 10);
    }

    private void OnCompanionManagerPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        switch (e.PropertyName)
        {
            case nameof(CompanionManager.VoiceState):
                UpdateStateDisplay(_companionManager!.VoiceState);
                break;

            case nameof(CompanionManager.StreamingResponseText):
                UpdateResponseText(_companionManager!.StreamingResponseText);
                break;

            case nameof(CompanionManager.AudioPowerLevel):
                UpdateWaveformBars(_companionManager!.AudioPowerLevel);
                break;
        }
    }

    private void UpdateStateDisplay(CompanionVoiceState voiceState)
    {
        switch (voiceState)
        {
            case CompanionVoiceState.Idle:
                HideStatusBubble();
                if (string.IsNullOrEmpty(_companionManager?.StreamingResponseText))
                    HideResponseBubble();
                WaveformPanel.Visibility = Visibility.Collapsed;
                PulseCursorIdle();
                break;

            case CompanionVoiceState.Listening:
                _waitingForUserDismiss = false;
                HideResponseBubble();
                ShowStatusBubble("Listening...");
                WaveformPanel.Visibility = Visibility.Visible;
                PulseCursorListening();
                break;

            case CompanionVoiceState.Processing:
                ShowStatusBubble("Thinking...");
                WaveformPanel.Visibility = Visibility.Collapsed;
                break;

            case CompanionVoiceState.Responding:
                HideStatusBubble();
                WaveformPanel.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void UpdateResponseText(string responseText)
    {
        if (string.IsNullOrEmpty(responseText))
        {
            HideResponseBubble();
            return;
        }

        ResponseTextBlock.Text = responseText;

        if (ResponseBubble.Visibility != Visibility.Visible)
            ShowResponseBubble();
    }

    private void UpdateWaveformBars(float powerLevel)
    {
        if (WaveformPanel.Visibility != Visibility.Visible) return;

        var random = new Random();
        double baseHeight = 6 + powerLevel * 18;

        WaveBar1.Height = baseHeight * (0.5 + random.NextDouble() * 0.5);
        WaveBar2.Height = baseHeight * (0.7 + random.NextDouble() * 0.3);
        WaveBar3.Height = baseHeight * (0.6 + random.NextDouble() * 0.4);
        WaveBar4.Height = baseHeight;
        WaveBar5.Height = baseHeight * (0.6 + random.NextDouble() * 0.4);
    }

    private void PulseCursorIdle()
    {
        var scaleTransform = new ScaleTransform(1, 1, 30, 30);
        CursorCanvas.RenderTransform = scaleTransform;

        var pulseAnimation = new DoubleAnimation(1.0, 1.05, TimeSpan.FromSeconds(1.5))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pulseAnimation);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pulseAnimation);
    }

    private void PulseCursorListening()
    {
        var scaleTransform = new ScaleTransform(1, 1, 30, 30);
        CursorCanvas.RenderTransform = scaleTransform;

        var pulseAnimation = new DoubleAnimation(1.0, 1.15, TimeSpan.FromSeconds(0.5))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pulseAnimation);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pulseAnimation);
    }

    private void OnPointingTargetDetected(PointingTarget target)
    {
        double targetCanvasX = target.ScreenX - SystemParameters.VirtualScreenLeft - 30;
        double targetCanvasY = target.ScreenY - SystemParameters.VirtualScreenTop - 30;

        var flyDuration = TimeSpan.FromMilliseconds(600);
        var easingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var flyXAnimation = new DoubleAnimation(
            System.Windows.Controls.Canvas.GetLeft(CursorCanvas),
            targetCanvasX,
            flyDuration
        )
        { EasingFunction = easingFunction };

        var flyYAnimation = new DoubleAnimation(
            System.Windows.Controls.Canvas.GetTop(CursorCanvas),
            targetCanvasY,
            flyDuration
        )
        { EasingFunction = easingFunction };

        flyXAnimation.Completed += (_, _) =>
        {
            ShowPointingBubble(target.Description, targetCanvasX + 70, targetCanvasY - 10);
        };

        CursorCanvas.BeginAnimation(System.Windows.Controls.Canvas.LeftProperty, flyXAnimation);
        CursorCanvas.BeginAnimation(System.Windows.Controls.Canvas.TopProperty, flyYAnimation);

        _waitingForUserDismiss = true;
    }

    private bool IsUserInputDetected()
    {
        // Mouse clicks
        if ((GetAsyncKeyState(0x01) & 0x8000) != 0) return true; // Left
        if ((GetAsyncKeyState(0x02) & 0x8000) != 0) return true; // Right

        // Keyboard: letters, digits, space, enter, escape, backspace, delete, tab, arrows
        for (int vk = 0x30; vk <= 0x5A; vk++) // 0-9, A-Z
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        if ((GetAsyncKeyState(0x20) & 0x8000) != 0) return true; // Space
        if ((GetAsyncKeyState(0x0D) & 0x8000) != 0) return true; // Enter
        if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) return true; // Escape
        if ((GetAsyncKeyState(0x08) & 0x8000) != 0) return true; // Backspace
        if ((GetAsyncKeyState(0x2E) & 0x8000) != 0) return true; // Delete
        if ((GetAsyncKeyState(0x09) & 0x8000) != 0) return true; // Tab
        for (int vk = 0x25; vk <= 0x28; vk++) // Arrow keys
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;

        return false;
    }

    private void ShowPointingBubble(string description, double canvasX, double canvasY)
    {
        ResponseTextBlock.Text = description;
        System.Windows.Controls.Canvas.SetLeft(ResponseBubble, canvasX);
        System.Windows.Controls.Canvas.SetTop(ResponseBubble, canvasY);
        ShowResponseBubble();
    }

    private void ShowResponseBubble()
    {
        ResponseBubble.Visibility = Visibility.Visible;
        ResponseBubble.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }

    private void HideResponseBubble()
    {
        var fadeOut = new DoubleAnimation(ResponseBubble.Opacity, 0, TimeSpan.FromMilliseconds(300));
        fadeOut.Completed += (_, _) => ResponseBubble.Visibility = Visibility.Collapsed;
        ResponseBubble.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void ShowStatusBubble(string statusText)
    {
        StatusTextBlock.Text = statusText;
        StatusBubble.Visibility = Visibility.Visible;
        StatusBubble.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    private void HideStatusBubble()
    {
        var fadeOut = new DoubleAnimation(StatusBubble.Opacity, 0, TimeSpan.FromMilliseconds(200));
        fadeOut.Completed += (_, _) => StatusBubble.Visibility = Visibility.Collapsed;
        StatusBubble.BeginAnimation(OpacityProperty, fadeOut);
    }
}
