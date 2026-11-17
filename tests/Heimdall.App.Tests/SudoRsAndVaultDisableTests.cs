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
using Heimdall.App.ViewModels;
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

/// <summary>
/// The wording sudo-rs prints reaches the same classifications as the sudo one, through the lists
/// that <see cref="PrivilegedFileTransfer"/> owns; the privileged listing text; and the second
/// warning of the vault disable dialog.
/// </summary>
public sealed class SudoRsAndVaultDisableTests
{
    private const string SealedCopiesKey = "VaultDisableSealedCopiesWarning";

    [Theory]
    [InlineData("sudo: Authentication failed, try again.", nameof(SudoFailureKind.PasswordRejected))]
    [InlineData("sudo: Maximum 3 incorrect authentication attempts", nameof(SudoFailureKind.PasswordRejected))]
    [InlineData("sudo: Authentication required but not attempted", nameof(SudoFailureKind.PasswordRejected))]
    [InlineData("sudo: Interactive authentication is required", nameof(SudoFailureKind.PasswordUnavailable))]
    [InlineData("sudo: interaction is required", nameof(SudoFailureKind.PasswordUnavailable))]
    public void EnsureSudoSucceeded_ReadsSudoRsAuthenticationFailures(string stderr, string expectedName)
    {
        SudoFailureKind expected = Enum.Parse<SudoFailureKind>(expectedName);

        SudoAuthenticationException failure = Assert.Throws<SudoAuthenticationException>(
            () => EmbeddedSftpViewModel.EnsureSudoSucceeded(1, stderr, "command"));

        Assert.Equal(expected, failure.Kind);
        Assert.True(SudoBatchGuard.IsBatchFatal(failure), "a failure that repeats for every entry must stop the batch");
    }

    [Theory]
    [InlineData("sudo: I'm afraid I can't do that")]
    [InlineData("sudo: alice is not allowed to authenticate as root")]
    public void EnsureSudoSucceeded_ReadsSudoRsRefusalAsNotPermitted(string stderr)
    {
        SudoAuthenticationException failure = Assert.Throws<SudoAuthenticationException>(
            () => EmbeddedSftpViewModel.EnsureSudoSucceeded(1, stderr, "command"));

        Assert.Equal(SudoFailureKind.NotPermitted, failure.Kind);
        Assert.True(SudoBatchGuard.IsBatchFatal(failure));
    }

    [Theory]
    [InlineData("sudo: Authentication failed, try again.", true, false)]
    [InlineData("sudo: Authentication required but not attempted", true, false)]
    [InlineData("sudo: Interactive authentication is required", false, true)]
    [InlineData("sudo: interaction is required", false, true)]
    [InlineData("sudo: a password is required", false, true)]
    [InlineData("sudo: sorry, you must have a tty to run sudo", false, true)]
    [InlineData("Sorry, try again.", true, false)]
    [InlineData("sudo: unable to resolve host labbox", false, false)]
    [InlineData("", false, false)]
    [InlineData(null, false, false)]
    public void TheSharedHelpers_AnswerForBothClassifiers(string? stderr, bool rejected, bool unavailable)
    {
        Assert.Equal(rejected, PrivilegedFileTransfer.IsPasswordRejectedMessage(stderr));
        Assert.Equal(unavailable, PrivilegedFileTransfer.IsPasswordUnavailableMessage(stderr));

        SudoFailureKind expected = unavailable
            ? SudoFailureKind.PasswordUnavailable
            : rejected ? SudoFailureKind.PasswordRejected : SudoFailureKind.None;
        Assert.Equal(expected, EmbeddedSftpViewModel.ClassifySudoStderr(stderr));
    }

    [Fact]
    public void TheListingBody_IsFindItself_SoARuleLimitedToFindMatchesAndNoVariableIsAsked()
    {
        string body = SudoDirectoryListing.Build("/srv/app");

        Assert.StartsWith("find -H '/srv/app' -mindepth 1 -maxdepth 1 -printf '", body, StringComparison.Ordinal);
        Assert.DoesNotContain("LC_ALL", body, StringComparison.Ordinal);
        Assert.Equal(
            "env LC_ALL=C sudo find -H '/srv/app' -mindepth 1 -maxdepth 1 -printf '%y\\0%m\\0%u\\0%g\\0%s\\0%T@\\0%f\\0'",
            EmbeddedSftpViewModel.BuildSudoInvocation(body, authenticateViaStdin: false));
        Assert.Equal(
            "env LC_ALL=C sudo -S -p '' find -H '/srv/app' -mindepth 1 -maxdepth 1 -printf '%y\\0%m\\0%u\\0%g\\0%s\\0%T@\\0%f\\0'",
            EmbeddedSftpViewModel.BuildSudoInvocation(body, authenticateViaStdin: true));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public void TheSealedCopiesWarning_ExistsInEveryLocale(string locale)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), "locales", locale + ".json")));

        Assert.True(document.RootElement.TryGetProperty(SealedCopiesKey, out JsonElement value), $"{locale} lacks {SealedCopiesKey}");
        Assert.False(string.IsNullOrWhiteSpace(value.GetString()));
    }

    [Fact]
    public void TheVaultDisableDialog_ShowsTheSealedCopiesWarning_NextToTheExistingOne()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Heimdall.App", "Views", "Dialogs", "VaultDisableDialog.xaml"));

        int existing = xaml.IndexOf("{loc:Translate VaultDisableWarning}", StringComparison.Ordinal);
        int added = xaml.IndexOf("{loc:Translate " + SealedCopiesKey + "}", StringComparison.Ordinal);

        Assert.True(existing >= 0, "the existing warning must stay");
        Assert.True(added > existing, "the sealed copies warning follows the existing one");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(xaml, System.Text.RegularExpressions.Regex.Escape(SealedCopiesKey)));
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Heimdall repository root.");
    }
}
