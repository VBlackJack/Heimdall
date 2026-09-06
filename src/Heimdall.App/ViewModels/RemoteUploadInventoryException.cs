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

namespace Heimdall.App.ViewModels;

/// <summary>
/// The destination could not be listed before an upload, so the batch was refused before its
/// first byte.
/// </summary>
/// <remarks>
/// A listing that fails for a reason other than the path's absence says nothing about what the
/// destination holds. Reading it as "no conflicts" sent every file as a replacement on a
/// network hiccup; the batch is refused instead, and nothing was written.
/// </remarks>
public sealed class RemoteUploadInventoryException : Exception
{
    public RemoteUploadInventoryException(string remoteDirectory, Exception innerException)
        : base($"Could not list the remote directory before uploading: {remoteDirectory}", innerException)
    {
        RemoteDirectory = remoteDirectory;
    }

    public string RemoteDirectory { get; }
}
