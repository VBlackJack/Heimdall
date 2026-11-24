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

    /// <summary>
    /// True when the process could not be placed in a job that ends its children with the run, so
    /// that only an interrupted run kills them.
    /// </summary>
    bool JobUnavailable { get; }

    /// <summary>Closes the standard input so the program sees the end of its input at once.</summary>
    void CloseStandardInput();

    /// <summary>Completes when the process has exited; throws <see cref="OperationCanceledException"/> when the token fires first.</summary>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Kills the process and every process it started. Best effort; never throws. Disposing the
    /// process also ends whatever is left in its job, when it has one.
    /// </summary>
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
        RecipeJob? job = RecipeJob.TryCreate();
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The process did not start.");

            // The process is placed in the job right after it starts: Process.Start offers no way to
            // start it suspended, so a child started in the few instants before this call is not in
            // the job. A failure (a job that forbids nesting, a process already gone) is not an error
            // of the run; it is reported as data, and only an interrupted run kills the children then.
            bool contained = job is not null && job.TryAssign(process);
            if (!contained)
            {
                job?.Dispose();
                job = null;
            }

            return new SystemRecipeProcess(process, job);
        }
        catch
        {
            job?.Dispose();
            process.Dispose();
            throw;
        }
    }

    private sealed class SystemRecipeProcess : IRecipeProcess
    {
        private readonly Process _process;

        private readonly RecipeJob? _job;

        public SystemRecipeProcess(Process process, RecipeJob? job)
        {
            _process = process;
            _job = job;
        }

        public bool JobUnavailable => _job is null;

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
            _job?.Terminate();
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
            {
                // Already gone, or not ours to kill: best effort, the caller does not wait on this call.
            }
        }

        public void Dispose()
        {
            // Closing the job ends every process still in it, which is what keeps a child that
            // outlived the program from running on after the run.
            _job?.Dispose();
            _process.Dispose();
        }
    }
}
