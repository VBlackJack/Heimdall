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
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Heimdall.App.Services;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels;
using Heimdall.App.Views;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Sftp;
using Heimdall.Ssh;

namespace Heimdall.App.UiTests;

[Collection(DesktopUiCollection.Name)]
public sealed class EmbeddedSftpInlineEditorLifecycleTests
{
    private const long MaxInlineEditFileBytes = 16L * 1024 * 1024;

    [Fact]
    public async Task InlineEditor_KnownOversizeFile_RefusesBeforeDownload()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(async () =>
        {
            BlockingUploadRemoteBrowser browser = new();
            LocalizationManager localizer = await CreateEnglishLocalizer();
            (EmbeddedSftpView owner, _) = CreateInitializedOwner(browser, localizer);
            try
            {
                SftpFileInfo file = CreateRemoteFile(MaxInlineEditFileBytes + 1);

                await InvokeEditFile(owner, file);

                EmbeddedSftpViewModel viewModel =
                    Assert.IsType<EmbeddedSftpViewModel>(owner.DataContext);
                Assert.Equal(0, browser.DownloadCallCount);
                Assert.Null(GetActiveInlineEditor(owner));
                Assert.True(viewModel.IsErrorStatus);
                Assert.Contains("16 MiB", viewModel.StatusText, StringComparison.Ordinal);
            }
            finally
            {
                owner.Dispose();
            }
        }).Task.Unwrap();
    }

    [Fact]
    public async Task InlineEditor_UnknownSizeGrowingPastLimit_CancelsBeforeDecodeAndEditor()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(async () =>
        {
            BlockingUploadRemoteBrowser browser = new()
            {
                ReportedDownloadBytes = MaxInlineEditFileBytes + 1,
                EmitUnrelatedOversizeProgressFirst = true
            };
            LocalizationManager localizer = await CreateEnglishLocalizer();
            (EmbeddedSftpView owner, _) = CreateInitializedOwner(browser, localizer);
            try
            {
                SftpFileInfo file = CreateRemoteFile(size: 0);

                await InvokeEditFile(owner, file);

                EmbeddedSftpViewModel viewModel =
                    Assert.IsType<EmbeddedSftpViewModel>(owner.DataContext);
                Assert.Equal(1, browser.DownloadCallCount);
                Assert.False(browser.UnrelatedProgressCausedCancellation);
                Assert.True(browser.DownloadCancellationObserved);
                Assert.Null(GetActiveInlineEditor(owner));
                Assert.True(viewModel.IsErrorStatus);
                Assert.Contains("16 MiB", viewModel.StatusText, StringComparison.Ordinal);
            }
            finally
            {
                owner.Dispose();
            }
        }).Task.Unwrap();
    }

    [Fact]
    public async Task InlineEditor_EditSaveCloseAndSessionDispose_PreserveSftpPaneOwnership()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(async () =>
        {
            BlockingUploadRemoteBrowser browser = new();
            (EmbeddedSftpView owner, SessionPaneModel pane) = CreateInitializedOwner(browser);
            try
            {
                ContentControl inlineEditorHost = Assert.IsType<ContentControl>(
                    owner.FindName("InlineEditorHost"));
                Grid browserSurface = Assert.IsType<Grid>(owner.FindName("BrowserSurface"));
                SftpFileInfo file = new(
                    "settings.conf",
                    "/remote/settings.conf",
                    RemoteEntryKind.File,
                    7,
                    DateTime.UtcNow,
                    "rw-r--r--",
                    "1000",
                    "1000");

                await InvokeEditFile(owner, file);

                EmbeddedEditorView firstEditor = Assert.IsType<EmbeddedEditorView>(
                    GetActiveInlineEditor(owner));
                EmbeddedEditorViewModel firstEditorViewModel =
                    Assert.IsType<EmbeddedEditorViewModel>(firstEditor.DataContext);
                Assert.Same(owner, pane.HostControl);
                Assert.Same(firstEditor, inlineEditorHost.Content);
                Assert.Equal(Visibility.Visible, inlineEditorHost.Visibility);
                Assert.Equal(Visibility.Collapsed, browserSurface.Visibility);

                Task<bool> firstSave = firstEditorViewModel.SaveAsync("first update");
                await browser.UploadStarted.Task;

                await firstEditorViewModel.RequestClose();
                Assert.Same(firstEditor, GetActiveInlineEditor(owner));
                Assert.Same(owner, pane.HostControl);

                browser.ReleaseUpload();
                Assert.True(await firstSave);
                Assert.Equal("first update", browser.UploadedContent);

                await firstEditorViewModel.RequestClose();
                Assert.Same(owner, pane.HostControl);
                Assert.Null(GetActiveInlineEditor(owner));
                Assert.Null(inlineEditorHost.Content);
                Assert.Equal(Visibility.Collapsed, inlineEditorHost.Visibility);
                Assert.Equal(Visibility.Visible, browserSurface.Visibility);

                browser.PrepareUpload();
                await InvokeEditFile(owner, file);
                EmbeddedEditorView secondEditor = Assert.IsType<EmbeddedEditorView>(
                    GetActiveInlineEditor(owner));
                EmbeddedEditorViewModel secondEditorViewModel =
                    Assert.IsType<EmbeddedEditorViewModel>(secondEditor.DataContext);
                CancellationTokenSource inlineEditorCancellation =
                    GetInlineEditorCancellation(owner);
                string activeTempPath = GetActiveInlineEditorTempPath(owner);
                TaskCompletionSource cancellationObserved = NewSignal();
                using CancellationTokenRegistration registration =
                    inlineEditorCancellation.Token.Register(
                        () => cancellationObserved.TrySetResult());
                Task<bool> secondSave = secondEditorViewModel.SaveAsync("second update");
                await browser.UploadStarted.Task;

                owner.Dispose();

                Assert.False(await secondSave);
                Assert.True(cancellationObserved.Task.IsCompletedSuccessfully);
                Assert.True(browser.UploadCancellationObserved.Task.IsCompletedSuccessfully);

                // Inverted deliberately. This assertion used to require the directory to
                // be gone, and it was the only thing pinning a data-loss path: the
                // directory holds the only copy of what the user typed, and tearing the
                // session down while a save is in flight is exactly when they have not
                // got it anywhere else.
                //
                // Two things changed. The retention is now load-bearing rather than
                // incidental - the close guard refuses to close a saving pane on the
                // stated grounds that the user's work is safe on disk - and a startup
                // sweeper reclaims these directories once they are a day old, so keeping
                // one costs a bounded amount of temp space instead of leaking forever.
                Assert.True(
                    Directory.Exists(activeTempPath),
                    "a teardown during a save must keep the user's edited text");
                Assert.Same(owner, pane.HostControl);
                Assert.Null(GetActiveInlineEditor(owner));
                Assert.Null(inlineEditorHost.Content);
                Assert.Equal(Visibility.Collapsed, inlineEditorHost.Visibility);
                Assert.Equal(Visibility.Collapsed, browserSurface.Visibility);
            }
            finally
            {
                owner.Dispose();
            }
        }).Task.Unwrap();
    }

    /// <summary>
    /// The user's edited text survives a teardown even when the upload afterwards
    /// unwinds - which is the case the retention used to get exactly backwards.
    /// </summary>
    /// <remarks>
    /// Disposing the view skips the in-flight edit directory, but it used to leave the
    /// path in the set of active directories, and the save's own finally deletes it on
    /// finding the view disposed. So the text was kept only while the upload stayed
    /// wedged forever, and destroyed the moment the teardown actually did what it is for.
    /// <para>
    /// Reachable with no new affordance: locking the workspace disposes panes and leaves
    /// the process running, so the finally gets its chance.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task InlineEditor_SessionDisposedThenUploadUnwinds_KeepsTheEditedText()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(async () =>
        {
            BlockingUploadRemoteBrowser browser = new();
            (EmbeddedSftpView owner, _) = CreateInitializedOwner(browser);
            try
            {
                await InvokeEditFile(owner, CreateRemoteFile(7));

                EmbeddedEditorView editor = Assert.IsType<EmbeddedEditorView>(
                    GetActiveInlineEditor(owner));
                EmbeddedEditorViewModel editorViewModel =
                    Assert.IsType<EmbeddedEditorViewModel>(editor.DataContext);

                string activeTempPath = GetActiveInlineEditorTempPath(owner);
                Task<bool> save = editorViewModel.SaveAsync("the only copy of this text");
                await browser.UploadStarted.Task;

                owner.Dispose();

                // The teardown succeeds and the upload unwinds afterwards. That is the
                // ordinary outcome, and it is the one that used to delete the directory.
                Assert.False(await save);

                Assert.True(
                    Directory.Exists(activeTempPath),
                    "the edited text must outlive both the teardown and the upload's unwind");
            }
            finally
            {
                owner.Dispose();
            }
        }).Task.Unwrap();
    }

    /// <summary>
    /// The editor's own Close button, during a save, must say the same thing the tab, the split
    /// pane and the floating window already say - and must stop raising a question it will ignore.
    /// </summary>
    /// <remarks>
    /// Built out of production parts end to end, because the defect lives exactly at the junction
    /// between the overlay and the pane's guard: a guard tested alone and an overlay tested alone
    /// both stay green while nothing connects them.
    /// </remarks>
    [Fact]
    public async Task MountedSftpPane_CloseDuringInlineSave_SpeaksTheGuardsRefusalInsteadOfAsking()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(async () =>
        {
            BlockingUploadRemoteBrowser browser = new();
            LocalizationManager localizer = await CreateEnglishLocalizer();
            IDialogService dialog = DispatchProxy.Create<IDialogService, RecordingDialogProxy>();
            RecordingDialogProxy recording = (RecordingDialogProxy)dialog;
            (EmbeddedSftpView owner, _) = CreateInitializedOwner(browser, localizer, dialog);
            try
            {
                await InvokeEditFile(owner, CreateRemoteFile(7));

                EmbeddedEditorView firstEditor = Assert.IsType<EmbeddedEditorView>(
                    GetActiveInlineEditor(owner));
                EmbeddedEditorViewModel firstEditorViewModel =
                    Assert.IsType<EmbeddedEditorViewModel>(firstEditor.DataContext);

                // The overlay resolves its own dialog service when it loads, and it never loads
                // here because the control is not in a visual tree. Injected directly so a raised
                // confirmation is recorded rather than dropped on a null service.
                SetEditorDialogService(firstEditorViewModel, dialog);

                // The common case, and the one that used to lie: text typed, then saved. IsModified
                // is cleared only after a successful persist, so it is still true during the save.
                firstEditorViewModel.NotifyTextChanged();
                Assert.True(firstEditorViewModel.IsModified);

                Task<bool> firstSave = firstEditorViewModel.SaveAsync("first update");
                await browser.UploadStarted.Task;

                await firstEditorViewModel.RequestClose();

                // The oracle. Asking is the defect, not merely a cosmetic detail: the user answered
                // this question and the answer was thrown away.
                Assert.Empty(recording.ConfirmCalls);

                // Derived from the keys, never from a literal, so rewording either catalogue entry
                // cannot break this test - only failing to speak can.
                (string Title, string Message) spoken = Assert.Single(recording.InfoCalls);
                Assert.Equal(localizer[CloseGuardLocaleKeys.BlockedTitle], spoken.Title);
                Assert.Equal(
                    localizer.Format(
                        SftpCloseGuardLocaleKeys.EditorSaveBlocked,
                        GetClosePaneLabel(owner)),
                    spoken.Message);
                Assert.Same(firstEditor, GetActiveInlineEditor(owner));

                // State-driven, not a permanent lock: once the save lands, the same gesture closes.
                string activeTempPath = GetActiveInlineEditorTempPath(owner);
                browser.ReleaseUpload();
                Assert.True(await firstSave);

                await firstEditorViewModel.RequestClose();

                Assert.Null(GetActiveInlineEditor(owner));
                Assert.Single(recording.InfoCalls);
                Assert.False(Directory.Exists(activeTempPath));
            }
            finally
            {
                owner.Dispose();
            }
        }).Task.Unwrap();
    }

    /// <summary>
    /// The junction the guard exists for: the object a close path actually hands to the arbiter is
    /// the pane's <c>HostControl</c>, so the guard is only reachable if THAT object implements
    /// <see cref="ICloseGuard"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately built out of production parts end to end - the real view, the real overlay
    /// path, the real editor, the real arbiter - because a guard tested through a stand-in and a
    /// host tested without one both stay green either side of a junction that neither crosses.
    /// </remarks>
    [Fact]
    public async Task MountedSftpPane_DirtyInlineEditor_ProvidesCloseGuardAndDefersArbiter()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(async () =>
        {
            BlockingUploadRemoteBrowser browser = new();
            (EmbeddedSftpView owner, SessionPaneModel pane) = CreateInitializedOwner(browser);
            try
            {
                PaneCloseArbiter arbiter = new();
                ContentControl inlineEditorHost = Assert.IsType<ContentControl>(
                    owner.FindName("InlineEditorHost"));

                // A pane with nothing in flight closes without a question.
                Assert.Equal(
                    CloseVerdict.Allow,
                    arbiter.Poll(InteractiveRequest(), [pane.HostControl]).Verdict);

                await InvokeEditFile(owner, CreateRemoteFile(size: 7));

                // The overlay is up, and the pane's host is still the SFTP view rather than the
                // editor: the editor never takes pane ownership, so the guard has to live here.
                EmbeddedEditorView inlineEditor = Assert.IsType<EmbeddedEditorView>(
                    GetActiveInlineEditor(owner));
                Assert.Same(inlineEditor, inlineEditorHost.Content);
                Assert.Equal(Visibility.Visible, inlineEditorHost.Visibility);
                Assert.Same(owner, pane.HostControl);

                ICloseGuard closeGuard = Assert.IsAssignableFrom<ICloseGuard>(pane.HostControl);
                CloseGuardState cleanState = closeGuard.SampleCloseGuardState();
                Assert.False(cleanState.IsBusy);

                // An editor holding no edits is still nothing to protect.
                Assert.Equal(
                    CloseVerdict.Allow,
                    arbiter.Poll(InteractiveRequest(), [pane.HostControl]).Verdict);

                EmbeddedEditorViewModel editorViewModel =
                    Assert.IsType<EmbeddedEditorViewModel>(inlineEditor.DataContext);
                editorViewModel.NotifyTextChanged();
                Assert.True(editorViewModel.IsModified);

                // The stamp has to move with the unsaved text, or a consent given while the editor
                // was clean would still match once it is dirty.
                CloseGuardState dirtyState = closeGuard.SampleCloseGuardState();
                Assert.True(dirtyState.IsBusy);
                Assert.NotEqual(cleanState.Epoch, dirtyState.Epoch);

                CloseDecision decision = arbiter.Poll(InteractiveRequest(), [pane.HostControl]);

                Assert.Equal(CloseVerdict.Defer, decision.Verdict);
                Assert.Equal(SftpCloseGuardLocaleKeys.EditorDirtyMessage, decision.ReasonKey);
                Assert.Same(owner, pane.HostControl);
            }
            finally
            {
                owner.Dispose();
            }
        }).Task.Unwrap();
    }

    private static CloseRequest InteractiveRequest()
        => CloseRequest.Interactive(DisconnectReason.TabClose);

    private static CancellationTokenSource GetInlineEditorCancellation(EmbeddedSftpView owner)
    {
        FieldInfo? field = typeof(EmbeddedSftpView).GetField(
            "_inlineEditorCancellation",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<CancellationTokenSource>(field.GetValue(owner));
    }

    /// <summary>The pane label the view interpolates into a close-guard message.</summary>
    private static string GetClosePaneLabel(EmbeddedSftpView owner)
    {
        MethodInfo? method = typeof(EmbeddedSftpView).GetMethod(
            "DescribeClosePane",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(owner, null));
    }

    /// <summary>
    /// Injects a dialog service into the mounted editor's view model, which normally resolves one
    /// from DI when the control loads - and never loads outside a visual tree.
    /// </summary>
    private static void SetEditorDialogService(
        EmbeddedEditorViewModel viewModel,
        IDialogService dialogService)
    {
        MethodInfo? method = typeof(EmbeddedEditorViewModel).GetMethod(
            "SetDialogService",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(viewModel, [dialogService]);
    }

    private static string GetActiveInlineEditorTempPath(EmbeddedSftpView owner)
    {
        FieldInfo? field = typeof(EmbeddedSftpView).GetField(
            "_activeInlineEditorTempPath",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<string>(field.GetValue(owner));
    }

    private static EmbeddedEditorView? GetActiveInlineEditor(EmbeddedSftpView owner)
    {
        PropertyInfo? property = typeof(EmbeddedSftpView).GetProperty(
            "ActiveInlineEditor",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return property.GetValue(owner) as EmbeddedEditorView;
    }

    private static (EmbeddedSftpView Owner, SessionPaneModel Pane) CreateInitializedOwner(
        IRemoteBrowser browser,
        LocalizationManager? localizer = null,
        IDialogService? dialogService = null)
    {
        ConstructorInfo? constructor = typeof(EmbeddedSftpView).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(IUiDispatcher), typeof(IRemoteClipboardService), typeof(IHostKeyVerifier)],
            modifiers: null);
        Assert.NotNull(constructor);
        EmbeddedSftpView owner = Assert.IsType<EmbeddedSftpView>(constructor.Invoke(
        [
            new ImmediateUiDispatcher(),
            new RemoteClipboardService(),
            RejectingHostKeyVerifier.Instance
        ]));
        SessionPaneModel pane = new()
        {
            HostControl = owner
        };
        SessionTabViewModel sessionTab = new()
        {
            RootContent = pane
        };
        owner.SetOwningPane(pane);
        owner.InitializeSession(
            browser,
            sessionTab,
            "Test SFTP",
            "test.example:22",
            localizer ?? new LocalizationManager(),
            dialogService ?? DispatchProxy.Create<IDialogService, NullDialogProxy>(),
            new HostKeyStore());
        return (owner, pane);
    }

    private static async Task<LocalizationManager> CreateEnglishLocalizer()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }

    private static Task InvokeEditFile(EmbeddedSftpView owner, SftpFileInfo file)
    {
        MethodInfo? method = typeof(EmbeddedSftpView).GetMethod(
            "EditFileAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(owner, [file]));
    }

    private static SftpFileInfo CreateRemoteFile(long size)
    {
        return new SftpFileInfo(
            "settings.conf",
            "/remote/settings.conf",
            RemoteEntryKind.File,
            size,
            DateTime.UtcNow,
            "rw-r--r--",
            "1000",
            "1000");
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action)
        {
            action();
        }

        public T Invoke<T>(Func<T> func)
        {
            return func();
        }

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> action)
        {
            return action();
        }

        public bool CheckAccess()
        {
            return true;
        }
    }

    /// <summary>
    /// Records what the pane said and consents to everything it was asked.
    /// </summary>
    /// <remarks>
    /// Consenting is what makes the refusal observable. <see cref="NullDialogProxy"/> answers
    /// <see langword="false"/> to every confirmation, so with it an editor that stayed open cannot
    /// be told apart from one that asked and was declined - the assertion passes for the wrong
    /// reason and survives every mutant. Here a raised question would be answered yes and the close
    /// would go through, so a silent editor is the only way the test can end mounted.
    /// </remarks>
    private class RecordingDialogProxy : DispatchProxy
    {
        public List<(string Title, string Message)> InfoCalls { get; } = [];

        public List<(string Title, string Message)> ConfirmCalls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDialogService.ShowInfo))
            {
                InfoCalls.Add(((string)args![0]!, (string)args![1]!));
                return null;
            }

            if (targetMethod?.Name == nameof(IDialogService.ShowConfirmAsync))
            {
                ConfirmCalls.Add(((string)args![0]!, (string)args![1]!));
                return Task.FromResult(true);
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private class NullDialogProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _ = args;
            Type returnType = targetMethod?.ReturnType ?? typeof(void);
            if (returnType == typeof(void))
            {
                return null;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsGenericType
                && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                Type resultType = returnType.GetGenericArguments()[0];
                object? defaultResult = resultType.IsValueType
                    ? Activator.CreateInstance(resultType)
                    : null;
                MethodInfo fromResult = typeof(Task)
                    .GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType);
                return fromResult.Invoke(null, [defaultResult]);
            }

            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }

    private sealed class BlockingUploadRemoteBrowser : IRemoteBrowser
    {
        private TaskCompletionSource _uploadStarted = NewSignal();
        private TaskCompletionSource _releaseUpload = NewSignal();

        public event Action<string>? DirectoryChanged
        {
            add { }
            remove { }
        }

        public event Action<SftpTransferProgress>? TransferProgress;

        public event Action<RemoteOperationWarning>? OperationWarningRaised
        {
            add { }
            remove { }
        }

        public event Action<string?>? Disconnected
        {
            add { }
            remove { }
        }

        public string CurrentDirectory => "/remote";

        public bool IsConnected => true;

        public TaskCompletionSource UploadStarted => _uploadStarted;

        public TaskCompletionSource UploadCancellationObserved { get; private set; } = NewSignal();

        public string? UploadedContent { get; private set; }

        public int DownloadCallCount { get; private set; }

        public long? ReportedDownloadBytes { get; init; }

        public bool EmitUnrelatedOversizeProgressFirst { get; init; }

        public bool UnrelatedProgressCausedCancellation { get; private set; }

        public bool DownloadCancellationObserved { get; private set; }

        public void PrepareUpload()
        {
            _uploadStarted = NewSignal();
            _releaseUpload = NewSignal();
            UploadCancellationObserved = NewSignal();
            UploadedContent = null;
        }

        public void ReleaseUpload()
        {
            _releaseUpload.TrySetResult();
        }

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(
            string? path = null,
            CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<SftpFileInfo>>([]);
        }

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default)
        {
            return Task.FromResult(CurrentDirectory);
        }

        public Task ChangeDirectoryAsync(string path, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public async Task DownloadFileAsync(
            string remotePath,
            string localPath,
            CancellationToken ct = default)
        {
            DownloadCallCount++;
            if (ReportedDownloadBytes is long reportedBytes)
            {
                if (EmitUnrelatedOversizeProgressFirst)
                {
                    TransferProgress?.Invoke(new SftpTransferProgress(
                        "other.log",
                        reportedBytes,
                        reportedBytes,
                        IsUpload: false));
                    UnrelatedProgressCausedCancellation = ct.IsCancellationRequested;
                }

                TransferProgress?.Invoke(new SftpTransferProgress(
                    Path.GetFileName(remotePath),
                    reportedBytes,
                    reportedBytes,
                    IsUpload: false));
                DownloadCancellationObserved = ct.IsCancellationRequested;
                ct.ThrowIfCancellationRequested();
            }

            await File.WriteAllTextAsync(localPath, "initial", Encoding.UTF8, ct);
        }

        public async Task UploadFileAsync(
            string localPath,
            string remotePath,
            CancellationToken ct = default)
        {
            _ = remotePath;
            _uploadStarted.TrySetResult();
            try
            {
                await _releaseUpload.Task.WaitAsync(ct);
                UploadedContent = await File.ReadAllTextAsync(localPath, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                UploadCancellationObserved.TrySetResult();
                throw;
            }
        }

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task ChmodAsync(string path, short mode, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task RenameAsync(
            string oldPath,
            string newPath,
            CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task CopyAsync(
            string sourcePath,
            string destinationPath,
            bool recursive,
            CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public void Disconnect()
        {
        }

        public void Dispose()
        {
        }
    }
}
