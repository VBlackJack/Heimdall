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

using System.Globalization;
using System.Text;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;

namespace Heimdall.Ssh;

/// <summary>Target host parameters and optional async connection resolver for fleet <c>authorized_keys</c> operations.</summary>
public sealed record SshFleetAuthorizedKeysTarget(
    string ServerId,
    string DisplayName,
    SshConnectionParams ConnectionParams,
    Func<CancellationToken, Task<(SshConnectionParams ResolvedParams, IDisposable? Lease)>>? ResolveConnectionAsync = null);

/// <summary>Outcome of scanning a single host's <c>~/.ssh/authorized_keys</c> against a search query.</summary>
public sealed record SshFleetAuthorizedKeysHostResult(
    string ServerId,
    string DisplayName,
    string Host,
    int Port,
    IReadOnlyList<AuthorizedKeyEntry> MatchedEntries,
    int TotalKeyCount,
    bool Success,
    string? ErrorMessage = null,
    SshAuthorizedKeysErrorCode ErrorCode = SshAuthorizedKeysErrorCode.None);

/// <summary>Outcome of revoking a specific key row on a specific fleet host.</summary>
public sealed record SshFleetKeyRevocationResult(
    string ServerId,
    string DisplayName,
    string Host,
    int Port,
    AuthorizedKeyEntry Entry,
    SshAuthorizedKeysMutationResult Mutation);

/// <summary>
/// Fleet-wide search and selective revocation workflow across SSH targets' <c>~/.ssh/authorized_keys</c>.
/// Enforces bounded concurrency, strict <c>exactRawLine</c> targeting, and symlink/last-key lockout protection.
/// </summary>
public static class SshFleetAuthorizedKeysService
{
    public const int DefaultMaxConcurrency = 4;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="entry"/> matches <paramref name="query"/>
    /// (case-insensitive substring match against fingerprint, comment, key type, options, or key blob).
    /// A blank query matches all entries for full fleet key inventory.
    /// </summary>
    public static bool MatchesQuery(AuthorizedKeyEntry entry, string? query)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        string needle = query.Trim();
        return entry.Fingerprint.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.Comment.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.KeyType.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.Options.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.KeyBase64.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<IReadOnlyList<SshFleetAuthorizedKeysHostResult>> ScanFleetAsync(
        IReadOnlyList<SshFleetAuthorizedKeysTarget> targets,
        string? query,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier verifier,
        int maxConcurrency = DefaultMaxConcurrency,
        Func<SshFleetAuthorizedKeysTarget, CancellationToken, Task<SshAuthorizedKeysListResult>>? listRunner = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(verifier);

        if (targets.Count == 0)
        {
            return [];
        }

        int boundedConcurrency = Math.Clamp(maxConcurrency, 1, 16);
        var ordered = new SshFleetAuthorizedKeysHostResult[targets.Count];
        using var gate = new SemaphoreSlim(boundedConcurrency, boundedConcurrency);

        var tasks = targets.Select(async (target, index) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                string logicalHost = string.IsNullOrWhiteSpace(target.ConnectionParams.LogicalHost)
                    ? target.ConnectionParams.Host
                    : target.ConnectionParams.LogicalHost;
                int logicalPort = target.ConnectionParams.LogicalPort ?? target.ConnectionParams.Port;

                SshAuthorizedKeysListResult listResult;
                if (listRunner is not null)
                {
                    listResult = await listRunner(target, ct).ConfigureAwait(false);
                }
                else
                {
                    IDisposable? lease = null;
                    try
                    {
                        SshConnectionParams resolved = target.ConnectionParams;
                        if (target.ResolveConnectionAsync is not null)
                        {
                            (resolved, lease) = await target.ResolveConnectionAsync(ct).ConfigureAwait(false);
                        }

                        listResult = await SshAuthorizedKeysService
                            .ListAsync(resolved, hostKeyStore, verifier, ct)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        lease?.Dispose();
                    }
                }

                if (!listResult.Success)
                {
                    ordered[index] = new SshFleetAuthorizedKeysHostResult(
                        target.ServerId,
                        target.DisplayName,
                        logicalHost,
                        logicalPort,
                        [],
                        0,
                        Success: false,
                        ErrorMessage: listResult.ErrorMessage,
                        ErrorCode: listResult.ErrorCode);
                    return;
                }

                var matched = listResult.Entries
                    .Where(e => MatchesQuery(e, query))
                    .ToList();

                ordered[index] = new SshFleetAuthorizedKeysHostResult(
                    target.ServerId,
                    target.DisplayName,
                    logicalHost,
                    logicalPort,
                    matched,
                    listResult.Entries.Count,
                    Success: true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ordered[index] = new SshFleetAuthorizedKeysHostResult(
                    target.ServerId,
                    target.DisplayName,
                    target.ConnectionParams.Host,
                    target.ConnectionParams.Port,
                    [],
                    0,
                    Success: false,
                    ErrorMessage: ex.Message,
                    ErrorCode: SshAuthorizedKeysErrorCode.ConnectionFailed);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return ordered;
    }

    public static async Task<IReadOnlyList<SshFleetKeyRevocationResult>> RevokeSelectedAsync(
        IReadOnlyList<(SshFleetAuthorizedKeysTarget Target, AuthorizedKeyEntry Entry)> selections,
        bool allowRemoveLastKey,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier verifier,
        int maxConcurrency = DefaultMaxConcurrency,
        Func<SshFleetAuthorizedKeysTarget, AuthorizedKeyEntry, bool, CancellationToken, Task<SshAuthorizedKeysMutationResult>>? revokeRunner = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(verifier);

        if (selections.Count == 0)
        {
            return [];
        }

        // Group mutations by server so rows on the same host run sequentially against ~/.ssh/authorized_keys
        // (avoids concurrent tmp/mv races on one host while keeping cross-host execution parallel).
        var orderedResults = new SshFleetKeyRevocationResult[selections.Count];
        var byServer = selections
            .Select((item, idx) => (item.Target, item.Entry, Index: idx))
            .GroupBy(static x => x.Target.ServerId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int boundedConcurrency = Math.Clamp(maxConcurrency, 1, 16);
        using var gate = new SemaphoreSlim(boundedConcurrency, boundedConcurrency);

        var tasks = byServer.Select(async serverGroup =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var (target, entry, index) in serverGroup)
                {
                    ct.ThrowIfCancellationRequested();
                    string logicalHost = string.IsNullOrWhiteSpace(target.ConnectionParams.LogicalHost)
                        ? target.ConnectionParams.Host
                        : target.ConnectionParams.LogicalHost;
                    int logicalPort = target.ConnectionParams.LogicalPort ?? target.ConnectionParams.Port;

                    SshAuthorizedKeysMutationResult mutation;
                    try
                    {
                        if (revokeRunner is not null)
                        {
                            mutation = await revokeRunner(target, entry, allowRemoveLastKey, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            IDisposable? lease = null;
                            try
                            {
                                SshConnectionParams resolved = target.ConnectionParams;
                                if (target.ResolveConnectionAsync is not null)
                                {
                                    (resolved, lease) = await target.ResolveConnectionAsync(ct).ConfigureAwait(false);
                                }

                                mutation = await SshAuthorizedKeysService.RemoveKeyAsync(
                                    resolved,
                                    entry.KeyType,
                                    entry.KeyBase64,
                                    allowRemoveLastKey,
                                    hostKeyStore,
                                    verifier,
                                    ct,
                                    exactRawLine: entry.RawLine).ConfigureAwait(false);
                            }
                            finally
                            {
                                lease?.Dispose();
                            }
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        mutation = new SshAuthorizedKeysMutationResult(
                            false,
                            ErrorMessage: ex.Message,
                            ErrorCode: SshAuthorizedKeysErrorCode.ConnectionFailed);
                    }

                    orderedResults[index] = new SshFleetKeyRevocationResult(
                        target.ServerId,
                        target.DisplayName,
                        logicalHost,
                        logicalPort,
                        entry,
                        mutation);
                }
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return orderedResults;
    }

    public static string FormatCsvReport(
        IReadOnlyList<SshFleetAuthorizedKeysHostResult> results,
        string? query = null)
    {
        ArgumentNullException.ThrowIfNull(results);

        var sb = new StringBuilder();
        sb.AppendLine("Server,Host,Port,Status,TotalKeys,Line,KeyType,Fingerprint,Options,Comment,Error");
        foreach (var host in results)
        {
            if (!host.Success)
            {
                sb.AppendLine(string.Join(",",
                    SshServerAuditEngine.SanitizeCsvCell(host.DisplayName),
                    SshServerAuditEngine.SanitizeCsvCell(host.Host),
                    host.Port.ToString(CultureInfo.InvariantCulture),
                    SshServerAuditEngine.SanitizeCsvCell("ERROR"),
                    "0",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    SshServerAuditEngine.SanitizeCsvCell(host.ErrorMessage ?? host.ErrorCode.ToString())));
                continue;
            }

            if (host.MatchedEntries.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    sb.AppendLine(string.Join(",",
                        SshServerAuditEngine.SanitizeCsvCell(host.DisplayName),
                        SshServerAuditEngine.SanitizeCsvCell(host.Host),
                        host.Port.ToString(CultureInfo.InvariantCulture),
                        SshServerAuditEngine.SanitizeCsvCell("EMPTY"),
                        host.TotalKeyCount.ToString(CultureInfo.InvariantCulture),
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty));
                }

                continue;
            }

            foreach (var key in host.MatchedEntries)
            {
                sb.AppendLine(string.Join(",",
                    SshServerAuditEngine.SanitizeCsvCell(host.DisplayName),
                    SshServerAuditEngine.SanitizeCsvCell(host.Host),
                    host.Port.ToString(CultureInfo.InvariantCulture),
                    SshServerAuditEngine.SanitizeCsvCell("MATCH"),
                    host.TotalKeyCount.ToString(CultureInfo.InvariantCulture),
                    key.LineNumber.ToString(CultureInfo.InvariantCulture),
                    SshServerAuditEngine.SanitizeCsvCell(key.KeyType),
                    SshServerAuditEngine.SanitizeCsvCell(key.Fingerprint),
                    SshServerAuditEngine.SanitizeCsvCell(key.Options),
                    SshServerAuditEngine.SanitizeCsvCell(key.Comment),
                    string.Empty));
            }
        }

        return sb.ToString();
    }

    public static string FormatMarkdownReport(
        IReadOnlyList<SshFleetAuthorizedKeysHostResult> results,
        string? query = null)
    {
        ArgumentNullException.ThrowIfNull(results);

        int succeeded = results.Count(r => r.Success);
        int matchedKeys = results.Sum(r => r.MatchedEntries.Count);
        int hostsWithMatches = results.Count(r => r.Success && r.MatchedEntries.Count > 0);
        int errors = results.Count(r => !r.Success);

        var sb = new StringBuilder();
        sb.AppendLine("# SSH Fleet Authorized Keys Audit");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(query)
            ? "- Query: (all keys)"
            : $"- Query: `{EscapeMarkdown(query.Trim())}`");
        sb.AppendLine($"- Hosts scanned: {results.Count} (OK: {succeeded}, Errors: {errors})");
        sb.AppendLine($"- Matches found: {matchedKeys} across {hostsWithMatches} host(s)");
        sb.AppendLine();
        sb.AppendLine("| Server | Endpoint | Line | Type | Fingerprint | Comment | Options |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");

        foreach (var host in results)
        {
            string endpoint = $"{host.Host}:{host.Port}";
            if (!host.Success)
            {
                sb.AppendLine(
                    $"| {EscapeMarkdown(host.DisplayName)} | `{EscapeMarkdown(endpoint)}` | - | ERROR | - | {EscapeMarkdown(host.ErrorMessage ?? host.ErrorCode.ToString())} | - |");
                continue;
            }

            foreach (var key in host.MatchedEntries)
            {
                sb.AppendLine(
                    $"| {EscapeMarkdown(host.DisplayName)} | `{EscapeMarkdown(endpoint)}` | {key.LineNumber} | `{EscapeMarkdown(key.KeyType)}` | `{EscapeMarkdown(key.Fingerprint)}` | {EscapeMarkdown(key.Comment)} | {EscapeMarkdown(key.Options)} |");
            }
        }

        return sb.ToString();
    }

    private static string EscapeMarkdown(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("|", "\\|", StringComparison.Ordinal)
                   .Replace("\r", " ", StringComparison.Ordinal)
                   .Replace("\n", " ", StringComparison.Ordinal);
}
