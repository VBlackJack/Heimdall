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

using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Shell;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed partial class SharedSessionConnectTests
{
    /// <summary>
    /// A session hidden by the tree's filter, and a shared session the tree does not list, are
    /// both named by the resolver the session restore dialog uses.
    /// </summary>
    [Fact]
    public async Task RestoreResolver_FindsASessionAFilterHides_AndASharedSessionOutsideTheTree()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.SeedLocalProfileAsync();
        AppSettings settings = await fixture.ConfigManager.LoadSettingsAsync();
        fixture.ViewModel.LoadServers(await fixture.ConfigManager.LoadServersAsync(), settings);
        fixture.ViewModel.SearchText = "no-profile-carries-this-text";
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (fixture.ViewModel.Servers.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Empty(fixture.ViewModel.Servers);

        IReadOnlyList<ServerItemViewModel> resolved = await ((ISessionRestoreServerResolver)fixture.ViewModel)
            .ResolveAsync([LocalProfileId, fixture.Shared.Profile.Id, "nowhere"]);

        Assert.Equal(2, resolved.Count);
        Assert.Equal("Local profile", resolved.Single(item => item.Id == LocalProfileId).DisplayName);
        Assert.Equal(fixture.Shared.Profile.DisplayName, resolved.Single(item => item.Id == fixture.Shared.Profile.Id).DisplayName);
    }
}
