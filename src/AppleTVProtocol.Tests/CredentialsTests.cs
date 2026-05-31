// SPDX-License-Identifier: MIT
//
// Round-trip tests for the JSON-on-disk credential store.

using System.Text;
using System.Text.Json;
using AppleTVProtocol;

namespace AppleTVProtocol.Tests;

public class CredentialsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CredentialStore _store;

    public CredentialsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atvwr-tests-" + Guid.NewGuid().ToString("N"));
        _store = new CredentialStore(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static StoredCredentials Sample(string deviceId = "AA:BB:CC:DD:EE:FF") => new(
        DeviceId:    deviceId,
        DisplayName: "Living Room",
        Credentials: new HapCredentials(
            AtvPublicKey:    new byte[32].Select((_, i) => (byte)(0x10 + i)).ToArray(),
            ClientPrivateKey:new byte[32].Select((_, i) => (byte)(0x40 + i)).ToArray(),
            AtvIdentifier:   new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 },
            ClientIdentifier:new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
        PairedAtUtc: new DateTime(2026, 5, 30, 18, 42, 0, DateTimeKind.Utc));

    [Fact]
    public void SaveLoad_RoundTripsAllFields()
    {
        var src = Sample();
        _store.Save(src);

        Assert.True(_store.Contains(src.DeviceId));
        var roundTripped = _store.Load(src.DeviceId);

        Assert.NotNull(roundTripped);
        Assert.Equal(src.DeviceId,    roundTripped!.DeviceId);
        Assert.Equal(src.DisplayName, roundTripped.DisplayName);
        Assert.Equal(src.PairedAtUtc, roundTripped.PairedAtUtc);
        Assert.Equal(src.Credentials.AtvPublicKey,     roundTripped.Credentials.AtvPublicKey);
        Assert.Equal(src.Credentials.ClientPrivateKey, roundTripped.Credentials.ClientPrivateKey);
        Assert.Equal(src.Credentials.AtvIdentifier,    roundTripped.Credentials.AtvIdentifier);
        Assert.Equal(src.Credentials.ClientIdentifier, roundTripped.Credentials.ClientIdentifier);
    }

    [Fact]
    public void Load_ReturnsNullWhenAbsent()
    {
        Assert.Null(_store.Load("nope"));
        Assert.False(_store.Contains("nope"));
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        _store.Save(Sample("delete-me"));
        Assert.True(_store.Contains("delete-me"));
        _store.Delete("delete-me");
        Assert.False(_store.Contains("delete-me"));
    }

    [Fact]
    public void Delete_IsIdempotent()
    {
        _store.Delete("never-existed");   // should not throw
    }

    [Fact]
    public void LoadAll_ReturnsEveryStoredEntryAndSkipsForeignFiles()
    {
        _store.Save(Sample("device-a"));
        _store.Save(Sample("device-b"));
        // Plant a non-credential json file that should be skipped silently
        File.WriteAllText(Path.Combine(_tempDir, "garbage.json"), "{ not credentials }");

        var all = _store.LoadAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, c => c.DeviceId == "device-a");
        Assert.Contains(all, c => c.DeviceId == "device-b");
    }

    [Fact]
    public void LoadAll_OnMissingDirectoryReturnsEmpty()
    {
        var freshStore = new CredentialStore(Path.Combine(_tempDir, "does-not-exist"));
        Assert.Empty(freshStore.LoadAll());
    }

    [Fact]
    public void Save_SanitizesPathIllegalCharactersInDeviceId()
    {
        // Colons are illegal on Windows; the canonical Apple deviceid has them.
        var src = Sample("AA:BB:CC:DD:EE:FF");
        _store.Save(src);
        Assert.True(_store.Contains("AA:BB:CC:DD:EE:FF"));
        Assert.NotNull(_store.Load("AA:BB:CC:DD:EE:FF"));
    }

    [Fact]
    public void PathFor_RejectsEmptyDeviceId()
    {
        Assert.Throws<ArgumentException>(() => _store.Save(Sample("")));
    }

    [Fact]
    public void Serialize_ProducesReadableCamelCaseJson()
    {
        var bytes = CredentialStore.Serialize(Sample());
        var text  = Encoding.UTF8.GetString(bytes);
        // Spot-check the camelCase key names — file format will be human-edited
        // for debugging now and then, so the shape matters.
        Assert.Contains("\"deviceId\"",          text);
        Assert.Contains("\"displayName\"",       text);
        Assert.Contains("\"atvPublicKey\"",      text);
        Assert.Contains("\"clientPrivateKey\"",  text);
        Assert.Contains("\"atvIdentifier\"",     text);
        Assert.Contains("\"clientIdentifier\"",  text);
        Assert.Contains("\"pairedAtUtc\"",       text);
    }

    [Fact]
    public void Deserialize_RejectsEmpty()
    {
        Assert.Throws<JsonException>(() => CredentialStore.Deserialize(Encoding.UTF8.GetBytes("")));
    }
}
