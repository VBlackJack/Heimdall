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
using System.Security;
using Heimdall.Core.Security;
using Heimdall.Core.Updates;

namespace Heimdall.App.Services;

/// <summary>
/// Production <see cref="IUpdateInstallerHost"/>: binds the relauncher orchestration to the
/// real environment, filesystem, and process launcher.
/// </summary>
internal sealed class SystemUpdateInstallerHost : IUpdateInstallerHost
{
    private const string ScriptPrefix = "Heimdall_relaunch_";
    private const string ScriptExtension = ".ps1";
    private const string LogExtension = ".log";
    private const string WritableProbePrefix = "Heimdall_writeprobe_";
    private const string WritableProbeExtension = ".tmp";
    private const string PreferredPowerShell = "pwsh.exe";
    private const string FallbackPowerShell = "powershell.exe";

    public string? ExecutablePath => Environment.ProcessPath;

    public int ProcessId => Environment.ProcessId;

    public string CreateScriptPath(string stagingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        return Path.Combine(
            stagingDirectory,
            $"{ScriptPrefix}{Guid.NewGuid():N}{ScriptExtension}");
    }

    public string CreateLogPath() =>
        Path.Combine(Path.GetTempPath(), $"{ScriptPrefix}{Guid.NewGuid():N}{LogExtension}");

    public string ResolvePowerShellExecutable()
    {
        try
        {
            var pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVariable))
            {
                foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = Path.Combine(directory.Trim(), PreferredPowerShell);
                    if (File.Exists(candidate))
                    {
                        return PreferredPowerShell;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or SecurityException or UnauthorizedAccessException)
        {
            // Fall through to the always-present Windows PowerShell host.
        }

        return FallbackPowerShell;
    }

    public bool IsDirectoryWritable(string directory)
    {
        var probe = Path.Combine(directory, $"{WritableProbePrefix}{Guid.NewGuid():N}{WritableProbeExtension}");
        try
        {
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (SecurityException)
        {
            return false;
        }
    }

    public void WriteProtectedText(string path, string content) =>
        SecureFileWriter.WriteAndProtect(path, content);

    public bool VerifySha256(string path, string expectedSha256) =>
        Sha256Verifier.Verify(path, expectedSha256);

    public bool StartDetached(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        return Process.Start(startInfo) is not null;
    }
}
