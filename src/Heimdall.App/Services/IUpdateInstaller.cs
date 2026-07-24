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

using Heimdall.Core.Updates;

namespace Heimdall.App.Services;

/// <summary>
/// Launches the detached relauncher that installs a verified update and restarts the app.
/// </summary>
public interface IUpdateInstaller
{
    /// <summary>
    /// Writes the relauncher script and launches the detached PowerShell host that will
    /// wait for this process to exit, run the verified installer silently, and relaunch
    /// the app. Returns true when the relauncher was launched; false when it could not be
    /// (e.g. the current executable path is unknown). The caller shuts the app down only
    /// when this returns true.
    /// </summary>
    bool BeginInstall(IVerifiedUpdatePackage package);
}
