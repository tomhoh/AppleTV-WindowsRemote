// SPDX-License-Identifier: MIT
//
// Decodes inbound MRP ProtocolMessage payloads into now-playing state.
// Direct port of the Swift sibling's MRPDecoder.swift — field numbers
// verified against pyatv's protobuf definitions.
//
//   SET_STATE_MESSAGE (type=4, ext field=9 → SetStateMessage)
//     SetStateMessage
//       field 3: PlaybackQueue
//         field 2 (repeated): ContentItem
//           field 2: ContentItemMetadata
//             field  1: title            : string
//             field  6: albumName        : string
//             field  7: trackArtistName  : string
//             field 14: duration         : double
//             field 35: elapsedTime      : double
//       field 6: playbackState   : enum (1=playing, 2=paused, 3=stopped, 5=seeking)
//       field 11: playbackStateTimestamp : double
//   UPDATE_CONTENT_ITEM_MESSAGE (type=56, ext field=60)
//     field 1 (repeated): ContentItem  (incremental metadata/elapsed deltas)

namespace AppleTVProtocol.AirPlay;

/// <summary>Partial now-playing state decoded from a single MRP message.</summary>
public sealed class MrpNowPlayingUpdate
{
    public string? Title;
    public string? Artist;
    public string? Album;
    public int?    PlaybackState;          // 1=playing 2=paused 3=stopped 5=seeking
    public double? Duration;
    public double? ElapsedTime;
    public double? PlaybackStateTimestamp;

    public bool IsEmpty =>
        Title is null && Artist is null && Album is null &&
        PlaybackState is null && Duration is null && ElapsedTime is null;

    public bool IsPlaying => PlaybackState is 1 or 5;
}

public static class MrpNowPlayingDecoder
{
    /// <summary>
    /// Attempt to decode a now-playing update from a raw ProtocolMessage
    /// payload. Returns null if the message type carries no playback data.
    /// </summary>
    public static MrpNowPlayingUpdate? Decode(ReadOnlySpan<byte> protocolMessage)
    {
        // ProtocolMessage field 1 = type (varint enum).
        var msgType = Protobuf.VarintField(protocolMessage, 1);
        return msgType switch
        {
            4  => DecodeSetState(protocolMessage),
            56 => DecodeUpdateContentItem(protocolMessage),
            _  => null,
        };
    }

    /// <summary>Raw message type (field 1) of a ProtocolMessage, for routing/logging.</summary>
    public static ulong? MessageType(ReadOnlySpan<byte> protocolMessage)
        => Protobuf.VarintField(protocolMessage, 1);

    private static MrpNowPlayingUpdate? DecodeSetState(ReadOnlySpan<byte> outer)
    {
        // Extension field 9 carries SetStateMessage bytes.
        var ssm = Protobuf.BytesField(outer, 9);
        if (ssm is null) return null;

        var u = new MrpNowPlayingUpdate();

        if (Protobuf.VarintField(ssm, 6) is { } st)
            u.PlaybackState = (int)st;
        if (Protobuf.DoubleField(ssm, 11) is { } ts)
            u.PlaybackStateTimestamp = ts;

        // playbackQueue (field 3) → first ContentItem (field 2) → metadata.
        var pq = Protobuf.BytesField(ssm, 3);
        if (pq is not null && Protobuf.BytesField(pq, 2) is { } ci)
            ApplyContentItem(ci, u);

        return u.IsEmpty ? null : u;
    }

    private static MrpNowPlayingUpdate? DecodeUpdateContentItem(ReadOnlySpan<byte> outer)
    {
        var inner = Protobuf.BytesField(outer, 60);
        if (inner is null) return null;

        var u = new MrpNowPlayingUpdate();
        // contentItems (field 1, repeated) — first item is the active one.
        if (Protobuf.BytesField(inner, 1) is { } ci)
            ApplyContentItem(ci, u);

        return u.IsEmpty ? null : u;
    }

    private static void ApplyContentItem(byte[] contentItem, MrpNowPlayingUpdate u)
    {
        // ContentItem field 2 = ContentItemMetadata.
        var meta = Protobuf.BytesField(contentItem, 2);
        if (meta is null) return;
        if (Protobuf.StringField(meta, 1)  is { } t) u.Title       = t;
        if (Protobuf.StringField(meta, 6)  is { } a) u.Album       = a;
        if (Protobuf.StringField(meta, 7)  is { } r) u.Artist      = r;
        if (Protobuf.DoubleField(meta, 14) is { } d) u.Duration    = d;
        if (Protobuf.DoubleField(meta, 35) is { } e) u.ElapsedTime = e;
    }
}
