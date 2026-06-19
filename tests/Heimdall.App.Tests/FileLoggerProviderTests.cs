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

using Heimdall.App.Logging;
using Microsoft.Extensions.Logging;

namespace Heimdall.App.Tests;

public sealed class FileLoggerProviderTests
{
    [Fact]
    public void IsEnabled_WithWarningMinimum_FiltersLowerLevels()
    {
        using var provider = new FileLoggerProvider(LogLevel.Warning, static (_, _, _) => { });
        ILogger logger = provider.CreateLogger("TwinShell.Test");

        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void LogWarning_ForEnabledLevel_WritesComposedMessageAndException()
    {
        List<(LogLevel Level, string Message, Exception? Exception)> entries = [];
        using var provider = new FileLoggerProvider(LogLevel.Warning, (level, message, exception) =>
            entries.Add((level, message, exception)));
        ILogger logger = provider.CreateLogger("TwinShell.JsonSync");
        var exception = new InvalidOperationException("rollback failed");

        logger.LogWarning(exception, "Rollback failed for {Action}", "import");

        var entry = Assert.Single(entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("[TwinShell.JsonSync]", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Rollback failed for import", entry.Message, StringComparison.Ordinal);
        Assert.Same(exception, entry.Exception);
    }

    [Fact]
    public void LogError_ForEnabledLevel_WritesErrorAndException()
    {
        List<(LogLevel Level, string Message, Exception? Exception)> entries = [];
        using var provider = new FileLoggerProvider(LogLevel.Warning, (level, message, exception) =>
            entries.Add((level, message, exception)));
        ILogger logger = provider.CreateLogger("TwinShell.JsonSync");
        var exception = new InvalidOperationException("import failed");

        logger.LogError(exception, "Import failed");

        var entry = Assert.Single(entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(exception, entry.Exception);
    }

    [Fact]
    public void LogInformation_ForFilteredLevel_DoesNotWrite()
    {
        List<(LogLevel Level, string Message, Exception? Exception)> entries = [];
        using var provider = new FileLoggerProvider(LogLevel.Warning, (level, message, exception) =>
            entries.Add((level, message, exception)));
        ILogger logger = provider.CreateLogger("TwinShell.JsonSync");

        logger.LogInformation("Fallback selected");

        Assert.Empty(entries);
    }
}
