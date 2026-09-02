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

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Starts the external Windows RDP client.
/// </summary>
internal interface IRdpExternalClientLauncher
{
    /// <summary>
    /// Launches mstsc.exe with the generated RDP file and returns a handle
    /// that can be observed for process exit.
    /// </summary>
    ILaunchedRdpClientProcess? Launch(string rdpFilePath);
}

/// <summary>
/// Observable handle for a launched external RDP client process.
/// </summary>
internal interface ILaunchedRdpClientProcess : IDisposable
{
    /// <summary>Operating system process identifier.</summary>
    int Id { get; }

    /// <summary>Exit code once the process has terminated.</summary>
    int ExitCode { get; }

    /// <summary>Enables or disables process exit notifications.</summary>
    bool EnableRaisingEvents { get; set; }

    /// <summary>Raised when the external process exits.</summary>
    event EventHandler Exited;
}

/// <summary>
/// Production launcher for mstsc.exe.
/// </summary>
internal sealed class MstscRdpExternalClientLauncher : IRdpExternalClientLauncher
{
    internal const string MstscExecutableName = "mstsc.exe";

    public ILaunchedRdpClientProcess? Launch(string rdpFilePath)
    {
        var process = Process.Start(CreateStartInfo(rdpFilePath));

        return process is null ? null : new ProcessRdpClientProcess(process);
    }

    /// <summary>
    /// Builds the start info for mstsc.exe. The image is named by its absolute path under
    /// the system directory, and the working directory is pinned there too: an unqualified
    /// name would be resolved through the CreateProcess search order, which reaches the
    /// process's current directory - a folder the user last browsed in a file dialog -
    /// before System32, and the client is handed the generated .rdp file plus a Credential
    /// Manager entry already primed with the profile password.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(string rdpFilePath)
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string executablePath = string.IsNullOrEmpty(systemDirectory)
            ? MstscExecutableName
            : Path.Combine(systemDirectory, MstscExecutableName);

        return new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = $"\"{rdpFilePath}\"",
            UseShellExecute = false,
            WorkingDirectory = systemDirectory
        };
    }

    private sealed class ProcessRdpClientProcess(Process process) : ILaunchedRdpClientProcess
    {
        public int Id => process.Id;

        public int ExitCode => process.ExitCode;

        public bool EnableRaisingEvents
        {
            get => process.EnableRaisingEvents;
            set => process.EnableRaisingEvents = value;
        }

        public event EventHandler Exited
        {
            add => process.Exited += value;
            remove => process.Exited -= value;
        }

        public void Dispose() => process.Dispose();
    }
}
