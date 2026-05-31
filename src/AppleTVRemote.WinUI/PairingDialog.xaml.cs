// SPDX-License-Identifier: MIT
//
// WinUI 3 mirror of the WPF PairingWindow. Lives as a ContentDialog so
// it overlays the MainWindow without spawning another OS window.

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AppleTVProtocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppleTVRemote.WinUI;

public sealed partial class PairingDialog : ContentDialog
{
    private readonly ObservableCollection<AppleTVDevice> _devices = new();
    private DeviceDiscovery? _discovery;
    private CancellationTokenSource? _discoveryCts;
    private AppleTVDevice? _picked;

    public StoredCredentials? PairedCredentials { get; private set; }

    public PairingDialog()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        Opened += async (_, _) => await StartDiscoveryAsync();
        Closed += (_, _) => StopDiscovery();
        PrimaryButtonClick += OnPrimaryClick;
    }

    // ─── Stage 1: discovery ────────────────────────────────────────

    private async Task StartDiscoveryAsync()
    {
        StopDiscovery();
        _devices.Clear();
        HeaderText.Text = "Discovering Apple TVs on your network…";
        SubText.Text    = "Make sure your TV is awake and on the same network.";

        _discoveryCts = new CancellationTokenSource();
        _discovery    = new DeviceDiscovery();
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _discovery.Discovered += (_, dev) =>
            dispatcher?.TryEnqueue(() =>
            {
                foreach (var existing in _devices)
                    if (existing.FullName == dev.FullName) return;
                _devices.Add(dev);
                UpdateHeaderForCount();
            });

        try
        {
            await _discovery.BrowseAsync(TimeSpan.FromSeconds(4), _discoveryCts.Token);
            UpdateHeaderForCount();
        }
        catch (OperationCanceledException) { }
    }

    private void UpdateHeaderForCount()
    {
        if (_devices.Count == 0)
        {
            HeaderText.Text = "No Apple TVs found.";
            SubText.Text    = "If your TV is awake and on the same network, try Pair again.";
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

    // ─── Stage 2: PIN entry ────────────────────────────────────────

    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not AppleTVDevice d) return;
        _picked = d;
        StopDiscovery();

        DeviceList.Visibility = Visibility.Collapsed;
        PinPanel  .Visibility = Visibility.Visible;
        IsPrimaryButtonEnabled = true;

        HeaderText.Text = $"Pair with \"{d.Name}\"";
        SubText   .Text = $"{d.Address}:{d.Port}   ·   {d.Model ?? "Apple TV"}";
        PinPrompt .Text = "Enter the 4-digit PIN shown on your Apple TV:";
        PinBox.Password = string.Empty;
        PinBox.Focus(FocusState.Keyboard);
    }

    private async void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_picked is null) return;
        var pin = PinBox.Password?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(pin)) { ShowPinError("PIN is required."); args.Cancel = true; return; }

        // Defer dialog close while we do async work.
        var deferral = args.GetDeferral();
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
        }
        catch (Exception ex)
        {
            ShowPinError(ex.Message);
            args.Cancel = true;
        }
        finally
        {
            SetBusy(false);
            deferral.Complete();
        }
    }

    private void ShowPinError(string text)
    {
        PinError.Text = text;
        PinError.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        IsPrimaryButtonEnabled   = !busy;
        IsSecondaryButtonEnabled = !busy;
        PinBox.IsEnabled         = !busy;
        PairProgress.Visibility  = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) PinError.Visibility = Visibility.Collapsed;
    }
}
