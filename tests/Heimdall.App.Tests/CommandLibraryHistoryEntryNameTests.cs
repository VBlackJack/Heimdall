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

using Heimdall.App.ViewModels.CommandLibrary;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins what a history row's spoken name is made of: title, host when present, sent or
/// copied, and time, in that order.
/// </summary>
public sealed class CommandLibraryHistoryEntryNameTests
{
    [Fact]
    public void ASentRowWithAHost_NamesTitleHostSentAndTime()
    {
        var entry = Entry(host: "srv-01", executed: true);

        Assert.Equal("Uptime, srv-01, [ToolCmdLibHistorySent], 12:00", entry.AccessibleName);
    }

    [Fact]
    public void ACopiedRowWithoutAHost_NamesTitleCopiedAndTime()
    {
        var entry = Entry(host: null, executed: false);

        Assert.Equal("Uptime, [ToolCmdLibHistoryCopied], 12:00", entry.AccessibleName);
    }

    private static CommandLibraryHistoryEntry Entry(string? host, bool executed) => new()
    {
        ActionTitle = "Uptime",
        GeneratedCommand = "uptime",
        Timestamp = "12:00",
        TargetHost = host,
        IsExecuted = executed,
        Localize = key => "[" + key + "]"
    };
}
