// SPDX-License-Identifier: MIT
//
// Modal pairing flow.
//   Stage 1 — DeviceList visible, kicks off DeviceDiscovery on open and
//             populates as devices arrive.
//   Stage 2 — PinPanel visible after the user picks a device. PIN entry
//             drives CompanionPairSetup; on success the host/port get
//             persisted alongside the credentials so the d-pad in the
//             main popup can reconnect later without re-prompting.
//
// On Pair success this Window sets DialogResult = true and closes; the
// caller (MainWindow) reads PairedCredentials to refresh its picker.

using System;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AppleTVProtocol;

namespace AppleTVRemote.Windows;

public partial class PairingWindow : Window
{
    private readonly ObservableCollection<AppleTVDevice> _devices = new();
    private DeviceDiscovery? _discovery;
    private CancellationTokenSource? _discoveryCts;
    private AppleTVDevice? _picked;

    public StoredCredentials? PairedCredentials { get; private set; }

    public PairingWindow()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        Loaded += async (_, _) => await StartDiscoveryAsync();
        Closed += (_, _) => StopDiscovery();
    }

    // ─── Stage 1: discovery ────────────────────────────────────────

    private async Task StartDiscoveryAsync()
    {
        StopDiscovery();
        _devices.Clear();
        HeaderText.Text = "Discovering Apple TVs on your network…";
        SubText.Text    = "Make sure your TV is awake and on the same network.";

        _discoveryCts = new CancellationTokenSource();
        _discovery = new DeviceDiscovery();
        _discovery.Discovered += (_, dev) =>
            Dispatcher.Invoke(() =>
            {
                // De-dup by full Bonjour name.
                foreach (var existing in _devices)
                    if (existing.FullName == dev.FullName) return;
                _devices.Add(dev);
                UpdateHeaderForCount();
            });

        try
        {
            // Browse for a few seconds then leave the listener up — late
            // arrivals still get added if the user is waiting.
            await _discovery.BrowseAsync(TimeSpan.FromSeconds(4), _discoveryCts.Token);
            UpdateHeaderForCount();
        }
        catch (OperationCanceledException) { /* user closed */ }
    }

    private void UpdateHeaderForCount()
    {
        if (_devices.Count == 0)
        {
            HeaderText.Text = "No Apple TVs found.";
            SubText.Text    = "If your TV is awake and on the same network, click Rescan.";
        }
        else
        {
            HeaderText.Text = $"{_devices.Count} Apple TV{(_devices.Count == 1 ? "" : "s")} found.";
            SubText.Text    = "Pick one to pair.";
        }
    }

    private void StopDiscovery()
    {
        _discoveryCts?.Cancel();
        _discoveryCts?.Dispose();
        _discoveryCts = null;
        _discovery?.Dispose();
        _discovery = null;
    }

    private async void OnRescanClicked(object sender, RoutedEventArgs e)
    {
        await StartDiscoveryAsync();
    }

    // ─── Stage 2: PIN entry ────────────────────────────────────────

    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not AppleTVDevice d) return;
        _picked = d;
        StopDiscovery();   // free up the multicast socket while the user types

        DeviceList.Visibility = Visibility.Collapsed;
        PinPanel  .Visibility = Visibility.Visible;
        BackBtn   .Visibility = Visibility.Visible;
        PairBtn   .Visibility = Visibility.Visible;
        RescanBtn .Visibility = Visibility.Collapsed;

        HeaderText.Text = $"Pair with “{d.Name}”";
        SubText   .Text = $"{d.Address}:{d.Port}   ·   {d.Model ?? "Apple TV"}";
        PinPrompt .Text = "Enter the 4-digit PIN shown on your Apple TV:";
        PinBox.Password = string.Empty;
        PinBox.Focus();
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        // Back to the device list.
        _picked = null;
        PinPanel  .Visibility = Visibility.Collapsed;
        BackBtn   .Visibility = Visibility.Collapsed;
        PairBtn   .Visibility = Visibility.Collapsed;
        DeviceList.Visibility = Visibility.Visible;
        RescanBtn .Visibility = Visibility.Visible;
        DeviceList.SelectedItem = null;
        UpdateHeaderForCount();
        PinError.Visibility = Visibility.Collapsed;
    }

    private async void OnPairClicked(object sender, RoutedEventArgs e)
    {
        if (_picked is null) return;
        var pin = PinBox.Password?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(pin))
        {
            ShowPinError("PIN is required.");
            return;
        }

        SetBusy(true);
        try
        {
            await using var conn = new CompanionConnection(_picked.Address.ToString(), _picked.Port);
            await conn.ConnectAsync();

            var pairingId = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString());
            var setup = new CompanionPairSetup(conn, timeout: TimeSpan.FromSeconds(30));
            var creds = await setup.RunAsync(pin, pairingId,
                                              clientSeed: null,
                                              displayName: _picked.Name);

            var deviceId = Convert.ToHexString(creds.AtvIdentifier).ToLowerInvariant();
            PairedCredentials = new StoredCredentials(
                DeviceId:    deviceId,
                DisplayName: _picked.Name,
                Credentials: creds,
                PairedAtUtc: DateTime.UtcNow,
                Host:        _picked.Address.ToString(),
                Port:        _picked.Port);

            new CredentialStore().Save(PairedCredentials);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowPinError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowPinError(string text)
    {
        PinError.Text = text;
        PinError.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        PairBtn .IsEnabled = !busy;
        BackBtn .IsEnabled = !busy;
        PinBox  .IsEnabled = !busy;
        PairProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) PinError.Visibility = Visibility.Collapsed;
    }
}
