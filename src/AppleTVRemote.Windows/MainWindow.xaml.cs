// SPDX-License-Identifier: MIT
//
// Minimal popup window:
//   - a device picker driven by CredentialStore
//   - a 5-button d-pad + Select
//   - bottom row: Menu / Home / PlayPause / Vol±
//
// Connection lifecycle: we keep a single CompanionConnection +
// CompanionSession alive for the currently-selected device; switching
// devices closes the old one and pair-verifies the new one.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AppleTVProtocol;

namespace AppleTVRemote.Windows;

public partial class MainWindow : Window
{
    private readonly CredentialStore _store = new();
    private CompanionConnection? _conn;
    private CompanionSession?    _session;
    private StoredCredentials?   _activeDevice;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshDevices();
        Closed += async (_, _) => await TeardownActiveAsync();
    }

    private void RefreshDevices()
    {
        DeviceCombo.ItemsSource = null;
        var creds = _store.LoadAll();
        DeviceCombo.ItemsSource = creds;
        DeviceCombo.DisplayMemberPath = nameof(StoredCredentials.DisplayName);
        if (creds.Count > 0) DeviceCombo.SelectedIndex = 0;
    }

    private async void OnHidClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        if (!Enum.TryParse<HidCommand>(tag, out var cmd))
        {
            MessageBox.Show($"unknown HID command: {tag}");
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
            MessageBox.Show($"{cmd} failed: {ex.Message}", "Apple TV Remote",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await TeardownActiveAsync();   // force reconnect on next click
        }
    }

    private void OnPairClicked(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Pairing UI not implemented yet — for now run:\n\n" +
            "    atv discover\n    atv pair <host> <port>\n\n" +
            "…from the CLI, then reopen this window.",
            "Apple TV Remote",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Make sure we have a live, encrypted CompanionSession for the
    /// currently-selected device. Lazily pair-verifies on first request
    /// or after the previous connection died.
    /// </summary>
    private async System.Threading.Tasks.Task<CompanionSession?> EnsureSessionAsync()
    {
        if (DeviceCombo.SelectedItem is not StoredCredentials selected)
        {
            MessageBox.Show("Select a paired device first (or pair one via the CLI).");
            return null;
        }

        if (_session is not null && _activeDevice?.DeviceId == selected.DeviceId)
            return _session;

        await TeardownActiveAsync();

        // TODO(Phase 7 iteration on Windows): we don't yet persist
        // host/port with credentials, and there's no nicer-than-MessageBox
        // input control hooked up. For now, fall back to the CLI for the
        // discover-and-pair flow, then re-open the tray app to use the
        // saved device. When the pairing UI lands here we'll capture the
        // host/port from the discovery list and write them next to the
        // credential file.
        MessageBox.Show(
            "TODO: connect-time host/port input not implemented yet.\n" +
            "For now, run `atv key <id> <host> <port> <button>` from the CLI.",
            "Apple TV Remote", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
#pragma warning disable CS0162 // unreachable while the stub above is in place
        var host = selected.DisplayName;   // placeholder
        int port = 49152;                   // placeholder
#pragma warning restore CS0162

        var conn = new CompanionConnection(host, port);
        await conn.ConnectAsync();
        await new CompanionPairVerify(conn).RunAsync(selected.Credentials);
        _conn          = conn;
        _session       = new CompanionSession(conn);
        _activeDevice  = selected;
        return _session;
    }

    private async System.Threading.Tasks.Task TeardownActiveAsync()
    {
        if (_session is { } s) { try { await s.DisposeAsync(); } catch { } }
        if (_conn    is { } c) { try { await c.DisposeAsync(); } catch { } }
        _session = null;
        _conn = null;
        _activeDevice = null;
    }
}
