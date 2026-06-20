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

using System.Reflection;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandPalette;
using Heimdall.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Verifies the Command Palette to Command Library bridge: the snippet-detail
/// "Open in Command Library" command opens the CMDLIB tool carrying the drilled-in
/// action id as <see cref="ToolContext.InitialActionId"/> and dismisses the palette.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public async Task OpenSnippetInLibrary_OpensCmdLibWithInitialActionIdAndClosesPalette()
    {
        using TestHarness harness = TestHarness.Create();
        CommandPaletteViewModel palette = harness.Main.CommandPalette;

        ActionModel action = new ActionModel { Id = "act-xyz", Title = "Demo snippet" };
        SetDetailAction(palette, action);
        palette.IsOpen = true;

        string? capturedToolId = null;
        ToolContext? capturedContext = null;
        harness.EmbeddedSessionManager.CreateToolControlBehavior = (
            SessionTabViewModel sessionTab,
            string toolId,
            ToolContext? context,
            Heimdall.Core.Configuration.AppSettings? settings) =>
        {
            capturedToolId = toolId;
            capturedContext = context;
            return new object();
        };

        await palette.OpenSnippetInLibraryCommand.ExecuteAsync(null);

        Assert.Equal(ToolRegistry.CommandLibraryToolId, capturedToolId);
        Assert.NotNull(capturedContext);
        Assert.Equal("act-xyz", capturedContext!.InitialActionId);
        Assert.False(palette.IsOpen);
    }

    [Fact]
    public async Task OpenSnippetInLibrary_NoDetailOpen_DoesNotOpenAnyTab()
    {
        using TestHarness harness = TestHarness.Create();
        CommandPaletteViewModel palette = harness.Main.CommandPalette;
        palette.IsOpen = true;

        await palette.OpenSnippetInLibraryCommand.ExecuteAsync(null);

        Assert.Empty(harness.Main.Connection.ActiveSessions);
    }

    private static void SetDetailAction(CommandPaletteViewModel palette, ActionModel action)
    {
        FieldInfo? field = typeof(CommandPaletteViewModel).GetField(
            "_detailAction",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(palette, action);
    }
}
