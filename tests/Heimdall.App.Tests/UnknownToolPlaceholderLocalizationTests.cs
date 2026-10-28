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

namespace Heimdall.App.Tests;

/// <summary>
/// The placeholder shown for a tool id the registry does not know is a locale string with the
/// tool id placeholder, in every language.
/// </summary>
/// <remarks>
/// Building the placeholder control needs a running WPF application for its theme resources, which
/// the blocking test lane does not have, so only the strings are pinned here.
/// </remarks>
public sealed class UnknownToolPlaceholderLocalizationTests
{
    private const string Key = "ErrorEmbeddedToolUnknown";

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public void TheKeyCarriesTheToolIdPlaceholderInEveryLanguage(string language)
    {
        string json = File.ReadAllText(Path.Combine(FindRepoRoot(), "locales", language + ".json"));
        using JsonDocument document = JsonDocument.Parse(json);

        string value = document.RootElement.GetProperty(Key).GetString()!;

        Assert.Contains("{0}", value, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from: {AppContext.BaseDirectory}");
    }
}
