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
using System.Linq;
using System.Text.Json;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The backup-residue reporter is wired into the FTP browser, and its two messages say what
/// Heimdall did rather than telling the user to repair the server by hand.
/// </summary>
/// <remarks>
/// The classifier and the de-duplication are behavioural tests in Heimdall.Sftp.Tests, where
/// they need no server. What cannot be reached there is the wiring: <c>ListDirectoryAsync</c>
/// wants a live FTP connection. These two readings are carried through
/// <see cref="ViewSource.IsStatementOfTheMethodBody"/>, so a call folded behind a term that
/// is false by construction is not mistaken for one that stands - which is also why the
/// event is raised outside the operation lock, at the top level of the method, instead of
/// inside the try that holds it.
/// </remarks>
public sealed class FtpBackupResidueWiringTests
{
    private const string BrowserFile = "FtpBrowser.cs";

    private const string ListingMember =
        "public async Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(";

    private const string DisconnectMember = "private bool DisconnectCore()";

    private const string ReportStatement = "RaiseBackupResidueWarnings(result, rawNames);";

    private const string ResetStatement = "_residueReporter.Reset();";

    [Fact]
    public void EveryListingConsultsTheResidueReporter()
    {
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(Logic(ListingMember), ReportStatement),
            "the residue report is not a step of the FTP listing");
    }

    [Fact]
    public void ADisconnectForgetsWhatItReported()
    {
        // Without this, whether the user is told depends on how long the application has
        // been running: reconnecting to the same server would stay silent about a residue
        // they never saw.
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(Logic(DisconnectMember), ResetStatement),
            "the reporter is not reset when the connection goes away");
    }

    [Theory]
    [InlineData("WarnFtpBackupResidueOriginalMissing")]
    [InlineData("WarnFtpBackupResidueOriginalPresent")]
    public void BothMessagesExistInBothLanguages(string key)
    {
        Assert.True(Locale("en.json").TryGetProperty(key, out JsonElement english));
        Assert.True(Locale("fr.json").TryGetProperty(key, out JsonElement french));
        Assert.False(string.IsNullOrWhiteSpace(english.GetString()));
        Assert.False(string.IsNullOrWhiteSpace(french.GetString()));
    }

    [Theory]
    [InlineData("WarnFtpBackupResidueOriginalMissing")]
    [InlineData("WarnFtpBackupResidueOriginalPresent")]
    public void NeitherMessageTellsTheUserToMoveOrDeleteAnything(string key)
    {
        // Heimdall reports and does not repair, for a measured reason: moving a residue back
        // would race another client's replacement, which is happening right now in exactly
        // the case the user most wants fixed. Handing them that move as an instruction would
        // put the same race in their hands while sounding like advice.
        string[] imperatives =
        [
            "rename ", "move ", "delete ", "remove ", "restore ",
            "renommez", "renommer", "deplacez", "supprimez", "supprimer", "restaurez",
        ];

        foreach (string language in new[] { "en.json", "fr.json" })
        {
            string lowered = (Locale(language).GetProperty(key).GetString() ?? string.Empty)
                .ToLowerInvariant();

            foreach (string imperative in imperatives)
            {
                Assert.DoesNotContain(imperative, lowered, StringComparison.Ordinal);
            }
        }
    }

    private static string Logic(string signature)
        => ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(ReadSftpSource(BrowserFile)),
            signature);

    private static string ReadSftpSource(string fileName)
    {
        string full = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.Sftp", fileName);
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }

    private static JsonElement Locale(string fileName)
    {
        string full = Path.Combine(ViewSource.RepoRoot(), "locales", fileName);
        Assert.True(File.Exists(full), $"Locale not found: {full}");
        return JsonDocument.Parse(File.ReadAllText(full)).RootElement;
    }
}
