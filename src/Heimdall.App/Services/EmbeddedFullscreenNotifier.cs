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

using Heimdall.App.ViewModels;
using Heimdall.App.Views;
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

internal static class EmbeddedFullscreenNotifier
{
    internal static void Notify(
        IEnumerable<SessionTabViewModel> sessions,
        bool isFullscreen)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        foreach (SessionTabViewModel session in sessions)
        {
            NotifyTree(session.RootContent, isFullscreen, NotifyHost);
        }
    }

    internal static void NotifyTree(
        ISplitContent? root,
        bool isFullscreen,
        Action<object, bool> notifyHost)
    {
        ArgumentNullException.ThrowIfNull(notifyHost);

        foreach (SessionPaneModel pane in SplitTreeHelper.EnumerateLeaves(root))
        {
            if (pane.HostControl is object hostControl)
            {
                notifyHost(hostControl, isFullscreen);
            }
        }
    }

    private static void NotifyHost(object hostControl, bool isFullscreen)
    {
        if (hostControl is EmbeddedRdpView rdpView)
        {
            rdpView.SetFullscreen(isFullscreen);
        }
        else if (hostControl is EmbeddedSshView sshView)
        {
            sshView.Visibility = System.Windows.Visibility.Visible;
        }
    }
}
