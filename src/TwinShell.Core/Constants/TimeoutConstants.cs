/*
 * Copyright 2025 Julien Bombled
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

namespace TwinShell.Core.Constants;

/// <summary>
/// Timeout constants for command execution and network operations.
/// </summary>
public static class TimeoutConstants
{
    /// <summary>
    /// Default command execution timeout in seconds (30 seconds).
    /// </summary>
    public const int CommandTimeoutSeconds = 30;

    /// <summary>
    /// Maximum command execution timeout in seconds (300 seconds = 5 minutes).
    /// </summary>
    public const int MaxCommandTimeoutSeconds = 300;

    /// <summary>
    /// Minimum command execution timeout in seconds (1 second).
    /// </summary>
    public const int MinCommandTimeoutSeconds = 1;

    /// <summary>
    /// PowerShell Gallery search timeout in seconds (60 seconds).
    /// </summary>
    public const int PowerShellGallerySearchTimeoutSeconds = 60;

    /// <summary>
    /// PowerShell Gallery install timeout in seconds (300 seconds = 5 minutes).
    /// </summary>
    public const int PowerShellGalleryInstallTimeoutSeconds = 300;

    /// <summary>
    /// HTTP request timeout in seconds for general API calls (30 seconds).
    /// </summary>
    public const int HttpTimeoutSeconds = 30;
}
