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
using Heimdall.App.Views;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// What an SFTP pane refuses, what it asks about, and what it lets through. The decision lives in
/// a WPF-free class precisely so it can be pinned here rather than only behind a desktop.
/// </summary>
public sealed class EmbeddedSftpCloseGuardTests
{
    [Fact]
    public void PollClose_NothingInFlight_Allows()
    {
        SftpCloseGuardSnapshot snapshot = new(false, false, false, 1);
        EmbeddedSftpCloseGuard guard = CreateGuard(() => snapshot);

        Assert.Equal(CloseVerdict.Allow, guard.PollClose(Request()).Verdict);
    }

    [Fact]
    public void PollClose_SaveInFlight_DeniesTerminally()
    {
        SftpCloseGuardSnapshot snapshot = new(false, true, false, 1);
        EmbeddedSftpCloseGuard guard = CreateGuard(() => snapshot);

        CloseDecision decision = guard.PollClose(Request());

        // Terminal rather than a question: tearing the pane down mid-save would leave a half-written
        // file on the server, and no answer the user could give would make that acceptable.
        Assert.Equal(CloseVerdict.Deny, decision.Verdict);
        Assert.Equal(SftpCloseGuardLocaleKeys.EditorSaveBlocked, decision.ReasonKey);
    }

    [Fact]
    public void PollClose_TransferInFlight_DefersToTheAsyncStage()
    {
        SftpCloseGuardSnapshot snapshot = new(true, false, false, 7);
        EmbeddedSftpCloseGuard guard = CreateGuard(() => snapshot);

        CloseDecision decision = guard.PollClose(Request());

        Assert.Equal(CloseVerdict.Defer, decision.Verdict);
        Assert.Equal(SftpCloseGuardLocaleKeys.TransferBlocked, decision.ReasonKey);
        Assert.Equal(7, decision.Epoch);
    }

    [Fact]
    public void PollClose_UnsavedEditorChanges_DefersToTheAsyncStage()
    {
        SftpCloseGuardSnapshot snapshot = new(false, false, true, 1);
        EmbeddedSftpCloseGuard guard = CreateGuard(() => snapshot);

        CloseDecision decision = guard.PollClose(Request());

        Assert.Equal(CloseVerdict.Defer, decision.Verdict);
        Assert.Equal(SftpCloseGuardLocaleKeys.EditorDirtyMessage, decision.ReasonKey);
    }

    [Fact]
    public void PollClose_SaveAndTransferBothInFlight_TheRefusalWins()
    {
        SftpCloseGuardSnapshot snapshot = new(true, true, false, 1);
        EmbeddedSftpCloseGuard guard = CreateGuard(() => snapshot);

        // Confirming would be a lie: consenting to lose the transfer would not make the half-written
        // file acceptable, so the terminal refusal has to be tested first.
        Assert.Equal(CloseVerdict.Deny, guard.PollClose(Request()).Verdict);
    }

    [Fact]
    public async Task ResolveCloseAsync_UserConfirms_Consents()
    {
        SftpCloseGuardSnapshot snapshot = new(true, false, false, 1);
        List<string> asked = [];
        EmbeddedSftpCloseGuard guard = CreateGuard(
            () => snapshot,
            (_, messageKey) =>
            {
                asked.Add(messageKey);
                return Task.FromResult(true);
            });

        Assert.True(await guard.ResolveCloseAsync(Request(), CancellationToken.None));
        Assert.Equal([SftpCloseGuardLocaleKeys.TransferMessage], asked);
    }

    [Fact]
    public async Task ResolveCloseAsync_UserDeclines_Refuses()
    {
        SftpCloseGuardSnapshot snapshot = new(true, false, false, 1);
        EmbeddedSftpCloseGuard guard = CreateGuard(() => snapshot, (_, _) => Task.FromResult(false));

        Assert.False(await guard.ResolveCloseAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task ResolveCloseAsync_WorkFinishedBeforeItRan_ConsentsWithoutAsking()
    {
        SftpCloseGuardSnapshot snapshot = new(false, false, false, 2);
        int prompts = 0;
        EmbeddedSftpCloseGuard guard = CreateGuard(
            () => snapshot,
            (_, _) =>
            {
                prompts++;
                return Task.FromResult(true);
            });

        // Closing several panes resolves them one at a time, so this pane's transfer may well have
        // ended while an earlier pane was being confirmed. Asking about it then would be a prompt
        // with no subject.
        Assert.True(await guard.ResolveCloseAsync(Request(), CancellationToken.None));
        Assert.Equal(0, prompts);
    }

    [Fact]
    public async Task ResolveCloseAsync_UnsavedChanges_AsksAboutTheEditorNotTheTransfer()
    {
        SftpCloseGuardSnapshot snapshot = new(false, false, true, 1);
        List<string> asked = [];
        EmbeddedSftpCloseGuard guard = CreateGuard(
            () => snapshot,
            (_, messageKey) =>
            {
                asked.Add(messageKey);
                return Task.FromResult(true);
            });

        await guard.ResolveCloseAsync(Request(), CancellationToken.None);

        Assert.Equal([SftpCloseGuardLocaleKeys.EditorDirtyMessage], asked);
    }

    [Fact]
    public void SampleCloseGuardState_CarriesBusinessAndEpochFromOneRead()
    {
        int reads = 0;
        EmbeddedSftpCloseGuard guard = CreateGuard(() =>
        {
            reads++;
            return new SftpCloseGuardSnapshot(true, false, false, 42);
        });

        CloseGuardState state = guard.SampleCloseGuardState();

        Assert.True(state.IsBusy);
        Assert.Equal(42, state.Epoch);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Snapshot_AnyProtectedWork_ReadsAsBusy(bool transfer, bool saving, bool dirty)
        => Assert.True(new SftpCloseGuardSnapshot(transfer, saving, dirty, 1).IsBusy);

    [Fact]
    public void Snapshot_NoProtectedWork_ReadsAsIdle()
        => Assert.False(new SftpCloseGuardSnapshot(false, false, false, 1).IsBusy);

    [Fact]
    public async Task LocaleKeys_AreTranslatedInBothCatalogues()
    {
        LocalizationManager english = await CreateLocalizerAsync("en");
        LocalizationManager french = await CreateLocalizerAsync("fr");

        // Reflected rather than listed, so a key added later without a translation is a red test
        // rather than silent drift.
        string[] keys =
        [
            .. typeof(SftpCloseGuardLocaleKeys)
                .GetFields()
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!),
            .. typeof(CloseGuardLocaleKeys)
                .GetFields()
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!)
        ];

        Assert.Equal(11, keys.Length);
        foreach (string key in keys)
        {
            Assert.NotEqual(key, english[key]);
            Assert.NotEqual(key, french[key]);
            Assert.NotEqual(english[key], french[key]);
        }
    }

    private static CloseRequest Request() => CloseRequest.Interactive(DisconnectReason.TabClose);

    private static EmbeddedSftpCloseGuard CreateGuard(
        Func<SftpCloseGuardSnapshot> sample,
        Func<string, string, Task<bool>>? confirmAsync = null)
        => new(
            sample,
            confirmAsync ?? ((_, _) => Task.FromResult(true)),
            () => "srv01");

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        LocalizationManager manager = new();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }
}
