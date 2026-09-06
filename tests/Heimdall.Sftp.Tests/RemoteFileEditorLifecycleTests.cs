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
using Heimdall.Core.Ssh;
using Heimdall.Sftp;
using Heimdall.Ssh;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// What happens to the staged copy when an edit session ends, and to the session when the
/// editor never starts.
/// </summary>
public sealed class RemoteFileEditorLifecycleTests
{
    /// <remarks>
    /// The pane closed, disposed the editor, and the editor deleted the staged file under the
    /// external editor still open on it; the user's next Ctrl+S recreated a file nothing watched
    /// and the remote save silently never happened.
    /// </remarks>
    [Fact]
    public void CloseEdit_EditorStillRunning_KeepsTheStagedCopy()
    {
        using RemoteFileEditor editor = CreateEditor(new NullBrowser());
        string localPath = CreateStagedFile();
        using Process process = StartLongRunningProcess();
        try
        {
            EditSession session = new()
            {
                RemotePath = "/srv/app/config.txt",
                LocalPath = localPath,
                EditorProcess = process,
            };
            Assert.True(editor.AddSessionForTesting(session));

            editor.CloseEdit("/srv/app/config.txt");

            Assert.True(File.Exists(localPath), "the editor still has the file open");
            Assert.Empty(editor.GetActiveEdits());
        }
        finally
        {
            KillQuietly(process);
            DeleteQuietly(localPath);
        }
    }

    [Fact]
    public void CloseEdit_EditorExited_DeletesTheStagedCopy()
    {
        using RemoteFileEditor editor = CreateEditor(new NullBrowser());
        string localPath = CreateStagedFile();
        Process process = StartLongRunningProcess();
        KillQuietly(process);
        process.WaitForExit();
        try
        {
            EditSession session = new()
            {
                RemotePath = "/srv/app/config.txt",
                LocalPath = localPath,
                EditorProcess = process,
            };
            Assert.True(editor.AddSessionForTesting(session));

            editor.CloseEdit("/srv/app/config.txt");

            Assert.False(File.Exists(localPath));
        }
        finally
        {
            DeleteQuietly(localPath);
        }
    }

    [Fact]
    public void EditSessionTransitions_MovesOnEveryOpenAndClose()
    {
        using RemoteFileEditor editor = CreateEditor(new NullBrowser());
        string localPath = CreateStagedFile();
        try
        {
            long before = editor.EditSessionTransitions;
            EditSession session = new() { RemotePath = "/srv/a", LocalPath = localPath };
            Assert.True(editor.AddSessionForTesting(session));

            editor.CloseEdit("/srv/a");

            Assert.True(editor.EditSessionTransitions > before, "a close must move the stamp");
        }
        finally
        {
            DeleteQuietly(localPath);
        }
    }

    /// <remarks>
    /// Process.Start was not caught, after the session was registered and the watcher started:
    /// an editor that did not exist left a watcher and a session orphaned for the life of the
    /// pane, and the user read a raw Win32 message.
    /// </remarks>
    [Fact]
    public async Task EditFileAsync_EditorCannotStart_UnregistersTheSessionAndRemovesTheCopy()
    {
        StagingBrowser browser = new();
        string missingEditor = Path.Combine(Path.GetTempPath(), $"no-such-editor-{Guid.NewGuid():N}.exe");
        using RemoteFileEditor editor = CreateEditor(browser, missingEditor);

        ExternalEditorLaunchException failure = await Assert.ThrowsAsync<ExternalEditorLaunchException>(
            () => editor.EditFileAsync("/srv/app/config.txt"));

        Assert.Equal(missingEditor, failure.EditorPath);
        Assert.Empty(editor.GetActiveEdits());
        Assert.NotNull(browser.LastLocalPath);
        Assert.False(File.Exists(browser.LastLocalPath), "the staged copy of a session that never got an editor is removed");
    }

    private static RemoteFileEditor CreateEditor(IRemoteBrowser browser, string editorPath = "notepad.exe")
        => new(browser, new HostKeyStore(), RejectingHostKeyVerifier.Instance, editorPath);

    private static string CreateStagedFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Heimdall", "edit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "config.txt");
        File.WriteAllText(path, "staged");
        return path;
    }

    private static Process StartLongRunningProcess()
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/c ping -n 60 127.0.0.1 > nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return Process.Start(startInfo) ?? throw new InvalidOperationException("the stand-in editor did not start");
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void DeleteQuietly(string localPath)
    {
        try
        {
            string? directory = Path.GetDirectoryName(localPath);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private class NullBrowser : IRemoteBrowser
    {
        public event Action<string>? DirectoryChanged { add { } remove { } }

        public event Action<SftpTransferProgress>? TransferProgress { add { } remove { } }

        public event Action<RemoteOperationWarning>? OperationWarningRaised { add { } remove { } }

        public event Action<string?>? Disconnected { add { } remove { } }

        public string CurrentDirectory => "/";

        public bool IsConnected => true;

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(string? path = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SftpFileInfo>>([]);

        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default) => Task.FromResult("/");

        public Task ChangeDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

        public virtual Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();

        public Task ChmodAsync(string path, short mode, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CopyAsync(string sourcePath, string destinationPath, bool recursive, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Disconnect()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class StagingBrowser : NullBrowser
    {
        public string? LastLocalPath { get; private set; }

        public override Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
        {
            LastLocalPath = localPath;
            File.WriteAllText(localPath, "downloaded");
            return Task.CompletedTask;
        }
    }
}
