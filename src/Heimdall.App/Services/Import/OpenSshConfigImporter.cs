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

using System.IO;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;

namespace Heimdall.App.Services.Import;

/// <summary>
/// Computes import statuses and persists selected OpenSSH config candidates.
/// </summary>
public sealed class OpenSshConfigImporter(IConfigManager configManager)
{
    private readonly IConfigManager _configManager = configManager;

    /// <summary>
    /// Computes candidate import statuses against the current server inventory.
    /// </summary>
    public async Task<IReadOnlyList<OpenSshImportCandidateAssessment>> ComputeStatusesAsync(
        IReadOnlyList<OpenSshImportCandidate> candidates,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        ct.ThrowIfCancellationRequested();
        var existingProfiles = await _configManager.LoadServersAsync().ConfigureAwait(false);
        var settings = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        return ComputeStatuses(candidates, existingProfiles, settings.SshGateways);
    }

    /// <summary>
    /// Computes candidate import statuses against the supplied server inventory.
    /// </summary>
    public IReadOnlyList<OpenSshImportCandidateAssessment> ComputeStatuses(
        IReadOnlyList<OpenSshImportCandidate> candidates,
        IEnumerable<ServerProfileDto> existingProfiles)
    {
        return ComputeStatuses(candidates, existingProfiles, []);
    }

    /// <summary>
    /// Computes candidate import statuses against the supplied server and gateway inventory.
    /// </summary>
    public IReadOnlyList<OpenSshImportCandidateAssessment> ComputeStatuses(
        IReadOnlyList<OpenSshImportCandidate> candidates,
        IEnumerable<ServerProfileDto> existingProfiles,
        IEnumerable<SshGatewayDto> existingGateways)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(existingProfiles);
        ArgumentNullException.ThrowIfNull(existingGateways);

        var existingAliases = existingProfiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.DisplayName))
            .Select(profile => profile.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingGatewayList = existingGateways.ToList();
        var batchGateways = new Dictionary<GatewayBatchKey, SshGatewayDto>();
        var usedGatewayNames = existingGatewayList
            .Where(gateway => !string.IsNullOrWhiteSpace(gateway.Name))
            .Select(gateway => gateway.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return candidates
            .Select(candidate =>
            {
                var preview = BuildGatewayPlan(
                    candidate,
                    existingGatewayList,
                    batchGateways,
                    usedGatewayNames,
                    createMissingGateways: false);

                return new OpenSshImportCandidateAssessment(
                    candidate,
                    existingAliases.Contains(candidate.Alias)
                        ? ImportCandidateStatus.Duplicate
                        : ImportCandidateStatus.New,
                    preview.Steps);
            })
            .ToList();
    }

    /// <summary>
    /// Imports the selected candidates without overwriting existing aliases.
    /// </summary>
    public async Task<ImportOutcome> ImportSelectedAsync(
        IReadOnlyList<OpenSshImportCandidate> selected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selected);

        ct.ThrowIfCancellationRequested();
        var inventory = await _configManager.LoadServersAsync().ConfigureAwait(false);
        var settings = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        var existingAliases = inventory
            .Where(profile => !string.IsNullOrWhiteSpace(profile.DisplayName))
            .Select(profile => profile.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingGatewayList = settings.SshGateways.ToList();
        var batchGateways = new Dictionary<GatewayBatchKey, SshGatewayDto>();
        var usedGatewayNames = settings.SshGateways
            .Where(gateway => !string.IsNullOrWhiteSpace(gateway.Name))
            .Select(gateway => gateway.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var createdGateways = new List<SshGatewayDto>();
        var plannedProfiles = new List<ServerProfileDto>();

        var skippedDuplicates = 0;
        var warningCount = 0;

        foreach (var candidate in selected)
        {
            ct.ThrowIfCancellationRequested();

            if (!existingAliases.Add(candidate.Alias))
            {
                skippedDuplicates++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(candidate.IdentityFile) && !File.Exists(candidate.IdentityFile))
            {
                warningCount++;
                Heimdall.Core.Logging.FileLogger.Warn(
                    $"OpenSSH import preserved missing IdentityFile path for alias '{candidate.Alias}': {candidate.IdentityFile}");
            }

            var gatewayPlan = BuildGatewayPlan(
                candidate,
                existingGatewayList,
                batchGateways,
                usedGatewayNames,
                createMissingGateways: true);
            foreach (var gateway in gatewayPlan.CreatedGateways)
            {
                existingGatewayList.Add(gateway);
                settings.SshGateways.Add(gateway);
                createdGateways.Add(gateway);
            }

            plannedProfiles.Add(MapCandidate(candidate, gatewayPlan.LastGatewayId));
        }

        if (createdGateways.Count > 0)
        {
            await _configManager.SaveSettingsAsync(settings).ConfigureAwait(false);
        }

        return await _configManager.MutateServersAsync(currentInventory =>
        {
            var currentAliases = currentInventory
                .Where(profile => !string.IsNullOrWhiteSpace(profile.DisplayName))
                .Select(profile => profile.DisplayName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int importedCount = 0;
            int finalSkippedDuplicates = skippedDuplicates;

            foreach (ServerProfileDto profile in plannedProfiles)
            {
                if (!currentAliases.Add(profile.DisplayName))
                {
                    finalSkippedDuplicates++;
                    continue;
                }

                currentInventory.Add(profile);
                importedCount++;
            }

            return new ImportOutcome(
                importedCount,
                finalSkippedDuplicates,
                WarningCount: warningCount);
        }).ConfigureAwait(false);
    }

    private static ServerProfileDto MapCandidate(OpenSshImportCandidate candidate, string? sshGatewayId)
    {
        return new ServerProfileDto
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = candidate.Alias,
            Origin = ProfileOrigin.ImportOpenSsh,
            RemoteServer = candidate.HostName,
            ConnectionType = "SSH",
            SshMode = "Embedded",
            SshPort = candidate.Port,
            SshUsername = string.IsNullOrWhiteSpace(candidate.User) ? null : candidate.User,
            SshKeyPath = string.IsNullOrWhiteSpace(candidate.IdentityFile) ? null : candidate.IdentityFile,
            SshGatewayId = string.IsNullOrWhiteSpace(sshGatewayId) ? null : sshGatewayId
        };
    }

    private static GatewayPlan BuildGatewayPlan(
        OpenSshImportCandidate candidate,
        IReadOnlyList<SshGatewayDto> existingGateways,
        IDictionary<GatewayBatchKey, SshGatewayDto> batchGateways,
        ISet<string> usedGatewayNames,
        bool createMissingGateways)
    {
        if (candidate.ProxyJumpChain.Count == 0)
        {
            return GatewayPlan.Empty;
        }

        var steps = new List<OpenSshGatewayPreviewStep>();
        var created = new List<SshGatewayDto>();
        string? parentGatewayId = null;
        string? lastGatewayId = null;

        foreach (var hop in candidate.ProxyJumpChain)
        {
            var batchKey = new GatewayBatchKey(
                Normalize(hop.HostName),
                hop.Port,
                Normalize(hop.User),
                Normalize(hop.IdentityFile));
            string? reusedName = null;
            SshGatewayDto? gateway;
            if (batchGateways.TryGetValue(batchKey, out var batchMatch))
            {
                gateway = batchMatch;
            }
            else
            {
                gateway = FindExistingGateway(existingGateways, hop);
                reusedName = gateway?.Name;
            }

            if (gateway is null)
            {
                gateway = CreateGateway(hop, parentGatewayId, usedGatewayNames);
                batchGateways[batchKey] = gateway;

                if (createMissingGateways)
                {
                    created.Add(gateway);
                }
            }

            steps.Add(new OpenSshGatewayPreviewStep(
                hop.HostName,
                hop.Port,
                hop.User,
                reusedName));

            parentGatewayId = gateway.Id;
            lastGatewayId = gateway.Id;
        }

        return new GatewayPlan(lastGatewayId, steps, created);
    }

    private static SshGatewayDto? FindExistingGateway(
        IEnumerable<SshGatewayDto> existingGateways,
        OpenSshProxyJumpHop hop)
    {
        return existingGateways.FirstOrDefault(gateway =>
            string.Equals(gateway.Host, hop.HostName, StringComparison.OrdinalIgnoreCase) &&
            gateway.Port == hop.Port &&
            string.Equals(Normalize(gateway.User), Normalize(hop.User), StringComparison.OrdinalIgnoreCase));
    }

    private static SshGatewayDto CreateGateway(
        OpenSshProxyJumpHop hop,
        string? parentGatewayId,
        ISet<string> usedGatewayNames)
    {
        var baseName = string.IsNullOrWhiteSpace(hop.User)
            ? hop.HostName
            : $"{hop.User}@{hop.HostName}";
        var name = MakeUniqueName(baseName, usedGatewayNames);
        return new SshGatewayDto
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Host = hop.HostName,
            Port = hop.Port,
            User = string.IsNullOrWhiteSpace(hop.User) ? string.Empty : hop.User,
            KeyPath = string.IsNullOrWhiteSpace(hop.IdentityFile) ? null : hop.IdentityFile,
            ParentGatewayId = parentGatewayId
        };
    }

    /// <summary>
    /// Hard upper bound on the suffix tried by <see cref="MakeUniqueName"/>.
    /// In practice no real config reaches double-digit collisions; the cap
    /// is purely a defensive guard against pathological inputs.
    /// </summary>
    private const int MaxUniqueNameSuffix = 1000;

    private static string MakeUniqueName(string baseName, ISet<string> usedGatewayNames)
    {
        var candidate = string.IsNullOrWhiteSpace(baseName) ? "SSH Gateway" : baseName;
        if (usedGatewayNames.Add(candidate))
        {
            return candidate;
        }

        for (var suffix = 2; suffix <= MaxUniqueNameSuffix; suffix++)
        {
            var suffixed = $"{candidate} ({suffix})";
            if (usedGatewayNames.Add(suffixed))
            {
                return suffixed;
            }
        }

        // Fallback for the (very unlikely) saturation case: append a
        // random hex tag instead of looping unboundedly.
        var fallback = $"{candidate} ({Guid.NewGuid():N})";
        usedGatewayNames.Add(fallback);
        return fallback;
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private sealed record GatewayBatchKey(string Host, int Port, string User, string KeyPath);

    private sealed record GatewayPlan(
        string? LastGatewayId,
        IReadOnlyList<OpenSshGatewayPreviewStep> Steps,
        IReadOnlyList<SshGatewayDto> CreatedGateways)
    {
        public static GatewayPlan Empty { get; } = new(null, [], []);
    }
}

/// <summary>
/// Associates an import candidate with its computed import status.
/// </summary>
public sealed record OpenSshImportCandidateAssessment(
    OpenSshImportCandidate Candidate,
    ImportCandidateStatus Status,
    IReadOnlyList<OpenSshGatewayPreviewStep> GatewayPreviewSteps);

/// <summary>
/// Describes one gateway preview step for an OpenSSH import candidate.
/// </summary>
public sealed record OpenSshGatewayPreviewStep(
    string Host,
    int Port,
    string? User,
    string? ReusedGatewayName);
