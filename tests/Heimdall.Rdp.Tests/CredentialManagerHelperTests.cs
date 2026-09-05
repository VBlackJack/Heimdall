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

public sealed class CredentialManagerHelperTests
{
    [Fact]
    public void CreateDomainCredentialOwnershipMarker_ReturnsFreshLaunchMarkers()
    {
        string first = CredentialManagerHelper.CreateDomainCredentialOwnershipMarker();
        string second = CredentialManagerHelper.CreateDomainCredentialOwnershipMarker();

        Assert.StartsWith(CredentialManagerHelper.DomainCredentialOwnershipPrefix, first);
        Assert.StartsWith(CredentialManagerHelper.DomainCredentialOwnershipPrefix, second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void WriteDomainCredential_NoEntry_WritesWithCurrentLaunchMarker()
    {
        const string marker = "Heimdall:RDP:current";
        string? writtenComment = null;

        bool result = CredentialManagerHelper.WriteDomainCredential(
            "TERMSRV/server",
            "user",
            "password",
            marker,
            (_, probeMarker) =>
            {
                Assert.Equal(CredentialManagerHelper.DomainCredentialOwnershipPrefix, probeMarker);
                return new CredentialManagerHelper.CredentialProbeResult(true, false, false, null);
            },
            (
                string _,
                string _,
                string _,
                uint credentialType,
                uint _,
                string? comment,
                out string? writeError) =>
            {
                Assert.Equal(CredentialManagerHelper.CredTypeDomainPassword, credentialType);
                writtenComment = comment;
                writeError = null;
                return true;
            },
            out bool credentialWritten,
            out string? error);

        Assert.True(result);
        Assert.True(credentialWritten);
        Assert.Null(error);
        Assert.Equal(marker, writtenComment);
    }

    [Fact]
    public void WriteDomainCredential_ForeignEntry_DoesNotWrite()
    {
        bool writeCalled = false;

        bool result = CredentialManagerHelper.WriteDomainCredential(
            "TERMSRV/server",
            "user",
            "password",
            "Heimdall:RDP:current",
            (_, _) => new CredentialManagerHelper.CredentialProbeResult(true, true, false, null),
            (
                string _,
                string _,
                string _,
                uint _,
                uint _,
                string? _,
                out string? writeError) =>
            {
                writeCalled = true;
                writeError = null;
                return true;
            },
            out bool credentialWritten,
            out string? error);

        Assert.True(result);
        Assert.False(credentialWritten);
        Assert.False(writeCalled);
        Assert.Null(error);
    }

    [Fact]
    public void WriteDomainCredential_PreviousHeimdallEntry_ReplacesWithCurrentMarker()
    {
        const string marker = "Heimdall:RDP:current";
        string? writtenComment = null;

        bool result = CredentialManagerHelper.WriteDomainCredential(
            "TERMSRV/server",
            "user",
            "password",
            marker,
            (_, _) => new CredentialManagerHelper.CredentialProbeResult(true, true, true, null),
            (
                string _,
                string _,
                string _,
                uint _,
                uint _,
                string? comment,
                out string? writeError) =>
            {
                writtenComment = comment;
                writeError = null;
                return true;
            },
            out bool credentialWritten,
            out string? error);

        Assert.True(result);
        Assert.True(credentialWritten);
        Assert.Null(error);
        Assert.Equal(marker, writtenComment);
    }

    [Fact]
    public void DeleteCredential_CurrentLaunchMarkerIntact_DeletesDomainPasswordOnly()
    {
        List<uint> attemptedTypes = [];

        bool result = CredentialManagerHelper.DeleteCredential(
            "TERMSRV/server",
            "Heimdall:RDP:current",
            (_, marker) =>
            {
                Assert.Equal("Heimdall:RDP:current", marker);
                return new CredentialManagerHelper.CredentialProbeResult(true, true, true, null);
            },
            (_, type) =>
            {
                attemptedTypes.Add(type);
                return new CredentialManagerHelper.CredentialDeleteResult(true, 0);
            },
            out bool credentialDeleted,
            out string? error);

        Assert.True(result);
        Assert.True(credentialDeleted);
        Assert.Null(error);
        Assert.Equal([CredentialManagerHelper.CredTypeDomainPassword], attemptedTypes);
        Assert.DoesNotContain(CredentialManagerHelper.CredTypeGeneric, attemptedTypes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void DeleteCredential_MarkerAbsentOrChanged_DoesNotDelete(
        bool credentialExists,
        bool markerMatches)
    {
        bool deleteCalled = false;

        bool result = CredentialManagerHelper.DeleteCredential(
            "TERMSRV/server",
            "Heimdall:RDP:current",
            (_, _) => new CredentialManagerHelper.CredentialProbeResult(
                true,
                credentialExists,
                markerMatches,
                null),
            (_, _) =>
            {
                deleteCalled = true;
                return new CredentialManagerHelper.CredentialDeleteResult(true, 0);
            },
            out bool credentialDeleted,
            out string? error);

        Assert.True(result);
        Assert.False(credentialDeleted);
        Assert.False(deleteCalled);
        Assert.Null(error);
    }

    [Fact]
    public void ProbeCredential_MetadataReadThrows_FreesNativeCredentialInFinally()
    {
        IntPtr expectedPointer = new IntPtr(42);
        IntPtr freedPointer = IntPtr.Zero;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            CredentialManagerHelper.ProbeCredential(
                "TERMSRV/server",
                CredentialManagerHelper.CredTypeDomainPassword,
                "Heimdall:RDP:current",
                exactMarker: true,
                (
                    string _,
                    uint _,
                    out IntPtr credentialPointer,
                    out int errorCode) =>
                {
                    credentialPointer = expectedPointer;
                    errorCode = 0;
                    return true;
                },
                pointer => freedPointer = pointer,
                _ => throw new InvalidOperationException("metadata read failed")));

        Assert.Equal("metadata read failed", exception.Message);
        Assert.Equal(expectedPointer, freedPointer);
    }

    [Fact]
    public void ProbeCredential_NotFound_ReturnsAbsentWithoutReadingOrFreeing()
    {
        bool commentRead = false;
        bool freeCalled = false;

        CredentialManagerHelper.CredentialProbeResult result = CredentialManagerHelper.ProbeCredential(
            "TERMSRV/server",
            CredentialManagerHelper.CredTypeDomainPassword,
            CredentialManagerHelper.DomainCredentialOwnershipPrefix,
            exactMarker: false,
            (
                string _,
                uint _,
                out IntPtr credentialPointer,
                out int errorCode) =>
            {
                credentialPointer = IntPtr.Zero;
                errorCode = CredentialManagerHelper.ErrorNotFound;
                return false;
            },
            _ => freeCalled = true,
            _ =>
            {
                commentRead = true;
                return null;
            });

        Assert.True(result.Success);
        Assert.False(result.Exists);
        Assert.False(result.MarkerMatches);
        Assert.Null(result.Error);
        Assert.False(commentRead);
        Assert.False(freeCalled);
    }

    private static readonly DateTime SweepNow = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    private static string MarkerAgedBy(TimeSpan age, int processId = 4242)
        => CredentialManagerHelper.CreateDomainCredentialOwnershipMarker(processId, SweepNow - age);

    [Fact]
    public void IsStaleOwnedMarker_OlderThanTheLiveWindow_IsStaleWhateverTheProcess()
    {
        string ownMarker = MarkerAgedBy(TimeSpan.FromMinutes(2), Environment.ProcessId);
        string foreignMarker = MarkerAgedBy(TimeSpan.FromMinutes(2), 99999);

        Assert.True(CredentialManagerHelper.IsStaleOwnedMarker(ownMarker, SweepNow));
        Assert.True(CredentialManagerHelper.IsStaleOwnedMarker(foreignMarker, SweepNow));
    }

    [Fact]
    public void IsStaleOwnedMarker_InsideTheLiveWindow_IsNotStale()
    {
        string marker = MarkerAgedBy(CredentialManagerHelper.LiveLaunchMarkerWindow - TimeSpan.FromSeconds(1));

        Assert.False(CredentialManagerHelper.IsStaleOwnedMarker(marker, SweepNow));
    }

    // The single-field format carries no timestamp, so it cannot be judged and is left alone:
    // deleting it on its prefix alone would reach into a launch an older build may still be running.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Heimdall:RDP:legacy-single-field")]
    [InlineData("Heimdall:RDP:4242:not-a-number:abcd")]
    [InlineData("Somebody else's comment")]
    public void IsStaleOwnedMarker_UnjudgeableComment_IsNotStale(string? comment)
    {
        Assert.False(CredentialManagerHelper.IsStaleOwnedMarker(comment, SweepNow));
    }

    [Fact]
    public void SweepStaleOwnedCredentials_DeletesOnlyStaleHeimdallDomainPasswordEntries()
    {
        List<(string Target, uint Type)> deleted = [];
        IReadOnlyList<CredentialManagerHelper.StoredCredentialSummary> stored =
        [
            new("TERMSRV/stale", CredentialManagerHelper.CredTypeDomainPassword, MarkerAgedBy(TimeSpan.FromHours(1))),
            new("TERMSRV/live", CredentialManagerHelper.CredTypeDomainPassword, MarkerAgedBy(TimeSpan.FromSeconds(5))),
            new("TERMSRV/foreign", CredentialManagerHelper.CredTypeDomainPassword, "Saved by the user in Credential Manager"),
            new("TERMSRV/unmarked", CredentialManagerHelper.CredTypeDomainPassword, null),
            new("TERMSRV/generic", CredentialManagerHelper.CredTypeGeneric, MarkerAgedBy(TimeSpan.FromHours(1))),
        ];

        int count = CredentialManagerHelper.SweepStaleOwnedCredentials(
            SweepNow,
            (string filter, out IReadOnlyList<CredentialManagerHelper.StoredCredentialSummary> credentials, out int errorCode) =>
            {
                Assert.Equal(CredentialManagerHelper.RdpCredentialTargetFilter, filter);
                credentials = stored;
                errorCode = 0;
                return true;
            },
            (target, type) =>
            {
                deleted.Add((target, type));
                return new CredentialManagerHelper.CredentialDeleteResult(true, 0);
            },
            warn: null);

        Assert.Equal(1, count);
        Assert.Equal([("TERMSRV/stale", CredentialManagerHelper.CredTypeDomainPassword)], deleted);
    }

    [Fact]
    public void SweepStaleOwnedCredentials_NothingStored_DeletesNothingAndStaysSilent()
    {
        List<string> warnings = [];
        bool deleteCalled = false;

        int count = CredentialManagerHelper.SweepStaleOwnedCredentials(
            SweepNow,
            (string _, out IReadOnlyList<CredentialManagerHelper.StoredCredentialSummary> credentials, out int errorCode) =>
            {
                credentials = [];
                errorCode = CredentialManagerHelper.ErrorNotFound;
                return false;
            },
            (_, _) =>
            {
                deleteCalled = true;
                return new CredentialManagerHelper.CredentialDeleteResult(true, 0);
            },
            warnings.Add);

        Assert.Equal(0, count);
        Assert.False(deleteCalled);
        Assert.Empty(warnings);
    }

    [Fact]
    public void SweepStaleOwnedCredentials_EnumerationFails_WarnsWithTheCodeAndDeletesNothing()
    {
        List<string> warnings = [];

        int count = CredentialManagerHelper.SweepStaleOwnedCredentials(
            SweepNow,
            (string _, out IReadOnlyList<CredentialManagerHelper.StoredCredentialSummary> credentials, out int errorCode) =>
            {
                credentials = [];
                errorCode = 5;
                return false;
            },
            (_, _) => throw new InvalidOperationException("delete must not be reached"),
            warnings.Add);

        Assert.Equal(0, count);
        string warning = Assert.Single(warnings);
        Assert.Contains("WIN32_ERROR_5", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void SweepStaleOwnedCredentials_DeleteFails_WarnsWithTheTargetOnlyAndKeepsGoing()
    {
        List<string> warnings = [];
        string staleMarker = MarkerAgedBy(TimeSpan.FromHours(1));
        IReadOnlyList<CredentialManagerHelper.StoredCredentialSummary> stored =
        [
            new("TERMSRV/first", CredentialManagerHelper.CredTypeDomainPassword, staleMarker),
            new("TERMSRV/second", CredentialManagerHelper.CredTypeDomainPassword, staleMarker),
        ];

        int count = CredentialManagerHelper.SweepStaleOwnedCredentials(
            SweepNow,
            (string _, out IReadOnlyList<CredentialManagerHelper.StoredCredentialSummary> credentials, out int errorCode) =>
            {
                credentials = stored;
                errorCode = 0;
                return true;
            },
            (target, _) => target.EndsWith("first", StringComparison.Ordinal)
                ? new CredentialManagerHelper.CredentialDeleteResult(false, 5)
                : new CredentialManagerHelper.CredentialDeleteResult(true, 0),
            warnings.Add);

        Assert.Equal(1, count);
        string warning = Assert.Single(warnings);
        Assert.Contains("TERMSRV/first", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(staleMarker, warning, StringComparison.Ordinal);
    }
}
