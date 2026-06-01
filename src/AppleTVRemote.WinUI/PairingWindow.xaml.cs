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
        // Query BOTH companion-link and airplay — pyatv does this. Some
        // Apple TVs respond faster to one than the other, and AirPlay's
        // 'model' TXT field is another way to recognise the device.
        _discovery    = new DeviceDiscovery(DeviceDiscovery.CompanionAndAirplay);
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        int totalSeen = 0;
        _discovery.Discovered += (_, dev) =>
            dispatcher?.TryEnqueue(() =>
            {
                System.Threading.Interlocked.Increment(ref totalSeen);

                // Filter: only Apple TVs / HomePods (they set the
                // 'rpmd' TXT field; iPhones in screen-mirror mode and
                // Macs with Sharing on don't). Diagnostic counter in
                // the subtitle so users can tell the scan is working
                // even when nothing's getting past the filter.
                if (!dev.IsAppleTV)
                {
                    if (_devices.Count == 0)
                        SubText.Text = $"Scanning… ({totalSeen} non-Apple-TV device{(totalSeen == 1 ? "" : "s")} seen so far)";
                    return;
                }

                // Dedup by DeviceKey so one physical Apple TV doesn't
                // show up twice (once per service). Prefer the
                // companion-link entry — the airplay entry uses port
                // 7000, which is wrong for pairing.
                for (int i = 0; i < _devices.Count; i++)
                {
                    if (_devices[i].DeviceKey != dev.DeviceKey) continue;
                    if (!_devices[i].IsCompanionLinkEntry && dev.IsCompanionLinkEntry)
                        _devices[i] = dev;
                    return;
                }
                _devices.Add(dev);
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

    private async void OnEnterIpClicked(object sender, RoutedEventArgs e)
    {
        // Workaround for Windows multicast unreliability — direct unicast
        // probe to a user-supplied IP. Apple TVs reply to unicast in ms.
        var box = new TextBox { PlaceholderText = "192.168.1.42", FontSize = 16 };
        var dlg = new ContentDialog
        {
            Title           = "Apple TV IP address",
            Content         = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Find it on your Apple TV under Settings → Network → Wi-Fi/Ethernet → IP address.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.8, FontSize = 12,
                    },
                    box,
                },
            },
            PrimaryButtonText   = "Probe",
            SecondaryButtonText = "Cancel",
            DefaultButton       = ContentDialogButton.Primary,
            XamlRoot            = Content.XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result != ContentDialogResult.Primary) return;
        var ipText = (box.Text ?? string.Empty).Trim();
        if (!System.Net.IPAddress.TryParse(ipText, out var ip))
        {
            var bad = new ContentDialog
            {
                Title = "Apple TV Remote",
                Content = $"'{ipText}' isn't a valid IP address.",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await bad.ShowAsync();
            return;
        }

        HeaderText.Text = $"Probing {ip}…";
        SubText.Text    = "Sending unicast mDNS query directly to the TV.";
        try
        {
            var dev = await DeviceDiscovery.UnicastProbeAsync(ip,
                timeout: TimeSpan.FromSeconds(4));
            if (dev is null)
            {
                var nope = new ContentDialog
                {
                    Title = "Apple TV Remote",
                    Content = $"No reply from {ip}. Check the address and that the TV is awake.",
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                };
                await nope.ShowAsync();
                UpdateDeviceListVisibility();
                return;
            }
            _picked = dev;
            StopDiscovery();
            SetStage2(dev);
        }
        catch (Exception ex)
        {
            var err = new ContentDialog
            {
                Title = "Apple TV Remote",
                Content = $"Probe failed: {ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await err.ShowAsync();
            UpdateDeviceListVisibility();
        }
    }

    // ─── Stage 2: PIN entry ────────────────────────────────────────

    private async void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not AppleTVDevice d) return;
        if (!d.IsCompanionLinkEntry)
        {
            DeviceList.SelectedItem = null;
            var dlg = new ContentDialog
            {
                Title           = "Apple TV Remote",
                Content         = $"Found “{d.Name}” via AirPlay, but it hasn't advertised the Companion service yet.\n" +
                                  "Wake the Apple TV (press a button on its remote) and wait a moment.",
                CloseButtonText = "OK",
                XamlRoot        = Content.XamlRoot,
            };
            await dlg.ShowAsync();
            return;
        }
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
        PairLog.Text = string.Empty;
        PairLogScroll.Visibility = Visibility.Visible;
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        void AppendLog(string msg) =>
            dq?.TryEnqueue(() =>
            {
                PairLog.Text += $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n";
            });
        try
        {
            AppendLog($"connecting to {_picked.AddressString}:{_picked.Port} (TCP)…");
            await using var conn = new CompanionConnection(_picked.Address.ToString(), _picked.Port);
            await conn.ConnectAsync();
            AppendLog("TCP connected, starting pair-setup");

            var pairingId = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString());
            var setup = new CompanionPairSetup(conn, timeout: TimeSpan.FromSeconds(30));
            var creds = await setup.RunAsync(pin, pairingId,
                                              clientSeed: null,
                                              displayName: _picked.Name,
                                              onProgress: AppendLog);

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
