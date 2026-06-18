// SPDX-License-Identifier: MIT
//
// Parsed snapshot of the inner `_c` dict carried in an `_iMC` (Media
// Control) event from the Apple TV. The actual fields vary by tvOS
// version + what's playing — most are optional. The full inner dict
// is kept in <see cref="Raw"/> so callers can pull anything we didn't
// name.

namespace AppleTVProtocol;

public sealed record NowPlayingUpdate(
    string? Title,
    string? Artist,
    string? Album,
    string? AppBundleId,
    bool    IsPlaying,
    IReadOnlyDictionary<string, object?> Raw)
{
    /// <summary>
    /// Best-effort parser. Pulls common-name string fields out of the
    /// inner dict; unknown keys are preserved in <see cref="Raw"/>.
    /// </summary>
    public static NowPlayingUpdate FromInnerDict(IDictionary<string, object?> inner)
    {
        string? Get(params string[] keys)
        {
            foreach (var k in keys)
                if (inner.TryGetValue(k, out var v) && v is string s && s.Length > 0)
                    return s;
            return null;
        }

        // The `_mcF` (Media Control Flags) value is the only field we've
        // confirmed in the wild — non-zero indicates the TV thinks media
        // is active (playing or paused). Title/artist may or may not be
        // present even when _mcF > 0; without an AirPlay-MRP tunnel many
        // tvOS versions only push the flag.
        bool isPlaying = false;
        if (inner.TryGetValue("_mcF",  out var f1) ||
            inner.TryGetValue("_mcF1", out f1)     ||
            inner.TryGetValue("_mcF2", out f1))
        {
            isPlaying = CompanionSession.UnboxInt(f1) is > 0;
        }

        return new NowPlayingUpdate(
            Title:       Get("title", "iTit", "kMRMediaRemoteNowPlayingInfoTitle"),
            Artist:      Get("artist", "iArt", "kMRMediaRemoteNowPlayingInfoArtist"),
            Album:       Get("album", "iAlb", "kMRMediaRemoteNowPlayingInfoAlbum"),
            AppBundleId: Get("appBundleID", "_bundleID", "_bID"),
            IsPlaying:   isPlaying,
            Raw:         new Dictionary<string, object?>(inner));
    }

    /// <summary>True when the event carries enough info to actually render a banner.</summary>
    public bool HasDisplayableMetadata =>
        !string.IsNullOrWhiteSpace(Title) ||
        !string.IsNullOrWhiteSpace(Artist) ||
        !string.IsNullOrWhiteSpace(Album);
}
