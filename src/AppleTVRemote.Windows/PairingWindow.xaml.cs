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
using System.Windows.Media;
using AppleTVProtocol;
using Microsoft.Win32;

namespace AppleTVRemote.Windows;

public partial class PairingWindow : Window
{
    private readonly ObservableCollection<AppleTVDevice> _devices = new();
    private DeviceDiscovery? _discovery;
    private CancellationTokenSource? _discoveryCts;
    private AppleTVDevice? _picked;

    public StoredCredentials? PairedCredentials { get; private set; }

    /// <summary>
    /// Value from the "show this PC on the Apple TV as" field, trimmed and
    /// truncated. Falls back to <see cref="Environment.MachineName"/> if the
    /// user cleared it. Called on the UI thread via <see cref="Dispatcher"/>
    /// so it stays consistent with what the user sees.
    /// </summary>
    private string ResolveControllerName()
    {
        string raw = Dispatcher.Invoke(() => ClientNameBox.Text?.Trim() ?? string.Empty);
        return TruncateForATV(string.IsNullOrEmpty(raw) ? Environment.MachineName : raw);
    }

    private static string TruncateForATV(string s) =>
        s.Length <= 30 ? s : s.Substring(0, 30);

    public PairingWindow()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        // Prefill the "show this PC on the Apple TV as" field with the
        // machine name, capped at 30 chars (matches the ATV Settings row
        // truncation). The user can edit it before submitting the PIN.
        ClientNameBox.Text = TruncateForATV(Environment.MachineName);
        // Apply theme before the visual tree paints to avoid a flash of
        // the light defaults on a dark-mode system.
        ApplyTheme();
        Loaded += async (_, _) =>
        {
            ApplyTheme();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            await StartDiscoveryAsync();
        };
        Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            StopDiscovery();
        };
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Dispatcher.Invoke(ApplyTheme);
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme")    is int a) return a == 0;
            if (key?.GetValue("SystemUsesLightTheme") is int s) return s == 0;
        }
        catch { }
        return false;
    }

    private void ApplyTheme()
    {
        if (IsSystemDark())
        {
            Resources["WindowBackground"] = Solid("#FF1F1F1F");
            Resources["WindowForeground"] = Solid("#E6FFFFFF");
            Resources["WindowSubtle"]     = Solid("#99FFFFFF");
            Resources["WindowStroke"]     = Solid("#33FFFFFF");
            Resources["ListBackground"]   = Solid("#FF2B2B2B");
        }
        else
        {
            Resources["WindowBackground"] = Solid("#FFF8F8F8");
            Resources["WindowForeground"] = Solid("#E6000000");
            Resources["WindowSubtle"]     = Solid("#80000000");
            Resources["WindowStroke"]     = Solid("#26000000");
            Resources["ListBackground"]   = Solid("#FFFFFFFF");
        }
    }

    private static SolidColorBrush Solid(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex)!;
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ─── Stage 1: discovery ────────────────────────────────────────

    private async Task StartDiscoveryAsync()
    {
        StopDiscovery();
        _devices.Clear();
        HeaderText.Text = "Discovering Apple TVs on your network…";
        SubText.Text    = "Make sure your TV is awake and on the same network.";

        _discoveryCts = new CancellationTokenSource();
        // Query BOTH companion-link and airplay — pyatv does this too.
        // Some Apple TVs respond faster to one than the other, and the
        // AirPlay TXT carries a 'model' field that lets us recognise the
        // device even when the companion-link advert hasn't arrived yet.
        _discovery = new DeviceDiscovery(DeviceDiscovery.CompanionAndAirplay);
        int totalSeen = 0;
        _discovery.Discovered += (_, dev) =>
            Dispatcher.Invoke(() =>
            {
                System.Threading.Interlocked.Increment(ref totalSeen);

                // Filter: only Apple TVs / HomePods (they set the
                // 'rpmd' TXT field; iPhones in screen-mirror mode and
                // Macs with Sharing on don't). Diagnostic counter in
                // the sub-text so the user can tell the scan is alive
                // even when nothing makes it through.
                if (!dev.IsAppleTV)
                {
                    if (_devices.Count == 0)
                        SubText.Text = $"Scanning… ({totalSeen} non-Apple-TV device{(totalSeen == 1 ? "" : "s")} seen so far)";
                    return;
                }

                // Dedup by DeviceKey (deviceid TXT, or IP). An Apple TV
                // that shows up under both _companion-link and _airplay
                // collapses to one row — but we union the TXT records
                // from both so the airplay 'fn' (friendlyName) survives
                // even after we pick the companion-link entry as the
                // canonical one (whose Port is the pairing port; airplay
                // is port 7000).
                for (int i = 0; i < _devices.Count; i++)
                {
                    if (_devices[i].DeviceKey != dev.DeviceKey) continue;
                    // Pick whichever is the companion-link entry as the
                    // "base" (its Port is the pairing port), then merge
                    // the OTHER one's TXT in.
                    var (companionLink, other) = _devices[i].IsCompanionLinkEntry
                        ? (_devices[i], dev)
                        : dev.IsCompanionLinkEntry
                            ? (dev, _devices[i])
                            : (_devices[i], dev);
                    _devices[i] = companionLink.MergedWith(other);
                    return;
                }
                _devices.Add(dev);
                UpdateHeaderForCount();
            });

        // Just start the discovery — re-query timer keeps shouting until
        // the window closes. No deadline; the user picks when they're done.
        _discovery.Start();
        await Task.CompletedTask;
        UpdateHeaderForCount();
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

    private async void OnEnterIpClicked(object sender, RoutedEventArgs e)
    {
        var prompt = new IpPromptWindow { Owner = this };
        if (prompt.ShowDialog() != true) return;
        var ipText = prompt.EnteredIp;
        if (!System.Net.IPAddress.TryParse(ipText, out var ip))
        {
            MessageBox.Show($"'{ipText}' isn't a valid IP address.", "Apple TV Remote",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        HeaderText.Text = $"Probing {ip}…";
        SubText.Text    = "Sending unicast mDNS query directly to the TV.";
        try
        {
            var dev = await DeviceDiscovery.UnicastProbeAsync(ip, timeout: TimeSpan.FromSeconds(4));
            if (dev is null)
            {
                MessageBox.Show(
                    $"No reply from {ip}. Check the address (Apple TV → Settings → " +
                    "Network → Wi-Fi) and that the TV is awake.",
                    "Apple TV Remote", MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateHeaderForCount();
                return;
            }
            StartPairFor(dev);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Probe failed: {ex.Message}", "Apple TV Remote",
                MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateHeaderForCount();
        }
    }

    // ─── Pair flow (3 stages: connecting → PIN-on-TV → enter PIN) ──

    private TaskCompletionSource<string>? _pinTcs;
    private CancellationTokenSource? _pairCts;

    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not AppleTVDevice d) return;
        if (!d.IsCompanionLinkEntry)
        {
            DeviceList.SelectedItem = null;
            MessageBox.Show(
                $"Found “{d.Name}” via AirPlay, but it hasn't advertised the Companion service yet.\n" +
                "Wake the Apple TV (press a button on its remote) and click Rescan.",
                "Apple TV Remote", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        StartPairFor(d);
    }

    /// <summary>
    /// Start the pair flow: hide the device list, show "connecting…",
    /// kick off pair-setup in the background. The PIN entry stays
    /// hidden until M2 arrives — that's when the TV displays the PIN,
    /// and only then can the user read it off the screen.
    /// </summary>
    private void StartPairFor(AppleTVDevice d)
    {
        _picked = d;
        StopDiscovery();

        DeviceList.Visibility = Visibility.Collapsed;
        PinPanel  .Visibility = Visibility.Collapsed;   // hidden until M2
        BackBtn   .Visibility = Visibility.Visible;
        PairBtn   .Visibility = Visibility.Collapsed;   // shown when M2 arrives
        RescanBtn .Visibility = Visibility.Collapsed;
        ByIpBtn   .Visibility = Visibility.Collapsed;

        HeaderText.Text = $"Connecting to {d.Name}…";
        SubText   .Text = $"{d.AddressString}:{d.Port}   ·   {d.ModelFriendly}";

        PairLog.Clear();
        PairLog.Visibility   = Visibility.Visible;
        PairProgress.Visibility = Visibility.Visible;

        _pinTcs  = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pairCts = new CancellationTokenSource();
        _ = RunPairSetupAsync(d, _pinTcs, _pairCts.Token);
    }

    private async Task RunPairSetupAsync(AppleTVDevice dev, TaskCompletionSource<string> pinTcs, CancellationToken ct)
    {
        void AppendLog(string msg) =>
            Dispatcher.Invoke(() => PairLog.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"));

        try
        {
            AppendLog($"connecting to {dev.AddressString}:{dev.Port} (TCP)…");
            await using var conn = new CompanionConnection(dev.Address.ToString(), dev.Port);
            await conn.ConnectAsync(ct);
            AppendLog("TCP connected, sending M1 to wake PIN display");

            // Stable per-installation client identifier. Previously we
            // generated a fresh Guid.NewGuid() for every pair-setup
            // attempt, which caused every re-pair to leave an orphan
            // controller entry on the Apple TV (the TV then polled
            // those stale entries forever as "unknown" devices). Mac
            // sibling has always persisted its clientID in UserDefaults
            // for the same reason — see HAPPairing.swift's clientID.
            var pairingId = ClientIdStore.LoadOrCreateBytes();
            var setup = new CompanionPairSetup(conn, timeout: TimeSpan.FromMinutes(2));
            var creds = await setup.RunAsync(
                getPinAsync: async () =>
                {
                    // M2 just arrived → TV is now displaying the PIN.
                    // Flip the UI from "waiting…" to PIN entry.
                    Dispatcher.Invoke(() =>
                    {
                        HeaderText.Text = $"Read the PIN from {dev.Name}";
                        SubText.Text    = "Type it below and click Pair.";
                        PinPanel.Visibility = Visibility.Visible;
                        PairBtn .Visibility = Visibility.Visible;
                        PinPrompt.Text = "Enter the 4-digit PIN shown on your Apple TV:";
                        PinBox.Password = string.Empty;
                        PinBox.IsEnabled = true;
                        PairBtn.IsEnabled = true;
                        PairProgress.Visibility = Visibility.Collapsed;
                        PinBox.Focus();
                    });
                    var pin = await pinTcs.Task.WaitAsync(ct);
                    Dispatcher.Invoke(() =>
                    {
                        // User clicked Pair — disable inputs while we finish M3-M6.
                        PinBox.IsEnabled = false;
                        PairBtn.IsEnabled = false;
                        PairProgress.Visibility = Visibility.Visible;
                    });
                    return pin;
                },
                clientPairingId: pairingId,
                clientSeed: null,
                displayName: null,
                onProgress: AppendLog,
                cancel: ct,
                // Late-bound so edits to ClientNameBox up until the user
                // clicks Pair land in the M5 name TLV. Reads on the UI
                // thread via Dispatcher.Invoke; safe because RunAsync is
                // fire-and-forget from a UI-thread event handler.
                displayNameProvider: ResolveControllerName);

            // Success — save credentials and close the dialog.
            var deviceId = Convert.ToHexString(creds.AtvIdentifier).ToLowerInvariant();
            PairedCredentials = new StoredCredentials(
                DeviceId:       deviceId,
                DisplayName:    dev.Name,
                Credentials:    creds,
                PairedAtUtc:    DateTime.UtcNow,
                Host:           dev.Address.ToString(),
                Port:           dev.Port,
                ControllerName: ResolveControllerName());
            new CredentialStore().Save(PairedCredentials);

            Dispatcher.Invoke(() =>
            {
                DialogResult = true;
                Close();
            });
        }
        catch (OperationCanceledException)
        {
            AppendLog("pair-setup cancelled");
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                ShowPinError(BuildErrorMessage(ex));
                PairProgress.Visibility = Visibility.Collapsed;
                // Leave the dialog open so the user can read the log + retry.
            });
        }
    }

    private async void OnBackClicked(object sender, RoutedEventArgs e)
    {
        // Cancel any in-flight pair-setup so the connection closes.
        _pairCts?.Cancel();
        _pinTcs?.TrySetCanceled();
        _picked = null;

        PinPanel  .Visibility = Visibility.Collapsed;
        BackBtn   .Visibility = Visibility.Collapsed;
        PairBtn   .Visibility = Visibility.Collapsed;
        PairLog   .Visibility = Visibility.Collapsed;
        PairProgress.Visibility = Visibility.Collapsed;
        DeviceList.Visibility = Visibility.Visible;
        RescanBtn .Visibility = Visibility.Visible;
        ByIpBtn   .Visibility = Visibility.Visible;
        DeviceList.SelectedItem = null;
        UpdateHeaderForCount();
        PinError.Visibility = Visibility.Collapsed;

        await Task.Yield();
    }

    /// <summary>
    /// User clicked Pair after entering the PIN. The actual pair-setup
    /// state machine is already running in the background — all we do
    /// here is unblock its <c>getPinAsync</c> by resolving the TCS.
    /// </summary>
    private void OnPairClicked(object sender, RoutedEventArgs e)
    {
        var pin = PinBox.Password?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(pin)) { ShowPinError("PIN is required."); return; }
        _pinTcs?.TrySetResult(pin);
    }

    /// <summary>
    /// Walk the inner-exception chain so the user sees the actual cause
    /// (the outer HapPairingException is usually a one-liner like
    /// "M6 decrypt failed (wrong PIN?)" but the inner CryptographicException
    /// has the real detail).
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
        PairBtn .IsEnabled = !busy;
        BackBtn .IsEnabled = !busy;
        PinBox  .IsEnabled = !busy;
        PairProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) PinError.Visibility = Visibility.Collapsed;
    }
}
