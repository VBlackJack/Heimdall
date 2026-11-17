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
/// A privileged write or read was refused for its path or its file: the path goes through a symbolic
/// link that is not trusted, or the file has other hard links in a folder that others can write to.
/// </summary>
/// <remarks>
/// The refusal holds until somebody changes the server, so it is not retried.
/// </remarks>
public sealed class PrivilegedTransferRefusedException : InvalidOperationException
{
    public PrivilegedTransferRefusedException(EntryRefusalReason reason, string remotePath)
        : base($"The privileged transfer of '{remotePath}' was refused: {reason}.")
    {
        Reason = reason;
        RemotePath = remotePath ?? string.Empty;
    }

    /// <summary>Why the transfer was refused: <see cref="EntryRefusalReason.PathLink"/> or <see cref="EntryRefusalReason.HardLink"/>.</summary>
    public EntryRefusalReason Reason { get; }

    /// <summary>The remote path the transfer was for.</summary>
    public string RemotePath { get; }
}
