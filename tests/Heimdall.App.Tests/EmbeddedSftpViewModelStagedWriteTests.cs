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
using System.Reflection;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

/// <summary>
/// How the application names each stage a privileged write can fail at. Only a refused password
/// is reported as a rejected password.
/// </summary>
public sealed class EmbeddedSftpViewModelStagedWriteTests
{
    private const string SentinelLine = PrivilegedFileCommands.InstallStartedMarker + "\n";

    [Theory]
    [InlineData(1, "sudo: no password was provided\nsudo: 1 incorrect password attempt", nameof(SudoFailureKind.PasswordRejected))]
    [InlineData(1, "sudo: a password is required", nameof(SudoFailureKind.PasswordUnavailable))]
    [InlineData(1, "sudo: sorry, you must have a tty to run sudo", nameof(SudoFailureKind.PasswordUnavailable))]
    public void ThrowIfWriteStageFailed_PasswordFailures_KeepTheAuthenticationError(
        int exitStatus,
        string stderr,
        string expectedKind)
    {
        SudoAuthenticationException error = Assert.Throws<SudoAuthenticationException>(
            () => EmbeddedSftpViewModel.ThrowIfWriteStageFailed(
                new PrivilegedCommandResult(exitStatus, string.Empty, stderr)));

        Assert.Equal(Enum.Parse<SudoFailureKind>(expectedKind), error.Kind);
    }

    [Theory]
    [InlineData(1, "alice is not in the sudoers file.  This incident will be reported.", nameof(PrivilegedWriteFailureKind.NotPermitted))]
    [InlineData(127, "sh: 1: sudo: not found", nameof(PrivilegedWriteFailureKind.SudoMissing))]
    [InlineData(74, "mktemp: failed to create directory: No space left on device", nameof(PrivilegedWriteFailureKind.StagingFailed))]
    public void ThrowIfWriteStageFailed_OtherStages_KeepTheirOwnKind(
        int exitStatus,
        string stderr,
        string expectedKind)
    {
        PrivilegedWriteFailedException error = Assert.Throws<PrivilegedWriteFailedException>(
            () => EmbeddedSftpViewModel.ThrowIfWriteStageFailed(
                new PrivilegedCommandResult(exitStatus, string.Empty, stderr)));

        Assert.Equal(Enum.Parse<PrivilegedWriteFailureKind>(expectedKind), error.Kind);
    }

    [Fact]
    public void ThrowIfWriteStageFailed_UnrecognisedStageFailure_IsAGenericFailureShowingStderr()
    {
        InvalidOperationException error = Assert.ThrowsAny<InvalidOperationException>(
            () => EmbeddedSftpViewModel.ThrowIfWriteStageFailed(
                new PrivilegedCommandResult(1, string.Empty, "sudo: something unexpected")));

        Assert.IsNotType<PrivilegedWriteFailedException>(error);
        Assert.Contains("something unexpected", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfWriteStageFailed_InstallerStatuses_AreLeftToTheInstallerHandling()
    {
        // The installer announced itself, so its statuses are its own: 78 is a taken destination.
        EmbeddedSftpViewModel.ThrowIfWriteStageFailed(new PrivilegedCommandResult(
            PrivilegedFileCommands.DestinationExistsExitStatus,
            string.Empty,
            SentinelLine + "Refusing existing destination."));
        EmbeddedSftpViewModel.ThrowIfWriteStageFailed(new PrivilegedCommandResult(0, string.Empty, SentinelLine));
    }

    [Fact]
    public void EnsureSudoSucceeded_StatusWithoutAnyMessage_IsNoLongerARejectedPassword()
    {
        // The retired status 79 must not be given a meaning by a leftover branch.
        Assert.Throws<InvalidOperationException>(
            () => EmbeddedSftpViewModel.EnsureSudoSucceeded(79, string.Empty, "atomic write"));
    }

    [Theory]
    [InlineData(nameof(PrivilegedWriteFailureKind.PasswordRejected), "ErrorSudoPasswordRejected")]
    [InlineData(nameof(PrivilegedWriteFailureKind.PasswordUnavailable), "ErrorSudoPasswordUnavailable")]
    [InlineData(nameof(PrivilegedWriteFailureKind.NotPermitted), "ErrorSudoNotPermitted")]
    [InlineData(nameof(PrivilegedWriteFailureKind.SudoMissing), "ErrorSudoMissing")]
    [InlineData(nameof(PrivilegedWriteFailureKind.StagingFailed), "SftpErrorSudoStagingFailed")]
    public async Task DescribeTransferError_EachStageFailure_HasItsOwnLocalizedMessage(
        string kindName,
        string expectedKey)
    {
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        EmbeddedSftpViewModel viewModel = new(new FakeUiDispatcher());
        SetLocalizer(viewModel, localizer);
        var kind = Enum.Parse<PrivilegedWriteFailureKind>(kindName);

        string message = viewModel.DescribeTransferError(
            new PrivilegedWriteFailedException(kind, "raw stderr that must stay out of the UI"));

        Assert.Equal(localizer[expectedKey], message);
        Assert.NotEqual(expectedKey, message);
        Assert.DoesNotContain("raw stderr", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeTransferError_OnlyThePasswordRefusalIsReportedAsARejectedPassword()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        EmbeddedSftpViewModel viewModel = new(new FakeUiDispatcher());
        SetLocalizer(viewModel, localizer);
        string rejected = localizer["ErrorSudoPasswordRejected"];

        foreach (PrivilegedWriteFailureKind kind in new[]
                 {
                     PrivilegedWriteFailureKind.PasswordUnavailable,
                     PrivilegedWriteFailureKind.NotPermitted,
                     PrivilegedWriteFailureKind.SudoMissing,
                     PrivilegedWriteFailureKind.StagingFailed,
                     PrivilegedWriteFailureKind.SudoFailed,
                 })
        {
            Assert.NotEqual(rejected, viewModel.DescribeTransferError(new PrivilegedWriteFailedException(kind, "x")));
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task NewStageMessages_ExistInEveryLocale(string locale)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);

        foreach (string key in new[] { "ErrorSudoNotPermitted", "ErrorSudoMissing", "SftpErrorSudoStagingFailed" })
        {
            Assert.NotEqual(key, localizer[key]);
            Assert.False(string.IsNullOrWhiteSpace(localizer[key]), key);
        }
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        LocalizationManager manager = new();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    private static void SetLocalizer(EmbeddedSftpViewModel viewModel, LocalizationManager localizer)
    {
        FieldInfo? field = typeof(EmbeddedSftpViewModel).GetField(
            "_localizer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(viewModel, localizer);
    }
}
