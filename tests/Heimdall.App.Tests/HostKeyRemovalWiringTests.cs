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
using System.Text.RegularExpressions;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// A hashed known_hosts entry replaced by the connection factory is removed through a detached
/// trust service whose own event reaches nobody; the store event is the only path to persistence.
/// The statement is read from source with comments and literals blanked, so it establishes that
/// the wiring is written, never that it runs.
/// </summary>
public sealed class HostKeyRemovalWiringTests
{
    [Fact]
    public void TheStoreKeyRemovedEvent_IsForwardedToThePersistenceCoalescerOnce()
    {
        Assert.Equal(1, WiringCount(AppSource()));
    }

    [Fact]
    public void TheWiringReading_SeesTheStatementAndOnlyThat()
    {
        const string Wired = "    hostKeyStore.KeyRemoved += key => hostKeyPersistence.Remove(key);\n";
        const string Commented = "    // hostKeyStore.KeyRemoved += key => hostKeyPersistence.Remove(key);\n";
        const string Other = "    hostKeyStore.KeyRemoved += key => { };\n";

        Assert.Equal(1, WiringCount(Wired));
        Assert.Equal(0, WiringCount(Commented));
        Assert.Equal(0, WiringCount(Other));
    }

    private static int WiringCount(string source)
        => Regex.Matches(
            ViewSource.WithoutCommentsAndLiterals(source),
            @"hostKeyStore\.KeyRemoved\s*\+=\s*key\s*=>\s*hostKeyPersistence\.Remove\(key\)\s*;",
            RegexOptions.None,
            TimeSpan.FromSeconds(5)).Count;

    private static string AppSource()
        => File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "App.xaml.cs"));
}
