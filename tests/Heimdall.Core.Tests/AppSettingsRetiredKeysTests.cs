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

using System.Text.Json;
using Heimdall.Core.Configuration;

namespace Heimdall.Core.Tests;

/// <summary>
/// Settings that nothing read are retired without breaking the files that still carry them.
/// </summary>
/// <remarks>
/// EnableEventLog, EnableSessionPersistence and EmbeddedIdleTimeoutMs were declared, migrated
/// from the legacy tool and saved, and read by nothing: a user could find them in settings.json
/// and change them to no effect. They leave the type; a file written before keeps loading, and
/// keeps its values, through the extension data every settings file already round-trips.
/// </remarks>
public sealed class AppSettingsRetiredKeysTests
{
    [Fact]
    public void AFileWithTheRetiredKeysLoadsAndKeepsThem()
    {
        const string json = """
            {
              "EnableEventLog": true,
              "EnableSessionPersistence": true,
              "EmbeddedIdleTimeoutMs": 30000,
              "MaxEmbeddedSessions": 7
            }
            """;

        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(7, settings.MaxEmbeddedSessions);
        Assert.Null(typeof(AppSettings).GetProperty("EnableEventLog"));
        Assert.Null(typeof(AppSettings).GetProperty("EnableSessionPersistence"));
        Assert.Null(typeof(AppSettings).GetProperty("EmbeddedIdleTimeoutMs"));
        Assert.True(settings.ExtensionData["EnableEventLog"].GetBoolean());
        Assert.True(settings.ExtensionData["EnableSessionPersistence"].GetBoolean());
        Assert.Equal(30000, settings.ExtensionData["EmbeddedIdleTimeoutMs"].GetInt32());

        string written = JsonSerializer.Serialize(settings);
        Assert.Contains("\"EmbeddedIdleTimeoutMs\":30000", written, StringComparison.Ordinal);
    }
}
