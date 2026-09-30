// SPDX-License-Identifier: MIT
//
// Borderless transparent floating "Search" strip. Direct port of the Mac
// sibling's TextInputWindow.swift.
//
// Lifecycle:
//   * CompanionSession fires KeyboardActiveChanged(active: true) when the
//     TV opens a text field (_tiStarted). App.xaml.cs listens and calls
//     TextInputWindowManager.Show(session).
//   * The strip positions itself centered horizontally on the primary
//     screen work area, vertically below the tray popup.
//   * Typing sends RTI events via CompanionSession.SendTextAsync;
//     Backspace via SendBackspaceAsync.
//   * The strip auto-dismisses when the TV closes the field
//     (_tiStopped), the connection drops, the user presses Escape, or
//     the user clicks outside our app.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AppleTVProtocol;

namespace AppleTVRemote.Windows;

public partial class TextInputWindow : Window
{
    private readonly CompanionSession _session;
    private readonly Action           _onClose;
    private string  _previousText = string.Empty;
    private bool    _suppressChange;

    public TextInputWindow(CompanionSession session, Action onClose)
    {
        InitializeComponent();
        _session = session;
        _onClose = onClose;

        Loaded    += (_, _) => InputBox.Focus();
        Deactivated += (_, _) => Close();
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressChange) return;
        var current = InputBox.Text ?? string.Empty;
        // Toggle placeholder visibility.
        PlaceholderText.Visibility = current.Length == 0
            ? Visibility.Visible : Visibility.Collapsed;

        // Diff against the previous value: appends → SendText, shrinks →
        // SendBackspace one-per-char, same-length-different-content →
        // send the whole new string as a fresh append (paste-replace).
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

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.Enter)
        {
            e.Handled = true;
            Close();
        }
    }
}
