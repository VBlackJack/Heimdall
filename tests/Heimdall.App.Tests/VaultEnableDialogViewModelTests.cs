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

namespace Heimdall.App.Tests;

public sealed class VaultEnableDialogViewModelTests
{
    private const string StrongPassword = "StrongMaster1!Vault";

    private static LocalizationManager Localizer() => new LocalizationManager();

    private static char[] C(string s) => s.ToCharArray();

    [Fact]
    public void Evaluate_WeakPassword_NotAcceptable_CannotSubmit()
    {
        var viewModel = new VaultEnableDialogViewModel(_ => Task.CompletedTask, Localizer());

        viewModel.Evaluate(C("short1"), C("short1"));

        Assert.False(viewModel.IsPasswordAcceptable);
        Assert.False(viewModel.CanSubmit);
    }

    [Fact]
    public void Evaluate_StrongButConfirmMismatch_CannotSubmit()
    {
        var viewModel = new VaultEnableDialogViewModel(_ => Task.CompletedTask, Localizer());

        viewModel.Evaluate(C(StrongPassword), C("different-confirm"));

        Assert.True(viewModel.IsPasswordAcceptable);
        Assert.False(viewModel.IsConfirmMatch);
        Assert.False(viewModel.CanSubmit);
    }

    [Fact]
    public void Evaluate_StrongAndConfirmMatch_CanSubmit()
    {
        var viewModel = new VaultEnableDialogViewModel(_ => Task.CompletedTask, Localizer());

        viewModel.Evaluate(C(StrongPassword), C(StrongPassword));

        Assert.True(viewModel.CanSubmit);
    }

    [Fact]
    public async Task Enable_Success_SetsCompleted_AndZeroesPassword()
    {
        var viewModel = new VaultEnableDialogViewModel(_ => Task.CompletedTask, Localizer());
        viewModel.Evaluate(C(StrongPassword), C(StrongPassword));
        var password = C(StrongPassword);

        await viewModel.EnableCommand.ExecuteAsync(password);

        Assert.True(viewModel.IsCompleted);
        Assert.Equal("", viewModel.ErrorMessage);
        Assert.All(password, c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task Enable_WhenNotSubmittable_DoesNotInvokeService()
    {
        var invoked = false;
        var viewModel = new VaultEnableDialogViewModel(_ => { invoked = true; return Task.CompletedTask; }, Localizer());

        // No Evaluate -> not acceptable -> CanSubmit false.
        await viewModel.EnableCommand.ExecuteAsync(C(StrongPassword));

        Assert.False(invoked);
        Assert.False(viewModel.IsCompleted);
    }

    [Fact]
    public async Task Enable_ServiceThrows_ShowsError_NotCompleted()
    {
        var viewModel = new VaultEnableDialogViewModel(
            _ => throw new InvalidOperationException("boom"), Localizer());
        viewModel.Evaluate(C(StrongPassword), C(StrongPassword));

        await viewModel.EnableCommand.ExecuteAsync(C(StrongPassword));

        Assert.False(viewModel.IsCompleted);
        Assert.Equal("VaultEnableError", viewModel.ErrorMessage);
    }
}
