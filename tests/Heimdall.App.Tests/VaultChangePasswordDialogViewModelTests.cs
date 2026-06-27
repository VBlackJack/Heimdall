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

public sealed class VaultChangePasswordDialogViewModelTests
{
    private const string NewPassword = "NewMaster2!Vault";

    private static LocalizationManager Localizer() => new LocalizationManager();

    private static char[] C(string s) => s.ToCharArray();

    [Fact]
    public void Evaluate_WeakNew_CannotSubmit()
    {
        var viewModel = new VaultChangePasswordDialogViewModel((_, _) => Task.CompletedTask, Localizer());

        viewModel.Evaluate(C("weak1"), C("weak1"));

        Assert.False(viewModel.CanSubmit);
    }

    [Fact]
    public void Evaluate_StrongAndMatch_CanSubmit()
    {
        var viewModel = new VaultChangePasswordDialogViewModel((_, _) => Task.CompletedTask, Localizer());

        viewModel.Evaluate(C(NewPassword), C(NewPassword));

        Assert.True(viewModel.CanSubmit);
    }

    [Fact]
    public async Task Change_WrongCurrentPassword_ShowsGenericUnlockError()
    {
        var viewModel = new VaultChangePasswordDialogViewModel(
            (_, _) => throw new VaultUnlockException(), Localizer());
        viewModel.Evaluate(C(NewPassword), C(NewPassword));

        await viewModel.ChangeCommand.ExecuteAsync(
            new VaultChangePasswordInput(C("wrong-current"), C(NewPassword)));

        Assert.False(viewModel.IsCompleted);
        Assert.Equal("VaultUnlockError", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Change_Success_SetsCompleted_AndZeroesBothBuffers()
    {
        var viewModel = new VaultChangePasswordDialogViewModel((_, _) => Task.CompletedTask, Localizer());
        viewModel.Evaluate(C(NewPassword), C(NewPassword));
        var current = C("OldMaster1!Vault");
        var next = C(NewPassword);

        await viewModel.ChangeCommand.ExecuteAsync(new VaultChangePasswordInput(current, next));

        Assert.True(viewModel.IsCompleted);
        Assert.All(current, c => Assert.Equal('\0', c));
        Assert.All(next, c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task Change_WhenNotSubmittable_DoesNotInvokeService()
    {
        var invoked = false;
        var viewModel = new VaultChangePasswordDialogViewModel(
            (_, _) => { invoked = true; return Task.CompletedTask; }, Localizer());

        await viewModel.ChangeCommand.ExecuteAsync(
            new VaultChangePasswordInput(C("OldMaster1!Vault"), C(NewPassword)));

        Assert.False(invoked);
        Assert.False(viewModel.IsCompleted);
    }
}
