// SPDX-License-Identifier: MIT
//
// Borderless transparent floating "Search" strip. Direct port of the Mac
// sibling's TextInputWindow.swift.
//
// Lifecycle:
//   * CompanionSession fires KeyboardActiveChanged(active: true) when the
//     TV opens a text field (_tiStarted). App.xaml.cs listens and calls
//     TextInputWindowManager.Show(session).
//   * The strip positions itself screen-centered (macOS Spotlight
//     style — see TextInputWindowManager.PositionCentered).
//   * Every change asserts the whole box via
//     CompanionSession.AssertTextAsync, so the field on the TV always
//     mirrors this box exactly.
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
        var current = InputBox.Text ?? string.Empty;
        // Toggle placeholder visibility.
        PlaceholderText.Visibility = current.Length == 0
            ? Visibility.Visible : Visibility.Collapsed;

        // Push the whole box rather than the difference.
        //
        // Diffing assumed the field started out matching this box. When the
        // user typed a term, navigated to the results and came back, the field
        // still held the term while the box opened empty — so everything typed
        // landed *after* the old text, out of sight and unreachable. Reading
        // the field back to fix that does not work either; the plist path for
        // its contents reports nothing on current tvOS (see
        // RTITextOperations.ExtractCurrentText).
        //
        // Asserting makes this box the single source of truth, so the field
        // always shows exactly what is here. It also stops deletion costing a
        // _tiStop/_tiStart round trip per character, which is what
        // SendBackspaceAsync was doing.
        //
        // The trade: typing replaces the field rather than appending to it.
        // That is the right default for a search box, and the only behaviour
        // that can be made to match what the user sees.
        if (current == _previousText) return;

        _previousText = current;
        _ = _session.AssertTextAsync(current);
    }

    /// <summary>
    /// Empties the field on the TV and this box together.
    ///
    /// Asserts the empty string rather than sending one backspace per
    /// character: the field may hold any amount of text, and a backspace storm
    /// is slow and races the TV echoing each one back.
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

        // Baseline first: assigning Text raises OnTextChanged, and a stale
        // baseline would have it assert again for no reason.
        _previousText = string.Empty;
        InputBox.Text = string.Empty;

        InputBox.Focus();
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
