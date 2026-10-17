/*
 * Copyright 2026 Julien Bombled
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.IO;
using System.Text.Json;
using Heimdall.App.Services.SharedSessions;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// The store of the passwords a user chose to remember for shared sessions: what it keeps, under
/// which key, and what it forgets.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class SharedSessionSecretStoreTests : IDisposable
{
    private readonly CredentialProtectorStateScope _scope = new();

    private const string SourceA = "sourcea1";
    private const string SourceB = "sourceb1";
    private const string PlainPassword = "plain-text-not-on-disk-7461";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "heimdall-shared-secrets",
        Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_directory, SharedSessionSecretStore.FileName);

    public void Dispose()
    {
        _scope.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Remember_IsFoundAgainByAStoreOpenedLaterOnTheSameFile()
    {
        string key = SharedSessionSecretStore.GatewayKey("gw1.example.test", 22, "jdoe");
        await new SharedSessionSecretStore(StorePath).RememberAsync(SharedSecretKind.Gateway, SourceA, key, "sealed-1");

        var reopened = new SharedSessionSecretStore(StorePath);

        Assert.Equal("sealed-1", reopened.Find(SharedSecretKind.Gateway, SourceA, key));
        Assert.Null(reopened.Find(SharedSecretKind.Session, SourceA, key));
        Assert.Null(reopened.Find(SharedSecretKind.Gateway, SourceB, key));
        Assert.Equal(1, reopened.CountFor(SourceA));
    }

    [Fact]
    public async Task TheDocument_HoldsTheSealedFormOnly_NeverThePassword()
    {
        string sealedSecret = CredentialProtector.Protect(PlainPassword);
        var store = new SharedSessionSecretStore(StorePath);

        await store.RememberAsync(SharedSecretKind.Session, SourceA, "key", sealedSecret);

        string onDisk = await File.ReadAllTextAsync(StorePath);
        Assert.DoesNotContain(PlainPassword, onDisk, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(onDisk);
        JsonElement entry = Assert.Single(document.RootElement.GetProperty("Entries").EnumerateArray());
        Assert.Equal(sealedSecret, entry.GetProperty("Secret").GetString());
        Assert.Equal(PlainPassword, CredentialProtector.Unprotect(store.Find(SharedSecretKind.Session, SourceA, "key")));
    }

    [Fact]
    public async Task Remember_SameKeyAgain_ReplacesTheSecret()
    {
        var store = new SharedSessionSecretStore(StorePath);

        await store.RememberAsync(SharedSecretKind.Session, SourceA, "key", "old");
        await store.RememberAsync(SharedSecretKind.Session, SourceA, "key", "new");

        Assert.Equal("new", store.Find(SharedSecretKind.Session, SourceA, "key"));
        Assert.Equal(1, store.CountFor(SourceA));
    }

    [Fact]
    public async Task ForgetSource_ForgetsThatSourceOnly()
    {
        var store = new SharedSessionSecretStore(StorePath);
        await store.RememberAsync(SharedSecretKind.Gateway, SourceA, "g", "s1");
        await store.RememberAsync(SharedSecretKind.Session, SourceA, "s", "s2");
        await store.RememberAsync(SharedSecretKind.Session, SourceB, "s", "s3");

        await store.ForgetSourceAsync(SourceA);

        Assert.Equal(0, store.CountFor(SourceA));
        Assert.Equal(1, store.CountFor(SourceB));
        Assert.Equal(0, new SharedSessionSecretStore(StorePath).CountFor(SourceA));
    }

    [Fact]
    public async Task ForgetAllExcept_KeepsOnlyTheNamedSources()
    {
        var store = new SharedSessionSecretStore(StorePath);
        await store.RememberAsync(SharedSecretKind.Gateway, SourceA, "g", "s1");
        await store.RememberAsync(SharedSecretKind.Session, SourceB, "s", "s2");

        await store.ForgetAllExceptAsync(new HashSet<string>(StringComparer.Ordinal) { SourceB });

        Assert.Equal(0, store.CountFor(SourceA));
        Assert.Equal(1, store.CountFor(SourceB));
    }

    [Fact]
    public async Task Forget_OneKey_LeavesTheOthers()
    {
        var store = new SharedSessionSecretStore(StorePath);
        await store.RememberAsync(SharedSecretKind.Gateway, SourceA, "g", "s1");
        await store.RememberAsync(SharedSecretKind.Session, SourceA, "s", "s2");

        await store.ForgetAsync(SharedSecretKind.Session, SourceA, "s");

        Assert.Null(store.Find(SharedSecretKind.Session, SourceA, "s"));
        Assert.Equal("s1", store.Find(SharedSecretKind.Gateway, SourceA, "g"));
    }

    [Fact]
    public async Task UnreadableDocument_ReadsAsEmpty_AndTheNextRememberReplacesIt()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(StorePath, "{ not json");
        var store = new SharedSessionSecretStore(StorePath);

        Assert.Equal(0, store.CountFor(SourceA));
        await store.RememberAsync(SharedSecretKind.Session, SourceA, "s", "s1");

        Assert.Equal("s1", new SharedSessionSecretStore(StorePath).Find(SharedSecretKind.Session, SourceA, "s"));
    }

    [Fact]
    public void GatewayKey_IgnoresTheCaseOfTheHost_AndChangesWithHostPortOrUser()
    {
        string key = SharedSessionSecretStore.GatewayKey("gw1.example.test", 22, "jdoe");

        Assert.Equal(key, SharedSessionSecretStore.GatewayKey(" GW1.Example.Test ", 22, "jdoe"));
        Assert.NotEqual(key, SharedSessionSecretStore.GatewayKey("gw2.example.test", 22, "jdoe"));
        Assert.NotEqual(key, SharedSessionSecretStore.GatewayKey("gw1.example.test", 2222, "jdoe"));
        Assert.NotEqual(key, SharedSessionSecretStore.GatewayKey("gw1.example.test", 22, "asmith"));
    }

    [Fact]
    public void SessionKey_ChangesWithProfileHostPortOrUser()
    {
        string key = SharedSessionSecretStore.SessionKey("shared-a-1", "srv1.example.test", 22, "jdoe");

        Assert.Equal(key, SharedSessionSecretStore.SessionKey("shared-a-1", "SRV1.example.test", 22, "jdoe"));
        Assert.NotEqual(key, SharedSessionSecretStore.SessionKey("shared-a-2", "srv1.example.test", 22, "jdoe"));
        Assert.NotEqual(key, SharedSessionSecretStore.SessionKey("shared-a-1", "srv2.example.test", 22, "jdoe"));
        Assert.NotEqual(key, SharedSessionSecretStore.SessionKey("shared-a-1", "srv1.example.test", 2222, "jdoe"));
        Assert.NotEqual(key, SharedSessionSecretStore.SessionKey("shared-a-1", "srv1.example.test", 22, "asmith"));
    }
}
