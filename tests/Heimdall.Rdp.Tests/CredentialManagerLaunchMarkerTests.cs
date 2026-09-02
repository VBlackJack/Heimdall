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

using Heimdall.Rdp;

namespace Heimdall.Rdp.Tests;

/// <summary>
/// Pins the ownership marker's live-launch semantics: a Heimdall entry that a launch still
/// in flight depends on must not be reclaimed by the next launch to the same host.
/// </summary>
public sealed class CredentialManagerLaunchMarkerTests
{
    private const int OwningProcessId = 4242;

    private static readonly DateTime WriteInstant =
        new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void WriteDomainCredential_EntryOwnedByALiveLaunch_KeepsTheFirstLaunchsCredential()
    {
        string firstMarker = CredentialManagerHelper.CreateDomainCredentialOwnershipMarker(
            OwningProcessId,
            WriteInstant);
        FakeCredentialStore store = new FakeCredentialStore(firstMarker, "admin", "pw-a");

        bool result = CredentialManagerHelper.WriteDomainCredential(
            "TERMSRV/srv01",
            "svc",
            "pw-b",
            CredentialManagerHelper.CreateDomainCredentialOwnershipMarker(
                OwningProcessId,
                WriteInstant.AddSeconds(3)),
            store.Probe,
            store.Write,
            OwningProcessId,
            WriteInstant.AddSeconds(3),
            out bool credentialWritten,
            out string? error);

        Assert.True(result);
        Assert.False(credentialWritten);
        Assert.Null(error);
        Assert.Equal(firstMarker, store.Comment);
        Assert.Equal("admin", store.UserName);
        Assert.Equal("pw-a", store.Secret);
    }

    [Fact]
    public void WriteDomainCredential_EntryOlderThanTheLiveWindow_IsReclaimed()
    {
        string staleMarker = CredentialManagerHelper.CreateDomainCredentialOwnershipMarker(
            OwningProcessId,
            WriteInstant);
        FakeCredentialStore store = new FakeCredentialStore(staleMarker, "admin", "pw-a");
        DateTime now = WriteInstant +
            CredentialManagerHelper.LiveLaunchMarkerWindow +
            TimeSpan.FromSeconds(1);
        string currentMarker = CredentialManagerHelper.CreateDomainCredentialOwnershipMarker(
            OwningProcessId,
            now);

        bool result = CredentialManagerHelper.WriteDomainCredential(
            "TERMSRV/srv01",
            "svc",
            "pw-b",
            currentMarker,
            store.Probe,
            store.Write,
            OwningProcessId,
            now,
            out bool credentialWritten,
            out string? error);

        Assert.True(result);
        Assert.True(credentialWritten);
        Assert.Null(error);
        Assert.Equal(currentMarker, store.Comment);
        Assert.Equal("svc", store.UserName);
        Assert.Equal("pw-b", store.Secret);
    }

    [Fact]
    public void WriteDomainCredential_EntryFromAnotherProcess_IsReclaimed()
    {
        string otherProcessMarker = CredentialManagerHelper.CreateDomainCredentialOwnershipMarker(
            OwningProcessId + 1,
            WriteInstant);
        FakeCredentialStore store = new FakeCredentialStore(otherProcessMarker, "admin", "pw-a");
        string currentMarker = CredentialManagerHelper.CreateDomainCredentialOwnershipMarker(
            OwningProcessId,
            WriteInstant.AddSeconds(3));

        bool result = CredentialManagerHelper.WriteDomainCredential(
            "TERMSRV/srv01",
            "svc",
            "pw-b",
            currentMarker,
            store.Probe,
            store.Write,
            OwningProcessId,
            WriteInstant.AddSeconds(3),
            out bool credentialWritten,
            out string? error);

        Assert.True(result);
        Assert.True(credentialWritten);
        Assert.Equal(currentMarker, store.Comment);
        Assert.Equal("svc", store.UserName);
    }

    [Fact]
    public void CreateDomainCredentialOwnershipMarker_CarriesTheProcessAndTheWriteInstant()
    {
        string marker = CredentialManagerHelper.CreateDomainCredentialOwnershipMarker(
            OwningProcessId,
            WriteInstant);

        Assert.StartsWith(CredentialManagerHelper.DomainCredentialOwnershipPrefix, marker);
        Assert.True(
            CredentialManagerHelper.IsLiveLaunchMarker(
                marker,
                OwningProcessId,
                WriteInstant.AddSeconds(1)));
        Assert.NotEqual(
            marker,
            CredentialManagerHelper.CreateDomainCredentialOwnershipMarker(
                OwningProcessId,
                WriteInstant));
    }

    [Theory]
    [InlineData("Heimdall:RDP:deadbeef")]
    [InlineData("Heimdall:RDP:")]
    [InlineData("Heimdall:RDP:notanumber:0:x")]
    [InlineData("Heimdall:RDP:4242:notaninstant:x")]
    [InlineData("Heimdall:RDP:4242:-1:x")]
    [InlineData("SomeoneElse:4242:0:x")]
    [InlineData(null)]
    public void IsLiveLaunchMarker_UnparsableOrForeignMarkers_AreReclaimable(string? marker)
    {
        Assert.False(
            CredentialManagerHelper.IsLiveLaunchMarker(marker, OwningProcessId, WriteInstant));
    }

    [Fact]
    public void IsLiveLaunchMarker_MarkerWrittenInTheFuture_IsReclaimable()
    {
        string marker = CredentialManagerHelper.CreateDomainCredentialOwnershipMarker(
            OwningProcessId,
            WriteInstant.AddMinutes(5));

        Assert.False(
            CredentialManagerHelper.IsLiveLaunchMarker(marker, OwningProcessId, WriteInstant));
    }

    /// <summary>
    /// Minimal stand-in for one Credential Manager target: it answers the probe with the
    /// comment it currently holds, and a write replaces all three stored fields.
    /// </summary>
    private sealed class FakeCredentialStore(string? comment, string? userName, string? secret)
    {
        public string? Comment { get; private set; } = comment;

        public string? UserName { get; private set; } = userName;

        public string? Secret { get; private set; } = secret;

        public CredentialManagerHelper.CredentialProbeResult Probe(string target, string marker)
        {
            if (Comment is null)
            {
                return new CredentialManagerHelper.CredentialProbeResult(true, false, false, null);
            }

            return new CredentialManagerHelper.CredentialProbeResult(
                true,
                true,
                Comment.StartsWith(marker, StringComparison.Ordinal),
                null,
                Comment);
        }

        public bool Write(
            string target,
            string userName,
            string secret,
            uint credentialType,
            uint credentialPersist,
            string? comment,
            out string? error)
        {
            UserName = userName;
            Secret = secret;
            Comment = comment;
            error = null;
            return true;
        }
    }
}
