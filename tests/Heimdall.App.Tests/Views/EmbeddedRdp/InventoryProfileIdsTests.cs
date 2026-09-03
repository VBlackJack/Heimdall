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
using Heimdall.App.Views.EmbeddedRdp;
using Heimdall.Core.Codecs;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// What a pane is allowed to believe about which identifiers real profiles carry.
/// </summary>
public sealed class InventoryProfileIdsTests
{
    [Fact]
    public async Task AnIdentifierTheInventoryCarriesIsReportedAsItsOwn()
    {
        Func<string, bool> isInventoryProfileId = await InventoryProfileIds.LoadPredicateAsync(
            Inventory("prod", "prod_deadbeef"));

        Assert.True(isInventoryProfileId("prod"));
        Assert.True(isInventoryProfileId("prod_deadbeef"));
    }

    [Fact]
    public async Task AKeyMintedForAPaneIsNotReportedAsAProfile()
    {
        // The other half: report every identifier as a profile and a split pane's approval is
        // filed under a key that dies with the pane, so the certificate is asked about forever.
        Func<string, bool> isInventoryProfileId = await InventoryProfileIds.LoadPredicateAsync(
            Inventory("prod"));

        Assert.False(isInventoryProfileId("prod_deadbeef"));
    }

    [Fact]
    public async Task TheComparisonIsExactRatherThanTolerantOfCase()
    {
        // The trust store keys its sets Ordinal, so a predicate that matched loosely here would
        // hand the builder an identifier the store then treats as a different profile.
        Func<string, bool> isInventoryProfileId = await InventoryProfileIds.LoadPredicateAsync(
            Inventory("prod"));

        Assert.False(isInventoryProfileId("PROD"));
    }

    [Fact]
    public async Task AnApplicationWithNoConfigurationStoreDecodesNothing()
    {
        // The fail-safe direction, and it is a direction rather than a niceness. Answering "no
        // profile has this identifier" would send every runtime identifier through the
        // inversion - the defect itself - while answering "this identifier is a profile" only
        // costs a question the user has already answered once.
        Func<string, bool> isInventoryProfileId =
            await InventoryProfileIds.LoadPredicateAsync(null);

        Assert.True(isInventoryProfileId("prod_deadbeef"));
        Assert.True(isInventoryProfileId("anything-at-all"));
    }

    [Fact]
    public async Task AnInventoryThatCannotBeReadDecodesNothingEither()
    {
        Func<string, bool> isInventoryProfileId = await InventoryProfileIds.LoadPredicateAsync(
            new UnreadableInventory());

        Assert.True(isInventoryProfileId("prod_deadbeef"));
    }

    [Fact]
    public async Task AnInventoryThatLoadsAndHoldsNoProfileDecodesNothingEither()
    {
        // The read succeeds here, so the loader cannot fall back on "the answer is unknown".
        // It answers the same way for a reason of its own: with no profile in the inventory
        // there is nothing for an approval to be filed under, so inverting the mint buys
        // nothing, while the trust store persists under whatever key it is handed - an approval
        // decoded to "prod" now is waiting in the settings file for a profile called "prod" to
        // arrive and connect on a certificate nobody was asked about for it.
        //
        // Reachable rather than defensive: ConfigManager returns an empty inventory document
        // without throwing when servers.json is absent, which is what an external restore or a
        // relocated configuration directory leaves behind while a pane is still open.
        Func<string, bool> isInventoryProfileId = await InventoryProfileIds.LoadPredicateAsync(
            Inventory());

        Assert.True(isInventoryProfileId("prod_deadbeef"));

        // Stated as the decision the predicate is fed into, so this fails on what the empty
        // inventory costs rather than on the boolean that carries it there.
        Assert.Equal(
            "prod_deadbeef",
            SessionIdCodec.ResolveInventoryId("prod_deadbeef", isInventoryProfileId));
    }

    [Fact]
    public async Task AnInventoryOfNothingButBlankIdentifiersIsEmptyToo()
    {
        // The count that decides is taken after the blanks are dropped, which is the only
        // reading that matches what the set could ever match: entries carrying no identifier
        // are not profiles this predicate can recognise.
        Func<string, bool> isInventoryProfileId = await InventoryProfileIds.LoadPredicateAsync(
            Inventory("   ", ""));

        Assert.True(isInventoryProfileId("prod_deadbeef"));
    }

    [Fact]
    public async Task AProfileWithNoIdentifierIsNotAWildcard()
    {
        // A blank identifier in the file would otherwise put string.Empty in the set, and an
        // empty-string question is asked by nothing sensible - but a blank entry surviving the
        // load is the kind of thing that makes a later membership test mean something else.
        Func<string, bool> isInventoryProfileId = await InventoryProfileIds.LoadPredicateAsync(
            Inventory("prod", "   ", ""));

        Assert.False(isInventoryProfileId(string.Empty));
        Assert.False(isInventoryProfileId("   "));
        Assert.True(isInventoryProfileId("prod"));
    }

    private static IConfigManager Inventory(params string[] profileIds) =>
        new StubInventory(profileIds);

    /// <summary>An inventory of identifiers, which is all this loader is allowed to read.</summary>
    private sealed class StubInventory(IEnumerable<string> profileIds) : IConfigManager
    {
        private readonly List<ServerProfileDto> _servers = profileIds
            .Select(id => new ServerProfileDto { Id = id })
            .ToList();

        public event Action<AppSettings>? SettingsChanged;

        public string ConfigPath => "mem://config";

        public string SettingsPath => "mem://settings.json";

        public string ServersPath => "mem://servers.json";

        public Task<List<ServerProfileDto>> LoadServersAsync() => Task.FromResult(_servers);

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(new AppSettings());

        public Task SaveSettingsAsync(AppSettings settings)
        {
            SettingsChanged?.Invoke(settings);
            return Task.CompletedTask;
        }

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) =>
            Task.FromResult(false);

        public Task<int> MergeTrustedHostKeysAsync(
            IEnumerable<KeyValuePair<string, string>> entries) => Task.FromResult(0);

        public Task MergeSettingAsync(Action<AppSettings> mutate) => Task.CompletedTask;

        public Task<TResult> MutateServersAsync<TResult>(
            Func<List<ServerProfileDto>, TResult> mutate) =>
            Task.FromResult(mutate(_servers));

        public Task SaveServersAsync(List<ServerProfileDto> servers) => Task.CompletedTask;
    }

    /// <summary>The inventory file locked, which is a state the loader has to have an answer for.</summary>
    private sealed class UnreadableInventory : IConfigManager
    {
        public event Action<AppSettings>? SettingsChanged;

        public string ConfigPath => "mem://config";

        public string SettingsPath => "mem://settings.json";

        public string ServersPath => "mem://servers.json";

        public Task<List<ServerProfileDto>> LoadServersAsync() =>
            throw new IOException("The inventory file is locked.");

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(new AppSettings());

        public Task SaveSettingsAsync(AppSettings settings)
        {
            SettingsChanged?.Invoke(settings);
            return Task.CompletedTask;
        }

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) =>
            Task.FromResult(false);

        public Task<int> MergeTrustedHostKeysAsync(
            IEnumerable<KeyValuePair<string, string>> entries) => Task.FromResult(0);

        public Task MergeSettingAsync(Action<AppSettings> mutate) => Task.CompletedTask;

        public Task<TResult> MutateServersAsync<TResult>(
            Func<List<ServerProfileDto>, TResult> mutate) =>
            Task.FromResult(mutate([]));

        public Task SaveServersAsync(List<ServerProfileDto> servers) => Task.CompletedTask;
    }
}
