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
using Heimdall.App.ViewModels.Dialogs;

namespace Heimdall.App.Tests;

/// <summary>
/// The RDP username hint must not promise something the Domain box below it does not do.
/// </summary>
/// <remarks>
/// The hint used to end "Heimdall splits the domain automatically before connecting", which was
/// wrong: the down-level name is deliberately kept whole, because splitting it breaks NLA on some
/// hosts. Removing that sentence was right. Its replacement, however, said a domain typed into the
/// username "is also read into the Domain field below" - and named a control the user can see and
/// watch stay empty. One false promise about the product had been swapped for a false promise
/// about the interface, which is the harder of the two to catch: the first is refuted by a
/// connection, the second only by someone looking at the box.
///
/// Both halves are pinned here. The behavioural one is the real invariant; the wording assertion
/// exists because the wording is what a user reads, and nothing else in the suite reads it.
/// </remarks>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerDialogRdpDomainHintTruthTests
{
    [Theory]
    [InlineData(@"CORP\jdoe")]
    [InlineData("jdoe@corp.example.com")]
    [InlineData("jdoe")]
    public void TypingADomainIntoTheUsername_DoesNotFillTheDomainBox(string typed)
    {
        ServerDialogViewModel vm = new()
        {
            DisplayName = "RDP host",
            RemoteServer = "host.example.com",
            ConnectionType = "RDP",
            RdpUsername = typed,
        };

        Assert.True(
            string.IsNullOrEmpty(vm.RdpDomain),
            $"RdpDomain became '{vm.RdpDomain}' after typing '{typed}' into the username. If the "
            + "Domain box is now filled from the username, that is a behaviour change, and the "
            + "hint below must be reworded to match it rather than the other way round.");
    }

    // The derivation that does exist happens at connect time and feeds the ActiveX credential
    // parameters. It never touches this dialog's Domain box, so the hint must not point at it.
    [Theory]
    [InlineData("en", "Domain field")]
    [InlineData("fr", "champ Domaine")]
    public void TheUsernameHint_DoesNotClaimTheDomainBoxIsFilledForYou(string locale, string boxReference)
    {
        string hint = ReadLocale(locale)
            .RootElement.GetProperty("ServerDialogRdpUsernameHint").GetString()!;

        Assert.False(
            hint.Contains(boxReference, StringComparison.OrdinalIgnoreCase),
            $"The {locale} RDP username hint points the user at the {boxReference}, which nothing "
            + "in the product writes to. Say what actually happens - the domain is used when "
            + "connecting - or make the box fill itself and update the test above.");
    }

    private static JsonDocument ReadLocale(string locale) =>
        JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "locales", $"{locale}.json")));

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
