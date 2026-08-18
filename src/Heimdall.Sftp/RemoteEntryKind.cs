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
    /// every file-only path accepted it. An entry of this kind is never transferred: its content is
    /// not known to be byte-addressable, and reading it can block forever or return data that does
    /// not correspond to a stored object.
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
