// SPDX-License-Identifier: MIT
//
// WinUI 3 borderless "Search" strip. Direct mirror of the WPF sibling in
// AppleTVRemote.Windows/TextInputWindow.xaml.cs, with WinUI presenter
// tweaks instead of AllowsTransparency (which WinUI doesn't have).

using System;
using AppleTVProtocol;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinUIEx;

namespace AppleTVRemote.WinUI;

public sealed partial class TextInputWindow : WindowEx
{
    private readonly CompanionSession _session;
    private readonly Action           _onClose;
    private string _previousText = string.Empty;

    public TextInputWindow(CompanionSession session, Action onClose)
    {
        InitializeComponent();
        _session = session;
        _onClose = onClose;

        // Borderless — matches WPF sibling's AllowsTransparency=true;
        // no title bar / close button. Presenter also disallows resize,
        // maximize, minimize. WinUI doesn't have transparent-window
        // alpha, so we settle for a system material fill inside.
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsResizable   = false;
        }

        InputBox.Loaded  += (_, _) => InputBox.Focus(FocusState.Programmatic);
        Activated += (_, args) =>
        {
            // Auto-dismiss when the strip loses focus. TextInputWindowManager
            // installs a global mouse hook for the outside-click path, but
            // this catches the case where the user tab-switches to another
            // window entirely.
            if (args.WindowActivationState == WindowActivationState.Deactivated)
                Close();
        };
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var current = InputBox.Text ?? string.Empty;
        var old = _previousText;
        _previousText = current;
        if (current.Length > old.Length)
        {
            var appended = current.Substring(old.Length);
            _ = _session.SendTextAsync(appended);
        }
        else if (current.Length < old.Length)
        {
            int removed = old.Length - current.Length;
            for (int i = 0; i < removed; i++)
                _ = _session.SendBackspaceAsync();
        }
        else if (current != old)
        {
            _ = _session.SendTextAsync(current);
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape || e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            Close();
        }
    }
}
