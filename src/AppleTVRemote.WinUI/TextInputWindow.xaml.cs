// SPDX-License-Identifier: MIT
//
// WinUI 3 borderless "Search" strip. Direct mirror of the WPF sibling in
// AppleTVRemote.Windows/TextInputWindow.xaml.cs, with WinUI presenter
// tweaks instead of AllowsTransparency (which WinUI doesn't have).

using System;
using System.Runtime.InteropServices;
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
        StripBorderAndTitleBar();
        RoundCorners();

        // No attempt to pre-fill from the TV: the field's contents are not
        // reported in the RTI plist on current tvOS (see
        // RTITextOperations.ExtractCurrentText), and OnTextChanged asserts the
        // whole box anyway, so this box is the source of truth.
        InputBox.Loaded += (_, _) => InputBox.Focus(FocusState.Programmatic);
        Activated += (_, args) =>
        {
            CompanionTrace.Write("RTI",
                $"text strip activation: {args.WindowActivationState}");

            // Cheap and idempotent, and it costs nothing to keep: anything that
            // restores the frame would put a title bar on a search strip.
            StripBorderAndTitleBar();

            // Auto-dismiss when the strip loses focus. TextInputWindowManager
            // installs a global mouse hook for the outside-click path, but
            // this catches the case where the user tab-switches to another
            // window entirely.
            if (args.WindowActivationState == WindowActivationState.Deactivated)
                Close();
        };
    }

    /// <summary>
    /// Removes the window frame and title bar, and locks out resizing.
    ///
    /// Separated out so it can be re-applied: this is a borderless strip, and
    /// anything that restores the frame turns the capsule back into a square.
    /// </summary>
    private void StripBorderAndTitleBar()
    {
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            // hasBorder: true, deliberately, on a window that wants no chrome.
            //
            // DWM rounds the window *frame*, so with the frame removed
            // DWMWA_WINDOW_CORNER_PREFERENCE returned S_OK and changed nothing -
            // measured. Keeping a border gives DWM something to round, at the
            // cost of a hairline system edge, which on a dark strip is close to
            // invisible. The title bar stays off regardless: that is the part
            // that would actually look wrong.
            p.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsResizable   = false;
        }
    }

    /// <summary>
    /// Asks DWM to round the window's corners.
    ///
    /// This is the whole shape story now that transparency is gone. It is also
    /// the one part that may not take: DWM rounding is normally applied to
    /// windows with a frame, and this one has had its frame removed, so a
    /// borderless window is the case where the attribute is least guaranteed.
    /// Custom-chrome applications do rely on it, which is the reason to expect
    /// it to work; a failure here is silent and leaves square corners rather
    /// than breaking anything.
    /// </summary>
    private void RoundCorners()
    {
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWCP_ROUND = 2;

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            int preference = DWMWCP_ROUND;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE,
                                           ref preference, sizeof(int));
            CompanionTrace.Write("RTI", $"corner rounding hr=0x{hr:X8}");
        }
        catch (Exception ex)
        {
            CompanionTrace.Write("RTI", "corner rounding failed: " + ex.Message);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
                                                    ref int value, int size);

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
