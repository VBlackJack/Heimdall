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
/// The host key probe stops before authentication and reads only a key that plink presented and
/// then refused.
/// </summary>
public sealed class PlinkHostKeyRejectionProbeTests
{
    private const string RejectionLine = "Host key not in manually configured list";
    private const string FingerprintHeader = "Host key fingerprint is:";
    private const string KeyLine = "ssh-ed25519 255 SHA256:FGu8GWjk83MKlI+31IUBrGUMiJ46UKMUyY7XcrS9YeI";
    private const string ExpectedAlgorithm = "ssh-ed25519";
    private const string ExpectedFingerprint = "SHA256:FGu8GWjk83MKlI+31IUBrGUMiJ46UKMUyY7XcrS9YeI";

    // Measured with the bundled plink 0.83 against an OpenSSH 9.2 server in a throwaway
    // container: exit code 1, and the server saw a close before authentication.
    private static readonly string[] RealTranscript =
    [
        "Looking up host \"127.0.0.1\" for SSH connection",
        "Connecting to 127.0.0.1 port 2299",
        "Connected to 127.0.0.1 (from 127.0.0.1:56849)",
        "We claim version: SSH-2.0-PuTTY_Release_0.83",
        "Remote version: SSH-2.0-OpenSSH_9.2p1 Debian-2+deb12u10",
        "Using SSH protocol version 2",
        "No GSSAPI security context available",
        "Enabling strict key exchange semantics",
        "Doing NTRU Prime / Curve25519 hybrid key exchange, using hash SHA-512 (unaccelerated)",
        "Server also has ecdsa-sha2-nistp256/rsa-sha2-512/rsa-sha2-256 host keys, but we don't know any of them",
        FingerprintHeader,
        KeyLine,
        RejectionLine,
        "FATAL ERROR: Host key not in manually configured list",
    ];

    [Fact]
    public void ProbeArguments_StopBeforeAuthentication_AndStayMinimal()
    {
        string[] args = PlinkHostKeyProbe.BuildProbeArguments(2299, "probe@host.example.test");

        Assert.Contains("-noagent", args);
        Assert.Contains("-noshare", args);
        Assert.Contains("-N", args);
        int hostKey = Array.IndexOf(args, "-hostkey");
        Assert.True(hostKey >= 0);
        Assert.Equal(PlinkHostKeyProbe.UnsatisfiableFingerprint, args[hostKey + 1]);
        Assert.Matches("^SHA256:A{43}$", args[hostKey + 1]);
        Assert.Equal("probe@host.example.test", args[^1]);
        Assert.True(hostKey < args.Length - 1);
        Assert.Equal(["-v", "-batch"], args[..2]);
        Assert.Equal("2299", args[Array.IndexOf(args, "-P") + 1]);
        foreach (string forbidden in new[] { "-A", "-L", "-R", "-D", "-load" })
        {
            Assert.DoesNotContain(forbidden, args);
        }
    }

    [Fact]
    public void StrictParser_AcceptsTheRealRejectionTranscript()
    {
        PlinkHostKeyPresentation? presentation = Parse(Transcript(RealTranscript));

        Assert.NotNull(presentation);
        Assert.Equal(ExpectedAlgorithm, presentation!.Algorithm);
        Assert.Equal(ExpectedFingerprint, presentation.Fingerprint);
    }

    [Fact]
    public void StrictParser_AcceptsCrLfLineEndings()
    {
        PlinkHostKeyPresentation? presentation = Parse(string.Join("\r\n", RealTranscript) + "\r\n");

        Assert.Equal(ExpectedFingerprint, presentation?.Fingerprint);
    }

    [Fact]
    public void StrictParser_RejectsACachedKeyTranscript()
    {
        string[] cached =
        [
            .. RealTranscript[..10],
            FingerprintHeader,
            KeyLine,
            "Using username \"probe\".",
            "Access granted",
        ];

        Assert.Null(Parse(Transcript(cached)));
    }

    [Fact]
    public void StrictParser_RejectsAFingerprintThatOnlyAppearsInTheServerVersion()
    {
        string[] versionOnly =
        [
            "Remote version: SSH-2.0-Evil SHA256:FGu8GWjk83MKlI+31IUBrGUMiJ46UKMUyY7XcrS9YeI",
            "FATAL ERROR: Network error: Connection refused",
        ];

        Assert.Null(Parse(Transcript(versionOnly)));
    }

    [Fact]
    public void StrictParser_RejectsAnMd5OnlyFingerprint()
    {
        string[] md5 =
        [
            FingerprintHeader,
            "ssh-rsa 2048 MD5:aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99",
            RejectionLine,
        ];

        Assert.Null(Parse(Transcript(md5)));
    }

    [Fact]
    public void StrictParser_RejectsATranscriptCutBeforeTheRejectionLine()
    {
        string[] cut = [.. RealTranscript[..12]];

        Assert.Null(Parse(Transcript(cut)));
    }

    [Fact]
    public void StrictParser_RejectsATypeLineThatIsNotDirectlyAfterTheHeader()
    {
        string[] gap =
        [
            .. RealTranscript[..11],
            "Some other line",
            KeyLine,
            RejectionLine,
        ];

        Assert.Null(Parse(Transcript(gap)));
    }

    [Theory]
    [InlineData("Using username \"probe\".")]
    [InlineData("Access granted")]
    [InlineData("Authenticating with public key \"imported-openssh-key\" from agent")]
    [InlineData("Pageant is not running")]
    [InlineData("Started a shell/command")]
    [InlineData("Opening session as main channel")]
    public void StrictParser_RejectsARejectionTranscriptThatAlsoShowsProgress(string marker)
    {
        string[] progressed = [.. RealTranscript[..13], marker];

        Assert.Null(Parse(Transcript(progressed)));
    }

    [Fact]
    public void StrictParser_RejectsADuplicatedHeader()
    {
        string[] duplicated =
        [
            .. RealTranscript[..4],
            FingerprintHeader,
            "ssh-ed25519 255 SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            .. RealTranscript[4..],
        ];

        Assert.Null(Parse(Transcript(duplicated)));
    }

    [Fact]
    public void StrictParser_RejectsTheThreeLinesInjectedInAnEarlierMessage()
    {
        string[] injected =
        [
            .. RealTranscript[..5],
            FingerprintHeader,
            "ssh-ed25519 255 SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            RejectionLine,
            .. RealTranscript[5..],
        ];

        Assert.Null(Parse(Transcript(injected)));
    }

    [Fact]
    public void StrictParser_AcceptsTrailingBlankLinesAfterTheFatalLine()
    {
        Assert.Equal(ExpectedFingerprint, Parse(Transcript(RealTranscript) + "\n\n  \n")?.Fingerprint);
    }

    [Fact]
    public void StrictParser_RejectsATranscriptWhoseLastLineIsNotThePlinkFatalRejection()
    {
        string[] other = [.. RealTranscript[..13], "FATAL ERROR: Network error: Connection reset"];

        Assert.Null(Parse(Transcript(other)));
    }

    [Theory]
    [InlineData("Doing GSSAPI key exchange with hash SHA-256")]
    [InlineData("doing gssapi Key Exchange")]
    public void StrictParser_RejectsAGssapiKeyExchange(string line)
    {
        string[] gssapi = [.. RealTranscript[..8], line, .. RealTranscript[8..]];

        Assert.Null(Parse(Transcript(gssapi)));
    }

    [Fact]
    public void StrictParser_AcceptsTheBenignNoGssapiContextLine()
    {
        Assert.Contains("No GSSAPI security context available", RealTranscript);
        Assert.NotNull(Parse(Transcript(RealTranscript)));
    }

    [Fact]
    public void StrictParser_RejectsAProcessKilledOnTimeout()
    {
        Assert.Null(PlinkHostKeyProbe.TryParseRejectedPresentation(Transcript(RealTranscript), exitedByItself: false));
    }

    [Fact]
    public void StrictParser_RejectsBlankInput()
    {
        Assert.Null(Parse(string.Empty));
        Assert.Null(Parse(null));
    }

    private static PlinkHostKeyPresentation? Parse(string? stderr)
        => PlinkHostKeyProbe.TryParseRejectedPresentation(stderr, exitedByItself: true);

    private static string Transcript(IEnumerable<string> lines) => string.Join("\n", lines) + "\n";
}
