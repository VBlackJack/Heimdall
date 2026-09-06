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

using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// B-07: the plink probe read the key type with a pattern that only accepted names
/// starting with <c>ssh-</c>, so an ECDSA host key was presented and stored as
/// <c>ssh-unknown</c>. The samples below follow the shape plink prints with
/// <c>-v -batch</c> against a host whose key is not cached.
/// </summary>
public sealed class PlinkHostKeyProbeTests
{
    private const string Ed25519Fingerprint = "SHA256:Yk7f2Y3m8pQeQ0aYyGdJ0v3rQm1aN5oW9zK4Lx8cT2E";
    private const string RsaFingerprint = "SHA256:Q3q1z1u8Hh0FZ8rV5b6x7c9d0e1f2g3h4i5j6k7l8m9";
    private const string EcdsaFingerprint = "SHA256:p9Xa2bC3dE4fG5hI6jK7lM8nO0pQ1rS2tU3vW4xY5zA";
    private const string SecurityKeyFingerprint = "SHA256:aB1cD2eF3gH4iJ5kL6mN7oP8qR9sT0uV1wX2yZ3aB4c";

    [Theory]
    [InlineData("ssh-ed25519", "255", Ed25519Fingerprint)]
    [InlineData("ssh-rsa", "3072", RsaFingerprint)]
    [InlineData("ecdsa-sha2-nistp256", "256", EcdsaFingerprint)]
    [InlineData("ecdsa-sha2-nistp384", "384", EcdsaFingerprint)]
    [InlineData("sk-ssh-ed25519@openssh.com", "255", SecurityKeyFingerprint)]
    public void TryParsePresentation_PlinkUncachedHostKeyStderr_ExtractsAlgorithmAndFingerprint(
        string algorithm,
        string bits,
        string fingerprint)
    {
        string stderr = BuildUncachedHostKeyStderr(algorithm, bits, fingerprint);

        PlinkHostKeyPresentation? presentation = PlinkHostKeyProbe.TryParsePresentation(stderr);

        Assert.NotNull(presentation);
        Assert.Equal(algorithm, presentation.Algorithm);
        Assert.Equal(fingerprint, presentation.Fingerprint);
    }

    [Fact]
    public void TryParsePresentation_FingerprintWithoutKeyTypeLine_FallsBackToUnknownAlgorithm()
    {
        string stderr = $"Host key fingerprint is:\r\n{RsaFingerprint}\r\nConnection abandoned.\r\n";

        PlinkHostKeyPresentation? presentation = PlinkHostKeyProbe.TryParsePresentation(stderr);

        Assert.NotNull(presentation);
        Assert.Equal(PlinkHostKeyProbe.UnknownAlgorithm, presentation.Algorithm);
        Assert.Equal(RsaFingerprint, presentation.Fingerprint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Looking up host \"203.0.113.10\" for SSH connection\r\nConnecting to 203.0.113.10 port 22\r\nFATAL ERROR: Network error: Connection refused\r\n")]
    public void TryParsePresentation_NoFingerprint_ReturnsNull(string? stderr)
    {
        Assert.Null(PlinkHostKeyProbe.TryParsePresentation(stderr));
    }

    private static string BuildUncachedHostKeyStderr(string algorithm, string bits, string fingerprint)
    {
        return string.Join(
            "\r\n",
            "Looking up host \"203.0.113.10\" for SSH connection",
            "Connecting to 203.0.113.10 port 22",
            "We claim version: SSH-2.0-PuTTY_Release_0.83",
            "Remote version: SSH-2.0-OpenSSH_9.6",
            "Using SSH protocol version 2",
            $"Host key fingerprint is:",
            $"{algorithm} {bits} {fingerprint}",
            "The host key is not cached for this server:",
            "  203.0.113.10 (port 22)",
            "You have no guarantee that the server is the computer",
            "you think it is.",
            $"The server's {algorithm} key fingerprint is:",
            $"{algorithm} {bits} {fingerprint}",
            "Connection abandoned.",
            string.Empty);
    }
}
