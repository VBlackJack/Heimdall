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
/// Represents a remote copy refused because the transport cannot publish a destination without
/// risking an existing one.
/// </summary>
/// <remarks>
/// Derives from <see cref="IOException"/> so the documented failure contract of
/// <c>IRemoteBrowser.CopyAsync</c> still holds for callers that only catch that type. Callers
/// that can localize the reason match on this type instead.
/// </remarks>
public sealed class RemoteCopyUnsupportedException : IOException
{
    /// <summary>
    /// Initializes a new instance for a copy the transport cannot perform safely.
    /// </summary>
    /// <param name="sourcePath">Requested copy source.</param>
    /// <param name="destinationPath">Requested copy destination.</param>
    /// <param name="transport">Transport that cannot guarantee the destination is untouched.</param>
    public RemoteCopyUnsupportedException(
        string sourcePath,
        string destinationPath,
        string transport)
        : base(
            $"{transport} cannot copy '{sourcePath}' to '{destinationPath}' without risking an " +
            "existing destination: the protocol offers no commit that fails when the destination " +
            "already exists.")
    {
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
        Transport = transport;
    }

    /// <summary>Gets the requested copy source.</summary>
    public string SourcePath { get; }

    /// <summary>Gets the requested copy destination.</summary>
    public string DestinationPath { get; }

    /// <summary>Gets the transport that cannot guarantee an untouched destination.</summary>
    public string Transport { get; }
}
