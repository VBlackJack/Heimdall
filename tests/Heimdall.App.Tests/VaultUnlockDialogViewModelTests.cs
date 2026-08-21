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
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Tests;

public sealed class VaultUnlockDialogViewModelTests
{
    // An empty LocalizationManager returns the key itself, so assertions compare
    // against the locale keys without loading the locale files.
    private static LocalizationManager Localizer() => new LocalizationManager();

    private static char[] Pw(string s) => s.ToCharArray();

    [Fact]
    public async Task Unlock_Success_SetsVerified_AndZeroesPassword()
    {
        var viewModel = new VaultUnlockDialogViewModel(
            _ => Task.CompletedTask, new PinManager(), Localizer(), migrationInProgress: false);
        var password = Pw("MasterPass1!");

        await viewModel.UnlockCommand.ExecuteAsync(password);

        Assert.True(viewModel.IsVerified);
        Assert.Equal("", viewModel.ErrorMessage);
        Assert.All(password, c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task Unlock_WrongPassword_GenericError_IncrementsFailure_ZeroesPassword()
    {
        var lockout = new PinManager(maxAttempts: 5, lockoutDuration: TimeSpan.FromMinutes(5));
        var viewModel = new VaultUnlockDialogViewModel(
            _ => throw new VaultUnlockException(), lockout, Localizer(), migrationInProgress: false);
        var password = Pw("WrongPass1!");

        await viewModel.UnlockCommand.ExecuteAsync(password);

        Assert.False(viewModel.IsVerified);
        Assert.Equal("VaultUnlockError", viewModel.ErrorMessage);
        Assert.Equal(1, lockout.FailureCount);
        Assert.All(password, c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task Unlock_ReachesMaxFailures_LocksOut()
    {
        var lockout = new PinManager(maxAttempts: 2, lockoutDuration: TimeSpan.FromMinutes(5));
        var viewModel = new VaultUnlockDialogViewModel(
            _ => throw new VaultUnlockException(), lockout, Localizer(), migrationInProgress: false);

        await viewModel.UnlockCommand.ExecuteAsync(Pw("bad-attempt-1!"));
        Assert.False(viewModel.IsLockedOut);

        await viewModel.UnlockCommand.ExecuteAsync(Pw("bad-attempt-2!"));

        Assert.True(viewModel.IsLockedOut);
        Assert.False(viewModel.IsVerified);
        Assert.False(viewModel.CanSubmit);
    }

    [Fact]
    public async Task Unlock_WhenLockedOut_DoesNotInvokeUnlock()
    {
        var lockout = new PinManager(maxAttempts: 1, lockoutDuration: TimeSpan.FromMinutes(5));
        lockout.RegisterFailure(); // already locked out

        var invoked = false;
        var viewModel = new VaultUnlockDialogViewModel(
            _ => { invoked = true; return Task.CompletedTask; }, lockout, Localizer(), migrationInProgress: false);

        await viewModel.UnlockCommand.ExecuteAsync(Pw("anything-1!"));

        Assert.False(invoked);
        Assert.True(viewModel.IsLockedOut);
        Assert.False(viewModel.IsVerified);
    }

    [Fact]
    public async Task Unlock_UnexpectedException_GenericError_NoFailureIncrement()
    {
        var lockout = new PinManager(maxAttempts: 5, lockoutDuration: TimeSpan.FromMinutes(5));
        var viewModel = new VaultUnlockDialogViewModel(
            _ => throw new InvalidOperationException("io"), lockout, Localizer(), migrationInProgress: false);

        await viewModel.UnlockCommand.ExecuteAsync(Pw("MasterPass1!"));

        Assert.False(viewModel.IsVerified);
        Assert.Equal("VaultUnlockError", viewModel.ErrorMessage);
        Assert.Equal(0, lockout.FailureCount); // a transient error is not a wrong-password attempt
    }

    [Fact]
    public async Task Unlock_EmptyPassword_IsNoOp()
    {
        var invoked = false;
        var viewModel = new VaultUnlockDialogViewModel(
            _ => { invoked = true; return Task.CompletedTask; }, new PinManager(), Localizer(), migrationInProgress: false);

        await viewModel.UnlockCommand.ExecuteAsync(Array.Empty<char>());

        Assert.False(invoked);
        Assert.False(viewModel.IsVerified);
    }

    [Fact]
    public async Task Unlock_MigrationInProgress_ShowsMigrationBusyMessageDuringAwait()
    {
        string? busyDuringCall = null;
        VaultUnlockDialogViewModel viewModel = null!;
        viewModel = new VaultUnlockDialogViewModel(
            _ => { busyDuringCall = viewModel.BusyMessage; return Task.CompletedTask; },
            new PinManager(), Localizer(), migrationInProgress: true);

        await viewModel.UnlockCommand.ExecuteAsync(Pw("MasterPass1!"));

        Assert.Equal("VaultUnlockMigrationStatus", busyDuringCall);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Unlock_NoMigration_ShowsUnlockBusyMessageDuringAwait()
    {
        string? busyDuringCall = null;
        VaultUnlockDialogViewModel viewModel = null!;
        viewModel = new VaultUnlockDialogViewModel(
            _ => { busyDuringCall = viewModel.BusyMessage; return Task.CompletedTask; },
            new PinManager(), Localizer(), migrationInProgress: false);

        await viewModel.UnlockCommand.ExecuteAsync(Pw("MasterPass1!"));

        Assert.Equal("VaultUnlockBusy", busyDuringCall);
    }

    [Fact]
    public void Constructor_ShowHelloFalse_DisablesHelloCommand()
    {
        var viewModel = new VaultUnlockDialogViewModel(
            _ => Task.CompletedTask,
            new PinManager(),
            Localizer(),
            migrationInProgress: false,
            () => Task.FromResult(VaultHelloUnlockResult.Success),
            showHelloUnlock: false);

        Assert.False(viewModel.ShowHelloUnlock);
        Assert.False(viewModel.CanUnlockWithHello);
    }

    [Fact]
    public async Task UnlockWithHello_Success_VerifiesWithoutMasterPassword()
    {
        var viewModel = new VaultUnlockDialogViewModel(
            _ => throw new InvalidOperationException("master path should not run"),
            new PinManager(),
            Localizer(),
            migrationInProgress: false,
            () => Task.FromResult(VaultHelloUnlockResult.Success),
            showHelloUnlock: true);

        await viewModel.UnlockWithHelloCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsVerified);
        Assert.False(viewModel.IsMasterPasswordVerified);
        Assert.Equal("", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task UnlockWithHello_NotFound_SetsReenrollGateButDoesNotReenroll()
    {
        var confirmCalls = 0;
        var enrollCalls = 0;
        var viewModel = new VaultUnlockDialogViewModel(
            _ => Task.CompletedTask,
            new PinManager(),
            Localizer(),
            migrationInProgress: false,
            () => Task.FromResult(VaultHelloUnlockResult.Failure(VaultHelloFailureReason.NotFound)),
            showHelloUnlock: true,
            () =>
            {
                confirmCalls++;
                return Task.FromResult(true);
            },
            () =>
            {
                enrollCalls++;
                return Task.CompletedTask;
            });

        await viewModel.UnlockWithHelloCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsVerified);
        Assert.True(viewModel.HasPendingHelloReenroll);
        Assert.Equal("VaultHelloUnlockNotFound", viewModel.ErrorMessage);
        Assert.Equal(0, confirmCalls);
        Assert.Equal(0, enrollCalls);
    }

    [Fact]
    public async Task Unlock_MasterPasswordSuccessAfterHelloNotFound_OffersReenroll()
    {
        var confirmCalls = 0;
        var enrollCalls = 0;
        var viewModel = new VaultUnlockDialogViewModel(
            _ => Task.CompletedTask,
            new PinManager(),
            Localizer(),
            migrationInProgress: false,
            () => Task.FromResult(VaultHelloUnlockResult.Failure(VaultHelloFailureReason.NotFound)),
            showHelloUnlock: true,
            () =>
            {
                confirmCalls++;
                return Task.FromResult(true);
            },
            () =>
            {
                enrollCalls++;
                return Task.CompletedTask;
            });

        await viewModel.UnlockWithHelloCommand.ExecuteAsync(null);
        await viewModel.UnlockCommand.ExecuteAsync(Pw("MasterPass1!"));

        Assert.True(viewModel.IsVerified);
        Assert.True(viewModel.IsMasterPasswordVerified);
        Assert.False(viewModel.HasPendingHelloReenroll);
        Assert.Equal(1, confirmCalls);
        Assert.Equal(1, enrollCalls);
    }

    [Fact]
    public async Task Unlock_MasterPasswordSuccessWithoutBrokenHello_DoesNotOfferReenroll()
    {
        var confirmCalls = 0;
        var enrollCalls = 0;
        var viewModel = new VaultUnlockDialogViewModel(
            _ => Task.CompletedTask,
            new PinManager(),
            Localizer(),
            migrationInProgress: false,
            () => Task.FromResult(VaultHelloUnlockResult.Failure(VaultHelloFailureReason.NotFound)),
            showHelloUnlock: true,
            () =>
            {
                confirmCalls++;
                return Task.FromResult(true);
            },
            () =>
            {
                enrollCalls++;
                return Task.CompletedTask;
            });

        await viewModel.UnlockCommand.ExecuteAsync(Pw("MasterPass1!"));

        Assert.True(viewModel.IsVerified);
        Assert.True(viewModel.IsMasterPasswordVerified);
        Assert.Equal(0, confirmCalls);
        Assert.Equal(0, enrollCalls);
    }

    [Fact]
    public async Task Unlock_WhenLockedOut_SchedulesItsOwnReopen()
    {
        var lockout = new PinManager(maxAttempts: 1, lockoutDuration: TimeSpan.FromMinutes(5));
        var viewModel = new VaultUnlockDialogViewModel(
            _ => throw new VaultUnlockException(), lockout, Localizer(), migrationInProgress: false);

        await viewModel.UnlockCommand.ExecuteAsync(Pw("bad-attempt-1!"));

        Assert.True(viewModel.IsLockedOut);

        // The password box, the unlock button and the Hello button are all bound to
        // CanSubmit and go dead together, so no user action can reach the view-model
        // while the gate is shut. If the gate does not re-check itself, the expiry
        // PinManager already applies is never observed and the only way out is to
        // kill the process.
        Assert.True(viewModel.IsLockoutRefreshScheduled);
    }

    [Fact]
    public async Task Unlock_WhenLockoutExpires_ReopensTheGateAndStopsRefreshing()
    {
        var lockout = new PinManager(maxAttempts: 1, lockoutDuration: TimeSpan.FromMinutes(5));
        var viewModel = new VaultUnlockDialogViewModel(
            _ => throw new VaultUnlockException(), lockout, Localizer(), migrationInProgress: false);

        await viewModel.UnlockCommand.ExecuteAsync(Pw("bad-attempt-1!"));
        Assert.False(viewModel.CanSubmit);

        // Expire the lockout without waiting on a clock: restoring a past expiry is
        // exactly what PinManager does when it reconciles persisted state.
        lockout.RestoreLockoutState(failureCount: 1, lockoutUntilUtc: DateTime.UtcNow.AddMinutes(-1));
        viewModel.RefreshLockoutState();

        Assert.False(viewModel.IsLockedOut);
        Assert.True(viewModel.CanSubmit);
        Assert.Equal("", viewModel.LockoutMessage);
        Assert.False(viewModel.IsLockoutRefreshScheduled);
    }

    [Fact]
    public void Constructor_NotLockedOut_DoesNotRefresh()
    {
        var viewModel = new VaultUnlockDialogViewModel(
            _ => Task.CompletedTask, new PinManager(), Localizer(), migrationInProgress: false);

        Assert.False(viewModel.IsLockedOut);
        Assert.False(viewModel.IsLockoutRefreshScheduled);
    }
}
