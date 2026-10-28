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
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The staging helper is only worth having if the paste creates its folder through it. That site
/// cannot run in a test, so it is read. The startup sweep sits inside a lambda of OnStartup, where
/// a source reading cannot establish anything; it is checked by launching the application.
/// </summary>
public sealed class CrossServerPasteStagingWiringTests
{
    [Fact]
    public void ThePaste_CreatesItsStagingFolderThroughTheStagingHelper()
    {
        string source = ReadSource("ViewModels", "EmbeddedSftpViewModel.Clipboard.cs");
        string body = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(source),
            "private static string CreateCrossEndpointTempPath()");

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(body, "return CrossServerPasteStaging.CreateTempPath();"),
            "the paste does not create its staging path through the staging helper");
        Assert.DoesNotContain("\"cross-server-paste\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.CreateDirectory(directory)", source, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relativeToApp)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Heimdall.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);
        string path = Path.Combine([directory!, "src", "Heimdall.App", .. relativeToApp]);
        return File.ReadAllText(path);
    }
}
