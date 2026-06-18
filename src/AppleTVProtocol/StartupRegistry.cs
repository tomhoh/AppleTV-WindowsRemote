// SPDX-License-Identifier: MIT
//
// "Open at login" toggle backed by HKCU\Software\Microsoft\Windows
// \CurrentVersion\Run — the standard per-user Windows autostart slot.
// Doesn't require admin and only affects the current user.

using Microsoft.Win32;

namespace AppleTVProtocol;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class StartupRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Name under which we register the autostart entry. Both the WPF and
    /// WinUI builds share this slot — only one can win, which is the
    /// desired behavior (the user picks which flavor they want to launch).
    /// </summary>
    public const string EntryName = "AppleTVRemote";

    /// <summary>True if an autostart entry exists for this app (any path).</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(EntryName) is not null;
            }
            catch { return false; }
        }
    }

    /// <summary>Register the given exe path to launch at user login.</summary>
    public static void Enable(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RunKey);
            // Quote the path to survive spaces in the install location.
            key?.SetValue(EntryName, $"\"{executablePath}\"");
        }
        catch { /* best-effort — user can fall back to Task Scheduler */ }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(EntryName, throwOnMissingValue: false);
        }
        catch { /* swallow — nothing actionable */ }
    }

    /// <summary>Flip the autostart state, returning the new IsEnabled value.</summary>
    public static bool Toggle(string executablePath)
    {
        if (IsEnabled) { Disable(); return false; }
        Enable(executablePath);
        return IsEnabled;
    }
}
