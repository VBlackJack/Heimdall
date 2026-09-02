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

using System.Diagnostics;
using System.Text;
using Heimdall.Core.Security;
using Microsoft.Extensions.Logging;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;

namespace TwinShell.Infrastructure.Services;

/// <summary>
/// Service for executing PowerShell and Bash commands using System.Diagnostics.Process
/// </summary>
public sealed class CommandExecutionService : ICommandExecutionService
{
    /// <summary>Shell the Unix branch runs, resolved through the platform's own PATH.</summary>
    private const string LinuxShellExecutableName = "bash";

    private readonly ILogger<CommandExecutionService>? _logger;

    public CommandExecutionService(ILogger<CommandExecutionService>? logger = null)
    {
        _logger = logger;
    }
    /// <summary>
    /// Executes a command on the specified platform
    /// </summary>
    public async Task<ExecutionResult> ExecuteAsync(
        string command,
        Platform platform,
        CancellationToken cancellationToken,
        int timeoutSeconds = 30,
        Action<OutputLine>? onOutputReceived = null)
    {
        var result = new ExecutionResult
        {
            StartedAt = DateTime.UtcNow
        };

        var stopwatch = Stopwatch.StartNew();
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        // Declare event handlers outside to allow detachment in finally block
        DataReceivedEventHandler? outputHandler = null;
        DataReceivedEventHandler? errorHandler = null;
        Process? process = null;

        try
        {
            // Determine executable and arguments based on platform
            var processStartInfo = CreateProcessStartInfo(command, platform);

            process = new Process { StartInfo = processStartInfo };

            // BUGFIX: Declare event handlers for detachment in finally block
            outputHandler = (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    stdoutBuilder.AppendLine(e.Data);
                    onOutputReceived?.Invoke(new OutputLine
                    {
                        Text = e.Data,
                        IsError = false,
                        Timestamp = DateTime.UtcNow
                    });
                }
            };

            errorHandler = (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    stderrBuilder.AppendLine(e.Data);
                    onOutputReceived?.Invoke(new OutputLine
                    {
                        Text = e.Data,
                        IsError = true,
                        Timestamp = DateTime.UtcNow
                    });
                }
            };

            // Attach event handlers
            process.OutputDataReceived += outputHandler;
            process.ErrorDataReceived += errorHandler;

            // Start the process
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Create timeout cancellation token source
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            // Wait for process to exit or cancellation
            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Kill the process if cancelled or timed out
                try
                {
                    if (!process.HasExited)
                    {
                        // BUGFIX: entireProcessTree parameter is Windows-only
                        if (OperatingSystem.IsWindows())
                        {
                            process.Kill(entireProcessTree: true);
                        }
                        else
                        {
                            process.Kill();
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process already exited - ignore
                }

                result.WasCancelled = cancellationToken.IsCancellationRequested;
                result.TimedOut = timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
                result.Success = false;
                result.ErrorMessage = result.TimedOut
                    ? $"Execution timed out after {timeoutSeconds} seconds"
                    : "Execution was cancelled by user";

                stopwatch.Stop();
                result.Duration = stopwatch.Elapsed;
                result.Stdout = stdoutBuilder.ToString();
                result.Stderr = stderrBuilder.ToString();
                result.ExitCode = -1;

                return result;
            }

            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
            result.ExitCode = process.ExitCode;
            result.Stdout = stdoutBuilder.ToString();
            result.Stderr = stderrBuilder.ToString();
            result.Success = process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
            result.Success = false;

            // Log the full exception details securely on the server side
            _logger?.LogError(ex, "Command execution failed");

            // Return only a generic error message to the user (no stack trace exposure)
            result.ErrorMessage = "Command execution failed";
            result.ExitCode = -1;
            result.Stderr = string.Empty; // Do not expose exception details
        }
        finally
        {
            // BUGFIX: Detach event handlers to prevent memory leaks
            if (process != null)
            {
                if (outputHandler != null)
                {
                    process.OutputDataReceived -= outputHandler;
                }
                if (errorHandler != null)
                {
                    process.ErrorDataReceived -= errorHandler;
                }
                process.Dispose();
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the start info for a command, so a test can inspect the image the child runs.
    /// </summary>
    internal ProcessStartInfo CreateProcessStartInfo(string command, Platform platform)
    {
        var (executable, arguments, workingDirectory) = GetExecutableAndArguments(command, platform);

        return new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory
        };
    }

    /// <summary>
    /// Gets the executable, arguments and working directory based on platform. The Windows
    /// host is named by absolute path and the child's working directory is pinned to the
    /// system directory: an unqualified name would be resolved by CreateProcess through the
    /// application directory and this process's current directory before the system one.
    /// </summary>
    private (string executable, string arguments, string workingDirectory) GetExecutableAndArguments(
        string command,
        Platform platform)
    {
        // Detect current OS if platform is "Both"
        var actualPlatform = platform;
        if (platform == Platform.Both)
        {
            actualPlatform = OperatingSystem.IsWindows() ? Platform.Windows : Platform.Linux;
        }

        return actualPlatform switch
        {
            Platform.Windows => (
                SystemExecutablePath.WindowsPowerShell,
                BuildPowerShellCommand(command),
                SystemExecutablePath.SystemDirectory),
            Platform.Linux => (LinuxShellExecutableName, BuildBashCommand(command), string.Empty),
            _ => throw new NotSupportedException($"Platform {platform} is not supported for command execution")
        };
    }

    /// <summary>
    /// Builds a safe PowerShell command using Base64 encoding to avoid escaping issues
    /// </summary>
    private string BuildPowerShellCommand(string command)
    {
        // Use base64 encoding to avoid all escaping issues
        // This is the safest approach for PowerShell command execution
        var bytes = Encoding.Unicode.GetBytes(command);
        var encoded = Convert.ToBase64String(bytes);
        return $"-NoProfile -NonInteractive -EncodedCommand {encoded}";
    }

    /// <summary>
    /// Builds a safe Bash command using single quotes
    /// </summary>
    private string BuildBashCommand(string command)
    {
        // Use single quotes which treat everything as literal
        // Only need to escape single quotes themselves
        var escaped = "'" + command.Replace("'", "'\\''") + "'";
        return $"-c {escaped}";
    }

}
