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
    /// The host every supported Windows carries, chosen unconditionally and named by its
    /// absolute path under the system directory.
    /// </summary>
    /// <remarks>
    /// This used to prefer pwsh.exe when the PATH offered it and fall back here
    /// otherwise. Nothing in the code gave a reason for the preference, and it had a
    /// cost that only showed up in support: whether an update behaved one way or the
    /// other depended on whether the user happened to have installed PowerShell 7.
    /// One host means one behaviour to reason about and one to test against.
    /// <para>
    /// The name used to be unqualified, which handed the choice back to the CreateProcess
    /// search order: the application directory and the process's current directory are
    /// searched before the system directory, and this is the launch that goes on to
    /// replace the installed binaries.
    /// </para>
    /// </remarks>
    private static string WindowsPowerShell => SystemExecutablePath.WindowsPowerShell;

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

    /// <inheritdoc/>
    /// <remarks>
    /// Read from the operating system rather than from anything the application configured,
    /// because nothing in the application configured it: the redirection is established by
    /// whatever started the process, and this is the only place its destination is recorded.
    /// Every failure answers null - a diagnostic that cannot be located is simply not carried
    /// forward, and must never be allowed to interrupt an update.
    /// </remarks>
    public string? ResolveStandardErrorFilePath()
    {
        try
        {
            IntPtr handle = NativeMethods.GetStdHandle(NativeMethods.StdErrorHandle);
            if (handle == IntPtr.Zero || handle == NativeMethods.InvalidHandleValue)
            {
                return null;
            }

            if (NativeMethods.GetFileType(handle) != NativeMethods.FileTypeDisk)
            {
                return null;
            }

            var buffer = new System.Text.StringBuilder(1024);
            uint written = NativeMethods.GetFinalPathNameByHandle(
                handle,
                buffer,
                (uint)buffer.Capacity,
                NativeMethods.FileNameNormalized);

            if (written == 0 || written >= buffer.Capacity)
            {
                return null;
            }

            string path = buffer.ToString();

            // The call answers in the extended-length form. Left as it is the path would be
            // handed to the command processor, which does not accept that prefix.
            const string ExtendedLengthPrefix = @"\\?\";
            return path.StartsWith(ExtendedLengthPrefix, StringComparison.Ordinal)
                ? path[ExtendedLengthPrefix.Length..]
                : path;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Core.Logging.FileLogger.Warn($"Standard error destination not resolved: {ex.Message}");
            return null;
        }
    }

    private static class NativeMethods
    {
        internal const int StdErrorHandle = -12;
        internal const uint FileTypeDisk = 0x0001;
        internal const uint FileNameNormalized = 0x0;
        internal static readonly IntPtr InvalidHandleValue = new(-1);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GetStdHandle(int nStdHandle);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetFileType(IntPtr hFile);

        [System.Runtime.InteropServices.DllImport(
            "kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandle(
            IntPtr hFile,
            System.Text.StringBuilder lpszFilePath,
            uint cchFilePath,
            uint dwFlags);
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

    public bool StartDetached(string fileName, string arguments) =>
        Process.Start(CreateDetachedStartInfo(fileName, arguments)) is not null;

    /// <summary>
    /// Builds the start info for the detached relauncher. The working directory is pinned to
    /// the system directory rather than inherited: the inherited one is whatever folder the
    /// user last browsed, and a child holding a handle on the install directory is also the
    /// thing the relauncher is about to replace.
    /// </summary>
    /// <remarks>
    /// The module path is not inherited verbatim either. Heimdall started from a PowerShell 7
    /// session carries PowerShell 7's PSModulePath, and Windows PowerShell given it loads
    /// PowerShell 7's core manifests and cannot load its Security module, so the relauncher ran
    /// without Get-AuthenticodeSignature. The child gets the path PowerShell 7 would have given it.
    /// </remarks>
    internal static ProcessStartInfo CreateDetachedStartInfo(string fileName, string arguments) =>
        CreateDetachedStartInfo(
            fileName,
            arguments,
            Environment.GetEnvironmentVariable(WindowsPowerShellModulePath.VariableName),
            PowerShellModuleRoots.ForCurrentUser(),
            File.Exists);

    internal static ProcessStartInfo CreateDetachedStartInfo(
        string fileName,
        string arguments,
        string? inheritedModulePath,
        PowerShellModuleRoots moduleRoots,
        Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = SystemExecutablePath.SystemDirectory,
        };
        WindowsPowerShellModulePath.ApplyTo(startInfo, inheritedModulePath, moduleRoots, fileExists);
        return startInfo;
    }
}
