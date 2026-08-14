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

namespace Heimdall.App.Tests;

/// <summary>
/// xUnit collection that serializes App-side test classes mutating the static
/// state of <c>CredentialProtector</c> (the vault DEK / enabled slots), so they
/// never run concurrently and cross-contaminate each other.
/// </summary>
/// <remarks>
/// Reading that state is as much a reason to join as writing it. A class that only protects a
/// value inherits whatever another class left behind, or has it replaced mid-test: the legacy
/// integrity key is process-global, so a value protected with one key and read back with another
/// simply fails to decrypt, and the failure surfaces far from its cause.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class CredentialProtectorAppCollection
{
    /// <summary>The collection name shared by all members.</summary>
    public const string Name = "CredentialProtectorAppState";
}

/// <summary>
/// Guards the membership rule above. Omitting a single class is invisible in review and produces
/// a failure that depends on which other tests share the run: on 2026-08-14 one omitted class
/// failed four runs out of four when paired with a writer, and passed alone.
/// </summary>
public sealed class CredentialProtectorCollectionMembershipTests
{
    [Fact]
    public void EveryTestClassTouchingTheProtectorJoinsTheSerializingCollection()
    {
        string testsDirectory = Path.GetDirectoryName(FindThisFile())!;
        Regex classDeclaration = new(@"^\s*(?:public|internal).*\bclass\s+(\w+)", RegexOptions.Compiled);

        List<string> offenders = [];

        foreach (string path in Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories))
        {
            // This file names the protector in its own prose, so it would flag itself.
            if (Path.GetFileName(path) == "CredentialProtectorAppCollection.cs")
            {
                continue;
            }

            // Matched line by line: the repository stores CRLF, and an anchored multiline regex
            // over the raw text silently never matches.
            string[] lines = File.ReadAllLines(path);
            if (!lines.Any(line => line.Contains("CredentialProtector.", StringComparison.Ordinal)))
            {
                continue;
            }

            bool joined = lines.Any(line => line.Contains($"Collection({nameof(CredentialProtectorAppCollection)}.Name)", StringComparison.Ordinal));
            if (!joined)
            {
                string? declared = lines
                    .Select(line => classDeclaration.Match(line))
                    .FirstOrDefault(match => match.Success)?.Groups[1].Value;

                offenders.Add($"{Path.GetFileName(path)} ({declared ?? "unknown class"})");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These test classes touch the CredentialProtector process-global state without joining " +
            $"the {Name} collection, so their result depends on what else is in the run:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private const string Name = CredentialProtectorAppCollection.Name;

    private static string FindThisFile()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            string candidate = Path.Combine(directory, "CredentialProtectorAppCollection.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            string nested = Path.Combine(directory, "tests", "Heimdall.App.Tests", "CredentialProtectorAppCollection.cs");
            if (File.Exists(nested))
            {
                return nested;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException("Cannot locate CredentialProtectorAppCollection.cs from the test binary directory.");
    }
}
