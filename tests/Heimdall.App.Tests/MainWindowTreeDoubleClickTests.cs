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
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed class MainWindowTreeDoubleClickTests
{
    public static TheoryData<object?, ServerItemViewModel?, ServerItemViewModel?> DoubleClickTargets
    {
        get
        {
            ServerItemViewModel hitServer = CreateServer("hit-server");
            ServerItemViewModel selectedServer = CreateServer("selected-server");

            return new TheoryData<object?, ServerItemViewModel?, ServerItemViewModel?>
            {
                { hitServer, selectedServer, hitServer },
                { new FolderViewModel { Name = "Production", FullPath = "Production" }, selectedServer, null },
                { null, selectedServer, null }
            };
        }
    }

    [Theory]
    [MemberData(nameof(DoubleClickTargets))]
    public void ResolveTreeDoubleClickServer_OnlyAcceptsHitServer(
        object? hitTarget,
        ServerItemViewModel? selectedServer,
        ServerItemViewModel? expectedServer)
    {
        ServerItemViewModel? resolved = MainWindow.ResolveTreeDoubleClickServer(hitTarget, selectedServer);

        Assert.Same(expectedServer, resolved);
    }

    private static ServerItemViewModel CreateServer(string id)
    {
        return ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = id,
            DisplayName = id,
            RemoteServer = "server.example.test",
            ConnectionType = "SSH"
        });
    }
}
