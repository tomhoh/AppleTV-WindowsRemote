// SPDX-License-Identifier: MIT
//
// Fetches and caches Apple TV app icons via the iTunes Search API.
//
// pyatv / our CompanionSession.ListAppsAsync returns just {bundleID → name}
// — the TV does not ship icon bytes. To get artwork we hit
//   https://itunes.apple.com/lookup?bundleId=<id>&entity=software
// read `results[0].artworkUrl100`, bump the URL to 200x200bb, and download
// the PNG. Results are written to %LOCALAPPDATA%\AppleTVRemote\Icons\<id>.png
// with a 12 h staleness threshold; an in-memory cache avoids disk hits on
// every render. Bundle IDs that iTunes doesn't recognise are remembered for
// the session so we don't keep banging the API.
//
// UI-agnostic: returns raw PNG bytes. WPF wraps in BitmapImage from a stream;
// WinUI does the same with its Microsoft.UI.Xaml.Media.Imaging.BitmapImage.

using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace AppleTVProtocol;

public static class AppIconCache
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, byte[]>     _mem      = new();
    private static readonly ConcurrentDictionary<string, bool>       _notFound = new();
    private static readonly ConcurrentDictionary<string, Task<byte[]?>> _inflight = new();
    private static readonly TimeSpan _staleness = TimeSpan.FromHours(12);

    private static string CacheDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AppleTVRemote",
                "Icons");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Resolve the icon for a bundle id and return the absolute path to the
    /// cached PNG on disk. Null if iTunes has nothing on file or the fetch
    /// failed. Convenient for image loaders that prefer file paths over bytes
    /// (e.g. WinUI <c>BitmapImage.UriSource</c>).
    /// </summary>
    public static async Task<string?> GetCachedPathAsync(string bundleId, CancellationToken cancel = default)
    {
        var bytes = await GetAsync(bundleId, cancel).ConfigureAwait(false);
        if (bytes is null) return null;
        var path = Path.Combine(CacheDir, SanitizeFileName(bundleId) + ".png");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Return PNG bytes for an app icon, or null if iTunes has nothing on file
    /// for this bundle id. Fast path: in-memory, then disk; slow path: HTTP fetch.
    /// Concurrent callers for the same id share one in-flight task.
    /// </summary>
    public static Task<byte[]?> GetAsync(string bundleId, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(bundleId)) return Task.FromResult<byte[]?>(null);

        if (_mem.TryGetValue(bundleId, out var cached)) return Task.FromResult<byte[]?>(cached);
        if (_notFound.ContainsKey(bundleId))            return Task.FromResult<byte[]?>(null);

        return _inflight.GetOrAdd(bundleId, id => FetchAsync(id, cancel))
                        .ContinueWith(t =>
                        {
                            _inflight.TryRemove(bundleId, out _);
                            return t.Result;
                        }, TaskScheduler.Default);
    }

    private static async Task<byte[]?> FetchAsync(string bundleId, CancellationToken cancel)
    {
        // 1. Disk cache check — accept if fresh.
        var path = Path.Combine(CacheDir, SanitizeFileName(bundleId) + ".png");
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 0 && (DateTime.UtcNow - fi.LastWriteTimeUtc) < _staleness)
            {
                var bytes = await File.ReadAllBytesAsync(path, cancel).ConfigureAwait(false);
                _mem[bundleId] = bytes;
                return bytes;
            }
        }
        catch { /* fall through to network */ }

        // 2. iTunes lookup — { results: [{ artworkUrl100: "...100x100bb.png", ... }, ...] }
        string? artwork;
        try
        {
            var lookup = $"https://itunes.apple.com/lookup?bundleId={Uri.EscapeDataString(bundleId)}&entity=software";
            using var resp = await _http.GetAsync(lookup, cancel).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancel).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.GetArrayLength() == 0 ||
                !results[0].TryGetProperty("artworkUrl100", out var artElem))
            {
                _notFound[bundleId] = true;
                return null;
            }
            artwork = artElem.GetString();
        }
        catch
        {
            // Swallow — iTunes outages / DNS / proxy. Leave it un-cached so we try
            // again next launch instead of poisoning the not-found set.
            return null;
        }
        if (string.IsNullOrEmpty(artwork)) { _notFound[bundleId] = true; return null; }

        // Bump resolution: "100x100bb" → "200x200bb" (matches the Mac sibling).
        artwork = artwork.Replace("100x100bb", "200x200bb", StringComparison.Ordinal);

        // 3. Download the PNG.
        byte[] pngBytes;
        try
        {
            pngBytes = await _http.GetByteArrayAsync(artwork, cancel).ConfigureAwait(false);
        }
        catch { return null; }
        if (pngBytes.Length == 0) return null;

        // 4. Write through to disk + in-memory cache. Disk write is best-effort
        // (read-only profile, AV-locked file, etc.).
        try { await File.WriteAllBytesAsync(path, pngBytes, cancel).ConfigureAwait(false); }
        catch { /* keep memory copy regardless */ }
        _mem[bundleId] = pngBytes;
        return pngBytes;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        return new string(chars);
    }
}
