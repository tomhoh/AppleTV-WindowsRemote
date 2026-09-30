// SPDX-License-Identifier: MIT
//
// Builds and parses the RTI (Remote Text Input) binary-plist payloads used
// in Companion `_tiC` events. Direct port of the Swift sibling's
// RTITextOperations.swift + pyatv's plist_payloads/rti_text_operations.py.
//
// The payload is an RTIKeyedArchiver-encoded binary plist containing an
// `RTITextOperations` object. Every value inside an instance dict is a
// UID reference into the `$objects` array — even plain strings. That
// forces us to hand-roll the plist via BinaryPlistWriter, since neither
// .NET nor Swift's PropertyListSerialization emit the required `0x8N`
// UID type.

using AppleTVProtocol.AirPlay;

namespace AppleTVProtocol;

public static class RTITextOperations
{
    // MARK: - Encode

    /// <summary>
    /// Build a payload that inserts <paramref name="text"/> at the current
    /// cursor position. `$objects` layout (mirrors pyatv exactly):
    /// <code>
    ///   [0]  "$null"
    ///   [1]  RTITextOperations instance  { keyboardOutput:UID(2), targetSessionUUID:UID(5), $class:UID(7) }
    ///   [2]  TIKeyboardOutput instance   { insertionText:UID(3),  $class:UID(4) }
    ///   [3]  text string
    ///   [4]  TIKeyboardOutput class dict { $classname, $classes }
    ///   [5]  NSUUID instance dict        { NS.uuidbytes:&lt;Data&gt;, $class:UID(6) }
    ///   [6]  NSUUID class dict           { $classname, $classes }
    ///   [7]  RTITextOperations class dict { $classname, $classes }
    /// </code>
    /// </summary>
    public static byte[] InputPayload(byte[] sessionUuid, string text)
    {
        var w = new BinaryPlistWriter();

        // Shared string keys
        var skClass       = w.AddString("$class");
        var skClassname   = w.AddString("$classname");
        var skClasses     = w.AddString("$classes");
        var skKbOut       = w.AddString("keyboardOutput");
        var skTargetUuid  = w.AddString("targetSessionUUID");
        var skInsertText  = w.AddString("insertionText");
        var skNsUuidBytes = w.AddString("NS.uuidbytes");

        // $objects[3]: text
        var obj3Text = w.AddString(text);

        // $objects[4]: TIKeyboardOutput class dict
        var sTIKBName    = w.AddString("TIKeyboardOutput");
        var sNsObject    = w.AddString("NSObject");
        var aTIKBClasses = w.AddArray(new[] { sTIKBName, sNsObject });
        var obj4TIKBCls  = w.AddDict(new[] {
            (skClassname, sTIKBName),
            (skClasses,   aTIKBClasses),
        });

        // $objects[5]: NSUUID instance (references [6] via UID(6))
        var oUuidData      = w.AddData(sessionUuid);
        var sNsUuid        = w.AddString("NSUUID");
        var aNsUuidClasses = w.AddArray(new[] { sNsUuid, sNsObject });
        var obj6NsUuidCls  = w.AddDict(new[] {
            (skClassname, sNsUuid),
            (skClasses,   aNsUuidClasses),
        });
        var obj5NsUuidInst = w.AddDict(new[] {
            (skNsUuidBytes, oUuidData),
            (skClass,       w.AddUid(6)),
        });

        // $objects[7]: RTITextOperations class dict
        var sRtiName    = w.AddString("RTITextOperations");
        var aRtiClasses = w.AddArray(new[] { sRtiName, sNsObject });
        var obj7RtiCls  = w.AddDict(new[] {
            (skClassname, sRtiName),
            (skClasses,   aRtiClasses),
        });

        // $objects[2]: TIKeyboardOutput instance
        var obj2TIKBInst = w.AddDict(new[] {
            (skInsertText, w.AddUid(3)),
            (skClass,      w.AddUid(4)),
        });

        // $objects[1]: RTITextOperations instance
        var obj1RtiInst = w.AddDict(new[] {
            (skKbOut,      w.AddUid(2)),
            (skTargetUuid, w.AddUid(5)),
            (skClass,      w.AddUid(7)),
        });

        // $objects[0]: "$null"
        var obj0Null = w.AddNull();

        // Assemble $objects array — order matters (indices are hard-coded above)
        var objsArray = w.AddArray(new[] {
            obj0Null, obj1RtiInst, obj2TIKBInst, obj3Text,
            obj4TIKBCls, obj5NsUuidInst, obj6NsUuidCls, obj7RtiCls,
        });

        // Outer keyed-archiver wrapper
        var skVersion  = w.AddString("$version");
        var skArchiver = w.AddString("$archiver");
        var skTop      = w.AddString("$top");
        var skObjects  = w.AddString("$objects");
        var svArchiver = w.AddString("RTIKeyedArchiver");
        var skTextOps  = w.AddString("textOperations");
        var svVersion  = w.AddInt(100_000);

        var topDict  = w.AddDict(new[] { (skTextOps, w.AddUid(1)) });
        var rootDict = w.AddDict(new[] {
            (skVersion,  svVersion),
            (skArchiver, svArchiver),
            (skTop,      topDict),
            (skObjects,  objsArray),
        });

        return w.Build(rootDict);
    }

    /// <summary>
    /// Build a payload that replaces the field content with <paramref name="text"/>.
    /// Used for backspace (send truncated text) and clear (send empty text).
    /// Distinct object graph from <see cref="InputPayload"/>: uses
    /// <c>textToAssert</c> instead of <c>keyboardOutput.insertionText</c>.
    /// </summary>
    public static byte[] AssertPayload(byte[] sessionUuid, string text)
    {
        var w = new BinaryPlistWriter();

        var skClass       = w.AddString("$class");
        var skClassname   = w.AddString("$classname");
        var skClasses     = w.AddString("$classes");
        var skKbOut       = w.AddString("keyboardOutput");
        var skTargetUuid  = w.AddString("targetSessionUUID");
        var skTextAssert  = w.AddString("textToAssert");
        var skNsUuidBytes = w.AddString("NS.uuidbytes");

        var obj4Text = w.AddString(text);

        var sTIKBName    = w.AddString("TIKeyboardOutput");
        var sNsObject    = w.AddString("NSObject");
        var aTIKBClasses = w.AddArray(new[] { sTIKBName, sNsObject });
        var obj3TIKBCls  = w.AddDict(new[] {
            (skClassname, sTIKBName),
            (skClasses,   aTIKBClasses),
        });

        var oUuidData      = w.AddData(sessionUuid);
        var sNsUuid        = w.AddString("NSUUID");
        var aNsUuidClasses = w.AddArray(new[] { sNsUuid, sNsObject });
        var obj6NsUuidCls  = w.AddDict(new[] {
            (skClassname, sNsUuid),
            (skClasses,   aNsUuidClasses),
        });
        var obj5NsUuidInst = w.AddDict(new[] {
            (skNsUuidBytes, oUuidData),
            (skClass,       w.AddUid(6)),
        });

        var sRtiName    = w.AddString("RTITextOperations");
        var aRtiClasses = w.AddArray(new[] { sRtiName, sNsObject });
        var obj7RtiCls  = w.AddDict(new[] {
            (skClassname, sRtiName),
            (skClasses,   aRtiClasses),
        });

        var obj2TIKBInst = w.AddDict(new[] { (skClass, w.AddUid(3)) });

        var obj1RtiInst = w.AddDict(new[] {
            (skKbOut,      w.AddUid(2)),
            (skTargetUuid, w.AddUid(5)),
            (skTextAssert, w.AddUid(4)),
            (skClass,      w.AddUid(7)),
        });

        var obj0Null = w.AddNull();

        var objsArray = w.AddArray(new[] {
            obj0Null, obj1RtiInst, obj2TIKBInst, obj3TIKBCls, obj4Text,
            obj5NsUuidInst, obj6NsUuidCls, obj7RtiCls,
        });

        var skVersion  = w.AddString("$version");
        var skArchiver = w.AddString("$archiver");
        var skTop      = w.AddString("$top");
        var skObjects  = w.AddString("$objects");
        var svArchiver = w.AddString("RTIKeyedArchiver");
        var skTextOps  = w.AddString("textOperations");
        var svVersion  = w.AddInt(100_000);

        var topDict  = w.AddDict(new[] { (skTextOps, w.AddUid(1)) });
        var rootDict = w.AddDict(new[] {
            (skVersion,  svVersion),
            (skArchiver, svArchiver),
            (skTop,      topDict),
            (skObjects,  objsArray),
        });

        return w.Build(rootDict);
    }

    /// <summary>Convenience: same as AssertPayload with empty text.</summary>
    public static byte[] ClearPayload(byte[] sessionUuid) => AssertPayload(sessionUuid, "");

    // MARK: - Decode

    /// <summary>
    /// Pull the 16-byte NSUUID out of the `_tiD` plist returned by _tiStart.
    /// Walks `$top.sessionUUID` → UID → `$objects[uid]` → `NS.uuidbytes`,
    /// following the object graph rather than assuming fixed indices.
    /// Returns null if the plist doesn't match the expected shape.
    /// </summary>
    public static byte[]? ExtractSessionUuid(byte[] tiD)
    {
        var plist = BinaryPlistReader.Parse(tiD) as Dictionary<string, object?>;
        if (plist is null) return null;
        if (plist["$objects"] is not object?[] objects) return null;
        if (plist["$top"] is not Dictionary<string, object?> top) return null;
        if (top.GetValueOrDefault("sessionUUID") is not PlistUid uid) return null;
        if (uid.Index >= objects.Length) return null;

        var target = objects[uid.Index];
        // Newer tvOS: session UUID is stored as a raw 16-byte Data at the UID index.
        if (target is byte[] raw && raw.Length == 16) return raw;
        // Older tvOS: wrapped in an NSUUID dict with NS.uuidbytes.
        if (target is Dictionary<string, object?> dict &&
            dict.GetValueOrDefault("NS.uuidbytes") is byte[] bytes)
        {
            return bytes;
        }
        return null;
    }

    /// <summary>
    /// Pull the current field text from the `_tiD` plist.
    /// Path: `$top.documentState` → `docSt` → `contextBeforeInput`.
    /// </summary>
    public static string? ExtractCurrentText(byte[] tiD)
    {
        var plist = BinaryPlistReader.Parse(tiD) as Dictionary<string, object?>;
        if (plist is null) return null;
        if (plist["$objects"] is not object?[] objects) return null;
        if (plist["$top"] is not Dictionary<string, object?> top) return null;

        object? Resolve(object? element) =>
            element is PlistUid u && u.Index < objects.Length ? objects[u.Index] : element;

        if (top.GetValueOrDefault("documentState") is not { } docStateRef) return null;
        if (Resolve(docStateRef) is not Dictionary<string, object?> docStateDict) return null;
        if (docStateDict.GetValueOrDefault("docSt") is not { } docStRef) return null;
        if (Resolve(docStRef) is not Dictionary<string, object?> docStDict) return null;
        if (docStDict.GetValueOrDefault("contextBeforeInput") is not { } contextRef) return null;
        return Resolve(contextRef) as string;
    }
}
