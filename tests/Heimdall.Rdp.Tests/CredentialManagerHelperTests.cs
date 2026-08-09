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
}
