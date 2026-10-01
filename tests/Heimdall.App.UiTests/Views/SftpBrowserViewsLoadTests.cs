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

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.Views;
using Heimdall.App.Views.Dialogs;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Sftp;
using Heimdall.Ssh;

namespace Heimdall.App.UiTests.Views;

/// <summary>
/// The SFTP browser, the local browser and the conflict dialog are loaded for real and every
/// binding they declare is resolved. A property renamed in a view model, or a resource a style
/// asks for and no dictionary holds, shows up here as a binding or resource failure instead of as
/// an empty cell in front of a user.
/// </summary>
[Collection(DesktopUiCollection.Name)]
public sealed class SftpBrowserViewsLoadTests
{
    private static readonly string[] ViewModelTypeNames =
    [
        nameof(EmbeddedSftpViewModel),
        nameof(LocalFileBrowserViewModel),
        nameof(FileConflictDialogViewModel),
        nameof(FileConflictRowViewModel),
        nameof(SftpFileInfo),
        nameof(SftpTransferJob),
        nameof(SftpPathSegment),
        nameof(LocalFileEntry),
    ];

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void RemoteBrowser_Loads_BindsEverythingAndNamesItsRowsByNameAndType()
    {
        WpfTestHost.ResetLocale();
        List<string> failures = [];

        WpfTestHost.Invoke(() =>
        {
            using BindingFailureCapture capture = new(failures);
            EmbeddedSftpView owner = CreateRemoteView(new ListingBrowser(
                new SftpFileInfo("docs", "/docs", RemoteEntryKind.Directory, 0, DateTime.UtcNow, "rwxr-xr-x", "1000", "1000"),
                new SftpFileInfo("a.txt", "/a.txt", RemoteEntryKind.File, 1536, DateTime.UtcNow, "rw-r--r--", "1000", "1000")));
            Window window = new() { Content = owner, Width = 900, Height = 600, ShowActivated = false };
            try
            {
                window.Show();
                _ = owner.NavigateToPath("/");
                Pump();
                owner.UpdateLayout();
                Pump();

                ListView list = (ListView)owner.FindName("FileListView");
                Assert.Equal(2, list.Items.Count);
                ListViewItem first = Assert.IsType<ListViewItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
                string name = AutomationProperties.GetName(first);
                Assert.Contains("docs", name, StringComparison.Ordinal);
                Assert.NotEqual("docs", name);

                Assert.NotNull(owner.FindName("BtnDownload"));
                Assert.NotNull(owner.FindName("DisconnectedOverlay"));
                Assert.NotNull(owner.FindName("PathBreadcrumb"));
            }
            finally
            {
                window.Close();
                owner.Dispose();
            }
        });

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Positive control: without it a capture that heard nothing would pass every load above.</summary>
    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void TheCapture_HearsABindingToAPropertyThatDoesNotExist()
    {
        List<string> failures = [];

        WpfTestHost.Invoke(() =>
        {
            using BindingFailureCapture capture = new(failures);
            TextBlock probe = new() { DataContext = new SftpPathSegment("a", "/a") };
            probe.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("NoSuchProperty"));
            probe.Measure(new Size(100, 20));
            Pump();
        });

        Assert.NotEmpty(failures);
    }

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void LocalBrowser_Loads_AndBindsEverything()
    {
        WpfTestHost.ResetLocale();
        List<string> failures = [];
        string folder = Path.Combine(Path.GetTempPath(), "heimdall-local-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "note.txt"), "x");

        try
        {
            WpfTestHost.Invoke(() =>
            {
                using BindingFailureCapture capture = new(failures);
                LocalFileBrowserView view = new(folder, WpfTestHost.Localizer);
                Window window = new() { Content = view, Width = 700, Height = 500, ShowActivated = false };
                try
                {
                    window.Show();
                    Pump();
                    view.UpdateLayout();

                    ListView list = (ListView)view.FindName("FileListView");
                    Assert.True(list.Items.Count >= 0);
                    Assert.True(view.IsLoaded);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void ConflictDialog_Loads_WithComparisonsAndVirtualizedRows()
    {
        WpfTestHost.ResetLocale();
        List<string> failures = [];

        WpfTestHost.Invoke(() =>
        {
            using BindingFailureCapture capture = new(failures);
            FileConflictAnalysisItem[] conflicts = Enumerable.Range(0, 400)
                .Select(index => new FileConflictAnalysisItem(
                    index,
                    $"/src/f{index}.txt",
                    $"/dst/f{index}.txt",
                    HasConflict: true,
                    FileConflictItemKind.File,
                    FileConflictItemKind.File,
                    FileConflictResolutionActions.All))
                .ToArray();
            FileConflictDialogViewModel viewModel = new(
                conflicts,
                WpfTestHost.Localizer,
                _ => new FileConflictComparison(
                    new FileConflictSideInfo(2000, DateTime.UtcNow),
                    new FileConflictSideInfo(1000, DateTime.UtcNow.AddDays(-1))));
            FileConflictDialog dialog = new()
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 100,
                Top = 100,
                DataContext = viewModel,
            };
            try
            {
                dialog.Show();
                dialog.UpdateLayout();
                Pump();

                Assert.True(dialog.IsLoaded);
                Assert.True(viewModel.HasComparison);
                Assert.NotNull(dialog.FindName("ReplaceIfNewerButton"));

                // Virtualized: far fewer containers than rows were realized.
                ItemsControl rows = FindDescendant<ItemsControl>(dialog)!;
                int realized = Enumerable.Range(0, rows.Items.Count)
                    .Count(index => rows.ItemContainerGenerator.ContainerFromIndex(index) is not null);
                Assert.True(realized < conflicts.Length / 2, $"{realized} of {conflicts.Length} rows were built up front.");
            }
            finally
            {
                dialog.Close();
            }
        });

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static EmbeddedSftpView CreateRemoteView(IRemoteBrowser browser)
    {
        ConstructorInfo? constructor = typeof(EmbeddedSftpView).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(IUiDispatcher), typeof(IRemoteClipboardService), typeof(IHostKeyVerifier)],
            modifiers: null);
        Assert.NotNull(constructor);
        EmbeddedSftpView owner = Assert.IsType<EmbeddedSftpView>(constructor.Invoke(
        [
            new InlineDispatcher(),
            new RemoteClipboardService(),
            DispatchProxy.Create<IHostKeyVerifier, QuietProxy>(),
        ]));
        SessionPaneModel pane = new() { HostControl = owner };
        SessionTabViewModel sessionTab = new() { RootContent = pane };
        owner.SetOwningPane(pane);
        owner.InitializeSession(
            browser,
            sessionTab,
            "Test SFTP",
            "test.example:22",
            WpfTestHost.Localizer,
            DispatchProxy.Create<IDialogService, QuietProxy>(),
            new HostKeyStore());
        return owner;
    }

    private static void Pump()
        => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match && match is ItemsControl { Items.Count: > 100 })
            {
                return match;
            }

            T? nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>Collects the binding warnings that name one of the view models under test.</summary>
    private sealed class BindingFailureCapture : TraceListener
    {
        private readonly List<string> _failures;
        private readonly SourceLevels _previousLevel;

        public BindingFailureCapture(List<string> failures)
        {
            _failures = failures;
            PresentationTraceSources.Refresh();
            _previousLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            PresentationTraceSources.DataBindingSource.Listeners.Add(this);
        }

        public override void Write(string? message) => Record(message);

        public override void WriteLine(string? message) => Record(message);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
                PresentationTraceSources.DataBindingSource.Switch.Level = _previousLevel;
            }

            base.Dispose(disposing);
        }

        private void Record(string? message)
        {
            if (message is not null
                && ViewModelTypeNames.Any(name => message.Contains(name, StringComparison.Ordinal)))
            {
                _failures.Add(message);
            }
        }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();

        public T Invoke<T>(Func<T> func) => func();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> action) => action();

        public bool CheckAccess() => true;
    }

    private class QuietProxy : DispatchProxy
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

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                Type resultType = returnType.GetGenericArguments()[0];
                object? value = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [value]);
            }

            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }

    private sealed class ListingBrowser(params SftpFileInfo[] entries) : IRemoteBrowser
    {
        public event Action<string>? DirectoryChanged
        {
            add { }
            remove { }
        }

        public event Action<SftpTransferProgress>? TransferProgress
        {
            add { }
            remove { }
        }

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

        public string CurrentDirectory => "/";

        public bool IsConnected => true;

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(string? path = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SftpFileInfo>>(entries);

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default) => Task.FromResult("/");

        public Task ChangeDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default) => Task.CompletedTask;

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default) => Task.CompletedTask;

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

        public Task ChmodAsync(string path, short mode, CancellationToken ct = default) => Task.CompletedTask;

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => Task.CompletedTask;

        public Task CopyAsync(string sourcePath, string destinationPath, bool recursive, CancellationToken ct = default)
            => Task.CompletedTask;

        public void Disconnect()
        {
        }

        public void Dispose()
        {
        }
    }
}
