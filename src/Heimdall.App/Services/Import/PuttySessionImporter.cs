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

using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;

namespace Heimdall.App.Services.Import;

/// <summary>
/// Reads, classifies, and persists PuTTY SSH sessions.
/// </summary>
public sealed class PuttySessionImporter(
    IPuttySessionRegistrySource registrySource,
    IConfigManager configManager)
{
    private readonly IPuttySessionRegistrySource _registrySource = registrySource;
    private readonly IConfigManager _configManager = configManager;

    public async Task<PuttySessionParseResult> ReadAndParseAsync(CancellationToken ct = default)
    {
        var sessions = await _registrySource.ReadSessionsAsync(ct).ConfigureAwait(false);
        return PuttySessionParser.Parse(sessions);
    }

    public async Task<IReadOnlyList<PuttySessionCandidateAssessment>> ComputeStatusesAsync(
        IReadOnlyList<PuttySessionCandidate> candidates,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        ct.ThrowIfCancellationRequested();
        var existingProfiles = await _configManager.LoadServersAsync().ConfigureAwait(false);
        return ComputeStatuses(candidates, existingProfiles);
    }

    public IReadOnlyList<PuttySessionCandidateAssessment> ComputeStatuses(
        IReadOnlyList<PuttySessionCandidate> candidates,
        IEnumerable<ServerProfileDto> existingProfiles)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(existingProfiles);

        var existingNames = existingProfiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.DisplayName))
            .Select(profile => profile.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return candidates
            .Select(candidate =>
            {
                var status = string.IsNullOrWhiteSpace(candidate.HostName)
                    ? ImportCandidateStatus.Invalid
                    : existingNames.Contains(candidate.DisplayName)
                        ? ImportCandidateStatus.Duplicate
                        : ImportCandidateStatus.New;
                return new PuttySessionCandidateAssessment(candidate, status);
            })
            .ToList();
    }

    public async Task<ImportOutcome> ImportSelectedAsync(
        IReadOnlyList<PuttySessionCandidate> selected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selected);

        ct.ThrowIfCancellationRequested();
        return await _configManager.MutateServersAsync(inventory =>
        {
            var existingNames = inventory
                .Where(profile => !string.IsNullOrWhiteSpace(profile.DisplayName))
                .Select(profile => profile.DisplayName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var importedCount = 0;
            var skippedDuplicates = 0;
            var skippedInvalid = 0;

            foreach (PuttySessionCandidate candidate in selected)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(candidate.HostName))
                {
                    skippedInvalid++;
                    continue;
                }

                if (!existingNames.Add(candidate.DisplayName))
                {
                    skippedDuplicates++;
                    continue;
                }

                inventory.Add(MapCandidate(candidate));
                importedCount++;
            }

            return new ImportOutcome(importedCount, skippedDuplicates, skippedInvalid);
        }).ConfigureAwait(false);
    }

    private static ServerProfileDto MapCandidate(PuttySessionCandidate candidate)
    {
        return new ServerProfileDto
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = candidate.DisplayName,
            Origin = ProfileOrigin.ImportPutty,
            RemoteServer = candidate.HostName!,
            ConnectionType = "SSH",
            SshMode = "Embedded",
            SshPort = candidate.Port,
            SshUsername = string.IsNullOrWhiteSpace(candidate.UserName) ? null : candidate.UserName,
            SshKeyPath = string.IsNullOrWhiteSpace(candidate.PublicKeyFile) ? null : candidate.PublicKeyFile
        };
    }
}

public sealed record PuttySessionCandidateAssessment(
    PuttySessionCandidate Candidate,
    ImportCandidateStatus Status);
