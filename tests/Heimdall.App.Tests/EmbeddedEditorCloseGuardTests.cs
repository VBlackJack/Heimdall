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

using Heimdall.App.Services;
using Heimdall.App.Views;

namespace Heimdall.App.Tests;

/// <summary>
/// The local editor pane's close guard. The pane implemented none, and the arbiter skips every
/// host that is not a guard: the tab, the split pane and the floating window closed without a
/// question and the unsaved text was gone.
/// </summary>
public sealed class EmbeddedEditorCloseGuardTests
{
    [Fact]
    public void PollClose_NothingUnsaved_Allows()
    {
        EmbeddedEditorCloseGuard guard = CreateGuard(() => new EditorCloseGuardSnapshot(false, 1));

        Assert.Equal(CloseVerdict.Allow, guard.PollClose(Request()).Verdict);
    }

    [Fact]
    public void PollClose_UnsavedChanges_DefersWithTheUnsavedQuestion()
    {
        EmbeddedEditorCloseGuard guard = CreateGuard(() => new EditorCloseGuardSnapshot(true, 7));

        CloseDecision decision = guard.PollClose(Request());

        Assert.Equal(CloseVerdict.Defer, decision.Verdict);
        Assert.Equal(EditorCloseGuardLocaleKeys.UnsavedMessage, decision.ReasonKey);
        Assert.Equal(7, decision.Epoch);
    }

    [Fact]
    public async Task ResolveCloseAsync_UnsavedChanges_AsksAndHonoursTheAnswer()
    {
        List<string> asked = [];
        EmbeddedEditorCloseGuard guard = CreateGuard(
            () => new EditorCloseGuardSnapshot(true, 1),
            (_, messageKey) =>
            {
                asked.Add(messageKey);
                return Task.FromResult(false);
            });

        Assert.False(await guard.ResolveCloseAsync(Request(), CancellationToken.None));
        Assert.Equal([EditorCloseGuardLocaleKeys.UnsavedMessage], asked);
    }

    [Fact]
    public async Task ResolveCloseAsync_SavedMeanwhile_ConsentsWithoutAsking()
    {
        int prompts = 0;
        EmbeddedEditorCloseGuard guard = CreateGuard(
            () => new EditorCloseGuardSnapshot(false, 1),
            (_, _) =>
            {
                prompts++;
                return Task.FromResult(false);
            });

        Assert.True(await guard.ResolveCloseAsync(Request(), CancellationToken.None));
        Assert.Equal(0, prompts);
    }

    [Fact]
    public void SampleCloseGuardState_CarriesBusinessAndEpoch()
    {
        EmbeddedEditorCloseGuard guard = CreateGuard(() => new EditorCloseGuardSnapshot(true, 42));

        CloseGuardState state = guard.SampleCloseGuardState();

        Assert.True(state.IsBusy);
        Assert.Equal(42, state.Epoch);
    }

    private static CloseRequest Request() => CloseRequest.Interactive(DisconnectReason.TabClose);

    private static EmbeddedEditorCloseGuard CreateGuard(
        Func<EditorCloseGuardSnapshot> sample,
        Func<string, string, Task<bool>>? confirmAsync = null)
        => new(sample, confirmAsync ?? ((_, _) => Task.FromResult(true)));
}
