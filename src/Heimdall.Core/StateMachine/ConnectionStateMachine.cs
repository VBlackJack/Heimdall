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

namespace Heimdall.Core.StateMachine;

/// <summary>
/// Manages connection state transitions for multiple server connections.
/// Thread-safe: all tracked state and notification queue mutations share one lock.
/// </summary>
public sealed class ConnectionStateMachine
{
    private readonly Dictionary<string, ConnectionStateData> _connections = new();
    private readonly Queue<PendingStateChange> _pendingStateChanges = new();
    private readonly object _lock = new();
    private bool _isPublishingStateChanges;

    private static readonly Dictionary<ConnectionState, HashSet<ConnectionState>> ValidTransitions = new()
    {
        [ConnectionState.Disconnected] = [ConnectionState.Initializing, ConnectionState.Error],
        [ConnectionState.Initializing] = [ConnectionState.ValidatingConfig, ConnectionState.Error, ConnectionState.Disconnected],
        [ConnectionState.ValidatingConfig] = [ConnectionState.EstablishingTunnel, ConnectionState.LaunchingRdp, ConnectionState.LaunchingSsh, ConnectionState.LaunchingSftp, ConnectionState.LaunchingFtp, ConnectionState.LaunchingLocal, ConnectionState.LaunchingVnc, ConnectionState.LaunchingTelnet, ConnectionState.LaunchingCitrix, ConnectionState.LaunchingWinRm, ConnectionState.Error, ConnectionState.Disconnected],
        [ConnectionState.EstablishingTunnel] = [ConnectionState.TunnelEstablished, ConnectionState.Error, ConnectionState.Disconnected],
        [ConnectionState.TunnelEstablished] = [ConnectionState.LaunchingRdp, ConnectionState.LaunchingSsh, ConnectionState.LaunchingSftp, ConnectionState.LaunchingFtp, ConnectionState.LaunchingLocal, ConnectionState.LaunchingVnc, ConnectionState.LaunchingTelnet, ConnectionState.LaunchingCitrix, ConnectionState.LaunchingWinRm, ConnectionState.Error, ConnectionState.Disconnecting],
        [ConnectionState.LaunchingRdp] = [ConnectionState.Connected, ConnectionState.LaunchedExternalClient, ConnectionState.Error, ConnectionState.Disconnecting, ConnectionState.Disconnected],
        [ConnectionState.LaunchingSsh] = [ConnectionState.Connected, ConnectionState.Error, ConnectionState.Disconnecting],
        [ConnectionState.LaunchingSftp] = [ConnectionState.Connected, ConnectionState.Error, ConnectionState.Disconnecting],
        [ConnectionState.LaunchingLocal] = [ConnectionState.Connected, ConnectionState.RemoteSessionHandedOff, ConnectionState.Error, ConnectionState.Disconnecting],
        [ConnectionState.LaunchingVnc] = [ConnectionState.Connected, ConnectionState.Error, ConnectionState.Disconnecting],
        [ConnectionState.LaunchingFtp] = [ConnectionState.Connected, ConnectionState.Error, ConnectionState.Disconnecting],
        [ConnectionState.LaunchingTelnet] = [ConnectionState.Connected, ConnectionState.Error, ConnectionState.Disconnecting],
        [ConnectionState.LaunchingCitrix] = [ConnectionState.Connected, ConnectionState.Error, ConnectionState.Disconnecting],
        [ConnectionState.LaunchingWinRm] = [ConnectionState.Connected, ConnectionState.RemoteSessionHandedOff, ConnectionState.Error, ConnectionState.Disconnecting],
        [ConnectionState.LaunchedExternalClient] = [ConnectionState.Disconnected, ConnectionState.Error, ConnectionState.Disconnecting],
        [ConnectionState.Connected] = [ConnectionState.Disconnecting, ConnectionState.Disconnected, ConnectionState.Error],
        [ConnectionState.RemoteSessionHandedOff] = [ConnectionState.Disconnecting, ConnectionState.Disconnected, ConnectionState.Error],
        // Connected is reachable from Disconnecting because a disconnect can lose a race it did
        // not know it was in. The RDP control keeps an auto-reconnect attempt in flight after the
        // user cancels it, and that attempt can still succeed: the session is then genuinely live
        // while this machine had already been told the disconnect had begun. Without the edge the
        // view reports Connected and the machine keeps saying Disconnecting, and every consumer
        // that counts live sessions believes the machine.
        [ConnectionState.Disconnecting] = [ConnectionState.Disconnected, ConnectionState.Error, ConnectionState.Connected],
        [ConnectionState.Error] = [ConnectionState.Disconnected, ConnectionState.Initializing],
    };

    private static readonly Dictionary<ConnectionState, StateMetadata> Metadata = new()
    {
        [ConnectionState.Disconnected] = new("StatusReady", "LogDisconnected", IsTerminal: false, AllowsUserAction: true, IsProgress: false),
        [ConnectionState.Initializing] = new("StatusConnectingProgress", "LogInitializing", IsTerminal: false, AllowsUserAction: false, IsProgress: true),
        [ConnectionState.ValidatingConfig] = new("StatusConnectingProgress", "LogValidating", IsTerminal: false, AllowsUserAction: false, IsProgress: true),
        [ConnectionState.EstablishingTunnel] = new("StatusEstablishingTunnel", "LogTunnelCreating", IsTerminal: false, AllowsUserAction: false, IsProgress: true),
        [ConnectionState.TunnelEstablished] = new("StatusTunnelEstablished", "LogTunnelCreated", IsTerminal: false, AllowsUserAction: true, IsProgress: false),
        [ConnectionState.LaunchingRdp] = new("StatusConnecting", "LogRdpLaunching", IsTerminal: false, AllowsUserAction: false, IsProgress: true),
        [ConnectionState.LaunchingSsh] = new("StatusLaunchingSsh", "LogSshLaunching", IsTerminal: false, AllowsUserAction: false, IsProgress: true),
        [ConnectionState.LaunchingSftp] = new("StatusLaunchingSftp", "LogSftpLaunching", IsTerminal: false, AllowsUserAction: false, IsProgress: true),
        [ConnectionState.LaunchingLocal] = new("StatusLaunchingLocal", "LogLocalShellLaunching", IsTerminal: false, AllowsUserAction: false, IsProgress: true),
        [ConnectionState.LaunchingVnc] = new("StatusVncConnecting", "LogVncLaunching", IsTerminal: false, AllowsUserAction: false, IsProgress: true),
        [ConnectionState.LaunchingFtp] = new("StatusFtpConnecting", "LogFtpLaunching", IsTerminal: false, AllowsUserAction: false, IsProgress: true),
        [ConnectionState.LaunchingTelnet] = new("StatusTelnetConnecting", "LogTelnetLaunching", IsTerminal: false, AllowsUserAction: false, IsProgress: true),
        [ConnectionState.LaunchingCitrix] = new("StatusCitrixConnecting", "LogCitrixLaunching", IsTerminal: false, AllowsUserAction: false, IsProgress: true),
        [ConnectionState.LaunchingWinRm] = new("StatusLaunchingWinRm", "LogWinRmLaunching", IsTerminal: false, AllowsUserAction: false, IsProgress: true),
        [ConnectionState.LaunchedExternalClient] = new("StatusLaunchedExternalClient", "LogLaunchedExternalClient", IsTerminal: false, AllowsUserAction: true, IsProgress: false),
        [ConnectionState.Connected] = new("StatusConnected", "LogRdpConnection", IsTerminal: false, AllowsUserAction: true, IsProgress: false),
        [ConnectionState.RemoteSessionHandedOff] = new("StatusRemoteSessionHandedOff", "LogRemoteSessionHandedOff", IsTerminal: false, AllowsUserAction: true, IsProgress: false),
        [ConnectionState.Disconnecting] = new("StatusDisconnecting", "LogDisconnecting", IsTerminal: false, AllowsUserAction: false, IsProgress: true),
        [ConnectionState.Error] = new("StatusError", "LogError", IsTerminal: false, AllowsUserAction: true, IsProgress: false),
    };

    /// <summary>
    /// Raised after a successful state transition.
    /// Notifications are published in mutation order and never while the state lock is held.
    /// </summary>
    public event Action<ConnectionStateChange>? StateChanged;

    /// <summary>
    /// Gets the current state for a server, returning Disconnected if unknown.
    /// </summary>
    public ConnectionState GetState(string serverId)
    {
        lock (_lock)
        {
            return _connections.TryGetValue(serverId, out ConnectionStateData? data)
                ? data.CurrentState
                : ConnectionState.Disconnected;
        }
    }

    /// <summary>
    /// Gets the full state data for a server, or null if not tracked.
    /// Returns a snapshot copy to prevent external mutation.
    /// </summary>
    public ConnectionStateData? GetStateData(string serverId)
    {
        lock (_lock)
        {
            return _connections.TryGetValue(serverId, out ConnectionStateData? data)
                ? data.Snapshot()
                : null;
        }
    }

    /// <summary>
    /// Attempts to transition a server connection to a new state.
    /// Creates a new tracking entry if the server is not yet tracked.
    /// </summary>
    /// <returns>True if the transition was valid and applied.</returns>
    public bool TryTransition(string serverId, ConnectionState newState)
    {
        ConnectionState previousState;
        long revision;

        lock (_lock)
        {
            ConnectionStateData data = GetOrCreate(serverId);
            if (!IsValidTransition(data.CurrentState, newState))
            {
                return false;
            }

            previousState = data.CurrentState;
            data.PreviousState = previousState;
            data.CurrentState = newState;
            data.LastTransitionUtc = DateTime.UtcNow;
            revision = ++data.Revision;

            if (newState == ConnectionState.Initializing)
            {
                data.ConnectedAtUtc = null;
            }

            if (newState == ConnectionState.Connected
                || newState == ConnectionState.RemoteSessionHandedOff)
            {
                data.ConnectedAtUtc = DateTime.UtcNow;
            }

            if (newState == ConnectionState.Disconnected)
            {
                data.ErrorMessage = null;
                data.TunnelLocalPort = null;
                data.TunnelProcessId = null;
                data.ConnectedAtUtc = null;
            }

            EnqueueStateChange(
                new ConnectionStateChange(serverId, previousState, newState, null, revision));
        }

        PublishPendingStateChanges();
        return true;
    }

    /// <summary>
    /// Transitions a server to the Error state with a message.
    /// If the current state does not allow transitioning to Error, this is a no-op
    /// returning false.
    /// </summary>
    public bool SetError(string serverId, string errorMessage)
    {
        ConnectionState previousState;
        long revision;

        lock (_lock)
        {
            ConnectionStateData data = GetOrCreate(serverId);
            if (!IsValidTransition(data.CurrentState, ConnectionState.Error))
            {
                return false;
            }

            previousState = data.CurrentState;
            data.PreviousState = previousState;
            data.CurrentState = ConnectionState.Error;
            data.ErrorMessage = errorMessage;
            data.LastTransitionUtc = DateTime.UtcNow;
            revision = ++data.Revision;
            EnqueueStateChange(
                new ConnectionStateChange(
                    serverId,
                    previousState,
                    ConnectionState.Error,
                    errorMessage,
                    revision));
        }

        PublishPendingStateChanges();
        return true;
    }

    /// <summary>
    /// Stores tunnel information (local port and process ID) for a server connection.
    /// </summary>
    public void SetTunnelInfo(string serverId, int localPort, int processId)
    {
        lock (_lock)
        {
            ConnectionStateData data = GetOrCreate(serverId);
            data.TunnelLocalPort = localPort;
            data.TunnelProcessId = processId;
        }
    }

    /// <summary>
    /// Hands the tunnel port recorded for a server to exactly one caller and clears it,
    /// so a release decided from it can only happen once. Two paths release a pane's
    /// tunnel (its process exit, on the exit thread, and its close, on the UI thread);
    /// each used to read the port, release it and tear the state down, so a close landing
    /// between another path's read and teardown released one acquisition twice and closed
    /// a tunnel a third holder still used.
    /// </summary>
    /// <returns>True when a port was recorded and is now the caller's to release.</returns>
    public bool TryTakeTunnelLocalPort(string serverId, out int localPort)
    {
        lock (_lock)
        {
            if (_connections.TryGetValue(serverId, out ConnectionStateData? data)
                && data.TunnelLocalPort is int port
                && port > 0)
            {
                data.TunnelLocalPort = null;
                data.TunnelProcessId = null;
                localPort = port;
                return true;
            }
        }

        localPort = 0;
        return false;
    }

    /// <summary>
    /// Resets a server to Disconnected, clearing all associated data.
    /// Performs intermediate transitions (Disconnecting) when required by the state table.
    /// </summary>
    public void Reset(string serverId)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(serverId, out ConnectionStateData? data))
            {
                return;
            }

            if (data.CurrentState == ConnectionState.Disconnected)
            {
                return;
            }

            QueueResetTransitions(serverId, data);
        }

        PublishPendingStateChanges();
    }

    /// <summary>
    /// Transitions a connection to Disconnected, publishes the terminal notifications,
    /// and then removes the same lifecycle entry from tracking.
    /// </summary>
    public void Teardown(string serverId)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(serverId, out ConnectionStateData? data))
            {
                return;
            }

            if (data.CurrentState != ConnectionState.Disconnected)
            {
                QueueResetTransitions(serverId, data);
            }

            _pendingStateChanges.Enqueue(PendingStateChange.RemoveAfterPublish(
                serverId,
                data,
                data.Revision));
        }

        PublishPendingStateChanges();
    }

    /// <summary>
    /// Removes a server from tracking entirely.
    /// </summary>
    public void Remove(string serverId)
    {
        lock (_lock)
        {
            _connections.Remove(serverId);
        }
    }

    /// <summary>
    /// Returns a snapshot of all currently tracked connections.
    /// </summary>
    public IReadOnlyDictionary<string, ConnectionStateData> GetActiveConnections()
    {
        lock (_lock)
        {
            return _connections
                .Where(kv => kv.Value.CurrentState != ConnectionState.Disconnected
                          && kv.Value.CurrentState != ConnectionState.Error)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Snapshot());
        }
    }

    /// <summary>
    /// Returns server IDs in a specific state.
    /// </summary>
    public IEnumerable<string> GetServersByState(ConnectionState state)
    {
        lock (_lock)
        {
            return _connections
                .Where(kv => kv.Value.CurrentState == state)
                .Select(kv => kv.Key)
                .ToList();
        }
    }

    /// <summary>
    /// Checks whether a transition from one state to another is valid
    /// according to the static transition table.
    /// </summary>
    public static bool IsValidTransition(ConnectionState from, ConnectionState to)
    {
        return ValidTransitions.TryGetValue(from, out HashSet<ConnectionState>? targets) && targets.Contains(to);
    }

    /// <summary>
    /// Returns the metadata associated with a connection state.
    /// </summary>
    public static StateMetadata GetMetadata(ConnectionState state)
    {
        return Metadata[state];
    }

    private ConnectionStateData GetOrCreate(string serverId)
    {
        if (!_connections.TryGetValue(serverId, out ConnectionStateData? data))
        {
            data = new ConnectionStateData();
            _connections[serverId] = data;
        }

        return data;
    }

    private void QueueResetTransitions(string serverId, ConnectionStateData data)
    {
        // Error cannot transition through Disconnecting.
        if (data.CurrentState != ConnectionState.Error
            && IsValidTransition(data.CurrentState, ConnectionState.Disconnecting))
        {
            ConnectionState previousState = data.CurrentState;
            data.PreviousState = previousState;
            data.CurrentState = ConnectionState.Disconnecting;
            data.LastTransitionUtc = DateTime.UtcNow;
            long revision = ++data.Revision;
            EnqueueStateChange(
                new ConnectionStateChange(
                    serverId,
                    previousState,
                    ConnectionState.Disconnecting,
                    null,
                    revision));
        }

        ConnectionState finalPreviousState = data.CurrentState;
        data.PreviousState = finalPreviousState;
        data.CurrentState = ConnectionState.Disconnected;
        data.ErrorMessage = null;
        data.TunnelLocalPort = null;
        data.TunnelProcessId = null;
        data.ConnectedAtUtc = null;
        data.LastTransitionUtc = DateTime.UtcNow;
        long finalRevision = ++data.Revision;
        EnqueueStateChange(
            new ConnectionStateChange(
                serverId,
                finalPreviousState,
                ConnectionState.Disconnected,
                null,
                finalRevision));
    }

    private void EnqueueStateChange(ConnectionStateChange change)
    {
        _pendingStateChanges.Enqueue(PendingStateChange.Publish(change));
    }

    private void PublishPendingStateChanges()
    {
        lock (_lock)
        {
            if (_isPublishingStateChanges || _pendingStateChanges.Count == 0)
            {
                return;
            }

            _isPublishingStateChanges = true;
        }

        Exception? firstDispatchException = null;
        try
        {
            while (true)
            {
                PendingStateChange pending;
                lock (_lock)
                {
                    if (_pendingStateChanges.Count == 0)
                    {
                        _isPublishingStateChanges = false;
                        break;
                    }

                    pending = _pendingStateChanges.Dequeue();
                }

                try
                {
                    if (pending.Change is not null)
                    {
                        StateChanged?.Invoke(pending.Change);
                    }
                }
                catch (Exception ex)
                {
                    firstDispatchException ??= ex;
                }
                finally
                {
                    if (pending.EntryToRemove is not null)
                    {
                        lock (_lock)
                        {
                            if (_connections.TryGetValue(
                                    pending.ServerId,
                                    out ConnectionStateData? current)
                                && ReferenceEquals(current, pending.EntryToRemove)
                                && current.Revision == pending.ExpectedRevision
                                && current.CurrentState == ConnectionState.Disconnected)
                            {
                                _connections.Remove(pending.ServerId);
                            }
                        }
                    }
                }
            }

            if (firstDispatchException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(firstDispatchException)
                    .Throw();
            }
        }
        catch
        {
            lock (_lock)
            {
                _isPublishingStateChanges = false;
            }

            throw;
        }
    }

    private sealed record PendingStateChange(
        ConnectionStateChange? Change,
        string ServerId,
        ConnectionStateData? EntryToRemove,
        long ExpectedRevision)
    {
        public static PendingStateChange Publish(ConnectionStateChange change)
            => new(change, change.ServerId, null, 0);

        public static PendingStateChange RemoveAfterPublish(
            string serverId,
            ConnectionStateData entry,
            long expectedRevision)
            => new(null, serverId, entry, expectedRevision);
    }
}

/// <summary>
/// Holds the mutable state data for a single server connection.
/// </summary>
public sealed class ConnectionStateData
{
    public ConnectionState CurrentState { get; set; } = ConnectionState.Disconnected;
    public ConnectionState PreviousState { get; set; } = ConnectionState.Disconnected;
    public string? ErrorMessage { get; set; }
    public int? TunnelLocalPort { get; set; }
    public int? TunnelProcessId { get; set; }
    public DateTime? ConnectedAtUtc { get; set; }
    public DateTime LastTransitionUtc { get; set; } = DateTime.UtcNow;
    public long Revision { get; set; }

    /// <summary>
    /// Creates a shallow copy of this state data for safe external consumption.
    /// </summary>
    internal ConnectionStateData Snapshot() => new()
    {
        CurrentState = CurrentState,
        PreviousState = PreviousState,
        ErrorMessage = ErrorMessage,
        TunnelLocalPort = TunnelLocalPort,
        TunnelProcessId = TunnelProcessId,
        ConnectedAtUtc = ConnectedAtUtc,
        LastTransitionUtc = LastTransitionUtc,
        Revision = Revision,
    };
}

/// <summary>
/// Immutable notification for an applied connection-state transition.
/// </summary>
/// <param name="ServerId">Connection lifecycle identifier.</param>
/// <param name="PreviousState">State before the transition.</param>
/// <param name="NewState">State after the transition.</param>
/// <param name="ErrorMessage">Optional error associated with the transition.</param>
/// <param name="Revision">Monotonic revision within this connection lifecycle.</param>
public sealed record ConnectionStateChange(
    string ServerId,
    ConnectionState PreviousState,
    ConnectionState NewState,
    string? ErrorMessage,
    long Revision);

/// <summary>
/// Immutable metadata describing a connection state's UI and behavioral properties.
/// </summary>
/// <param name="DisplayKey">i18n key for user-facing display text.</param>
/// <param name="LogKey">i18n key for log messages.</param>
/// <param name="IsTerminal">Whether this state represents a final endpoint.</param>
/// <param name="AllowsUserAction">Whether user interactions are permitted in this state.</param>
/// <param name="IsProgress">Whether this state indicates an ongoing operation.</param>
public sealed record StateMetadata(
    string DisplayKey,
    string LogKey,
    bool IsTerminal,
    bool AllowsUserAction,
    bool IsProgress
);
