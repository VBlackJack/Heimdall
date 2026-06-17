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
/// Shared update install flow used by the settings screen and the update banner.
/// </summary>
public interface IUpdateInstallFlow
{
    /// <summary>
    /// Downloads the verified installer, launches the relauncher, and requests app shutdown on
    /// success. Returns the outcome; never throws for the handled failure modes.
    /// </summary>
    Task<UpdateInstallOutcome> RunAsync(
        UpdateInfo update,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}
