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
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.App.ViewModels.CommandLibrary;
using TwinShell.Core.Enums;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the two halves of the risk-level decision against values outside the
/// declared <see cref="CriticalityLevel"/> domain.
/// </summary>
/// <remarks>
/// A level is not only produced by the editor: the import envelope is deserialized with
/// a <c>JsonStringEnumConverter</c>, which accepts raw numbers and does not range-check
/// them. The execution guard already fails safe over the whole domain; these tests keep
/// the presentation from failing the other way, and keep the import door from letting
/// such a value in at all.
/// </remarks>
public sealed class CommandLibraryRiskDomainTests
{
    /// <summary>A level below the enum's own values, standing in for a corrupted import.</summary>
    private const CriticalityLevel UndeclaredLevel = (CriticalityLevel)99;

    [Fact]
    public void ResolveRiskBadge_ForUndeclaredLevel_ReadsAsDangerous()
    {
        var badge = CommandPresentationResolver.ResolveRiskBadge(UndeclaredLevel, key => key);

        Assert.Equal("ToolCmdLibRiskBadgeDanger", badge);
    }

    [Fact]
    public void ResolveRiskLabel_ForUndeclaredLevel_ReadsAsDangerous()
    {
        var label = CommandPresentationResolver.ResolveRiskLabel(UndeclaredLevel, key => key);

        Assert.Equal("ToolCmdLibRiskDangerous", label);
    }

    [Fact]
    public void ResolveRiskBrushKey_ForUndeclaredLevel_ReadsAsDangerous()
    {
        var brushKey = CommandPresentationResolver.ResolveRiskBrushKey(UndeclaredLevel);

        // Anchored on the literal key rather than on ResolveRiskBrushKey(Dangerous):
        // both sides of a self-comparison move together, so a mutant that folds the
        // dangerous arm into the default arm would leave such an assertion green.
        Assert.Equal("ErrorBrush", brushKey);
    }

    /// <summary>
    /// The display and the execution guard must agree: whatever the guard treats as
    /// needing confirmation must carry the dangerous presentation, and nothing else may.
    /// </summary>
    /// <remarks>
    /// Each expectation is written as a literal resource key. Comparing the resolver's
    /// output against its own output for <see cref="CriticalityLevel.Dangerous"/> would
    /// be vacuous: a mutant that returns the same wrong value for both still compares
    /// equal. Measured - that exact mutant survived the earlier self-comparing form.
    /// </remarks>
    [Theory]
    [InlineData(CriticalityLevel.Info, false)]
    [InlineData(CriticalityLevel.Run, false)]
    [InlineData(CriticalityLevel.Dangerous, true)]
    [InlineData(UndeclaredLevel, true)]
    public void Presentation_AgreesWithTheExecutionGuard(CriticalityLevel level, bool guardPrompts)
    {
        // Mirrors the threshold DangerousCommandGuard applies before sending.
        Assert.Equal(guardPrompts, level >= CriticalityLevel.Dangerous);

        var badge = CommandPresentationResolver.ResolveRiskBadge(level, key => key);
        var label = CommandPresentationResolver.ResolveRiskLabel(level, key => key);
        var brushKey = CommandPresentationResolver.ResolveRiskBrushKey(level);

        Assert.Equal(guardPrompts, badge == "ToolCmdLibRiskBadgeDanger");
        Assert.Equal(guardPrompts, label == "ToolCmdLibRiskDangerous");
        Assert.Equal(guardPrompts, brushKey == "ErrorBrush");
    }

    [Fact]
    public async Task ImportAsync_SnapsAnUndeclaredLevelToDangerous()
    {
        var service = new CommandLibraryTransferService();
        var actionService = new FakeActionService([]);

        var json = $$"""
        { "actions": [ { "id": "imp-1", "publicId": "{{Guid.NewGuid()}}",
          "title": "Crafted", "category": "Net", "level": 99, "platform": 42 } ] }
        """;

        await WithTempFileAsync(json, async path =>
        {
            var result = await service.ImportAsync(actionService, path);

            Assert.Equal(CommandLibraryImportOutcome.Success, result.Outcome);
            Assert.Equal(1, result.Imported);

            var stored = (await actionService.GetAllActionsAsync()).Single();
            Assert.Equal(CriticalityLevel.Dangerous, stored.Level);
            Assert.Equal(Platform.Both, stored.Platform);
        });
    }

    [Fact]
    public async Task ImportAsync_LeavesDeclaredEnumValuesAlone()
    {
        var service = new CommandLibraryTransferService();
        var actionService = new FakeActionService([]);

        var json = $$"""
        { "actions": [ { "id": "imp-1", "publicId": "{{Guid.NewGuid()}}",
          "title": "Ordinary", "category": "Net", "level": "info", "platform": "linux" } ] }
        """;

        await WithTempFileAsync(json, async path =>
        {
            await service.ImportAsync(actionService, path);

            var stored = (await actionService.GetAllActionsAsync()).Single();
            Assert.Equal(CriticalityLevel.Info, stored.Level);
            Assert.Equal(Platform.Linux, stored.Platform);
        });
    }

    /// <summary>
    /// Positive control for the guard threshold this file leans on: the guard must let
    /// the two sub-dangerous levels through without a dialog service at all.
    /// </summary>
    [Theory]
    [InlineData(CriticalityLevel.Info)]
    [InlineData(CriticalityLevel.Run)]
    public async Task ConfirmIfDangerousAsync_BelowThreshold_NeverPrompts(CriticalityLevel level)
    {
        var proceed = await DangerousCommandGuard.ConfirmIfDangerousAsync(
            level, dialog: null!, localize: key => key);

        Assert.True(proceed);
    }

    private static async Task WithTempFileAsync(string content, Func<string, Task> body)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, content);
            await body(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
