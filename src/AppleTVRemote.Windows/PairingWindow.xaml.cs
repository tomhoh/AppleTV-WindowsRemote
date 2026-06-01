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

    public PairingWindow()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
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

                // Dedup by DeviceKey (deviceid TXT, or IP) so an Apple TV
                // that shows up once via _companion-link and once via
                // _airplay is one row in the list. Prefer the
                // companion-link entry — that's the one with the right
                // port for pairing; the airplay entry has port 7000.
                for (int i = 0; i < _devices.Count; i++)
                {
                    if (_devices[i].DeviceKey != dev.DeviceKey) continue;
                    if (!_devices[i].IsCompanionLinkEntry && dev.IsCompanionLinkEntry)
                        _devices[i] = dev;   // upgrade airplay → companion
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
        // Workaround for the slow / unreliable Makaretu multicast on
        // Windows: lets the user type the TV's IP and we probe it via
        // direct unicast mDNS (responds in ms vs minutes via multicast).
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
            var dev = await DeviceDiscovery.UnicastProbeAsync(ip,
                timeout: TimeSpan.FromSeconds(4));
            if (dev is null)
            {
                MessageBox.Show(
                    $"No reply from {ip}. Check the address (Apple TV → Settings → " +
                    "Network → Wi-Fi) and that the TV is awake.",
                    "Apple TV Remote", MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateHeaderForCount();
                return;
            }
            // Got the companion port — proceed to PIN entry as if user
            // had clicked a discovered list row.
            _picked = dev;
            StopDiscovery();
            DeviceList.Visibility = Visibility.Collapsed;
            PinPanel  .Visibility = Visibility.Visible;
            BackBtn   .Visibility = Visibility.Visible;
            PairBtn   .Visibility = Visibility.Visible;
            RescanBtn .Visibility = Visibility.Collapsed;
            ByIpBtn   .Visibility = Visibility.Collapsed;

            HeaderText.Text = $"Pair with {dev.Name}";
            SubText   .Text = $"{dev.AddressString}:{dev.Port}   ·   {dev.ModelFriendly}";
            PinPrompt .Text = "Enter the 4-digit PIN shown on your Apple TV:";
            PinBox.Password = string.Empty;
            PinBox.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Probe failed: {ex.Message}", "Apple TV Remote",
                MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateHeaderForCount();
        }
    }

    // ─── Stage 2: PIN entry ────────────────────────────────────────

    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not AppleTVDevice d) return;
        if (!d.IsCompanionLinkEntry)
        {
            // AirPlay-only entry — we don't have the Companion port. Tell
            // the user to wake the TV (which makes it advertise
            // companion-link) and try again. Don't progress to Stage 2.
            DeviceList.SelectedItem = null;
            MessageBox.Show(
                $"Found “{d.Name}” via AirPlay, but it hasn't advertised the Companion service yet.\n" +
                "Wake the Apple TV (press a button on its remote) and click Rescan.",
                "Apple TV Remote", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _picked = d;
        StopDiscovery();   // free up the multicast socket while the user types

        DeviceList.Visibility = Visibility.Collapsed;
        PinPanel  .Visibility = Visibility.Visible;
        BackBtn   .Visibility = Visibility.Visible;
        PairBtn   .Visibility = Visibility.Visible;
        RescanBtn .Visibility = Visibility.Collapsed;

        HeaderText.Text = $"Pair with “{d.Name}”";
        SubText   .Text = $"{d.AddressString}:{d.Port}   ·   {d.ModelFriendly}";
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
        ByIpBtn   .Visibility = Visibility.Visible;
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
        PairLog.Clear();
        PairLog.Visibility = Visibility.Visible;
        void AppendLog(string msg) =>
            Dispatcher.Invoke(() => PairLog.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"));

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

            DialogResult = true;
            Close();
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
