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

namespace Heimdall.Terminal;

/// <summary>
/// Abstraction for a terminal session backed by a pseudo-console or pipe.
/// Implementations manage process lifetime, I/O streaming, and resize signaling.
/// </summary>
public interface ITerminalSession : IDisposable
{
    /// <summary>
    /// Raised when the process writes output data. The payload is a raw byte
    /// chunk that may contain partial UTF-8 sequences or ANSI escape codes.
    /// </summary>
    event Action<ReadOnlyMemory<byte>>? DataReceived;

    /// <summary>
    /// Raised when the child process exits. The argument is the exit code.
    /// A handler added after the session has ended may be invoked synchronously inside the subscription.
    /// </summary>
    event Action<int>? ProcessExited;

    /// <summary>
    /// Whether the child process is still running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// The OS process ID of the child, or null if not yet started.
    /// </summary>
    int? ProcessId { get; }

    /// <summary>
    /// Additional environment variables to inject into the child process.
    /// Set before calling <see cref="StartAsync"/>. Merged with the current process environment.
    /// </summary>
    Dictionary<string, string>? EnvironmentVariables { get; set; }

    /// <summary>
    /// Launches the child process inside a pseudo-console with the given
    /// initial terminal dimensions. The cancellation token can abort a session
    /// start that is in progress; synchronous implementations observe it
    /// cooperatively before starting work.
    /// </summary>
    Task StartAsync(
        string executable,
        string arguments,
        int columns = 80,
        int rows = 24,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes raw bytes to the process stdin (keyboard input, escape sequences).
    /// </summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>
    /// Convenience overload that UTF-8 encodes <paramref name="text"/> before writing.
    /// </summary>
    void Write(string text);

    /// <summary>
    /// Signals the pseudo-console to resize to the given dimensions.
    /// </summary>
    void Resize(int columns, int rows);

    /// <summary>
    /// Forcefully terminates the child process.
    /// </summary>
    void Kill();
}
