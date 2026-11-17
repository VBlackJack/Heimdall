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

using Heimdall.Core.Models;
using Microsoft.Win32;

namespace Heimdall.Core.Security;

/// <summary>
/// The organisation cap on <see cref="SftpSudoFallbackMode"/>, read from the machine policy key.
/// </summary>
/// <remarks>
/// <para>The value is read once, on first use, and kept: a change needs a restart. Only the
/// per-machine hive is read, because the per-user hive is writable by the user the cap
/// constrains.</para>
/// <para>This is a convenience for administrators, not a security boundary. The server's sudo
/// rules remain the real control over what a connected user can do as root.</para>
/// </remarks>
public sealed class SftpSudoFallbackPolicy
{
    /// <summary>The machine policy key, under the per-machine hive.</summary>
    public const string PolicyKeyPath = @"Software\Policies\Heimdall";

    /// <summary>The DWORD value that holds the cap: 0 Never, 1 Ask, 2 Automatic.</summary>
    public const string PolicyValueName = "SftpSudoFallbackMax";

    private readonly Lazy<SftpSudoFallbackMode?> _cap;

    /// <param name="readPolicyValue">
    /// Yields the raw policy value, or null when it is absent or not a number; the machine
    /// registry by default.
    /// </param>
    public SftpSudoFallbackPolicy(Func<int?>? readPolicyValue = null)
    {
        Func<int?> read = readPolicyValue ?? ReadMachinePolicyValue;
        _cap = new Lazy<SftpSudoFallbackMode?>(() => ParseCap(read()), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The cap the organisation set, or null when there is none.</summary>
    public SftpSudoFallbackMode? Cap => _cap.Value;

    /// <summary>The stricter of the user's choice and the cap.</summary>
    public SftpSudoFallbackMode Effective(SftpSudoFallbackMode userChoice)
        => Cap is { } cap && cap < userChoice ? cap : userChoice;

    /// <summary>
    /// Turns a raw policy value into a cap; a missing or out-of-range value means no cap, so a
    /// typo in a policy file never locks anyone out or silently tightens the application.
    /// </summary>
    internal static SftpSudoFallbackMode? ParseCap(int? raw)
        => raw is { } value && Enum.IsDefined(typeof(SftpSudoFallbackMode), value)
            ? (SftpSudoFallbackMode)value
            : null;

    private static int? ReadMachinePolicyValue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(PolicyKeyPath, writable: false);
            return key?.GetValue(PolicyValueName) is int value ? value : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            Logging.FileLogger.Warn($"SFTP sudo policy unreadable: {ex.Message}");
            return null;
        }
    }
}
