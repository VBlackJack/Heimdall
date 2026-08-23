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

        // Terminal rather than a question, and NOT because a teardown would leave a half-written
        // file on the server - every write path publishes by an atomic rename, so it would not.
        // The reason is that the write cannot be stopped: granting the close would return the pane
        // while the upload thread stays wedged in the SSH library holding the browser's client lock,
        // and the save the user believed had been accepted would silently never happen.
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

        // Confirming would be a lie: no answer the user gives can stop an uninterruptible write, so
        // the terminal refusal has to be tested first.
        Assert.Equal(CloseVerdict.Deny, guard.PollClose(Request()).Verdict);
    }

    [Fact]
    public void DescribeEditorSaveRefusal_SaveInFlight_NamesTheSameKeyPollCloseDenies()
    {
        SftpCloseGuardSnapshot snapshot = new(false, true, false, 1);
        EmbeddedSftpCloseGuard guard = CreateGuard(() => snapshot);

        // The point of the shared predicate: the pane surface and the editor overlay cannot name
        // two different sentences for one save, because there is only one sentence to name.
        Assert.Equal(
            SftpCloseGuardLocaleKeys.EditorSaveBlocked,
            EmbeddedSftpCloseGuard.DescribeEditorSaveRefusal(snapshot));
        Assert.Equal(
            guard.PollClose(Request()).ReasonKey,
            EmbeddedSftpCloseGuard.DescribeEditorSaveRefusal(snapshot));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public void DescribeEditorSaveRefusal_NoSaveInFlight_ReturnsNoRefusal(
        bool transferInProgress,
        bool saveInProgress,
        bool unsavedChanges)
    {
        SftpCloseGuardSnapshot snapshot = new(transferInProgress, saveInProgress, unsavedChanges, 1);

        // A running transfer and unsaved text are losable work the user may abandon knowingly, so
        // each stays a question. Only the save refuses - the editor's Close must not be widened
        // into a second veto surface.
        Assert.Null(EmbeddedSftpCloseGuard.DescribeEditorSaveRefusal(snapshot));
    }

    [Fact]
    public async Task ResolveCloseAsync_SaveInFlight_RefusesWithoutAsking()
    {
        SftpCloseGuardSnapshot snapshot = new(false, true, false, 1);
        int prompts = 0;
        EmbeddedSftpCloseGuard guard = CreateGuard(
            () => snapshot,
            (_, _) =>
            {
                prompts++;
                return Task.FromResult(true);
            });

        // The asynchronous phase has to hold the same line as the poll. Without this, a consenting
        // user reaches the confirmation and is told the close succeeded when it did not.
        Assert.False(await guard.ResolveCloseAsync(Request(), CancellationToken.None));
        Assert.Equal(0, prompts);
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

        // Five from SftpCloseGuardLocaleKeys plus six for the save-escape offer, plus
        // six from CloseGuardLocaleKeys. Raised inside the change that added them: a
        // count discovered by a red CI is a count nobody chose.
        Assert.Equal(17, keys.Length);
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
