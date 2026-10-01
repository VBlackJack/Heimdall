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
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Localization;
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

/// <summary>
/// An in-memory remote browser the SFTP view-model tests script: a tree of directories, recorded
/// calls, and hooks that let a test hold an operation open or make it fail.
/// </summary>
internal sealed class ScriptedRemoteBrowser : IRemoteBrowser
{
    private readonly Dictionary<string, List<SftpFileInfo>> _directories = new(StringComparer.Ordinal);
    private readonly object _gate = new();

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

    public string CurrentDirectory => "/";

    public bool IsConnected { get; set; } = true;

    /// <summary>Hook run before a listing returns; a test holds a listing open by awaiting a gate here.</summary>
    public Func<string, CancellationToken, Task>? ListingGate { get; set; }

    /// <summary>When set, listing a path throws what this returns for it (or lists normally on null).</summary>
    public Func<string, Exception?>? ListingFailure { get; set; }

    /// <summary>Hook run when a download starts; throw to fail it, or await to hold it open.</summary>
    public Func<string, string, CancellationToken, Task>? DownloadHandler { get; set; }

    /// <summary>Hook run when an upload starts.</summary>
    public Func<string, string, CancellationToken, Task>? UploadHandler { get; set; }

    /// <summary>Hook run when a delete starts.</summary>
    public Func<string, CancellationToken, Task>? DeleteHandler { get; set; }

    /// <summary>Hook run when a rename starts.</summary>
    public Func<string, string, CancellationToken, Task>? RenameHandler { get; set; }

    public List<(string Remote, string Local, bool Overwrite)> Downloads { get; } = [];

    public List<(string Local, string Remote, bool Overwrite)> Uploads { get; } = [];

    public List<string> CreatedDirectories { get; } = [];

    public List<string> Deletes { get; } = [];

    public List<(string From, string To)> Renames { get; } = [];

    public List<string> Listings { get; } = [];

    public void AddDirectory(string path, params SftpFileInfo[] entries)
    {
        lock (_gate)
        {
            _directories[path] = [.. entries];
        }
    }

    public void RaiseProgress(SftpTransferProgress progress) => TransferProgress?.Invoke(progress);

    public async Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(string? path = null, CancellationToken ct = default)
    {
        string target = path ?? CurrentDirectory;
        lock (_gate)
        {
            Listings.Add(target);
        }

        if (ListingGate is not null)
        {
            await ListingGate(target, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        if (ListingFailure?.Invoke(target) is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            return _directories.TryGetValue(target, out List<SftpFileInfo>? entries) ? [.. entries] : [];
        }
    }

    public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default) => Task.FromResult(CurrentDirectory);

    public Task ChangeDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

    public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
        => DownloadFileAsync(remotePath, localPath, overwrite: true, ct);

    public async Task DownloadFileAsync(string remotePath, string localPath, bool overwrite, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Downloads.Add((remotePath, localPath, overwrite));
        }

        if (DownloadHandler is not null)
        {
            await DownloadHandler(remotePath, localPath, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        string? directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(localPath, [], ct).ConfigureAwait(false);
    }

    public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
        => UploadFileAsync(localPath, remotePath, overwrite: true, ct);

    public async Task UploadFileAsync(string localPath, string remotePath, bool overwrite, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Uploads.Add((localPath, remotePath, overwrite));
        }

        if (UploadHandler is not null)
        {
            await UploadHandler(localPath, remotePath, ct).ConfigureAwait(false);
        }
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        lock (_gate)
        {
            CreatedDirectories.Add(path);
        }

        return Task.CompletedTask;
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Deletes.Add(path);
        }

        if (DeleteHandler is not null)
        {
            await DeleteHandler(path, ct).ConfigureAwait(false);
        }
    }

    public Task ChmodAsync(string path, short mode, CancellationToken ct = default) => Task.CompletedTask;

    public async Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Renames.Add((oldPath, newPath));
        }

        if (RenameHandler is not null)
        {
            await RenameHandler(oldPath, newPath, ct).ConfigureAwait(false);
        }
    }

    public Task CopyAsync(string sourcePath, string destinationPath, bool recursive, CancellationToken ct = default)
        => Task.CompletedTask;

    public void Disconnect() => IsConnected = false;

    public void Dispose()
    {
    }
}

/// <summary>Builds the pieces every SFTP view-model test needs.</summary>
internal static class SftpTestKit
{
    public static SftpFileInfo File(string path, long size = 0, string permissions = "rw-r--r--")
        => Entry(path, RemoteEntryKind.File, size, permissions);

    public static SftpFileInfo Directory(string path)
        => Entry(path, RemoteEntryKind.Directory, 0, "rwxr-xr-x");

    public static SftpFileInfo Link(string path)
        => Entry(path, RemoteEntryKind.SymbolicLink, 0, "rwxrwxrwx");

    public static SftpFileInfo Entry(string path, RemoteEntryKind kind, long size, string permissions)
        => new(
            path[(path.LastIndexOf('/') + 1)..],
            path,
            kind,
            size,
            DateTime.UnixEpoch,
            permissions,
            "1000",
            "1000");

    public static async Task<LocalizationManager> LoadLocalizerAsync(string locale = "en")
    {
        LocalizationManager manager = new();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    /// <summary>Creates a view model wired to a browser (and, optionally, a localizer) without a live session.</summary>
    public static EmbeddedSftpViewModel CreateViewModel(
        IRemoteBrowser? browser = null,
        IFileConflictDialogPresenter? presenter = null,
        LocalizationManager? localizer = null,
        IDialogService? dialogService = null)
    {
        EmbeddedSftpViewModel viewModel = new(
            new FakeUiDispatcher(),
            new RemoteClipboardService(),
            presenter ?? new ScriptedConflictPresenter(_ => null));
        if (browser is not null)
        {
            SetField(viewModel, "_browser", browser);
        }

        if (localizer is not null)
        {
            SetField(viewModel, "_localizer", localizer);
        }

        if (dialogService is not null)
        {
            viewModel.SetDialogService(dialogService);
        }

        viewModel.IsConnected = true;
        return viewModel;
    }

    private static void SetField(EmbeddedSftpViewModel viewModel, string name, object value)
    {
        FieldInfo? field = typeof(EmbeddedSftpViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(viewModel, value);
    }

    /// <summary>A scratch folder removed when disposed.</summary>
    public sealed class ScratchFolder : IDisposable
    {
        public ScratchFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "heimdall-sftp-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A test that left a handle open must not fail the run for it.
            }
        }
    }
}

/// <summary>A conflict presenter that answers from a function and counts how often it was asked.</summary>
internal sealed class ScriptedConflictPresenter(Func<FileConflictDialogViewModel, FileConflictDialogResult?> answer)
    : IFileConflictDialogPresenter
{
    public int CallCount { get; private set; }

    public Task<FileConflictDialogResult?> ShowAsync(FileConflictDialogViewModel viewModel)
    {
        CallCount++;
        return Task.FromResult(answer(viewModel));
    }
}

/// <summary>
/// A dialog service built on a proxy: every member answers a neutral default except the few a test
/// scripts, so the tests do not repeat the forty members of the interface.
/// </summary>
public class ScriptedDialogProxy : DispatchProxy
{
    public Func<string, string, bool> Confirm { get; set; } = static (_, _) => true;

    public Func<string, string, string?, string?> Input { get; set; } = static (_, _, _) => null;

    public List<(string Title, string Message)> Confirmations { get; } = [];

    public List<(string Title, string Prompt)> Inputs { get; } = [];

    public static (IDialogService Service, ScriptedDialogProxy Script) Create()
    {
        IDialogService service = DispatchProxy.Create<IDialogService, ScriptedDialogProxy>();
        return (service, (ScriptedDialogProxy)(object)service);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];

        switch (targetMethod.Name)
        {
            case nameof(IDialogService.ShowConfirmAsync):
                string title = (string)args[0]!;
                string message = (string)args[1]!;
                Confirmations.Add((title, message));
                return Task.FromResult(Confirm(title, message));

            case nameof(IDialogService.ShowInputAsync):
                Inputs.Add(((string)args[0]!, (string)args[1]!));
                return Task.FromResult(Input((string)args[0]!, (string)args[1]!, args[2] as string));
        }

        Type returnType = targetMethod.ReturnType;
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
            object? defaultValue = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
            return typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType)
                .Invoke(null, [defaultValue]);
        }

        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}
