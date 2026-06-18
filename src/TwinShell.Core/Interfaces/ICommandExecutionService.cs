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

using TwinShell.Core.Enums;
using TwinShell.Core.Models;

namespace TwinShell.Core.Interfaces;

/// <summary>
/// Service for executing PowerShell and Bash commands
/// </summary>
public interface ICommandExecutionService
{
    /// <summary>
    /// Executes a command on the specified platform
    /// </summary>
    /// <param name="command">Command to execute</param>
    /// <param name="platform">Platform (Windows = PowerShell, Linux = Bash)</param>
    /// <param name="cancellationToken">Cancellation token to stop execution</param>
    /// <param name="timeoutSeconds">Timeout in seconds (default: 30)</param>
    /// <param name="onOutputReceived">Callback for real-time output (optional)</param>
    /// <returns>Execution result</returns>
    Task<ExecutionResult> ExecuteAsync(
        string command,
        Platform platform,
        CancellationToken cancellationToken,
        int timeoutSeconds = 30,
        Action<OutputLine>? onOutputReceived = null);
}
