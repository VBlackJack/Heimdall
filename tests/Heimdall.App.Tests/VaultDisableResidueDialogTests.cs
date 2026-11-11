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
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Localization;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Tests;

public sealed class VaultDisableResidueDialogTests
{
    private static async Task<LocalizationManager> EnglishAsync()
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }

    [Fact]
    public async Task Disable_Residue_ShowsNamedStores()
    {
        LocalizationManager localizer = await EnglishAsync();
        var viewModel = new VaultDisableDialogViewModel(
            _ => throw new VaultResidueException(["VaultStorePasswordPresets", "VaultStoreCommandHistory"]),
            localizer);

        await viewModel.DisableCommand.ExecuteAsync("Master1!Vault".ToCharArray());

        Assert.False(viewModel.IsCompleted);
        Assert.Contains(localizer.GetString("VaultStorePasswordPresets"), viewModel.ErrorMessage);
        Assert.Contains(localizer.GetString("VaultStoreCommandHistory"), viewModel.ErrorMessage);
        Assert.Contains("stays on", viewModel.ErrorMessage);
        Assert.NotEqual(localizer.GetString("VaultDisableError"), viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Disable_RestoreIncomplete_TellsTheUserSomeSecretsAreWindowsOnlyUntilNextUnlock()
    {
        LocalizationManager localizer = await EnglishAsync();
        var viewModel = new VaultDisableDialogViewModel(
            _ => throw new VaultResidueException(["VaultStoreCommandHistory"], protectionRestored: false),
            localizer);

        await viewModel.DisableCommand.ExecuteAsync("Master1!Vault".ToCharArray());

        Assert.Contains(localizer.GetString("VaultStoreCommandHistory"), viewModel.ErrorMessage);
        Assert.Contains(localizer.GetString("VaultDisableRestoreIncomplete"), viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Disable_StepFailedAndRestoreIncomplete_ShowsTheGenericErrorAndTheWarning()
    {
        LocalizationManager localizer = await EnglishAsync();
        var viewModel = new VaultDisableDialogViewModel(
            _ => throw new VaultResidueException([], protectionRestored: false, new IOException("x")),
            localizer);

        await viewModel.DisableCommand.ExecuteAsync("Master1!Vault".ToCharArray());

        Assert.StartsWith(localizer.GetString("VaultDisableError"), viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains(localizer.GetString("VaultDisableRestoreIncomplete"), viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Disable_FailingStore_IsNamed()
    {
        LocalizationManager localizer = await EnglishAsync();
        var viewModel = new VaultDisableDialogViewModel(
            _ => throw new VaultStoreException("VaultStoreSharedSessionPasswords", new IOException("x")),
            localizer);

        await viewModel.DisableCommand.ExecuteAsync("Master1!Vault".ToCharArray());

        Assert.Contains(localizer.GetString("VaultStoreSharedSessionPasswords"), viewModel.ErrorMessage);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task ResidueKeys_ExistInEveryLocale(string locale)
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);

        foreach (string key in new[]
        {
            "VaultDisableResidueError",
            "VaultDisableUnsavedGatewaysMessage",
            "VaultStoreConfiguration",
            "VaultStoreSharedSessionPasswords",
            "VaultStorePasswordPresets",
            "VaultStoreCommandHistory",
            "VaultDisableRestoreIncomplete",
            "VaultStoreFailureError",
            "VaultStoresUnsealedNotice",
        })
        {
            Assert.True(localizer.HasKey(key), key);
        }
    }
}
