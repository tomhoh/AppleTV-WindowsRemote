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

        // No attempt to pre-fill from the TV: the field's contents are not
        // reported in the RTI plist on current tvOS (see
        // RTITextOperations.ExtractCurrentText), and OnTextChanged asserts the
        // whole box anyway, so this box is the source of truth.
        InputBox.Loaded += (_, _) => InputBox.Focus(FocusState.Programmatic);
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
        if (current == _previousText) return;

        _previousText = current;
        _ = _session.AssertTextAsync(current);
    }

    /// <summary>
    /// Empties the field on the TV and this box together.
    ///
    /// Asserts the empty string through the session rather than sending one
    /// backspace per character: the field may hold any amount of text, and a
    /// backspace storm is slow and races the TV echoing each one back.
    /// </summary>
    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _session.ClearTextAsync();
        }
        catch (Exception ex)
        {
            CompanionTrace.Write("RTI", "clear failed: " + ex.Message);
        }

        // Baseline first, for the same reason as the constructor: this
        // assignment raises OnTextChanged, and a stale baseline would make the
        // handler read the change as a deletion and fire backspaces at a field
        // that is already empty.
        _previousText = string.Empty;
        InputBox.Text = string.Empty;

        InputBox.Focus(FocusState.Programmatic);
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
