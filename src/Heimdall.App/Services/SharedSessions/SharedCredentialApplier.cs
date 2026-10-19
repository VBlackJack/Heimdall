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
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;

namespace Heimdall.App.Services.SharedSessions;

/// <summary>
/// Gives a connection to a shared session the passwords its user chose to remember, asks for the
/// ones it needs and does not have, and keeps or drops them once the connection says whether they
/// were right.
/// </summary>
/// <remarks>
/// <para>Runs on the detached copies a connection is made from, after the Windows Hello gate and
/// after the external credential provider: a password the vault provided is never replaced, and no
/// remembered password is read before the user proved who they are. Nothing it does reaches the
/// shared file, the offline copy, servers.json or the settings.</para>
/// <para>A password is asked for a shared bastion, and for a shared SSH or SFTP session, only when the
/// connection would otherwise have nothing to sign in with: no remembered password and no key in
/// the SSH agent. A password the user asked to remember is kept only once the server accepted it:
/// a bastion's when the connection behind it succeeded, a session's when the SSH client itself
/// signed in, not when a Plink terminal merely started. A remembered password is dropped when the
/// server it was sent to rejected the credentials: a bastion's when the connection failed at the
/// bastion, a session's when it failed at the session, so a wrong session password never costs
/// the user the bastion's. A session's is dropped too when the SSH client was refused it and the
/// connection then went on through Plink: the terminal starting says nothing of the password, the
/// refusal before it does.</para>
/// <para>A bastion or a session left to the SSH agent, whose server then refused the agent's keys,
/// is asked for its password from then on, for as long as Heimdall runs: the agent still offers
/// the same keys, and leaving it to them again would refuse every later attempt without ever
/// asking.</para>
/// </remarks>
public sealed class SharedCredentialApplier
{
    private static readonly string SshConnectionType = ConnectionTypeCatalog.Canonicalize("SSH");
    private static readonly string SftpConnectionType = ConnectionTypeCatalog.Canonicalize("SFTP");

    private static readonly HashSet<int> CredentialRejections =
    [
        (int)SshFailureCode.PasswordRejected,
        (int)SshFailureCode.AuthRejected,
        (int)SshFailureCode.TooManyAuthFailures,
    ];

    /// <summary>The refusals that blame the keys an SSH agent offered.</summary>
    private static readonly HashSet<int> AgentKeyRejections =
    [
        .. CredentialRejections,
        (int)SshFailureCode.KeyRejected,
    ];

    private readonly SharedSessionSecretStore _store;
    private readonly IDialogService _dialogService;
    private readonly LocalizationManager _localizer;
    private readonly Func<AppSettings, int> _offerableAgentIdentities;
    private readonly Func<string, string> _seal;
    private readonly Func<string, bool> _isReadable;

    /// <summary>The targets whose server refused the agent's keys; asked for a password from then on.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<SecretTarget, byte> _agentRefused = new();

    /// <param name="store">Where the remembered passwords are kept.</param>
    /// <param name="dialogService">Asks for a password.</param>
    /// <param name="localizer">Words the question.</param>
    /// <param name="offerableAgentIdentities">
    /// How many keys the SSH agent of these settings offers; null asks the agents themselves.
    /// </param>
    /// <param name="seal">
    /// Seals a typed password; null uses <see cref="CredentialProtector.Protect"/>, the form every
    /// other saved password takes.
    /// </param>
    /// <param name="isReadable">
    /// Whether a sealed password can still be opened on this machine; null asks the protector.
    /// </param>
    public SharedCredentialApplier(
        SharedSessionSecretStore store,
        IDialogService dialogService,
        LocalizationManager localizer,
        Func<AppSettings, int>? offerableAgentIdentities = null,
        Func<string, string>? seal = null,
        Func<string, bool>? isReadable = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _offerableAgentIdentities = offerableAgentIdentities
            ?? (settings => SshAgentRegistry.CreateDefault(settings.SshAgentPreference).OfferableIdentityCount());
        _seal = seal ?? CredentialProtector.Protect;
        _isReadable = isReadable ?? IsReadableByTheProtector;
    }

    /// <summary>
    /// Fills the remembered passwords of <paramref name="profile"/> and of the shared bastions on
    /// its route into the copies, and asks for the missing ones when <paramref name="allowPrompt"/>.
    /// </summary>
    /// <returns>The plan to settle once the connection ended, or null when the user cancelled a question.</returns>
    public async Task<SharedCredentialPlan?> ApplyAsync(
        ServerProfileDto profile,
        AppSettings settings,
        bool allowPrompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(settings);

        var plan = new SharedCredentialPlan(_store, _agentRefused);
        if (!SharedSessionIds.IsSharedProfile(profile))
        {
            return plan;
        }

        int? agentIdentities = null;
        async Task<bool> AgentHasKeysAsync()
        {
            agentIdentities ??= await Task.Run(() => _offerableAgentIdentities(settings), cancellationToken).ConfigureAwait(false);
            return agentIdentities > 0;
        }

        // The bastions first, from the one the session names up its chain: they are dialled first.
        foreach (SshGatewayDto gateway in SharedRoute(profile, settings))
        {
            if (!string.IsNullOrEmpty(gateway.SshPasswordEncrypted)
                || !string.IsNullOrWhiteSpace(gateway.KeyPath)
                || string.IsNullOrWhiteSpace(gateway.User)
                || SharedSessionIds.SourceIdOf(gateway.Id) is not { } gatewaySource)
            {
                continue;
            }

            var target = new SecretTarget(
                SharedSecretKind.Gateway,
                gatewaySource,
                SharedSessionSecretStore.GatewayKey(gateway.Host, gateway.Port, gateway.User));
            if (await FindReadableAsync(target).ConfigureAwait(false) is { } remembered)
            {
                gateway.SshPasswordEncrypted = remembered;
                plan.Used.Add(target);
                continue;
            }

            if (!allowPrompt)
            {
                continue;
            }

            if (!_agentRefused.ContainsKey(target) && await AgentHasKeysAsync().ConfigureAwait(false))
            {
                plan.LeftToTheAgent.Add(target);
                continue;
            }

            PasswordPromptResult? answer = await AskAsync(
                    _localizer.Format("DialogSharedGatewayPasswordPromptMessage", gateway.User, gateway.Host),
                    cancellationToken)
                .ConfigureAwait(false);
            if (answer is null)
            {
                return null;
            }

            gateway.SshPasswordEncrypted = _seal(answer.Password);
            if (answer.Remember)
            {
                plan.Pending.Add((target, gateway.SshPasswordEncrypted));
            }
        }

        string? sessionSource = SharedSessionIds.SourceIdOf(profile.InventoryProfileId)
            ?? SharedSessionIds.SourceIdOf(profile.Id);
        string profileId = SharedSessionIds.IsShared(profile.InventoryProfileId) ? profile.InventoryProfileId : profile.Id;
        if (sessionSource is null)
        {
            return plan;
        }

        // An SFTP session signs in through the same SSH client with the same fields, so it is
        // remembered under the same key shape and asked with the same question.
        if ((string.Equals(profile.ConnectionType, SshConnectionType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(profile.ConnectionType, SftpConnectionType, StringComparison.OrdinalIgnoreCase))
            && string.IsNullOrEmpty(profile.SshPasswordEncrypted)
            && !string.IsNullOrWhiteSpace(profile.SshUsername))
        {
            var target = new SecretTarget(
                SharedSecretKind.Session,
                sessionSource,
                SharedSessionSecretStore.SessionKey(profileId, profile.RemoteServer, profile.SshPort > 0 ? profile.SshPort : DefaultPorts.Ssh, profile.SshUsername));
            if (await FindReadableAsync(target).ConfigureAwait(false) is { } remembered)
            {
                profile.SshPasswordEncrypted = remembered;
                plan.Used.Add(target);
                ForbidRetype(profile);
            }
            else if (allowPrompt && !_agentRefused.ContainsKey(target) && await AgentHasKeysAsync().ConfigureAwait(false))
            {
                plan.LeftToTheAgent.Add(target);
            }
            else if (allowPrompt)
            {
                PasswordPromptResult? answer = await AskAsync(
                        _localizer.Format("DialogSshPasswordPromptMessage", profile.SshUsername + "@" + profile.RemoteServer),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (answer is null)
                {
                    return null;
                }

                profile.SshPasswordEncrypted = _seal(answer.Password);
                ForbidRetype(profile);
                if (answer.Remember)
                {
                    plan.Pending.Add((target, profile.SshPasswordEncrypted));
                }
            }
        }

        // An RDP session asks for its password in the RDP client, which Heimdall does not own:
        // there is no answer here to remember. Its bastions above are still covered.
        return plan;
    }

    /// <summary>
    /// An SFTP connection retypes a refused password itself when it may prompt, and a retyped
    /// password that works would make the connection look authenticated with the one the server
    /// refused. A password this class supplied, remembered or just asked, is therefore the only
    /// one tried: a refusal fails the connection, <see cref="SharedCredentialPlan.SettleAsync"/>
    /// forgets it and the next connect asks again through this class.
    /// </summary>
    private static void ForbidRetype(ServerProfileDto profile)
    {
        if (string.Equals(profile.ConnectionType, SftpConnectionType, StringComparison.OrdinalIgnoreCase))
        {
            profile.AllowCredentialPrompt = false;
        }
    }

    /// <summary>The shared bastions on the route of <paramref name="profile"/>, from the nearest up.</summary>
    internal static IEnumerable<SshGatewayDto> SharedRoute(ServerProfileDto profile, AppSettings settings)
    {
        // A shared profile takes no local folder default: only its own gateway counts.
        string? gatewayId = SshGatewayRoute.GatewayIdFor(profile, groupDefaults: null);
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrEmpty(gatewayId) && visited.Add(gatewayId))
        {
            SshGatewayDto? gateway = settings.SshGateways.FirstOrDefault(
                candidate => string.Equals(candidate.Id, gatewayId, StringComparison.OrdinalIgnoreCase));
            if (gateway is null)
            {
                yield break;
            }

            if (SharedSessionIds.IsShared(gateway.Id))
            {
                yield return gateway;
            }

            gatewayId = gateway.ParentGatewayId;
        }
    }

    /// <summary>
    /// The password remembered for <paramref name="target"/>, or null. One this machine can no
    /// longer open, after a change of Windows account or of the protection key, is forgotten and
    /// read as none: sent as it is, it would refuse the connection instead of asking again.
    /// </summary>
    private async Task<string?> FindReadableAsync(SecretTarget target)
    {
        if (_store.Find(target.Kind, target.SourceId, target.Key) is not { } remembered)
        {
            return null;
        }

        if (_isReadable(remembered))
        {
            return remembered;
        }

        await _store.ForgetAsync(target.Kind, target.SourceId, target.Key).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Whether the protector opens <paramref name="sealedSecret"/>. A secret behind a locked vault
    /// counts as readable: the connection asks for the vault to be unlocked, as it does for every
    /// other saved password.
    /// </summary>
    private static bool IsReadableByTheProtector(string sealedSecret)
    {
        try
        {
            return ConnectionHelpers.DecryptPassword(sealedSecret) is not null;
        }
        catch (Heimdall.Core.Security.Vault.VaultLockedException)
        {
            return true;
        }
    }

    private Task<PasswordPromptResult?> AskAsync(string message, CancellationToken cancellationToken) =>
        _dialogService.ShowPasswordInputWithRememberAsync(
            _localizer["DialogSshPasswordPromptTitle"],
            message,
            _localizer["DialogRememberPasswordOnThisComputer"],
            cancellationToken);

    /// <summary>
    /// Whether a connection that failed with <paramref name="failureCode"/> was refused for the
    /// credentials it sent, rather than for the network or the host.
    /// </summary>
    internal static bool IsCredentialRejection(int? failureCode) =>
        failureCode is int code && CredentialRejections.Contains(code);

    /// <summary>
    /// Whether a connection that failed with <paramref name="failureCode"/> was refused for the
    /// keys the SSH agent offered, when the agent was what it signed in with.
    /// </summary>
    internal static bool IsAgentKeyRejection(int? failureCode) =>
        failureCode is int code && AgentKeyRejections.Contains(code);

    /// <summary>What a remembered password is filed under.</summary>
    internal sealed record SecretTarget(SharedSecretKind Kind, string SourceId, string Key);

    /// <summary>The passwords a connection used and the ones it was asked to remember.</summary>
    public sealed class SharedCredentialPlan
    {
        private readonly SharedSessionSecretStore _store;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<SecretTarget, byte> _agentRefused;

        internal SharedCredentialPlan(
            SharedSessionSecretStore store,
            System.Collections.Concurrent.ConcurrentDictionary<SecretTarget, byte> agentRefused)
        {
            _store = store;
            _agentRefused = agentRefused;
        }

        /// <summary>The targets that got no password because the SSH agent offered keys.</summary>
        internal List<SecretTarget> LeftToTheAgent { get; } = [];

        internal List<SecretTarget> Used { get; } = [];

        internal List<(SecretTarget Target, string SealedSecret)> Pending { get; } = [];

        /// <summary>
        /// Keeps the passwords the user asked to remember once the server accepted them; drops the
        /// remembered ones it used when the server rejected the credentials, so the next attempt
        /// asks again instead of sending a stale password forever.
        /// </summary>
        /// <param name="succeeded">Whether the connection succeeded.</param>
        /// <param name="sessionAuthenticated">
        /// Whether the session's own sign-in was confirmed before the connection returned; a
        /// bastion's is whenever the connection behind it succeeded.
        /// </param>
        /// <param name="failureCode">The failure code of a connection that failed.</param>
        /// <param name="failedAtGateway">Whether the connection failed while dialling a bastion.</param>
        /// <param name="fallbackFrom">
        /// The refusal the SSH client met at the session before the connection retried through
        /// Plink; null when no such retry ran.
        /// </param>
        public async Task SettleAsync(
            bool succeeded,
            bool sessionAuthenticated,
            int? failureCode,
            bool failedAtGateway,
            SshFailureCode? fallbackFrom = null)
        {
            // The session refused its password, then Plink took over. A Plink terminal reports
            // success once its process started, so the outcome says nothing more of the password:
            // the refusal is the last word on it. The bastion was crossed to reach that refusal.
            if (IsCredentialRejection((int?)fallbackFrom))
            {
                await ForgetUsedAsync(SharedSecretKind.Session).ConfigureAwait(false);
            }

            if (IsAgentKeyRejection((int?)fallbackFrom))
            {
                MarkAgentRefused(SharedSecretKind.Session);
            }

            if (succeeded)
            {
                foreach ((SecretTarget target, string sealedSecret) in Pending)
                {
                    if (target.Kind == SharedSecretKind.Session && !sessionAuthenticated)
                    {
                        continue;
                    }

                    await _store.RememberAsync(target.Kind, target.SourceId, target.Key, sealedSecret).ConfigureAwait(false);
                }

                return;
            }

            SharedSecretKind rejected = failedAtGateway ? SharedSecretKind.Gateway : SharedSecretKind.Session;
            if (IsAgentKeyRejection(failureCode))
            {
                MarkAgentRefused(rejected);
            }

            if (!IsCredentialRejection(failureCode))
            {
                return;
            }

            await ForgetUsedAsync(rejected).ConfigureAwait(false);
        }

        private void MarkAgentRefused(SharedSecretKind rejected)
        {
            foreach (SecretTarget target in LeftToTheAgent.Where(target => target.Kind == rejected))
            {
                _agentRefused.TryAdd(target, 0);
            }
        }

        private async Task ForgetUsedAsync(SharedSecretKind rejected)
        {
            // Not logged: whether a password was remembered is itself credential data.
            foreach (SecretTarget target in Used.Where(target => target.Kind == rejected))
            {
                await _store.ForgetAsync(target.Kind, target.SourceId, target.Key).ConfigureAwait(false);
            }
        }
    }
}
