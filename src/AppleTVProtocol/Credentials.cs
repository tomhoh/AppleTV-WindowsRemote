// SPDX-License-Identifier: MIT
//
// Persisted HAP credentials for a paired Apple TV.
//
// pyatv stores these in ~/.pyatv/{deviceid}.conf as a colon-delimited
// string ("ltpk:ltsk:atv_id:client_id" in hex). We use JSON instead — it
// reads better, is forward-compatible, and trivially trivial to unit-test
// without touching the filesystem.
//
// File layout (one paired device per file):
//
//   %APPDATA%\AppleTV-WindowsRemote\credentials\{device-id}.json
//
//   {
//     "deviceId":  "AA:BB:CC:DD:EE:FF",
//     "displayName": "Living Room",
//     "atvPublicKey":   "<hex>",     // 32 bytes — the TV's long-term Ed25519 public key
//     "clientPrivateKey":"<hex>",    // 32 bytes — our long-term Ed25519 private seed
//     "atvIdentifier":  "<hex>",     // bytes — the TV's pairing identifier (HAP TLV8 Identifier)
//     "clientIdentifier":"<hex>",    // bytes — our pairing identifier (UUID)
//     "pairedAtUtc": "2026-05-30T18:42:00Z"
//   }

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppleTVProtocol;

/// <summary>
/// The HAP long-term credentials produced by a successful pair-setup. Pass
/// this back into pair-verify on every reconnect.
/// </summary>
public sealed record HapCredentials(
    byte[] AtvPublicKey,
    byte[] ClientPrivateKey,
    byte[] AtvIdentifier,
    byte[] ClientIdentifier);

/// <summary>
/// A <see cref="HapCredentials"/> bundled with the metadata we need to
/// reconnect later (device id, friendly name, and the host + port we
/// paired against — so the UI can reconnect without re-prompting).
///
/// <c>Host</c> and <c>Port</c> default to the empty-string / 0 so
/// credential files written by earlier versions keep loading; callers
/// that need them should fall back to a re-discovery in that case.
/// </summary>
public sealed record StoredCredentials(
    string DeviceId,
    string DisplayName,
    HapCredentials Credentials,
    DateTime PairedAtUtc,
    string Host = "",
    int Port = 0);

/// <summary>
/// JSON-backed credential store. Defaults to
/// <c>%APPDATA%\AppleTV-WindowsRemote\credentials\</c> on Windows,
/// <c>~/.config/AppleTV-WindowsRemote/credentials/</c> elsewhere.
/// </summary>
public sealed class CredentialStore
{
    private readonly string _root;

    public string Root => _root;

    public CredentialStore() : this(DefaultStorageDirectory()) { }

    public CredentialStore(string root) { _root = root; }

    public static string DefaultStorageDirectory()
    {
        // On Windows, ApplicationData == %APPDATA%; on Linux/macOS it's ~/.config
        // (XDG_CONFIG_HOME if set). Both are the right place per platform.
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(basePath))
            basePath = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? ".", ".config");
        return Path.Combine(basePath, "AppleTV-WindowsRemote", "credentials");
    }

    /// <summary>Save credentials for a device. Overwrites if present.</summary>
    public void Save(StoredCredentials creds)
    {
        Directory.CreateDirectory(_root);
        var file = PathFor(creds.DeviceId);
        File.WriteAllBytes(file, Serialize(creds));
    }

    /// <summary>Load credentials for a device, or <c>null</c> if none stored.</summary>
    public StoredCredentials? Load(string deviceId)
    {
        var file = PathFor(deviceId);
        if (!File.Exists(file)) return null;
        return Deserialize(File.ReadAllBytes(file));
    }

    public bool Contains(string deviceId) => File.Exists(PathFor(deviceId));

    public void Delete(string deviceId)
    {
        var file = PathFor(deviceId);
        if (File.Exists(file)) File.Delete(file);
    }

    public IReadOnlyList<StoredCredentials> LoadAll()
    {
        if (!Directory.Exists(_root)) return Array.Empty<StoredCredentials>();
        var result = new List<StoredCredentials>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
        {
            try { result.Add(Deserialize(File.ReadAllBytes(path))); }
            catch { /* skip malformed/foreign files silently */ }
        }
        return result;
    }

    private string PathFor(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId must not be empty", nameof(deviceId));
        // Sanitize: replace path-illegal characters so callers can pass raw
        // values like "AA:BB:CC:DD:EE:FF" (the colons are illegal on Windows).
        var sanitized = string.Join('_', deviceId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_root, sanitized + ".json");
    }

    // ── (de)serialisation — exposed internal for tests ──

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static byte[] Serialize(StoredCredentials creds)
    {
        var dto = new CredentialDto(
            creds.DeviceId,
            creds.DisplayName,
            Convert.ToHexString(creds.Credentials.AtvPublicKey).ToLowerInvariant(),
            Convert.ToHexString(creds.Credentials.ClientPrivateKey).ToLowerInvariant(),
            Convert.ToHexString(creds.Credentials.AtvIdentifier).ToLowerInvariant(),
            Convert.ToHexString(creds.Credentials.ClientIdentifier).ToLowerInvariant(),
            creds.PairedAtUtc.ToString("O"),
            creds.Host,
            creds.Port);
        return JsonSerializer.SerializeToUtf8Bytes(dto, _jsonOpts);
    }

    internal static StoredCredentials Deserialize(byte[] json)
    {
        var dto = JsonSerializer.Deserialize<CredentialDto>(json, _jsonOpts)
                  ?? throw new InvalidDataException("empty or null credential json");
        return new StoredCredentials(
            dto.DeviceId,
            dto.DisplayName,
            new HapCredentials(
                Convert.FromHexString(dto.AtvPublicKey),
                Convert.FromHexString(dto.ClientPrivateKey),
                Convert.FromHexString(dto.AtvIdentifier),
                Convert.FromHexString(dto.ClientIdentifier)),
            DateTime.Parse(dto.PairedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind),
            dto.Host ?? string.Empty,
            dto.Port);
    }

    internal sealed record CredentialDto(
        string DeviceId,
        string DisplayName,
        string AtvPublicKey,
        string ClientPrivateKey,
        string AtvIdentifier,
        string ClientIdentifier,
        string PairedAtUtc,
        string? Host = null,
        int    Port  = 0);
}
