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
                Assert.False(Directory.Exists(activeTempPath));
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

    private static CancellationTokenSource GetInlineEditorCancellation(EmbeddedSftpView owner)
    {
        FieldInfo? field = typeof(EmbeddedSftpView).GetField(
            "_inlineEditorCancellation",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<CancellationTokenSource>(field.GetValue(owner));
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
        LocalizationManager? localizer = null)
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
            DispatchProxy.Create<IDialogService, NullDialogProxy>(),
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
