// SPDX-License-Identifier: MIT
//
// WinUI 3 entry point.
//
// WinUI 3 has no equivalent of WPF's NotifyIcon, so we layer in two
// independent libraries:
//   - H.NotifyIcon.WinUI for the system-tray icon + context menu
//   - WinUIEx for window-management niceties on MainWindow
//
// The tray icon is held in code-behind here (rather than App.xaml) so
// the click handler can call back into App for the popup toggle.

using H.NotifyIcon;
using Microsoft.UI.Xaml;

namespace AppleTVRemote.WinUI;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private MainWindow?  _popup;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Apple TV Remote",
        };
        // Without an icon set, H.NotifyIcon registers with the shell but
        // has nothing to draw — the process runs (visible in Task Manager)
        // but no tray icon appears. Build one at runtime so we don't have
        // to ship a .ico alongside the EXE / MSIX.
        try
        {
            _trayIcon.Icon = TrayIconBuilder.Build();
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TrayIconBuilder failed: {ex}");
            _trayIcon.Icon = System.Drawing.SystemIcons.Application;
        }
        _trayIcon.LeftClickCommand    = new RelayCommand(_ => TogglePopup());
        _trayIcon.NoLeftClickDelay    = true;
        _trayIcon.ContextMenuMode     = ContextMenuMode.PopupMenu;
        _trayIcon.ForceCreate();
        // Tip: H.NotifyIcon needs an active dispatcher; create after Application is initialized.
    }

    private void TogglePopup()
    {
        if (_popup is null)
        {
            _popup = new MainWindow();
            _popup.Closed += (_, _) => _popup = null;
        }
        _popup.PositionNearTray();      // anchor to tray-icon cursor location
        _popup.Activate();
    }

    private sealed class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action<object?> _execute;
        public RelayCommand(Action<object?> execute) { _execute = execute; }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)   => _execute(parameter);
    }
}
