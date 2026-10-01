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

namespace Heimdall.App.Views.EmbeddedRdp;

internal static class RdpConnectionPhasePolicy
{
    public static int GetLitSegmentCount(RdpConnectionPhase phase) => phase switch
    {
        RdpConnectionPhase.None => 0,
        RdpConnectionPhase.Preparing => 1,
        RdpConnectionPhase.Connecting => 2,
        RdpConnectionPhase.Loading => 3,
        RdpConnectionPhase.Connected => 4,
        _ => 0
    };

    public static string? GetStatusKey(RdpConnectionPhase phase) => phase switch
    {
        RdpConnectionPhase.Preparing => "RdpStatusPreparing",
        RdpConnectionPhase.Connecting => "RdpStatusConnecting",
        RdpConnectionPhase.Loading => "RdpStatusLoading",
        RdpConnectionPhase.Connected => "RdpStatusConnected",
        _ => null
    };

    public static (bool CancelConnectVisible, bool DisconnectVisible) ResolveVisibility(
        RdpConnectionPhase phase)
        => phase switch
        {
            RdpConnectionPhase.Preparing => (true, false),
            RdpConnectionPhase.Connecting => (true, false),
            RdpConnectionPhase.Loading => (true, false),
            RdpConnectionPhase.Connected => (false, true),
            _ => (false, false)
        };

    /// <summary>
    /// Whether the header offers a way back in: the session ended in an error or a plain
    /// disconnect and no overlay is already carrying the Reconnect action.
    /// </summary>
    /// <remarks>
    /// Several endings (refused certificate, locked vault, startup failure, clean-exit codes)
    /// write only a status line and show no overlay; without this the only way back was the tab
    /// context menu.
    /// </remarks>
    public static bool IsReconnectActionVisible(RdpSessionStatus status, bool overlayVisible)
        => !overlayVisible
            && status is RdpSessionStatus.Error or RdpSessionStatus.Disconnected;
}
