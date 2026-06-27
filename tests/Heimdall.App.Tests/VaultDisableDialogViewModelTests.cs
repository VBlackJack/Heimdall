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

using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Localization;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Tests;

public sealed class VaultDisableDialogViewModelTests
{
    private static LocalizationManager Localizer() => new LocalizationManager();

    private static char[] C(string s) => s.ToCharArray();

    [Fact]
    public async Task Disable_Success_SetsCompleted_AndZeroesPassword()
    {
        var viewModel = new VaultDisableDialogViewModel(_ => Task.CompletedTask, Localizer());
        var password = C("Master1!Vault");

        await viewModel.DisableCommand.ExecuteAsync(password);

        Assert.True(viewModel.IsCompleted);
        Assert.All(password, c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task Disable_WrongPassword_ShowsGenericUnlockError()
    {
        var viewModel = new VaultDisableDialogViewModel(_ => throw new VaultUnlockException(), Localizer());

        await viewModel.DisableCommand.ExecuteAsync(C("wrong-password"));

        Assert.False(viewModel.IsCompleted);
        Assert.Equal("VaultUnlockError", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Disable_EmptyPassword_IsNoOp()
    {
        var invoked = false;
        var viewModel = new VaultDisableDialogViewModel(_ => { invoked = true; return Task.CompletedTask; }, Localizer());

        await viewModel.DisableCommand.ExecuteAsync(Array.Empty<char>());

        Assert.False(invoked);
        Assert.False(viewModel.IsCompleted);
    }
}
