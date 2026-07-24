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

namespace Heimdall.App.Services;

/// <summary>
/// Seam abstracting every impure dependency of <see cref="UpdateInstaller"/> (environment,
/// filesystem, process launching) so the orchestration is unit-testable without side effects.
/// </summary>
internal interface IUpdateInstallerHost
{
    /// <summary>Path of the currently running executable, or null when unknown.</summary>
    string? ExecutablePath { get; }

    /// <summary>Identifier of the current process.</summary>
    int ProcessId { get; }

    /// <summary>Creates a unique relauncher script path inside the restrictive staging directory.</summary>
    string CreateScriptPath(string stagingDirectory);

    /// <summary>Creates a unique temporary path for the relauncher transcript (.log).</summary>
    string CreateLogPath();

    /// <summary>Resolves the PowerShell host: pwsh.exe when available, else powershell.exe.</summary>
    string ResolvePowerShellExecutable();

    /// <summary>Probes whether the directory is writable (elevation probe).</summary>
    bool IsDirectoryWritable(string directory);

    /// <summary>Writes text with a restrictive ACL applied atomically at file creation.</summary>
    void WriteProtectedText(string path, string content);

    /// <summary>Checks a file against an expected SHA-256 digest.</summary>
    bool VerifySha256(string path, string expectedSha256);

    /// <summary>Starts a detached, hidden process; returns true when it started.</summary>
    bool StartDetached(string fileName, string arguments);
}
