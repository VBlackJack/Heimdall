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

using System.ComponentModel;
using System.IO;
using System.Security;
using System.Text;
using Heimdall.Core.Logging;
using Heimdall.Core.Updates;

namespace Heimdall.App.Services;

/// <summary>
/// Orchestrates the in-app update install: builds the relauncher script for a verified
/// installer, writes it to disk, and launches a detached PowerShell host. Every impure
/// operation is delegated to <see cref="IUpdateInstallerHost"/>; this class never shuts
/// the app down (that is the caller's job when <see cref="BeginInstall"/> returns true).
/// </summary>
internal sealed class UpdateInstaller : IUpdateInstaller
{
    private readonly IUpdateInstallerHost _host;

    public UpdateInstaller(IUpdateInstallerHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public bool BeginInstall(IVerifiedUpdatePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (string.IsNullOrWhiteSpace(package.InstallerPath)
            || string.IsNullOrWhiteSpace(package.StagingDirectory)
            || !IsSha256Hex(package.ExpectedSha256))
        {
            FileLogger.Warn("Update install aborted: the verified package metadata is invalid.");
            return false;
        }

        var exe = _host.ExecutablePath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            FileLogger.Warn("Update install aborted: cannot relaunch, unknown executable path.");
            return false;
        }

        string? installDir = Path.GetDirectoryName(exe);
        if (string.IsNullOrWhiteSpace(installDir))
        {
            FileLogger.Warn("Update install aborted: cannot determine the install directory.");
            return false;
        }

        bool launched;
        try
        {
            // Inside the try, all three: two of them create directories, and on a full
            // disk or a locked data directory they threw out of this method with the
            // attempt record already written, so the next startup announced a failure
            // for an update that never started.
            string scriptPath = _host.CreateScriptPath(package.StagingDirectory);
            string logPath = _host.CreateLogPath();
            string failureRecordPath = _host.CreateFailureRecordPath();
            bool requiresElevation = !_host.IsDirectoryWritable(installDir);

            var spec = new UpdateRelaunchSpec(
                InstallerPath: package.InstallerPath,
                ExpectedInstallerSha256: package.ExpectedSha256,
                TargetExecutablePath: exe,
                ProcessId: _host.ProcessId,
                ScriptPath: scriptPath,
                StagingDirectory: package.StagingDirectory,
                RequiresElevation: requiresElevation,
                InstallerArguments: UpdateRelaunchScript.InstallerArgumentsFor(installDir),
                LogPath: logPath,
                FailureRecordPath: failureRecordPath);

            string script = UpdateRelaunchScript.Build(spec);
            string expectedScriptSha256 = ComputeTextSha256(script);
            _host.WriteProtectedText(scriptPath, script);
            if (!_host.VerifySha256(scriptPath, expectedScriptSha256))
            {
                FileLogger.Warn("Update install aborted: relauncher script integrity check failed.");
                return false;
            }

            var psHost = _host.ResolvePowerShellExecutable();
            string args = UpdateRelaunchScript.BuildPowerShellArguments(
                scriptPath,
                expectedScriptSha256,
                exe,
                failureRecordPath);
            launched = _host.StartDetached(psHost, args);

            // The transcript path is recorded here because it is the only account of what
            // happens after this process exits, and until now its name appeared nowhere.
            FileLogger.Info(
                $"Update relauncher launched={launched} elevation={requiresElevation} transcript={logPath}");
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or Win32Exception
            or SecurityException
            or NotSupportedException
            or ArgumentException)
        {
            FileLogger.WarnDetailed("Update install failed to launch the relauncher", ex);
            return false;
        }

        return launched;
    }

    private static string ComputeTextSha256(string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(bytes, writable: false);
        return Sha256Verifier.ComputeHex(stream);
    }

    private static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}
