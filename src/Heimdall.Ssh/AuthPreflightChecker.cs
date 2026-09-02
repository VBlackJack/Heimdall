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

using Heimdall.Ssh.Agents;

namespace Heimdall.Ssh;

/// <summary>
/// Result of an authentication pre-flight check.
/// </summary>
/// <param name="Success">Whether all pre-flight checks passed.</param>
/// <param name="FailureCode">Failure code if checks failed.</param>
/// <param name="Message">Human-readable failure description.</param>
public sealed record PreflightResult(bool Success, SshFailureCode? FailureCode, string? Message)
{
    /// <summary>Create a passing result.</summary>
    public static PreflightResult Ok() => new(true, null, null);

    /// <summary>Create a failing result with a specific code and message.</summary>
    public static PreflightResult Fail(SshFailureCode code, string message) => new(false, code, message);
}

/// <summary>
/// Validates SSH connection prerequisites before attempting a connection.
/// Checks key file existence, SSH agent availability, and auth method availability.
/// </summary>
public static class AuthPreflightChecker
{
    /// <summary>
    /// Run pre-flight checks against the given connection parameters.
    /// </summary>
    /// <param name="connectionParams">SSH connection parameters to validate.</param>
    /// <param name="isTunnelMode">
    /// Whether the connection is a background tunnel (non-interactive).
    /// Tunnel mode requires at least one loaded SSH agent identity when no
    /// key file or password is configured.
    /// </param>
    /// <returns>A <see cref="PreflightResult"/> indicating pass or fail with details.</returns>
    public static PreflightResult Check(
        SshConnectionParams connectionParams,
        bool isTunnelMode = false,
        SshAgentRegistry? agentRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(connectionParams);

        // Key file specified but does not exist on disk
        if (!string.IsNullOrEmpty(connectionParams.KeyPath) && !File.Exists(connectionParams.KeyPath))
        {
            return PreflightResult.Fail(
                SshFailureCode.KeyFileNotFound,
                $"Key file not found: {connectionParams.KeyPath}");
        }

        // Tunnel mode: no key and no password - needs an agent as sole auth source.
        if (isTunnelMode
            && string.IsNullOrEmpty(connectionParams.KeyPath)
            && string.IsNullOrEmpty(connectionParams.Password))
        {
            var agentCheck = CheckAgentAvailability(
                agentRegistry ?? SshAgentRegistry.CreateDefault(connectionParams.SshAgentPreference));
            if (agentCheck is not null)
            {
                return agentCheck;
            }
        }

        return PreflightResult.Ok();
    }

    /// <summary>
    /// Verifies that at least one configured SSH agent has identities loaded.
    /// Returns a failure result if not, or null if agent auth is ready.
    /// </summary>
    private static PreflightResult? CheckAgentAvailability(SshAgentRegistry agentRegistry)
    {
        var agents = agentRegistry.GetAvailableAgents();
        if (agents.Count == 0)
        {
            return PreflightResult.Fail(
                SshFailureCode.PageantKeyUnavailable,
                "ErrorNoSshAgentRunning");
        }

        var failedAgents = 0;
        foreach (var agent in agents)
        {
            try
            {
                if (agent.GetIdentities().Count > 0)
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                failedAgents++;
                Core.Logging.FileLogger.Warn(
                    $"SSH agent {agent.Name}: preflight identity check failed: {ex.Message}");
            }
        }

        // Differentiate "all agents healthy but empty" from "every probe threw"
        // so an operator reading the log can quickly tell which one to chase.
        if (failedAgents == agents.Count)
        {
            Core.Logging.FileLogger.Warn(
                $"SSH agent preflight: all {agents.Count} configured agent(s) failed to enumerate identities; see prior warnings for per-agent errors.");
        }

        return PreflightResult.Fail(
            SshFailureCode.PageantNoIdentities,
            "ErrorSshAgentHasNoIdentities");
    }
}
