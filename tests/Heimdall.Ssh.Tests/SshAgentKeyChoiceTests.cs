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

using System.Security.Cryptography;
using Heimdall.Core.Ssh;
using Heimdall.Ssh.Agents;
using Heimdall.Ssh.OpenSsh;
using Heimdall.Ssh.Pageant;
using Renci.SshNet;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// A session that names one agent key offers that key alone, and is refused when it is not loaded.
/// </summary>
public sealed class SshAgentKeyChoiceTests
{
    private const string KeyType = "ssh-ed25519";
    private const string LocalizedTemplate = "Key {0} is missing";

    private static readonly byte[] BlobA = [1, 1, 1, 1];
    private static readonly byte[] BlobB = [2, 2, 2, 2];
    private static readonly byte[] BlobC = [3, 3, 3, 3];

    private static string FingerprintOf(byte[] blob) => SshAgentKeyFingerprints.Compute(blob);

    private static SshConnectionParams Params(string? fingerprint, string? password = null) => new()
    {
        Host = "example.com",
        Username = "user",
        Password = password,
        AgentKeyFingerprint = fingerprint,
    };

    private static SshAgentRegistry Registry(params FakeAgent[] agents) => new(agents);

    // The blobs of every agent key the authentication methods would offer, in offer order.
    private static List<byte[]> OfferedBlobs(ConnectionInfo info) =>
        info.AuthenticationMethods
            .OfType<PrivateKeyAuthenticationMethod>()
            .SelectMany(method => method.KeyFiles)
            .SelectMany(source => source.HostKeyAlgorithms)
            .Select(algorithm => algorithm.Data)
            .ToList();

    [Fact]
    public void ChosenKey_OffersOnlyThatKey()
    {
        var registry = Registry(new FakeAgent(
            PageantAgent.AgentName, [new FakeKey("a", BlobA), new FakeKey("b", BlobB), new FakeKey("c", BlobC)]));

        ConnectionInfo info = SshConnectionFactory.Create(Params(FingerprintOf(BlobB)), registry);

        List<byte[]> offered = OfferedBlobs(info);
        Assert.NotEmpty(offered);
        Assert.All(offered, blob => Assert.Equal(BlobB, blob));
    }

    [Fact]
    public void ChosenKey_FoundInSecondAgent()
    {
        var registry = Registry(
            new FakeAgent(OpenSshPipeAgent.AgentName, [new FakeKey("a", BlobA)]),
            new FakeAgent(PageantAgent.AgentName, [new FakeKey("b", BlobB)]));

        ConnectionInfo info = SshConnectionFactory.Create(Params(FingerprintOf(BlobB)), registry);

        Assert.All(OfferedBlobs(info), blob => Assert.Equal(BlobB, blob));
    }

    [Fact]
    public void ChosenKey_NotLoaded_Throws_NoFallback()
    {
        var registry = Registry(new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)]));
        string missing = FingerprintOf(BlobC);

        var thrown = Assert.Throws<SshAgentKeyNotLoadedException>(
            () => SshConnectionFactory.Create(Params(missing), registry));
        Assert.Equal(missing, thrown.Fingerprint);
        Assert.Contains(missing, thrown.Message, StringComparison.Ordinal);

        // Every public-facing builder goes through the same check.
        Assert.Throws<SshAgentKeyNotLoadedException>(
            () => SshConnectionFactory.CreateSshClient(Params(missing), registry));
        Assert.Throws<SshAgentKeyNotLoadedException>(
            () => SshConnectionFactory.CreateSftpClient(Params(missing), registry));
    }

    [Fact]
    public void ChosenKey_NoAgentReachable_Throws()
    {
        var registry = Registry(new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)], available: false));

        Assert.Throws<SshAgentKeyNotLoadedException>(
            () => SshConnectionFactory.Create(Params(FingerprintOf(BlobA)), registry));
    }

    [Theory]
    [InlineData("not-a-fingerprint")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sha256:uNiVztksCsDhcc0u9e8BujQXVUpKZIDTMczCvj3tD2s")]
    public void ChosenKey_Malformed_Throws(string fingerprint)
    {
        var registry = Registry(new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)]));

        var thrown = Assert.Throws<SshAgentKeyNotLoadedException>(
            () => SshConnectionFactory.Create(Params(fingerprint), registry));
        Assert.Equal(fingerprint, thrown.Fingerprint);
    }

    [Fact]
    public void ChosenKey_NotLoaded_ThrowsEvenWithPasswordConfigured()
    {
        var registry = Registry(new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)]));

        Assert.Throws<SshAgentKeyNotLoadedException>(
            () => SshConnectionFactory.Create(Params(FingerprintOf(BlobC), password: "secret"), registry));
    }

    [Fact]
    public void NoChoice_UnchangedBehaviour()
    {
        var registry = Registry(new FakeAgent(
            PageantAgent.AgentName, [new FakeKey("a", BlobA), new FakeKey("b", BlobB)]));

        ConnectionInfo info = SshConnectionFactory.Create(Params(fingerprint: null), registry);

        List<byte[]> offered = OfferedBlobs(info);
        Assert.Contains(offered, blob => blob.SequenceEqual(BlobA));
        Assert.Contains(offered, blob => blob.SequenceEqual(BlobB));
    }

    [Fact]
    public void FindIdentity_ByFingerprint()
    {
        var pageant = new FakeAgent(PageantAgent.AgentName, [new FakeKey("b", BlobB)]);
        var registry = Registry(new FakeAgent(OpenSshPipeAgent.AgentName, [new FakeKey("a", BlobA)]), pageant);

        SshAgentIdentityMatch? match = registry.FindIdentity(FingerprintOf(BlobB));

        Assert.NotNull(match);
        Assert.Equal(PageantAgent.AgentName, match.AgentName);
        Assert.Equal(BlobB, match.Key.PublicKeyBlob);
        Assert.Null(registry.FindIdentity(FingerprintOf(BlobC)));
    }

    [Fact]
    public void FindIdentity_SkipsAnAgentWhoseIdentityRequestFails()
    {
        var registry = Registry(
            new FakeAgent(OpenSshPipeAgent.AgentName, [], failsToList: true),
            new FakeAgent(PageantAgent.AgentName, [new FakeKey("a", BlobA)]));

        Assert.NotNull(registry.FindIdentity(FingerprintOf(BlobA)));
    }

    [Fact]
    public void Classify_AgentKeyNotLoaded()
    {
        var ex = new SshAgentKeyNotLoadedException(FingerprintOf(BlobA));

        SshFailureInfo info = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.AgentKeyNotLoaded, info.Code);
        Assert.True(info.IsFatal);
        Assert.Same(ex, info.OriginalException);
    }

    [Fact]
    public void FormatMessage_AgentKeyNotLoaded_CarriesTheFullFingerprint()
    {
        string fingerprint = FingerprintOf(BlobA);
        SshFailureInfo info = FailureClassifier.Classify(new SshAgentKeyNotLoadedException(fingerprint));
        string? requestedKey = null;

        string message = FailureClassifier.FormatMessage(info, key =>
        {
            requestedKey = key;
            return LocalizedTemplate;
        });

        Assert.Equal("ErrorSshAgentKeyNotLoaded", requestedKey);
        Assert.Equal($"Key {fingerprint} is missing", message);
    }

    [Fact]
    public void FormatMessage_AgentKeyNotLoaded_WithoutLocalizedText_FallsBackToTheMessageWithFingerprint()
    {
        string fingerprint = FingerprintOf(BlobA);
        SshFailureInfo info = FailureClassifier.Classify(new SshAgentKeyNotLoadedException(fingerprint));

        string message = FailureClassifier.FormatMessage(info, _ => null);

        Assert.Contains(fingerprint, message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindIdentity_RestrictedToAnAgent_IgnoresTheOtherOneHoldingTheSameKey()
    {
        var registry = Registry(
            new FakeAgent(OpenSshPipeAgent.AgentName, [new FakeKey("a", BlobA)]),
            new FakeAgent(PageantAgent.AgentName, [new FakeKey("a-in-pageant", BlobA)]));

        // The unrestricted search answers with the first agent in preference order.
        Assert.Equal(OpenSshPipeAgent.AgentName, registry.FindIdentity(FingerprintOf(BlobA))?.AgentName);

        SshAgentIdentityMatch? inPageant = registry.FindIdentity(FingerprintOf(BlobA), PageantAgent.AgentName);
        Assert.Equal(PageantAgent.AgentName, inPageant?.AgentName);
        Assert.Equal("a-in-pageant", inPageant?.Key.Comment);
        Assert.Null(registry.FindIdentity(FingerprintOf(BlobB), PageantAgent.AgentName));
        Assert.Null(registry.FindIdentity(FingerprintOf(BlobA), "No such agent"));
    }

    [Fact]
    public void ChosenKeyLoaded_WithPasswordAndKeyFile_EmbeddedOffersKeyFilePasswordAndOnlyTheChosenAgentKey()
    {
        using var rsa = RSA.Create(2048);
        string keyPath = Path.Combine(Path.GetTempPath(), $"heimdall_test_key_{Guid.NewGuid():N}.pem");
        File.WriteAllText(keyPath, new string(PemEncoding.Write("RSA PRIVATE KEY", rsa.ExportRSAPrivateKey())));
        try
        {
            var registry = Registry(new FakeAgent(
                PageantAgent.AgentName, [new FakeKey("a", BlobA), new FakeKey("b", BlobB), new FakeKey("c", BlobC)]));
            SshConnectionParams connectionParams = new()
            {
                Host = "example.com",
                Username = "user",
                Password = "secret",
                KeyPath = keyPath,
                AgentKeyFingerprint = FingerprintOf(BlobB),
            };

            ConnectionInfo info = SshConnectionFactory.Create(connectionParams, registry);

            // The chosen key restricts the agent keys and nothing else: the key file, the password
            // and keyboard-interactive are offered as they are without a chosen key.
            Assert.Equal(2, info.AuthenticationMethods.OfType<PrivateKeyAuthenticationMethod>().Count());
            Assert.Single(info.AuthenticationMethods.OfType<PasswordAuthenticationMethod>());
            Assert.Single(info.AuthenticationMethods.OfType<KeyboardInteractiveAuthenticationMethod>());

            List<byte[]> offered = OfferedBlobs(info);
            Assert.Contains(offered, blob => blob.SequenceEqual(BlobB));
            Assert.DoesNotContain(offered, blob => blob.SequenceEqual(BlobA));
            Assert.DoesNotContain(offered, blob => blob.SequenceEqual(BlobC));
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public void FormatMessage_AgentKeyNotLoaded_WithBracesInTheStoredValue_NeverThrows()
    {
        const string HandEdited = "SHA256:{0}{";
        SshFailureInfo info = FailureClassifier.Classify(new SshAgentKeyNotLoadedException(HandEdited));

        string message = FailureClassifier.FormatMessage(info, _ => LocalizedTemplate);

        Assert.Equal($"Key {HandEdited} is missing", message);
    }

    private sealed class FakeAgent(
        string name,
        IReadOnlyList<ISshAgentKey> identities,
        bool available = true,
        bool failsToList = false) : ISshAgent
    {
        public string Name { get; } = name;

        public bool IsAvailable() => available;

        public IReadOnlyList<ISshAgentKey> GetIdentities() =>
            failsToList ? throw new IOException("agent unreachable") : identities;
    }

    private sealed class FakeKey(string comment, byte[] publicKeyBlob) : ISshAgentKey
    {
        public string Comment => comment;
        public string KeyType => SshAgentKeyChoiceTests.KeyType;
        public byte[] PublicKeyBlob => publicKeyBlob;
        public byte[] Sign(byte[] data, SshAgentSignFlags flags) => [1];
    }
}
