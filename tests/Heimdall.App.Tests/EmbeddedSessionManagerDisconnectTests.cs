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

using Heimdall.App.Services;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Exercises the real <see cref="EmbeddedSessionManager.DisconnectSession"/> dispatch over
/// <see cref="SessionPaneModel.HostControl"/>.
/// </summary>
/// <remarks>
/// The <c>EmbeddedRdpView</c> arm is intentionally not covered: it is pure delegation to
/// <c>DisconnectForTeardown</c> and would require a WPF/COM host harness. The manager is built
/// with <c>null!</c> dependencies on purpose because <c>DisconnectSession</c> uses no instance
/// state; a future dependency on those fields would surface here. The post-cancel behavior where
/// MsTscAx raises <c>OnDisconnected</c> after <c>CancelAutoReconnect</c>, re-surfacing the overlay,
/// is a runtime COM contract and remains validated manually on a live RDP target.
/// </remarks>
public sealed class EmbeddedSessionManagerDisconnectTests
{
    [Fact]
    public void DisconnectSession_NullPane_Throws()
    {
        EmbeddedSessionManager manager = CreateManager();

        Assert.Throws<ArgumentNullException>(() =>
            manager.DisconnectSession(null!, DisconnectReason.UserAction));
    }

    [Fact]
    public void DisconnectSession_DisposableHost_DisposesOnce()
    {
        EmbeddedSessionManager manager = CreateManager();
        DisposableHostSpy host = new DisposableHostSpy();
        SessionPaneModel pane = CreatePane(host);

        manager.DisconnectSession(pane, DisconnectReason.UserAction);

        Assert.Equal(1, host.DisposeCount);
    }

    [Fact]
    public void DisconnectSession_HostThrowsObjectDisposed_Swallowed()
    {
        EmbeddedSessionManager manager = CreateManager();
        DisposableHostSpy host = new DisposableHostSpy(new ObjectDisposedException("host"));
        SessionPaneModel pane = CreatePane(host);

        manager.DisconnectSession(pane, DisconnectReason.UserAction);

        Assert.Equal(1, host.DisposeCount);
    }

    [Fact]
    public void DisconnectSession_HostThrowsGeneric_Swallowed()
    {
        EmbeddedSessionManager manager = CreateManager();
        DisposableHostSpy host = new DisposableHostSpy(new InvalidOperationException("boom"));
        SessionPaneModel pane = CreatePane(host);

        manager.DisconnectSession(pane, DisconnectReason.UserAction);

        Assert.Equal(1, host.DisposeCount);
    }

    [Fact]
    public void DisconnectSession_NullHost_DoesNotThrow()
    {
        EmbeddedSessionManager manager = CreateManager();
        SessionPaneModel pane = CreatePane(null);

        manager.DisconnectSession(pane, DisconnectReason.UserAction);

        Assert.Null(pane.HostControl);
    }

    [Fact]
    public void DisconnectSession_NonDisposableHost_DoesNotThrow()
    {
        EmbeddedSessionManager manager = CreateManager();
        object host = new object();
        SessionPaneModel pane = CreatePane(host);

        manager.DisconnectSession(pane, DisconnectReason.UserAction);

        Assert.Same(host, pane.HostControl);
    }

    [Fact]
    public void ResolveSftpFollowPane_ReturnsFollowEnabledSftpSibling()
    {
        object sshHost = new();
        object sftpHost = new();
        SessionPaneModel sshPane = CreatePane(sshHost);
        sshPane.ConnectionType = "SSH";
        SessionPaneModel sftpPane = CreatePane(sftpHost);
        sftpPane.ConnectionType = "SFTP";
        sftpPane.SftpFollowSshDirectory = true;
        SplitContainerModel root = new()
        {
            First = sshPane,
            Second = sftpPane,
            Orientation = SplitOrientation.Vertical
        };

        SessionPaneModel? resolved = EmbeddedSessionManager.ResolveSftpFollowPane(
            root,
            hostControl => ReferenceEquals(hostControl, sftpHost));

        Assert.Same(sftpPane, resolved);
    }

    [Fact]
    public void ResolveSftpFollowPane_ReturnsNullWhenFollowIsOff()
    {
        object sftpHost = new();
        SessionPaneModel sftpPane = CreatePane(sftpHost);
        sftpPane.ConnectionType = "SFTP";
        sftpPane.SftpFollowSshDirectory = false;

        SessionPaneModel? resolved = EmbeddedSessionManager.ResolveSftpFollowPane(
            sftpPane,
            hostControl => ReferenceEquals(hostControl, sftpHost));

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveSftpFollowPane_ReturnsNullWhenNoSftpSiblingExists()
    {
        object sshHost = new();
        SessionPaneModel sshPane = CreatePane(sshHost);
        sshPane.ConnectionType = "SSH";
        sshPane.SftpFollowSshDirectory = true;

        SessionPaneModel? resolved = EmbeddedSessionManager.ResolveSftpFollowPane(
            sshPane,
            static _ => false);

        Assert.Null(resolved);
    }

    private static EmbeddedSessionManager CreateManager()
        => new EmbeddedSessionManager(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

    private static SessionPaneModel CreatePane(object? hostControl)
    {
        return new SessionPaneModel
        {
            PaneId = "disconnect-test-pane",
            Title = "Disconnect Test",
            ConnectionType = "RDP",
            HostControl = hostControl
        };
    }

    private sealed class DisposableHostSpy : IDisposable
    {
        private readonly Exception? _exceptionToThrow;

        public DisposableHostSpy(Exception? exceptionToThrow = null)
        {
            _exceptionToThrow = exceptionToThrow;
        }

        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;

            if (_exceptionToThrow is not null)
            {
                throw _exceptionToThrow;
            }
        }
    }
}
