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

namespace Heimdall.Core.Security;

/// <summary>
/// Names a Windows system executable by its absolute path instead of leaving the image
/// to the CreateProcess search order.
/// </summary>
/// <remarks>
/// An unqualified image name started with <c>UseShellExecute=false</c> is resolved through
/// the application directory and the process's current directory before the system
/// directory, so an executable of the same name that the process happens to reach - a
/// folder the user last browsed in a file dialog, for instance - is launched with the
/// arguments meant for the system tool. Every launch site pairs one of these paths with
/// <see cref="SystemDirectory"/> as the child's working directory, which takes the current
/// directory out of the search for the child as well.
/// Off Windows the special folders resolve to an empty string; the bare name is then kept,
/// which is exactly the behaviour those platform branches had before.
/// </remarks>
public static class SystemExecutablePath
{
    /// <summary>Directory holding Windows PowerShell inside the system directory.</summary>
    private const string WindowsPowerShellDirectoryName = "WindowsPowerShell";

    /// <summary>Version directory Windows PowerShell has shipped under since its first release.</summary>
    private const string WindowsPowerShellVersionDirectoryName = "v1.0";

    /// <summary>Image name of Windows PowerShell, as opposed to pwsh.exe of PowerShell 7.</summary>
    private const string WindowsPowerShellExecutableName = "powershell.exe";

    /// <summary>System directory (System32), or an empty string when it cannot be resolved.</summary>
    public static string SystemDirectory => Environment.GetFolderPath(Environment.SpecialFolder.System);

    /// <summary>Windows directory, or an empty string when it cannot be resolved.</summary>
    public static string WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>
    /// Absolute path of Windows PowerShell. Named separately because it is the one system
    /// host that does not sit directly in the system directory, and because resolving it by
    /// name would let a PowerShell 7 installation decide which interpreter runs.
    /// </summary>
    public static string WindowsPowerShell
    {
        get
        {
            string systemDirectory = SystemDirectory;
            return string.IsNullOrEmpty(systemDirectory)
                ? WindowsPowerShellExecutableName
                : Path.Combine(
                    systemDirectory,
                    WindowsPowerShellDirectoryName,
                    WindowsPowerShellVersionDirectoryName,
                    WindowsPowerShellExecutableName);
        }
    }

    /// <summary>Absolute path of an executable that lives in the system directory.</summary>
    public static string InSystemDirectory(string executableName) =>
        Combine(SystemDirectory, executableName);

    /// <summary>Absolute path of an executable that lives in the Windows directory itself.</summary>
    public static string InWindowsDirectory(string executableName) =>
        Combine(WindowsDirectory, executableName);

    private static string Combine(string directory, string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        return string.IsNullOrEmpty(directory)
            ? executableName
            : Path.Combine(directory, executableName);
    }
}
