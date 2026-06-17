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

namespace Heimdall.App.Services;

/// <summary>
/// Result of running the shared update install flow.
/// </summary>
public enum UpdateInstallOutcome
{
    /// <summary>Installer launched; the app is shutting down.</summary>
    Started,

    /// <summary>The relauncher could not be launched (BeginInstall returned false).</summary>
    InstallLaunchFailed,

    /// <summary>The user or a cancellation token cancelled the operation.</summary>
    Cancelled,

    /// <summary>No published checksum or a SHA-256 mismatch was detected.</summary>
    VerificationFailed,

    /// <summary>Any other download failure.</summary>
    DownloadFailed,
}
