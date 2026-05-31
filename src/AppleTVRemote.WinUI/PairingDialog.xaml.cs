// SPDX-License-Identifier: MIT
//
// Compact two-stage pairing dialog (WinUI mirror of the SwiftUI mac
// PairingView):
//
//   Stage 1 — header + scrollable device list (or "no devices" hint).
//   Stage 2 — header + PIN entry. Auto-pairs once the user types 4 digits.

using System;
using System.Collections.ObjectModel;
using System.Linq;
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
        PrimaryButtonClick   += OnPrimaryClick;
        SecondaryButtonClick += OnSecondaryClick;
        PinBox.TextChanged += (_, _) =>
        {
            // Strip non-digits; auto-submit when 4 digits arrive.
            var raw = PinBox.Text ?? string.Empty;
            var clean = new string(raw.Where(char.IsDigit).ToArray());
            if (clean.Length > 4) clean = clean.Substring(0, 4);
            if (clean != raw)
            {
                PinBox.Text = clean;
                PinBox.SelectionStart = clean.Length;
            }
            IsPrimaryButtonEnabled = clean.Length == 4 && _picked is not null;
        };
    }

    // ─── Stage 1: discovery ────────────────────────────────────────

    private async Task StartDiscoveryAsync()
    {
        StopDiscovery();
        _devices.Clear();
        SetStage1();
        HeaderText.Text = "Pair Apple TV";
        SubText   .Text = "Looking for Apple TVs on your network…";

        _discoveryCts = new CancellationTokenSource();
        _discovery    = new DeviceDiscovery();
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _discovery.Discovered += (_, dev) =>
            dispatcher?.TryEnqueue(() =>
            {
                foreach (var existing in _devices)
                    if (existing.FullName == dev.FullName) return;
                _devices.Add(dev);
                UpdateDeviceListVisibility();
            });

        try
        {
            await _discovery.BrowseAsync(TimeSpan.FromSeconds(4), _discoveryCts.Token);
            UpdateDeviceListVisibility();
        }
        catch (OperationCanceledException) { }
    }

    private void UpdateDeviceListVisibility()
    {
        if (_devices.Count == 0)
        {
            SubText.Text          = "No Apple TVs found.";
            EmptyHint.Visibility  = Visibility.Visible;
            DeviceList.Visibility = Visibility.Collapsed;
        }
        else
        {
            SubText.Text          = $"{_devices.Count} device{(_devices.Count == 1 ? "" : "s")} found — tap one to pair.";
            EmptyHint.Visibility  = Visibility.Collapsed;
            DeviceList.Visibility = Visibility.Visible;
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
        SetStage2(d);
    }

    private void SetStage1()
    {
        ListPanel.Visibility = Visibility.Visible;
        PinPanel .Visibility = Visibility.Collapsed;
        PinError .Visibility = Visibility.Collapsed;
        SecondaryButtonText = "Cancel";
        IsPrimaryButtonEnabled = false;
    }

    private void SetStage2(AppleTVDevice device)
    {
        ListPanel.Visibility = Visibility.Collapsed;
        PinPanel .Visibility = Visibility.Visible;
        HeaderText.Text = $"Pair “{device.Name}”";
        SubText   .Text = $"{device.Address}:{device.Port}";
        PinPrompt .Text = "Enter the 4-digit PIN shown on your Apple TV.";
        PinBox.Text   = string.Empty;
        SecondaryButtonText = "Back";
        IsPrimaryButtonEnabled = false;
        PinBox.Focus(FocusState.Programmatic);
    }

    private async void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_picked is null) return;
        var pin = (PinBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(pin))
        {
            ShowPinError("PIN is required.");
            args.Cancel = true;
            return;
        }

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

    /// <summary>
    /// Override the default Secondary button behaviour: in Stage 2,
    /// "Back" returns to Stage 1 instead of dismissing the dialog.
    /// </summary>
    private void OnSecondaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (PinPanel.Visibility == Visibility.Visible)
        {
            args.Cancel = true;
            _picked = null;
            SetStage1();
            DeviceList.SelectedItem = null;
            // Re-arm discovery so the list isn't stuck empty after Back.
            _ = StartDiscoveryAsync();
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
