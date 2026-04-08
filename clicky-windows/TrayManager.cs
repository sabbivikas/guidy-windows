using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace ClickyWindows;

/// <summary>
/// Manages the Windows system tray icon and the floating companion panel.
/// This is the Windows equivalent of MenuBarPanelManager.swift.
///
/// Responsibilities:
/// - Create and own the NotifyIcon (system tray presence)
/// - Show/hide CompanionPanelWindow on tray icon click
/// - Own the OverlayWindow (full-screen transparent cursor overlay)
/// - Listen to CompanionManager.IsCursorEnabled to show/hide the overlay
/// </summary>
public sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly CompanionPanelWindow _companionPanel;
    private readonly OverlayWindow _overlayWindow;
    private readonly CompanionManager _companionManager;
    private bool _disposed = false;

    public TrayManager(CompanionManager companionManager)
    {
        _companionManager = companionManager;

        // Build the tray icon. We use a stock Windows icon here.
        // Replace with a proper .ico file by loading from resources:
        //   Icon = new Icon(Application.GetResourceStream(new Uri("pack://application:,,,/Resources/clicky.ico")).Stream)
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Clicky — AI Companion",
            Visible = true
        };

        // Left-click on the tray icon toggles the panel
        _notifyIcon.Click += OnTrayIconClicked;

        // Right-click context menu as a convenience (same actions as the panel)
        _notifyIcon.ContextMenuStrip = BuildContextMenu();

        // Create the companion panel (hidden until tray icon is clicked)
        _companionPanel = new CompanionPanelWindow();
        _companionPanel.BindToCompanionManager(companionManager);

        // Create the full-screen overlay (shown immediately if cursor is enabled)
        _overlayWindow = new OverlayWindow();
        _overlayWindow.BindToCompanionManager(companionManager);

        if (companionManager.IsCursorEnabled)
            _overlayWindow.Show();

        // Update overlay visibility when the user toggles the cursor
        companionManager.CursorEnabledChanged += OnCursorEnabledChanged;

        System.Diagnostics.Debug.WriteLine("🎯 Clicky: TrayManager initialized");
    }

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        // Only respond to left-click (MouseEventArgs.Button == Left)
        if (e is MouseEventArgs mouseEvent && mouseEvent.Button != MouseButtons.Left) return;

        if (_companionPanel.IsVisible)
            _companionPanel.Hide();
        else
            _companionPanel.ShowNearTrayIcon();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var contextMenu = new ContextMenuStrip();

        var showPanelItem = new ToolStripMenuItem("Open Clicky");
        showPanelItem.Click += (_, _) => _companionPanel.ShowNearTrayIcon();
        contextMenu.Items.Add(showPanelItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var quitItem = new ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => System.Windows.Application.Current.Shutdown();
        contextMenu.Items.Add(quitItem);

        return contextMenu;
    }

    private void OnCursorEnabledChanged(bool isEnabled)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (isEnabled)
                _overlayWindow.Show();
            else
                _overlayWindow.Hide();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _companionPanel.Close();
        _overlayWindow.Close();
    }
}
