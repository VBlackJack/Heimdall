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
using System.Runtime.InteropServices;
using Heimdall.Core.Network;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Default ARP table reader backed by platform-specific process and file I/O.
/// </summary>
public sealed class DefaultArpTableReader : IArpTableReader
{
    private const int ProcessTimeoutMs = 5000;

    /// <summary>Windows image name of the ARP tool, which lives in the system directory.</summary>
    internal const string WindowsArpExecutableName = "arp.exe";

    /// <summary>Unix image name of the same tool, resolved through the platform's own PATH.</summary>
    private const string UnixArpExecutableName = "arp";

    /// <summary>Dump the whole table, which is what the parsers expect.</summary>
    private const string ArpArguments = "-a";

    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var output = await ReadCommandOutputAsync(CreateWindowsArpStartInfo(), cancellationToken);
            return ArpTableParser.ParseWindows(output);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (!File.Exists("/proc/net/arp"))
            {
                throw new InvalidOperationException("ARP table is not available on this system.");
            }

            return ArpTableParser.ParseLinuxProcNet(File.ReadAllText("/proc/net/arp"));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var output = await ReadCommandOutputAsync(
                CreateStartInfo(UnixArpExecutableName, string.Empty),
                cancellationToken);
            return ArpTableParser.ParseMacOs(output);
        }

        throw new PlatformNotSupportedException("ARP monitor is not supported on this operating system.");
    }

    /// <summary>
    /// Builds the start info for the Windows ARP tool.
    /// </summary>
    internal static ProcessStartInfo CreateWindowsArpStartInfo() =>
        CreateStartInfo(
            SystemExecutablePath.InSystemDirectory(WindowsArpExecutableName),
            SystemExecutablePath.SystemDirectory);

    private static ProcessStartInfo CreateStartInfo(string fileName, string workingDirectory) =>
        new()
        {
            FileName = fileName,
            Arguments = ArpArguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

    private static async Task<string> ReadCommandOutputAsync(
        ProcessStartInfo psi,
        CancellationToken cancellationToken)
    {
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {psi.FileName} process.");

        var outputTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        if (!proc.WaitForExit(ProcessTimeoutMs))
        {
            try { proc.Kill(); } catch { /* already exited */ }
        }

        return await outputTask;
    }
}
