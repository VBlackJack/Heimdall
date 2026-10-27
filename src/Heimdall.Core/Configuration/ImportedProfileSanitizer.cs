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

namespace Heimdall.Core.Configuration;

/// <summary>A local file path value the import boundary dropped.</summary>
/// <param name="LabelKey">Locale key of the field's name, as the profile dialog shows it.</param>
/// <param name="Value">The value that was dropped, so the user can re-enter it if they trust it.</param>
public sealed record ImportedPathRemoval(string LabelKey, string Value);

/// <summary>What the import boundary took away from one imported profile or gateway.</summary>
/// <param name="Name">The profile's or gateway's display name.</param>
/// <param name="ClearedPaths">The local file paths dropped because they named a share or a device.</param>
/// <param name="ResetFlags">The switches of <see cref="ImportedTrustFlags.Denylist"/> that were on.</param>
public sealed record ImportedItemSanitization(
    string Name,
    IReadOnlyList<ImportedPathRemoval> ClearedPaths,
    IReadOnlyList<ImportedTrustFlag> ResetFlags);

/// <summary>What the import boundary took away, item by item, for the import to tell the user.</summary>
/// <param name="Items">One entry per profile or gateway that lost something, in import order.</param>
public sealed record ImportedProfileSanitizationReport(IReadOnlyList<ImportedItemSanitization> Items)
{
    /// <summary>A report of nothing taken away.</summary>
    public static ImportedProfileSanitizationReport Empty { get; } = new([]);

    /// <summary>Whether anything was taken away.</summary>
    public bool HasChanges => Items.Count > 0;

    /// <summary>How many local file path values were dropped, over every item.</summary>
    public int ClearedPathCount => Items.Sum(item => item.ClearedPaths.Count);

    /// <summary>This report followed by <paramref name="other"/>.</summary>
    public ImportedProfileSanitizationReport Concat(ImportedProfileSanitizationReport other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return other.Items.Count == 0 ? this
            : Items.Count == 0 ? other
            : new ImportedProfileSanitizationReport([.. Items, .. other.Items]);
    }
}

/// <summary>
/// Strips imported profile fields that must only be populated by trusted
/// local discovery or scanner flows before profiles cross into persisted
/// application configuration, and resets local-execution trust on import.
/// </summary>
public static class ImportedProfileSanitizer
{
    /// <summary>Locale key naming a gateway's key path in the import report.</summary>
    public const string GatewayKeyPathLabelKey = "GatewayDialogLabelKeyPath";

    /// <summary>
    /// The local file path fields of a profile, with the locale key of their name and the way to
    /// read and clear them. A path a file chose must not be a network share or a device
    /// (<see cref="ImportedPathPolicy"/>).
    /// </summary>
    private static readonly (string LabelKey, Func<ServerProfileDto, string?> Get, Action<ServerProfileDto> Clear)[] LocalPathFields =
    [
        ("ServerDialogLabelSshKey", static profile => profile.SshKeyPath, static profile => profile.SshKeyPath = null),
        ("ServerDialogLabelIcaFilePath", static profile => profile.CitrixIcaFilePath, static profile => profile.CitrixIcaFilePath = null),
        ("ServerDialogLabelExecutable", static profile => profile.LocalShellExecutable, static profile => profile.LocalShellExecutable = null),
        ("ServerDialogLabelWorkingDir", static profile => profile.LocalShellWorkingDirectory, static profile => profile.LocalShellWorkingDirectory = null),
        ("ServerDialogWebExternalProgram", static profile => profile.WebExternalProgram, static profile => profile.WebExternalProgram = null),
    ];

    /// <summary>
    /// Clears fields that are intended to be produced only by trusted local
    /// scanners and must not cross the trust boundary of an external import,
    /// clears any imported local-execution or certificate-skip trust assertion, turns off the
    /// switches of <see cref="ImportedTrustFlags.Denylist"/>, and drops local file paths that
    /// name a network share or a device.
    /// New rules can be added here as additional trust-boundary fields are identified.
    /// </summary>
    /// <returns>What was taken away from each profile, for the import to report.</returns>
    public static ImportedProfileSanitizationReport Sanitize(IList<ServerProfileDto> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        List<ImportedItemSanitization> items = [];
        foreach (var profile in profiles)
        {
            if (profile is null)
            {
                continue;
            }

            profile.CitrixLaunchCommandLine = null;
            profile.ExecutionConfirmed = false;

            // A trust decision about the remote certificate, made on the exporting machine.
            profile.WinRmSkipCertificateCheck = false;

            IReadOnlyList<ImportedTrustFlag> resetFlags = ImportedTrustFlags.Reset(profile);
            IReadOnlyList<ImportedPathRemoval> clearedPaths = ClearNetworkPaths(profile);
            if (resetFlags.Count > 0 || clearedPaths.Count > 0)
            {
                items.Add(new ImportedItemSanitization(profile.DisplayName, clearedPaths, resetFlags));
            }
        }

        return items.Count == 0 ? ImportedProfileSanitizationReport.Empty : new ImportedProfileSanitizationReport(items);
    }

    /// <summary>
    /// Drops the key path of an imported gateway when it names a network share or a device.
    /// </summary>
    /// <returns>What was dropped, or null when the gateway keeps everything.</returns>
    public static ImportedItemSanitization? SanitizeGatewayPaths(SshGatewayDto gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        string? keyPath = gateway.KeyPath;
        if (!ImportedPathPolicy.IsNetworkOrDevicePath(keyPath))
        {
            return null;
        }

        gateway.KeyPath = null;
        return new ImportedItemSanitization(
            gateway.Name,
            [new ImportedPathRemoval(GatewayKeyPathLabelKey, keyPath!)],
            []);
    }

    private static List<ImportedPathRemoval> ClearNetworkPaths(ServerProfileDto profile)
    {
        List<ImportedPathRemoval> cleared = [];
        foreach ((string labelKey, Func<ServerProfileDto, string?> get, Action<ServerProfileDto> clear) in LocalPathFields)
        {
            string? value = get(profile);
            if (ImportedPathPolicy.IsNetworkOrDevicePath(value))
            {
                clear(profile);
                cleared.Add(new ImportedPathRemoval(labelKey, value!));
            }
        }

        return cleared;
    }
}
