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
///
/// That wording half used to be a single assert-absence on the literal phrase "Domain field", so
/// any reworded version of the same false promise - "also copied into the box below" - was green.
/// It now states what the hint must say (where the domain is used) and rejects the promise by its
/// shape rather than by one of its spellings, with the reworded promise itself as the positive
/// control that the oracle can still fire.
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
    // parameters, so that is what the hint has to say. Asserting the true statement is present
    // beats asserting one false one is absent: a rewrite that drops the connect-time claim fails
    // here whatever it puts in its place.
    [Theory]
    [InlineData("en", "used when connecting")]
    [InlineData("fr", "utilisé à la connexion")]
    public void TheUsernameHint_SaysWhereADomainTypedHereIsUsed(string locale, string connectTimeClaim)
    {
        string hint = UsernameHint(locale);

        Assert.Contains(connectTimeClaim, hint, StringComparison.OrdinalIgnoreCase);
    }

    // The failure this file exists for is a promise about the interface: the hint pointing at a
    // control the user can watch stay empty. The oracle looks for that shape - a reference to a
    // place in the dialog paired with a verb of filling - rather than for one spelling of it.
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public void TheUsernameHint_DoesNotClaimTheDomainBoxIsFilledForYou(string locale)
    {
        string hint = UsernameHint(locale);

        Assert.False(
            PromisesTheBoxIsFilled(hint, locale),
            $"The {locale} RDP username hint tells the user something is written into a box of "
            + "this dialog, and nothing in the product writes to the Domain box. Say what "
            + "actually happens - the domain is used when connecting - or make the box fill "
            + "itself and update the behavioural test above.");
    }

    // The positive control. Without it the assertion above is an assert-absence that would also
    // pass on an empty string, on a missing key, or on any detector that never fires - which is
    // exactly how the phrase-matching version it replaces stayed green through a reworded
    // promise.
    [Theory]
    [InlineData("en", "A domain typed here is also copied into the box below.")]
    [InlineData("en", "The domain is read into the Domain field for you.")]
    [InlineData("fr", "Un domaine saisi ici est aussi copié dans le champ ci-dessous.")]
    [InlineData("fr", "Le domaine est reporté dans le champ Domaine.")]
    public void TheOracle_RejectsARewordedPromiseAboutTheBox(string locale, string rewordedPromise)
    {
        Assert.True(
            PromisesTheBoxIsFilled(rewordedPromise, locale),
            $"'{rewordedPromise}' promises the box is filled and the oracle let it through, so "
            + "the assertion above cannot fail and proves nothing.");
    }

    // A reference to somewhere in this dialog, next to a verb of putting something there. Either
    // half alone is innocent: the hint legitimately names the username formats, and "connecting"
    // is a verb about the far end, not about a control.
    private static bool PromisesTheBoxIsFilled(string hint, string locale)
    {
        (string[] Places, string[] Fillings) vocabulary = locale switch
        {
            "fr" => (
                ["champ", "case", "ci-dessous", "zone de saisie"],
                ["copi", "report", "rempl", "renseign", "pré-rempl", "recopi", "inscrit"]),
            _ => (
                ["field", "box", "below"],
                ["copied", "filled", "populated", "written into", "read into", "carried into"])
        };

        return vocabulary.Places.Any(place => hint.Contains(place, StringComparison.OrdinalIgnoreCase))
            && vocabulary.Fillings.Any(filling => hint.Contains(filling, StringComparison.OrdinalIgnoreCase));
    }

    private static string UsernameHint(string locale)
    {
        JsonElement root = ReadLocale(locale).RootElement;

        Assert.True(
            root.TryGetProperty("ServerDialogRdpUsernameHint", out JsonElement hint),
            $"'ServerDialogRdpUsernameHint' is missing from {locale}.json; the hint the user reads "
            + "under the RDP username box has no text at all.");

        return hint.GetString()!;
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
