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
/// The external credential command could not be started, typically because its executable is
/// not installed or not on the PATH.
/// </summary>
/// <remarks>
/// Reported apart from an empty answer: "the vault returned no password" sent users to check
/// the entry name and the unlock secret when the tool itself was missing.
/// </remarks>
public sealed class CredentialProviderLaunchException : Exception
{
    /// <summary>Initializes the exception for the executable that failed to start.</summary>
    /// <param name="executable">The executable named by the command template.</param>
    /// <param name="innerException">The launch failure.</param>
    public CredentialProviderLaunchException(string executable, Exception innerException)
        : base($"The credential command '{executable}' could not be started.", innerException)
    {
        Executable = executable;
    }

    /// <summary>The executable named by the command template.</summary>
    public string Executable { get; }
}
