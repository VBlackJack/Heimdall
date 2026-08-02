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
/// Represents a refused upload whose existing remote destination has an unsupported entry type.
/// </summary>
public sealed class RemoteUploadTargetUnsupportedException : IOException
{
    /// <summary>
    /// Initializes a new instance for the unsupported remote destination.
    /// </summary>
    /// <param name="remotePath">Offending remote destination path.</param>
    /// <param name="kind">Existing destination entry type.</param>
    public RemoteUploadTargetUnsupportedException(string remotePath, RemoteEntryKind kind)
        : base($"Remote upload target has an unsupported existing entry type ({kind}): {remotePath}")
    {
        RemotePath = remotePath;
        Kind = kind;
    }

    /// <summary>Gets the offending remote destination path.</summary>
    public string RemotePath { get; }

    /// <summary>Gets the existing destination entry type.</summary>
    public RemoteEntryKind Kind { get; }
}
