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
using System.Globalization;
using System.IO;
using System.Security;
using Heimdall.Core.Configuration;
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

    /// <summary>Sortable and readable, so successive attempts sit in order on disk.</summary>
    private const string LogTimestampFormat = "yyyyMMdd-HHmmss";
    private const string LogExtension = ".log";
    private const string WritableProbePrefix = "Heimdall_writeprobe_";
    private const string WritableProbeExtension = ".tmp";
    /// <summary>
    /// The host every supported Windows carries, chosen unconditionally.
    /// </summary>
    /// <remarks>
    /// This used to prefer pwsh.exe when the PATH offered it and fall back here
    /// otherwise. Nothing in the code gave a reason for the preference, and it had a
    /// cost that only showed up in support: whether an update behaved one way or the
    /// other depended on whether the user happened to have installed PowerShell 7.
    /// One host means one behaviour to reason about and one to test against.
    /// </remarks>
    private const string WindowsPowerShell = "powershell.exe";

    private readonly string _dataRoot;

    /// <param name="dataRoot">
    /// Application data root. Injectable so a test can point it at a temporary
    /// directory: a test that exercised the real one would read and write the
    /// operator's own profile, which is the defect BL-0063 records.
    /// </param>
    public SystemUpdateInstallerHost(string? dataRoot = null)
    {
        _dataRoot = string.IsNullOrWhiteSpace(dataRoot)
            ? ApplicationDataPathResolver.Resolve()
            : dataRoot;
    }

    public string? ExecutablePath => Environment.ProcessPath;

    public int ProcessId => Environment.ProcessId;

    public string CreateScriptPath(string stagingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        return Path.Combine(
            stagingDirectory,
            $"{ScriptPrefix}{Guid.NewGuid():N}{ScriptExtension}");
    }

    /// <summary>
    /// Where the relauncher writes its transcript: the log directory the application
    /// already shows and already opens for the user.
    /// </summary>
    /// <remarks>
    /// It used to be a random GUID under %TEMP%, and that name was recorded nowhere -
    /// not in the application log, not in the interface. The transcript is the only
    /// account of what happened after the application exited, so an update that failed
    /// left an explanation nobody could find. A sortable, dated name in the directory
    /// the About panel names makes it reachable without any new interface.
    /// </remarks>
    public string CreateLogPath()
    {
        string logsDirectory = ApplicationDataPathResolver.GetLogsDirectory(_dataRoot);
        Directory.CreateDirectory(logsDirectory);
        return Path.Combine(
            logsDirectory,
            $"{ScriptPrefix}{DateTime.UtcNow.ToString(LogTimestampFormat, CultureInfo.InvariantCulture)}{LogExtension}");
    }

    /// <summary>
    /// The relauncher's failure record, in the same directory the application reads it
    /// from - one definition, so writer and reader cannot point at different files.
    /// </summary>
    public string CreateFailureRecordPath()
    {
        string updatesDirectory = ApplicationDataPathResolver.GetUpdatesDirectory(_dataRoot);
        Directory.CreateDirectory(updatesDirectory);
        return UpdateOutcomeStore.FailureRecordPathIn(updatesDirectory);
    }

    /// <summary>Names the PowerShell host the relauncher runs under.</summary>
    /// <remarks>
    /// Always the same one. See <see cref="WindowsPowerShell"/> for why the earlier
    /// preference for pwsh.exe was dropped rather than kept as a fallback.
    /// </remarks>
    public string ResolvePowerShellExecutable() => WindowsPowerShell;

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
