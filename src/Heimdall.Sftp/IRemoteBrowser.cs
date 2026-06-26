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

namespace Heimdall.Sftp;

/// <summary>
/// Common interface for remote file browser implementations (SFTP, FTP).
/// Allows the embedded file browser view to work with any transport protocol.
/// </summary>
/// <remarks>
/// This interface is the deliberate common subset shared by the SFTP and FTP
/// implementations. Protocol-specific signals, such as SSH security events on
/// <see cref="SftpBrowser"/> and TLS state on <see cref="FtpBrowser"/>, are
/// exposed on the concrete types rather than on this interface.
/// </remarks>
public interface IRemoteBrowser : IDisposable
{
    /// <summary>Raised when the current working directory changes.</summary>
    event Action<string>? DirectoryChanged;

    /// <summary>Raised during file transfers to report progress.</summary>
    event Action<SftpTransferProgress>? TransferProgress;

    /// <summary>
    /// Raised when the connection is lost. The parameter contains an error
    /// message if the disconnection was unexpected, or null for a clean disconnect.
    /// </summary>
    event Action<string?>? Disconnected;

    /// <summary>Current remote working directory.</summary>
    string CurrentDirectory { get; }

    /// <summary>Whether the browser is connected to the remote host.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Lists all entries in the specified directory, or the current directory if
    /// <paramref name="path"/> is null.
    /// </summary>
    Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(string? path = null, CancellationToken ct = default);

    /// <summary>Returns the current remote working directory path.</summary>
    Task<string> GetCurrentDirectoryAsync(CancellationToken ct = default);

    /// <summary>Changes the current directory and raises <see cref="DirectoryChanged"/>.</summary>
    Task ChangeDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>Downloads a remote file to a local path.</summary>
    Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default);

    /// <summary>Uploads a local file to a remote path.</summary>
    Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default);

    /// <summary>Creates a directory on the remote host.</summary>
    Task CreateDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>Deletes a file or directory.</summary>
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>Changes the POSIX permissions of a remote file or directory.</summary>
    Task ChmodAsync(string path, short mode, CancellationToken ct = default);

    /// <summary>Renames (moves) a remote file or directory.</summary>
    Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default);

    /// <summary>
    /// Copies a remote file or directory to another path on the SAME server.
    /// <paramref name="recursive"/> applies when the source is a directory.
    /// Implementations must NOT overwrite: if <paramref name="destinationPath"/> already exists, throw <see cref="System.IO.IOException"/>.
    /// </summary>
    Task CopyAsync(string sourcePath, string destinationPath, bool recursive, CancellationToken ct = default);

    /// <summary>Disconnects from the remote host.</summary>
    void Disconnect();
}
