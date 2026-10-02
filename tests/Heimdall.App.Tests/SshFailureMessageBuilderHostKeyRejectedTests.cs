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

using System.IO;
using System.Text.Json;
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// A host key the user declined used to read "Connection was cancelled" with no host named.
/// </summary>
public sealed class SshFailureMessageBuilderHostKeyRejectedTests : IDisposable
{
    private const string RejectedTemplate = "FIXTURE key of {0}:{1} refused.";
    private const string CancelledSentence = "FIXTURE cancelled.";

    private readonly string _localesPath;

    public SshFailureMessageBuilderHostKeyRejectedTests()
    {
        _localesPath = Path.Combine(Path.GetTempPath(), $"heimdall-locales-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_localesPath);
        File.WriteAllText(
            Path.Combine(_localesPath, "en.json"),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [SshLocalizationKeys.ErrorSshHostKeyRejected] = RejectedTemplate,
                [SshLocalizationKeys.ErrorSshCancelled] = CancelledSentence,
            }));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_localesPath, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public async Task HostKeyRejected_NamesTheHostAndPort_AndIsNotTheCancelledSentence()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(_localesPath, "en");

        string message = SshFailureMessageBuilder.HostKeyRejected(localizer, "gw.example.test", 2222);

        Assert.Equal("FIXTURE key of gw.example.test:2222 refused.", message);
        Assert.NotEqual(SshFailureMessageBuilder.Cancelled(localizer), message);
    }
}
