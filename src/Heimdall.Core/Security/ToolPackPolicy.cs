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
/// The organisation veto on optional tool packs, read from the machine policy key.
/// </summary>
/// <remarks>
/// <para>One DWORD value per optional pack, named <c>ToolPack</c> followed by the
/// <see cref="ToolPack"/> member name (for example <c>ToolPackAudit</c>), under the same key as
/// <see cref="SftpSudoFallbackPolicy"/>. <c>0</c> forbids the pack, <c>1</c> leaves it free and
/// <c>2</c> is reserved for a later "forced on" and treated as free for now. An absent value, or any
/// other number, means no policy for that pack.</para>
/// <para><see cref="ToolPack.Core"/> is never forbidden, whatever the registry holds.</para>
/// <para>The values are read once, on first use, and kept: a change needs a restart. Only the
/// per-machine hive is read, because the per-user hive is writable by the user the veto
/// constrains.</para>
/// <para>This is a convenience for administrators, not a security boundary: the tools stay in the
/// binary and on disk.</para>
/// </remarks>
public sealed class ToolPackPolicy
{
    /// <summary>The machine policy key, shared with the other Heimdall policy values.</summary>
    public const string PolicyKeyPath = SftpSudoFallbackPolicy.PolicyKeyPath;

    /// <summary>The prefix of every pack value name; the pack name follows it.</summary>
    public const string PolicyValueNamePrefix = "ToolPack";

    /// <summary>The policy value that forbids a pack.</summary>
    public const int ForbiddenValue = 0;

    /// <summary>The policy value that leaves a pack free.</summary>
    public const int NoRestrictionValue = 1;

    /// <summary>Reserved for a later "forced on"; treated as no restriction today.</summary>
    public const int ReservedForcedOnValue = 2;

    private readonly Lazy<IReadOnlySet<ToolPack>> _forbidden;

    /// <param name="readPolicyValue">
    /// Yields the raw value of the named policy value, or null when it is absent or not a number;
    /// the machine registry by default.
    /// </param>
    public ToolPackPolicy(Func<string, int?>? readPolicyValue = null)
    {
        Func<string, int?> read = readPolicyValue ?? ReadMachinePolicyValue;
        _forbidden = new Lazy<IReadOnlySet<ToolPack>>(() => ReadForbidden(read), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The name of the policy value that governs the given pack.</summary>
    public static string ValueNameFor(ToolPack pack) => PolicyValueNamePrefix + pack;

    /// <summary>True when the organisation forbids the pack. Always false for <see cref="ToolPack.Core"/>.</summary>
    public bool IsForbidden(ToolPack pack) => _forbidden.Value.Contains(pack);

    /// <summary>
    /// Turns a raw policy value into a verdict: only <see cref="ForbiddenValue"/> forbids, so a
    /// typo in a policy file never silently removes tools.
    /// </summary>
    internal static bool IsForbiddenValue(int? raw) => raw == ForbiddenValue;

    private static IReadOnlySet<ToolPack> ReadForbidden(Func<string, int?> read)
    {
        HashSet<ToolPack> forbidden = [];
        foreach (ToolPack pack in Enum.GetValues<ToolPack>())
        {
            if (pack == ToolPack.Core)
            {
                continue;
            }

            if (IsForbiddenValue(read(ValueNameFor(pack))))
            {
                forbidden.Add(pack);
            }
        }

        return forbidden;
    }

    private static int? ReadMachinePolicyValue(string valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(PolicyKeyPath, writable: false);
            return key?.GetValue(valueName) is int value ? value : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            Logging.FileLogger.Warn($"Tool pack policy unreadable: {ex.Message}");
            return null;
        }
    }
}
