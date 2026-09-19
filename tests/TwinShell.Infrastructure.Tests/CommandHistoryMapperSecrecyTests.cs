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

using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Core.Security;
using TwinShell.Persistence.Entities;
using TwinShell.Persistence.Mappers;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// Pins what actually reaches the database for a command-history row.
/// </summary>
/// <remarks>
/// <c>HistorySecretEnvelope</c> has its own tests, but they only prove the envelope works;
/// they cannot show that the mapper uses it, or that it uses it on both fields. This is the
/// wiring, and the wiring is what was missing for as long as history stored a pattern
/// instead of a command.
/// </remarks>
public sealed class CommandHistoryMapperSecrecyTests
{
    private const string Command = "mysql -u root -phunter2";
    private const string Password = "hunter2";

    [Fact]
    public void TheStoredCommandDoesNotContainWhatTheUserTyped()
    {
        var entity = CommandHistoryMapper.ToEntity(SampleHistory(), new ReversibleProtector());

        Assert.DoesNotContain(Password, entity.GeneratedCommand, StringComparison.Ordinal);
        Assert.True(HistorySecretEnvelope.IsSealed(entity.GeneratedCommand));
    }

    /// <summary>
    /// The parameter values are the other half, and the half a fix is more likely to miss:
    /// they are the raw thing the user typed, before any escaping.
    /// </summary>
    [Fact]
    public void TheStoredParametersDoNotContainWhatTheUserTyped()
    {
        var entity = CommandHistoryMapper.ToEntity(SampleHistory(), new ReversibleProtector());

        Assert.DoesNotContain(Password, entity.ParametersJson, StringComparison.Ordinal);
        Assert.True(HistorySecretEnvelope.IsSealed(entity.ParametersJson));
    }

    /// <summary>
    /// What the panel is for: the row has to come back as it went in.
    /// </summary>
    [Fact]
    public void ARowComesBackExactlyAsItWasRecorded()
    {
        var protector = new ReversibleProtector();

        var restored = CommandHistoryMapper.ToModel(
            CommandHistoryMapper.ToEntity(SampleHistory(), protector), protector);

        Assert.True(restored.IsReadable);
        Assert.Equal(Command, restored.GeneratedCommand);
        Assert.Equal(Password, restored.Parameters["password"]);
    }

    /// <summary>
    /// The fields left in clear on purpose, so the panel stays readable and sortable even
    /// when the sealed halves cannot be opened.
    /// </summary>
    [Fact]
    public void TheTitleCategoryAndTimestampStayInClear()
    {
        var history = SampleHistory();

        var entity = CommandHistoryMapper.ToEntity(history, new ReversibleProtector());

        Assert.Equal("Open a MySQL shell", entity.ActionTitle);
        Assert.Equal("Databases", entity.Category);
        Assert.Equal(history.CreatedAt, entity.CreatedAt);
    }

    /// <summary>
    /// Rows written before sealing existed hold a command pattern in clear. Nothing
    /// migrates them, so the read path has to keep them readable forever.
    /// </summary>
    [Fact]
    public void ARowWrittenBeforeSealingStaysReadable()
    {
        var legacy = new CommandHistoryEntity
        {
            Id = "old",
            ActionId = "action-1",
            GeneratedCommand = "mysql -u root -p{password}",
            ParametersJson = "{}",
            Platform = Platform.Linux,
            CreatedAt = DateTime.UtcNow,
            Category = "Databases",
            ActionTitle = "Open a MySQL shell"
        };

        var model = CommandHistoryMapper.ToModel(legacy, new ReversibleProtector());

        Assert.True(model.IsReadable);
        Assert.Equal("mysql -u root -p{password}", model.GeneratedCommand);
    }

    /// <summary>
    /// A sealed row whose key is gone reports itself unreadable and carries no command,
    /// rather than handing ciphertext to the clipboard.
    /// </summary>
    [Fact]
    public void ARowThatCannotBeOpenedIsReportedUnreadable()
    {
        var sealedEntity = CommandHistoryMapper.ToEntity(SampleHistory(), new ReversibleProtector());

        var model = CommandHistoryMapper.ToModel(sealedEntity, new RefusingProtector());

        Assert.False(model.IsReadable);
        Assert.Equal(string.Empty, model.GeneratedCommand);
        Assert.Empty(model.Parameters);
    }

    private static CommandHistory SampleHistory() => new()
    {
        Id = "history-1",
        ActionId = "action-1",
        GeneratedCommand = Command,
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["password"] = Password
        },
        Platform = Platform.Linux,
        CreatedAt = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc),
        Category = "Databases",
        ActionTitle = "Open a MySQL shell"
    };

    /// <summary>
    /// Reversible stand-in for the real protector, which is a static holding process-wide
    /// vault state. None of the rules above are about the cipher, and driving that global
    /// state from a test would race every other test in the run.
    /// </summary>
    private sealed class ReversibleProtector : ISecretProtector
    {
        private const string Wrapper = "reversed:";

        public string Protect(string plainText) =>
            Wrapper + new string(plainText.Reverse().ToArray());

        public string? Unprotect(string protectedValue) =>
            protectedValue.StartsWith(Wrapper, StringComparison.Ordinal)
                ? new string(protectedValue[Wrapper.Length..].Reverse().ToArray())
                : null;
    }

    /// <summary>Stands for a key that is gone: a locked vault, another account.</summary>
    private sealed class RefusingProtector : ISecretProtector
    {
        public string Protect(string plainText) =>
            throw new InvalidOperationException("The vault is locked.");

        public string? Unprotect(string protectedValue) => null;
    }
}
