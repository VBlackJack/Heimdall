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
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

public sealed class RemoteClipboardServiceTests
{
    [Fact]
    public void SetGetClear_StoresSnapshotThenClears()
    {
        RemoteClipboardService service = new();
        SftpClipboardContent content = CreateContent();

        service.Set(content);

        Assert.NotNull(service.Current);
        Assert.Equal(SftpClipboardMode.Copy, service.Current!.Mode);
        Assert.Equal("/src", service.Current.SourceDirectory);
        Assert.Equal("host=server;port=22;user=alice", service.Current.SourceEndpointKey);
        Assert.Single(service.Current.Entries);

        service.Clear();

        Assert.Null(service.Current);
    }

    [Fact]
    public void Changed_FiresOnSetAndClear()
    {
        RemoteClipboardService service = new();
        int changedCount = 0;
        service.Changed += () => changedCount++;

        service.Set(CreateContent());
        service.Clear();

        Assert.Equal(2, changedCount);
    }

    private static SftpClipboardContent CreateContent()
    {
        SftpFileInfo entry = new(
            "a.txt",
            "/src/a.txt",
            false,
            1,
            DateTime.UnixEpoch,
            "rw-r--r--",
            "1000",
            "1000");

        return new SftpClipboardContent(
            [entry],
            "/src",
            SftpClipboardMode.Copy,
            "host=server;port=22;user=alice");
    }
}
