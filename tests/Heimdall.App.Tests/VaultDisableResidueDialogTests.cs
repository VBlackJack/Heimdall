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

    [Fact]
    public async Task Disable_Incomplete_KeepsTheDialogOpenWithRetry_AndNeverCompletes()
    {
        LocalizationManager localizer = await EnglishAsync();
        var retries = 0;
        var failRetry = true;
        var viewModel = new VaultDisableDialogViewModel(
            (_, _) => throw new VaultDisableIncompleteException(),
            () =>
            {
                retries++;
                return failRetry ? throw new VaultDisableIncompleteException() : Task.CompletedTask;
            },
            localizer);

        await viewModel.DisableCommand.ExecuteAsync("Master1!Vault".ToCharArray());

        Assert.True(viewModel.IsRetryPending);
        Assert.False(viewModel.IsCompleted);
        Assert.False(viewModel.CanSubmit);
        Assert.Equal(localizer.GetString("VaultDisableIncompleteError"), viewModel.ErrorMessage);

        await viewModel.RetryRestoreCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsRetryPending);
        Assert.False(viewModel.IsCompleted);

        failRetry = false;
        await viewModel.RetryRestoreCommand.ExecuteAsync(null);

        Assert.Equal(2, retries);
        Assert.False(viewModel.IsRetryPending);
        Assert.False(viewModel.IsCompleted, "a restored vault is not a completed disable");
        Assert.Equal(localizer.GetString("VaultDisableRetryDone"), viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Disable_Unreadable_ShowsStoresAndCounts_AndOffersTheExplicitAction()
    {
        LocalizationManager localizer = await EnglishAsync();
        bool? dropRequested = null;
        var viewModel = new VaultDisableDialogViewModel(
            (_, drop) =>
            {
                dropRequested = drop;
                return drop
                    ? Task.CompletedTask
                    : throw new VaultUnreadableSecretsException(
                        [new("VaultStoreCommandHistory", 3), new("VaultStorePasswordPresets", 1)]);
            },
            () => Task.CompletedTask,
            localizer);

        await viewModel.DisableCommand.ExecuteAsync("Master1!Vault".ToCharArray());

        Assert.False(viewModel.IsCompleted);
        Assert.True(viewModel.HasUnreadable);
        Assert.Contains("3", viewModel.ErrorMessage);
        Assert.Contains(localizer.GetString("VaultStoreCommandHistory"), viewModel.ErrorMessage);
        Assert.Contains("4", viewModel.DropButtonText);
        Assert.False(dropRequested);

        await viewModel.DropUnreadableCommand.ExecuteAsync("Master1!Vault".ToCharArray());

        Assert.True(dropRequested);
        Assert.True(viewModel.IsCompleted);
    }

    [Fact]
    public async Task Enable_StoresStillUnsealed_CompletesAndNamesThem()
    {
        LocalizationManager localizer = await EnglishAsync();
        var viewModel = new Heimdall.App.ViewModels.Dialogs.VaultEnableDialogViewModel(
            _ => Task.FromResult(new VaultEnableResult(["VaultStoreCommandHistory"])),
            localizer);
        viewModel.Evaluate("StrongMaster1!Pass", "StrongMaster1!Pass");

        await viewModel.EnableCommand.ExecuteAsync("StrongMaster1!Pass".ToCharArray());

        Assert.True(viewModel.IsCompleted);
        Assert.Contains(localizer.GetString("VaultStoreCommandHistory"), viewModel.CompletionNotice);
    }

    [Fact]
    public async Task Disable_Busy_SaysNothingWasChanged()
    {
        LocalizationManager localizer = await EnglishAsync();
        var viewModel = new VaultDisableDialogViewModel(_ => throw new VaultBusyException(), localizer);

        await viewModel.DisableCommand.ExecuteAsync("Master1!Vault".ToCharArray());

        Assert.False(viewModel.IsCompleted);
        Assert.Equal(localizer.GetString("VaultDisableBusyError"), viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Disable_UnreadablePresets_AreNamedAsAllOfThem()
    {
        LocalizationManager localizer = await EnglishAsync();
        var viewModel = new VaultDisableDialogViewModel(
            (_, _) => throw new VaultUnreadableSecretsException([new("VaultStorePasswordPresets", 1)]),
            () => Task.CompletedTask,
            localizer);

        await viewModel.DisableCommand.ExecuteAsync("Master1!Vault".ToCharArray());

        Assert.Contains(localizer.GetString("VaultStorePasswordPresetsAll"), viewModel.ErrorMessage);
    }

    [Fact]
    public async Task RetryDone_DoesNotClaimNothingWasTurnedOff()
    {
        LocalizationManager localizer = await EnglishAsync();

        Assert.DoesNotContain("Nothing was turned off", localizer.GetString("VaultDisableRetryDone"));
        Assert.DoesNotContain("not close", localizer.GetString("VaultDisableIncompleteError"), StringComparison.OrdinalIgnoreCase);
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
            "VaultDisableIncompleteError",
            "VaultDisableRetryBtn",
            "VaultDisableRetryBusy",
            "VaultDisableRetryDone",
            "VaultDisableUnreadableError",
            "VaultUnreadableStoreCount",
            "VaultDisableDropUnreadableBtn",
            "VaultEnablePartialNotice",
            "VaultDisableCloseRestoreLaterBtn",
            "VaultDisableBusyError",
            "VaultLockRefusedPending",
            "SettingsVaultStatusPending",
            "VaultStorePasswordPresetsAll",
        })
        {
            Assert.True(localizer.HasKey(key), key);
        }
    }
}
