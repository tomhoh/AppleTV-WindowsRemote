// SPDX-License-Identifier: MIT
//
// Mirror of the WPF MainWindow on the WinUI 3 stack.
//   - WindowEx (from WinUIEx) gives us IsMinimizable / IsMaximizable
//   - Same device picker + d-pad + bottom-row controls
//   - Same lazy-pair-verify-on-first-click connection management

using System;
using System.Threading.Tasks;
using AppleTVProtocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIEx;

namespace AppleTVRemote.WinUI;

public sealed partial class MainWindow : WindowEx
{
    private readonly CredentialStore _store = new();
    private CompanionConnection? _conn;
    private CompanionSession?    _session;
    private StoredCredentials?   _activeDevice;

    public MainWindow()
    {
        InitializeComponent();
        RefreshDevices();
        Closed += async (_, _) => await TeardownActiveAsync();
    }

    private void RefreshDevices()
    {
        DeviceCombo.ItemsSource = null;
        var creds = _store.LoadAll();
        DeviceCombo.DisplayMemberPath = nameof(StoredCredentials.DisplayName);
        DeviceCombo.ItemsSource = creds;
        if (creds.Count > 0) DeviceCombo.SelectedIndex = 0;
    }

    private async void OnHidClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        if (!Enum.TryParse<HidCommand>(tag, out var cmd))
        {
            await ShowMessageAsync($"Unknown HID command: {tag}");
            return;
        }
        try
        {
            var session = await EnsureSessionAsync();
            if (session is null) return;
            await session.PressAsync(cmd);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync($"{cmd} failed: {ex.Message}");
            await TeardownActiveAsync();   // force reconnect on next click
        }
    }

    private async void OnPairClicked(object sender, RoutedEventArgs e)
    {
        await ShowMessageAsync(
            "Pairing UI not implemented yet — for now run:\n\n" +
            "    atv discover\n    atv pair <host> <port>\n\n" +
            "…from the CLI, then reopen this window.");
    }

    private async Task<CompanionSession?> EnsureSessionAsync()
    {
        if (DeviceCombo.SelectedItem is not StoredCredentials selected)
        {
            await ShowMessageAsync("Select a paired device first (or pair one via the CLI).");
            return null;
        }
        if (_session is not null && _activeDevice?.DeviceId == selected.DeviceId)
            return _session;
        await TeardownActiveAsync();

        // TODO(Phase 7b iteration on Windows): persist host/port alongside
        // credentials and surface them in the picker so we don't need to
        // prompt here. For now this stub leaves the connect path TODO.
        await ShowMessageAsync(
            "TODO: connect-time host/port input not implemented yet.\n" +
            "For now, run `atv key <id> <host> <port> <button>` from the CLI.");
        return null;
    }

    private async Task TeardownActiveAsync()
    {
        if (_session is { } s) { try { await s.DisposeAsync(); } catch { } }
        if (_conn    is { } c) { try { await c.DisposeAsync(); } catch { } }
        _session = null;
        _conn = null;
        _activeDevice = null;
    }

    private async Task ShowMessageAsync(string text)
    {
        var dlg = new ContentDialog
        {
            Title             = "Apple TV Remote",
            Content           = text,
            CloseButtonText   = "OK",
            XamlRoot          = Content.XamlRoot,
        };
        await dlg.ShowAsync();
    }
}
