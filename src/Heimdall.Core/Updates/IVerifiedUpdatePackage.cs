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

namespace Heimdall.Core.Updates;

/// <summary>
/// Owns a verified installer, its restrictive staging directory, and a held
/// deny-write handle. Disposing before launch removes the staging directory.
/// </summary>
public interface IVerifiedUpdatePackage : IDisposable
{
    /// <summary>Path of the verified installer.</summary>
    string InstallerPath { get; }

    /// <summary>Published SHA-256 digest pinned to this installer.</summary>
    string ExpectedSha256 { get; }

    /// <summary>Restrictive directory that owns the installer and relauncher.</summary>
    string StagingDirectory { get; }

    /// <summary>
    /// Transfers staging cleanup to the detached relauncher after it has started.
    /// Disposal still releases the held deny-write handle.
    /// </summary>
    void TransferCleanupToRelauncher();
}
