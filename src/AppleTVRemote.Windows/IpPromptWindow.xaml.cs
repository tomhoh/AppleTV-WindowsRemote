// SPDX-License-Identifier: MIT
//
// Small modal that asks the user for an Apple TV IP address. Used as a
// workaround when multicast discovery is too slow / unreliable to find
// the TV; the caller then runs a unicast mDNS probe against that IP.

using System.Windows;
using System.Windows.Input;

namespace AppleTVRemote.Windows;

public partial class IpPromptWindow : Window
{
    public string EnteredIp => IpBox.Text?.Trim() ?? string.Empty;

    public IpPromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => IpBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)     { DialogResult = true;  Close(); }
    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  { DialogResult = true;  Close(); }
        if (e.Key == Key.Escape) { DialogResult = false; Close(); }
    }
}
