// SPDX-License-Identifier: MIT
//
// Constructs the MRP init messages (varint-length-prefixed protobuf
// ProtocolMessage frames). Direct port of the Swift sibling's MRPMessage.swift.
//
// In MRP's ProtocolMessage the inner message is an EXTENSION field whose
// number is NOT the same as the Type enum value — using the wrong field
// number makes the TV silently ignore the message. Numbers below are the
// pyatv-verified extension field numbers.

namespace AppleTVProtocol.AirPlay;

public static class MrpMessages
{
    // (Type enum value, extension field number) for each message we send.
    private const int TypeDeviceInfo         = 15, ExtDeviceInfo         = 20;
    private const int TypeClientUpdates      = 16, ExtClientUpdates      = 21;
    private const int TypeSetConnectionState = 38, ExtSetConnectionState = 42;
    private const int TypeGetKeyboardSession = 24, ExtGetKeyboardSession = 29;

    /// <summary>DEVICE_INFO — identifies this client. uniqueIdentifier should
    /// be the Companion clientID so the TV recognizes us and pushes state.</summary>
    public static byte[] DeviceInfo(string uniqueIdentifier)
    {
        var inner = new ProtobufWriter();
        inner.WriteStringField(1, uniqueIdentifier);
        inner.WriteStringField(2, "Mac Remote");
        inner.WriteStringField(3, "iPhone");                 // localizedModelName
        inner.WriteStringField(4, "18G82");                  // systemBuildVersion
        inner.WriteStringField(5, "com.apple.TVRemote");     // applicationBundleIdentifier
        inner.WriteStringField(6, "344.28");                 // applicationBundleVersion
        inner.WriteVarintField(7, 1);                        // protocolVersion
        inner.WriteVarintField(8, 108);                      // lastSupportedMessageType
        inner.WriteBoolField(9, true);                       // supportsSystemPairing
        inner.WriteBoolField(10, true);                      // allowsPairing
        inner.WriteStringField(12, "com.apple.TVMusic");     // systemMediaApplication
        inner.WriteBoolField(13, true);                      // supportsACL
        inner.WriteBoolField(14, true);                      // supportsSharedQueue
        inner.WriteBoolField(15, true);                      // supportsExtendedMotion
        inner.WriteVarintField(17, 2);                       // sharedQueueVersion
        inner.WriteVarintField(21, 1);                       // deviceClass = iPhone
        inner.WriteVarintField(22, 1);                       // logicalDeviceCount
        return MakeMessage(TypeDeviceInfo, ExtDeviceInfo, inner);
    }

    /// <summary>SET_CONNECTION_STATE — tells the TV we're Connected (state=2).</summary>
    public static byte[] SetConnectionState()
    {
        var inner = new ProtobufWriter();
        inner.WriteVarintField(1, 2);   // state = Connected
        return MakeMessage(TypeSetConnectionState, ExtSetConnectionState, inner);
    }

    /// <summary>CLIENT_UPDATES_CONFIG — subscribe to now-playing/volume/etc pushes.</summary>
    public static byte[] ClientUpdatesConfig()
    {
        var inner = new ProtobufWriter();
        inner.WriteBoolField(1, true);  // artworkUpdates
        inner.WriteBoolField(2, true);  // nowPlayingUpdates
        inner.WriteBoolField(3, true);  // volumeUpdates
        inner.WriteBoolField(4, true);  // keyboardUpdates
        inner.WriteBoolField(5, true);  // outputDeviceUpdates
        return MakeMessage(TypeClientUpdates, ExtClientUpdates, inner);
    }

    /// <summary>GET_KEYBOARD_SESSION — completes the pyatv-standard init sequence.</summary>
    public static byte[] GetKeyboardSession()
        => MakeMessage(TypeGetKeyboardSession, ExtGetKeyboardSession, inner: null);

    /// <summary>
    /// Wrap an inner message in a ProtocolMessage envelope. pyatv always
    /// includes identifier (field 2), errorCode (field 4 = 0), and
    /// uniqueIdentifier (field 85) — the TV ignores messages that lack them.
    /// Then varint-length-prefix the whole frame (MRP data-channel framing).
    /// </summary>
    private static byte[] MakeMessage(int typeEnum, int extField, ProtobufWriter? inner)
    {
        var outer = new ProtobufWriter();
        outer.WriteVarintField(1, (ulong)typeEnum);
        outer.WriteStringField(2, Guid.NewGuid().ToString().ToUpperInvariant());   // identifier
        outer.WriteVarintField(4, 0);                                              // errorCode
        outer.WriteStringField(85, Guid.NewGuid().ToString().ToUpperInvariant());  // uniqueIdentifier
        var innerBytes = inner?.ToArray();
        if (innerBytes is { Length: > 0 })
            outer.WriteBytesField(extField, innerBytes);

        var body = outer.ToArray();
        var prefix = ProtobufWriter.EncodeVarint((ulong)body.Length);
        var frame = new byte[prefix.Length + body.Length];
        prefix.CopyTo(frame, 0);
        body.CopyTo(frame, prefix.Length);
        return frame;
    }
}
