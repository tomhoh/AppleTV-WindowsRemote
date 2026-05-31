// SPDX-License-Identifier: MIT
//
// Standalone WinUI 3 pairing window. Used to live as a ContentDialog
// inside the remote popup, but a ContentDialog is constrained by its
// host XamlRoot — the 200×380 popup window — so any reasonably-sized
// pairing UI got clipped on the right edge. This is its own top-level
// WindowEx with proper screen real-estate.
//
// Modality is faked at the call-site: MainWindow disables the popup
// while the pairing window is open and listens for its Closed event
// to re-enable + refresh the device picker.

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AppleTVProtocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIEx;

namespace AppleTVRemote.WinUI;

public sealed partial class PairingWindow : WindowEx
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

        // Center on the primary monitor work area.
        this.CenterOnScreen();

        PinBox.TextChanged += (_, _) =>
        {
            // Filter to digits, max 4, and enable Pair once we have 4.
            var raw = PinBox.Text ?? string.Empty;
            var clean = new string(raw.Where(char.IsDigit).ToArray());
            if (clean.Length > 4) clean = clean.Substring(0, 4);
            if (clean != raw)
            {
                PinBox.Text = clean;
                PinBox.SelectionStart = clean.Length;
            }
            PairBtn.IsEnabled = clean.Length == 4 && _picked is not null;
        };

        Activated += async (_, args) =>
        {
            // Kick off discovery once the window's actually shown.
            if (_discovery is null && args.WindowActivationState != WindowActivationState.Deactivated)
                await StartDiscoveryAsync();
        };
        Closed += (_, _) => StopDiscovery();
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
                // Apple TVs / HomePods first, everything else below.
                if (dev.IsAppleDevice)
                {
                    int i = 0;
                    while (i < _devices.Count && _devices[i].IsAppleDevice) i++;
                    _devices.Insert(i, dev);
                }
                else
                {
                    _devices.Add(dev);
                }
                UpdateDeviceListVisibility();
            });

        _discovery.Start();
        await Task.CompletedTask;
        UpdateDeviceListVisibility();
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

    private async void OnRescanClicked(object sender, RoutedEventArgs e)
    {
        await StartDiscoveryAsync();
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
        BackBtn  .Visibility = Visibility.Collapsed;
        PairBtn  .Visibility = Visibility.Collapsed;
        RescanBtn.Visibility = Visibility.Visible;
        CancelBtn.Visibility = Visibility.Visible;
    }

    private void SetStage2(AppleTVDevice device)
    {
        ListPanel.Visibility = Visibility.Collapsed;
        PinPanel .Visibility = Visibility.Visible;
        BackBtn  .Visibility = Visibility.Visible;
        PairBtn  .Visibility = Visibility.Visible;
        RescanBtn.Visibility = Visibility.Collapsed;
        CancelBtn.Visibility = Visibility.Collapsed;

        HeaderText.Text = $"Pair “{device.Name}”";
        SubText   .Text = $"{device.Address}:{device.Port}";
        PinPrompt .Text = "Enter the 4-digit PIN shown on your Apple TV.";
        PinBox.Text = string.Empty;
        PairBtn.IsEnabled = false;
        PinBox.Focus(FocusState.Programmatic);
    }

    private async void OnBackClicked(object sender, RoutedEventArgs e)
    {
        _picked = null;
        DeviceList.SelectedItem = null;
        await StartDiscoveryAsync();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => this.Close();

    private async void OnPairClicked(object sender, RoutedEventArgs e)
    {
        if (_picked is null) return;
        var pin = (PinBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(pin)) { ShowPinError("PIN is required."); return; }

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
            this.Close();
        }
        catch (Exception ex)
        {
            ShowPinError(BuildErrorMessage(ex));
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Walk the inner-exception chain so the user sees the actual cause —
    /// HapPairingException usually just says "M6 decrypt failed" while
    /// the inner CryptographicException carries the real detail.
    /// </summary>
    private static string BuildErrorMessage(Exception ex)
    {
        var msgs = new System.Collections.Generic.List<string> { $"{ex.GetType().Name}: {ex.Message}" };
        var inner = ex.InnerException;
        int safety = 0;
        while (inner is not null && safety++ < 5)
        {
            msgs.Add($"  → {inner.GetType().Name}: {inner.Message}");
            inner = inner.InnerException;
        }
        return string.Join("\n", msgs);
    }

    private void ShowPinError(string text)
    {
        PinError.Text = text;
        PinError.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        PairBtn.IsEnabled    = !busy && (PinBox.Text ?? string.Empty).Length == 4;
        BackBtn.IsEnabled    = !busy;
        PinBox.IsEnabled     = !busy;
        PairProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) PinError.Visibility = Visibility.Collapsed;
    }
}
