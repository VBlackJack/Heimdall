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

using System.Diagnostics;

namespace Heimdall.Core.Recipes;

/// <summary>A started process, reduced to what the runner uses; the seam that keeps most tests free of real processes.</summary>
internal interface IRecipeProcess : IDisposable
{
    /// <summary>The raw standard output stream.</summary>
    Stream StandardOutput { get; }

    /// <summary>The raw standard error stream.</summary>
    Stream StandardError { get; }

    /// <summary>The exit code; valid once the process has exited.</summary>
    int ExitCode { get; }

    /// <summary>Closes the standard input so the program sees the end of its input at once.</summary>
    void CloseStandardInput();

    /// <summary>Completes when the process has exited; throws <see cref="OperationCanceledException"/> when the token fires first.</summary>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Kills the process and every process it started. Best effort; never throws.</summary>
    void KillTree();
}

/// <summary>Starts processes for the runner.</summary>
internal interface IRecipeProcessLauncher
{
    /// <summary>Starts the process described by <paramref name="startInfo"/>.</summary>
    /// <exception cref="Exception">Any failure to start; the runner reports its type and nothing else.</exception>
    IRecipeProcess Start(ProcessStartInfo startInfo);
}

/// <summary>The launcher that starts real operating system processes.</summary>
internal sealed class SystemRecipeProcessLauncher : IRecipeProcessLauncher
{
    /// <inheritdoc />
    public IRecipeProcess Start(ProcessStartInfo startInfo)
    {
        Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The process did not start.");

            return new SystemRecipeProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private sealed class SystemRecipeProcess : IRecipeProcess
    {
        private readonly Process _process;

        public SystemRecipeProcess(Process process)
        {
            _process = process;
        }

        public Stream StandardOutput => _process.StandardOutput.BaseStream;

        public Stream StandardError => _process.StandardError.BaseStream;

        public int ExitCode => _process.ExitCode;

        public void CloseStandardInput()
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The program already closed its end of the pipe; there is nothing left to close.
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);

        public void KillTree()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
            {
                // Already gone, or not ours to kill: best effort, the caller does not wait on this call.
            }
        }

        public void Dispose() => _process.Dispose();
    }
}
