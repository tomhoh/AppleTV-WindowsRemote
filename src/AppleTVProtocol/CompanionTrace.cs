// SPDX-License-Identifier: MIT
//
// Append-only diagnostic trace for Companion sessions.
//
// Writes one line per event to %LOCALAPPDATA%\AppleTVRemote\session.log
// (rolled when it exceeds ~1 MB). Off by default — opt in by calling
// CompanionTrace.Enable() from the app at startup. Every Send / Receive /
// state transition prints a single line so an end-user can copy-paste the
// log to a bug report.

using System.Collections.Concurrent;

namespace AppleTVProtocol;

public static class CompanionTrace
{
    private static readonly object _gate = new();
    private static string? _logPath;

    /// <summary>
    /// Turn tracing on. Subsequent <see cref="Write"/> calls append to the
    /// file at the returned path. Safe to call more than once.
    /// </summary>
    public static string Enable()
    {
        lock (_gate)
        {
            if (_logPath is not null) return _logPath;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AppleTVRemote");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "session.log");
            // Roll once per session if the file is big.
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 1_000_000)
                    File.Move(path, path + ".old", overwrite: true);
            }
            catch { /* logging is best-effort */ }
            _logPath = path;
            WriteRaw($"--- session opened {DateTime.UtcNow:O} ---");
            return path;
        }
    }

    /// <summary>Whether tracing is on. Cheap to check on every Send/Receive.</summary>
    public static bool IsEnabled => _logPath is not null;

    public static void Write(string category, string message)
    {
        if (_logPath is null) return;
        WriteRaw($"{DateTime.UtcNow:HH:mm:ss.fff} {category,-10} {message}");
    }

    public static void WriteBytes(string category, string label, byte[] bytes, int maxBytes = 64)
    {
        if (_logPath is null) return;
        int n = Math.Min(bytes.Length, maxBytes);
        var hex = Convert.ToHexString(bytes, 0, n).ToLowerInvariant();
        var more = bytes.Length > n ? $"…+{bytes.Length - n}B" : "";
        WriteRaw($"{DateTime.UtcNow:HH:mm:ss.fff} {category,-10} {label} [{bytes.Length}B] {hex}{more}");
    }

    private static void WriteRaw(string line)
    {
        try
        {
            lock (_gate)
            {
                if (_logPath is null) return;
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch { /* logging is best-effort */ }
    }
}
