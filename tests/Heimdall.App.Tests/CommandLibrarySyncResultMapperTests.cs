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

using FluentAssertions;
using Heimdall.App.Services.Sync;
using TwinShell.Core.Interfaces;

namespace Heimdall.App.Tests;

/// <summary>
/// Producer-first tests for <see cref="CommandLibrarySyncResultMapper.Map"/>,
/// the pure result-to-dialog mapper extracted from
/// <c>CommandLibraryViewModel.SyncAsync</c>
/// (src/Heimdall.App/ViewModels/CommandLibraryViewModel.Commands.cs, SyncAsync
/// result branch). The fake localizer is the identity function so fallback
/// assertions check the i18n KEY and runtime-text assertions check the raw string.
/// </summary>
public sealed class CommandLibrarySyncResultMapperTests
{
    private static readonly Func<string, string> Localize = key => key;

    [Fact]
    public void Map_Cancelled_WithMessage_IsInfoWithMessageBody()
    {
        var result = new GitOperationResult
        {
            Success = false,
            ErrorCode = GitSyncErrorCode.Cancelled,
            Message = "Sync was cancelled by the user."
        };

        var presentation = CommandLibrarySyncResultMapper.Map(result, Localize);

        presentation.Kind.Should().Be(SyncDialogKind.Info);
        presentation.Title.Should().Be("ToolCmdLibSyncCancelled");
        presentation.Body.Should().Be("Sync was cancelled by the user.");
    }

    [Fact]
    public void Map_Cancelled_WithWhitespaceMessage_FallsBackToCancelledMessageKey()
    {
        var result = new GitOperationResult
        {
            Success = false,
            ErrorCode = GitSyncErrorCode.Cancelled,
            Message = "   "
        };

        var presentation = CommandLibrarySyncResultMapper.Map(result, Localize);

        presentation.Kind.Should().Be(SyncDialogKind.Info);
        presentation.Title.Should().Be("ToolCmdLibSyncCancelled");
        presentation.Body.Should().Be("ToolCmdLibSyncCancelledMessage");
    }

    [Fact]
    public void Map_Success_NoWarnings_WithMessage_IsInfoWithMessageBody()
    {
        var result = new GitOperationResult
        {
            Success = true,
            Message = "Synced 3 actions."
        };

        var presentation = CommandLibrarySyncResultMapper.Map(result, Localize);

        presentation.Kind.Should().Be(SyncDialogKind.Info);
        presentation.Title.Should().Be("ToolCmdLibSyncComplete");
        presentation.Body.Should().Be("Synced 3 actions.");
    }

    [Fact]
    public void Map_Success_NoWarnings_NullMessage_FallsBackToCompleteKey()
    {
        var result = new GitOperationResult
        {
            Success = true,
            Message = null!
        };

        var presentation = CommandLibrarySyncResultMapper.Map(result, Localize);

        presentation.Kind.Should().Be(SyncDialogKind.Info);
        presentation.Title.Should().Be("ToolCmdLibSyncComplete");
        presentation.Body.Should().Be("ToolCmdLibSyncComplete");
    }

    [Fact]
    public void Map_Success_WithWarnings_WithMessage_IsWarningWithMessageBody()
    {
        var result = new GitOperationResult
        {
            Success = true,
            Message = "Synced with 1 warning.",
            Warnings = new List<string> { "Skipped a conflicting entry." }
        };

        var presentation = CommandLibrarySyncResultMapper.Map(result, Localize);

        presentation.Kind.Should().Be(SyncDialogKind.Warning);
        presentation.Title.Should().Be("ToolCmdLibSyncPartial");
        presentation.Body.Should().Be("Synced with 1 warning.");
    }

    [Fact]
    public void Map_Success_WithWarnings_NullMessage_FallsBackToCompleteKey()
    {
        var result = new GitOperationResult
        {
            Success = true,
            Message = null!,
            Warnings = new List<string> { "Skipped a conflicting entry." }
        };

        var presentation = CommandLibrarySyncResultMapper.Map(result, Localize);

        presentation.Kind.Should().Be(SyncDialogKind.Warning);
        presentation.Title.Should().Be("ToolCmdLibSyncPartial");
        presentation.Body.Should().Be("ToolCmdLibSyncComplete");
    }

    [Fact]
    public void Map_Failure_WithErrorDetails_IsErrorWithDetailsBody()
    {
        var result = new GitOperationResult
        {
            Success = false,
            ErrorCode = GitSyncErrorCode.NetworkError,
            Message = "Sync failed.",
            ErrorDetails = "Connection timed out after 30s."
        };

        var presentation = CommandLibrarySyncResultMapper.Map(result, Localize);

        presentation.Kind.Should().Be(SyncDialogKind.Error);
        presentation.Title.Should().Be("ToolCmdLibSyncError");
        presentation.Body.Should().Be("Connection timed out after 30s.");
    }

    [Fact]
    public void Map_Failure_NullErrorDetails_WithMessage_FallsBackToMessage()
    {
        var result = new GitOperationResult
        {
            Success = false,
            ErrorCode = GitSyncErrorCode.Unknown,
            Message = "Sync failed.",
            ErrorDetails = null
        };

        var presentation = CommandLibrarySyncResultMapper.Map(result, Localize);

        presentation.Kind.Should().Be(SyncDialogKind.Error);
        presentation.Title.Should().Be("ToolCmdLibSyncError");
        presentation.Body.Should().Be("Sync failed.");
    }

    [Fact]
    public void Map_Failure_NullErrorDetails_NullMessage_FallsBackToErrorKey()
    {
        var result = new GitOperationResult
        {
            Success = false,
            ErrorCode = GitSyncErrorCode.Unknown,
            Message = null!,
            ErrorDetails = null
        };

        var presentation = CommandLibrarySyncResultMapper.Map(result, Localize);

        presentation.Kind.Should().Be(SyncDialogKind.Error);
        presentation.Title.Should().Be("ToolCmdLibSyncError");
        presentation.Body.Should().Be("ToolCmdLibSyncError");
    }
}
