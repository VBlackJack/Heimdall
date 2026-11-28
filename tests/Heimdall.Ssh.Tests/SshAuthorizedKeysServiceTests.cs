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

using Heimdall.Ssh;

namespace Heimdall.Ssh.Tests;

public sealed class SshAuthorizedKeysServiceTests
{
    [Fact]
    public void ParseLines_ExtractsPlainAndRestrictedKeysWithSha256Fingerprints()
    {
        var pair1 = SshKeyPairGenerator.Generate(SshKeyAlgorithm.Ed25519, comment: "alice@laptop");
        var pair2 = SshKeyPairGenerator.Generate(SshKeyAlgorithm.Ed25519, comment: "backup-bot");

        string content =
            "# comment line\n" +
            "\n" +
            pair1.PublicKeyOpenSsh + "\n" +
            $"from=\"10.0.0.0/8\",command=\"/usr/bin/rrsync /srv/backup\",no-pty {pair2.PublicKeyOpenSsh}\n";

        var entries = SshAuthorizedKeysService.ParseLines(content);

        Assert.Equal(2, entries.Count);

        Assert.Equal(3, entries[0].LineNumber);
        Assert.Equal(string.Empty, entries[0].Options);
        Assert.Equal("ssh-ed25519", entries[0].KeyType);
        Assert.Equal("alice@laptop", entries[0].Comment);
        Assert.Equal(pair1.Fingerprint, entries[0].Fingerprint);

        Assert.Equal(4, entries[1].LineNumber);
        Assert.Equal("from=\"10.0.0.0/8\",command=\"/usr/bin/rrsync /srv/backup\",no-pty", entries[1].Options);
        Assert.Equal("ssh-ed25519", entries[1].KeyType);
        Assert.Equal("backup-bot", entries[1].Comment);
        Assert.Equal(pair2.Fingerprint, entries[1].Fingerprint);
    }

    [Fact]
    public void TryValidateOptions_AcceptsValidQuotedOptionsAndRejectsControlCharsOrUnquotedSpaces()
    {
        Assert.True(SshAuthorizedKeysService.TryValidateOptions(null, out _));
        Assert.True(SshAuthorizedKeysService.TryValidateOptions("no-pty,no-agent-forwarding,from=\"10.0.0.0/8\"", out var ok));
        Assert.Equal("no-pty,no-agent-forwarding,from=\"10.0.0.0/8\"", ok);
        Assert.True(SshAuthorizedKeysService.TryValidateOptions("command=\"/usr/bin/rrsync /srv\"", out _));

        Assert.False(SshAuthorizedKeysService.TryValidateOptions("no-pty no-forwarding", out _));
        Assert.False(SshAuthorizedKeysService.TryValidateOptions("from=\"unterminated", out _));
        Assert.False(SshAuthorizedKeysService.TryValidateOptions("no-pty\nssh-ed25519 AAAA", out _));
    }

    [Fact]
    public void BuildRemoveKeyCommand_EnforcesSymlinkGuardAndOptionalLastKeyGuard()
    {
        var pair = SshKeyPairGenerator.Generate(SshKeyAlgorithm.Ed25519, comment: "ops@host");
        string[] parts = pair.PublicKeyOpenSsh.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        string guarded = SshAuthorizedKeysService.BuildRemoveKeyCommand(parts[0], parts[1], allowRemoveLastKey: false);
        Assert.Contains("[ -L \"$ak\" ]", guarded, StringComparison.Ordinal);
        Assert.Contains(SshAuthorizedKeysService.SymlinkRefusedMarker, guarded, StringComparison.Ordinal);
        Assert.Contains("grep -Eq '^[[:space:]]*[^[:space:]#]' \"$tmp\"", guarded, StringComparison.Ordinal);
        Assert.DoesNotContain("[ -s \"$tmp\" ]", guarded, StringComparison.Ordinal);
        Assert.Contains(SshAuthorizedKeysService.LastKeyRefusedMarker, guarded, StringComparison.Ordinal);

        string allowLast = SshAuthorizedKeysService.BuildRemoveKeyCommand(parts[0], parts[1], allowRemoveLastKey: true);
        Assert.Contains("[ -L \"$ak\" ]", allowLast, StringComparison.Ordinal);
        Assert.DoesNotContain("grep -Eq '^[[:space:]]*[^[:space:]#]' \"$tmp\"", allowLast, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRemoveKeyCommand_WithExactRawLine_TargetsSingleRowForDuplicateKeysWithDifferentOptions()
    {
        var pair = SshKeyPairGenerator.Generate(SshKeyAlgorithm.Ed25519, comment: "ops@host");
        string[] parts = pair.PublicKeyOpenSsh.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        string exactRow = $"from=\"10.0.0.1\" {pair.PublicKeyOpenSsh}";

        string cmd = SshAuthorizedKeysService.BuildRemoveKeyCommand(parts[0], parts[1], allowRemoveLastKey: false, exactRow);

        Assert.Contains("grep -Fvx -- \"$exact_line\" \"$ak\"", cmd, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(exactRow)), cmd, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAddKeyCommand_IgnoresCommentedOutMatchingKeys()
    {
        var pair = SshKeyPairGenerator.Generate(SshKeyAlgorithm.Ed25519, comment: "ops@host");
        string cmd = SshAuthorizedKeysService.BuildAddKeyCommand(pair.PublicKeyOpenSsh, null);

        Assert.Contains("grep -Ev '^[[:space:]]*(#|$)' \"$ak\" | grep -Fq -- \"$key_id\"", cmd, StringComparison.Ordinal);
    }

    [Fact]
    public void SshPublicKeyDeployer_BuildDeployCommand_EnforcesSymlinkGuardAndIgnoresCommentedMatches()
    {
        var pair = SshKeyPairGenerator.Generate(SshKeyAlgorithm.Ed25519, comment: "ops@host");
        string cmd = SshPublicKeyDeployer.BuildDeployCommand(pair.PublicKeyOpenSsh);

        Assert.Contains("[ -L \"$ak\" ]", cmd, StringComparison.Ordinal);
        Assert.Contains(SshPublicKeyDeployer.MarkerSymlinkRefused, cmd, StringComparison.Ordinal);
        Assert.Contains("grep -Ev '^[[:space:]]*(#|$)' \"$ak\" | grep -Fq -- \"$key_id\"", cmd, StringComparison.Ordinal);
    }
}
