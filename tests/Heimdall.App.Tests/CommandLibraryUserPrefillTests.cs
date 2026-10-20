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

namespace Heimdall.App.Tests;

/// <summary>
/// A library opened from a session knows the user that session authenticates as, and fills a
/// user-named parameter with it the way it already fills a host-named one with the host.
/// </summary>
public sealed class CommandLibraryUserPrefillTests
{
    private const string DefaultUser = "root";

    [Theory]
    [InlineData("username")]
    [InlineData("user")]
    [InlineData("login")]
    [InlineData("account")]
    [InlineData("SamAccountName")]
    public async Task AUserNamedParameter_IsPrefilledFromTheSession(string parameterName)
    {
        using var viewModel = await CreateSelectedAsync(parameterName, username: "alice");

        Assert.Equal("alice", viewModel.Parameters.Single().Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithoutASessionUser_TheDefaultValueStays(string? username)
    {
        using var viewModel = await CreateSelectedAsync("username", username);

        Assert.Equal(DefaultUser, viewModel.Parameters.Single().Value);
    }

    // A parameter that is not about a user keeps its default even when the session has one.
    [Fact]
    public async Task AnUnrelatedParameter_IsNotPrefilledWithTheUser()
    {
        using var viewModel = await CreateSelectedAsync("path", username: "alice");

        Assert.Equal(DefaultUser, viewModel.Parameters.Single().Value);
    }

    private static async Task<CommandLibraryViewModel> CreateSelectedAsync(string parameterName, string? username)
    {
        var action = CommandLibraryTestHelpers.CreateLinuxAction(
            "action-1",
            "Show account",
            "id {" + parameterName + "}",
            CommandLibraryTestHelpers.OptionalParameter(parameterName, "Value", DefaultUser));

        var viewModel = new CommandLibraryViewModel(
            CommandLibraryTestHelpers.CreateResolverServiceProvider(action),
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            new SilentDialogService(),
            gitSyncService: null!,
            transferService: null!);

        await viewModel.InitializeAsync("srv-01", initialActionId: null, username);
        viewModel.SelectAction(viewModel.AllEntries[0]);
        return viewModel;
    }
}
