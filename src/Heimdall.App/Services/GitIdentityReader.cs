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
using System.IO;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services;

/// <summary>
/// The name and e-mail address a person has given Git on this machine; either may be missing.
/// </summary>
public sealed record GitIdentity(string? Name, string? Email);

/// <summary>
/// Reads the operator's global Git identity, so the Command Library sync can sign its commits
/// with it rather than with an anonymous placeholder.
/// </summary>
public interface IGitIdentityReader
{
    /// <summary>
    /// The global <c>user.name</c> and <c>user.email</c>, or <c>null</c> when no <c>git</c>
    /// executable is on the path or it answered nothing. Never throws.
    /// </summary>
    Task<GitIdentity?> ReadGlobalIdentityAsync();
}

/// <summary>
/// Asks the <c>git</c> found on the path for <c>git config --global user.name</c> and
/// <c>user.email</c>, each bounded by <see cref="ReadTimeout"/>.
/// </summary>
/// <remarks>
/// The executable is resolved to a full path from <c>PATH</c> before it is started, so a
/// <c>git.exe</c> in the current directory is never the one that runs.
/// </remarks>
public sealed class GitConfigIdentityReader : IGitIdentityReader
{
    /// <summary>How long one <c>git config</c> call may take before it is abandoned.</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private const string WindowsGitExecutableName = "git.exe";
    private const string UnixGitExecutableName = "git";
    private const string NameArguments = "config --global user.name";
    private const string EmailArguments = "config --global user.email";

    /// <inheritdoc/>
    public async Task<GitIdentity?> ReadGlobalIdentityAsync()
    {
        try
        {
            var git = ConnectionHelpers.FindInPath(OperatingSystem.IsWindows() ? WindowsGitExecutableName : UnixGitExecutableName);
            if (git is null || !Path.IsPathFullyQualified(git))
            {
                return null;
            }

            var name = await ReadValueAsync(git, NameArguments).ConfigureAwait(false);
            var email = await ReadValueAsync(git, EmailArguments).ConfigureAwait(false);

            return name is null && email is null ? null : new GitIdentity(name, email);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[CommandLibrary] Reading the Git identity failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<string?> ReadValueAsync(string git, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = git,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(git) ?? string.Empty,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        using var timeout = new CancellationTokenSource(ReadTimeout);
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var value = output.Trim();
            return process.ExitCode == 0 && value.Length > 0 ? value : null;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            return null;
        }
    }
}
