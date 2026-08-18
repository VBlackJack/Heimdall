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

/// <summary>Identifies the remote filesystem entry type reported by a listing producer.</summary>
public enum RemoteEntryKind
{
    /// <summary>An entry whose type could not be determined.</summary>
    /// <remarks>
    /// Zero on purpose, so an uninitialised or unmapped value is the non-transferable one. When the
    /// regular file was zero, forgetting to classify something made it look like an ordinary file and
    /// every file-only path accepted it.
    /// <para>
    /// The application transfer planner and the guarded SFTP upload path reject this kind, because its
    /// content is not known to be byte-addressable: reading it can block indefinitely or return data
    /// that corresponds to no stored object.
    /// </para>
    /// <para>
    /// This value is classification metadata, not an execution guard. A browser API that operates
    /// directly on a path is free to never consult it, and some do not, so each such API must enforce
    /// its own safety policy rather than assume this kind has already stopped the caller.
    /// </para>
    /// </remarks>
    Unknown = 0,

    /// <summary>A regular file.</summary>
    File,

    /// <summary>A directory.</summary>
    Directory,

    /// <summary>A symbolic link.</summary>
    SymbolicLink,

    /// <summary>A POSIX named pipe.</summary>
    Fifo,

    /// <summary>A POSIX socket.</summary>
    Socket,

    /// <summary>A POSIX block or character device.</summary>
    Device,
}
