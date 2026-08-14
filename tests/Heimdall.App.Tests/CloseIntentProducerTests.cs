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
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The intent each close producer emits, for the producers whose driver cannot be reached without
/// a full <c>MainViewModel</c> or <c>SessionCoordinator</c>.
/// </summary>
/// <remarks>
/// A count of <c>CloseIntent.Silent</c> occurrences was the original oracle and it was worthless:
/// totalling them is invariant under a PERMUTATION of intents, so it stayed green while a user
/// gesture ran silently and a vault lock could be blocked by a dialog. These assertions name the
/// argument at each call site instead, which a permutation cannot survive. The producers that ARE
/// reachable have behavioural oracles reading the observed request in
/// <c>ConnectionViewModelCloseTests</c>.
/// </remarks>
public sealed class CloseIntentProducerTests
{
    /// <summary>
    /// A silent request never reaches a guard at all - not merely "is allowed by" one. That is the
    /// property a lock, an application exit or a dead placeholder actually needs.
    /// </summary>
    [Theory]
    [InlineData(CloseIntent.Silent, 0)]
    [InlineData(CloseIntent.Interactive, 1)]
    public async Task SilentRequests_NeverConsultAGuard(CloseIntent intent, int expectedSamples)
    {
        PaneCloseArbiter arbiter = new();
        CountingCloseGuard guard = new();
        CloseRequest request = intent == CloseIntent.Silent
            ? CloseRequest.Silent(DisconnectReason.UserAction)
            : CloseRequest.Interactive(DisconnectReason.UserAction);

        arbiter.Poll(request, [guard]);
        await arbiter.ResolveAsync(request, [guard]);

        Assert.Equal(expectedSamples, guard.SampleCount);
    }

    [Theory]
    [InlineData("ViewModels/MainViewModel.cs", "DisconnectAllSessionsForLock")]
    [InlineData("ViewModels/Session/SessionCoordinator.cs", "OnSessionStartFailed")]
    [InlineData("ViewModels/Session/SessionCoordinator.cs", "OnReconnectAdHocRequestedAsync")]
    public void ProgrammaticTeardown_PassesSilentExplicitly(string relativePath, string methodName)
    {
        string body = ExtractMethodBody(ReadAppSource(relativePath), methodName);

        Assert.Contains("CloseIntent.Silent", body, StringComparison.Ordinal);
    }

    [Fact]
    public void StandardReconnectTeardown_PassesSilentExplicitly()
    {
        string body = ExtractMethodBody(
            ReadAppSource("ViewModels/Session/SessionCoordinator.cs"),
            "OnReconnectRequestedAsync");

        Assert.Contains("CloseIntent.Silent", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gestures a user actually performs must keep their guards. Naming them one by one is the
    /// point: a permutation that silenced any of these is what this whole file exists to catch.
    /// </summary>
    [Theory]
    [InlineData("ViewModels/Session/SessionCoordinator.cs", "OnCloseRequestedAsync")]
    [InlineData("ViewModels/Session/SessionCoordinator.cs", "OnDisconnectRequestedAsync")]
    [InlineData("Views/SessionPaneControl.xaml.cs", "OnClosePaneClick")]
    [InlineData("Views/FloatingSessionWindow.xaml.cs", "ResumeCloseAsync")]
    public void UserGestures_NeverPassSilent(string relativePath, string methodName)
    {
        string body = ExtractMethodBody(ReadAppSource(relativePath), methodName);

        Assert.DoesNotContain("CloseIntent.Silent", body, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseRequest.Silent", body, StringComparison.Ordinal);
    }

    /// <summary>Body of a method, by brace matching from its DECLARATION.</summary>
    /// <remarks>
    /// The declaration is matched by its return type, not by the bare name: a plain name search
    /// lands on a call site such as <c>await OnReconnectRequestedAsync(</c> and then brace-matches
    /// from whatever block follows it, which reads a body that is not the method's.
    /// </remarks>
    private static string ExtractMethodBody(string source, string methodName)
    {
        System.Text.RegularExpressions.Match declaration = System.Text.RegularExpressions.Regex.Match(
            source,
            $@"(?:void|Task|Task<[^>\r\n]+>)\s+{System.Text.RegularExpressions.Regex.Escape(methodName)}\s*\(");
        Assert.True(declaration.Success, $"Declaration not found: {methodName}");

        int open = source.IndexOf('{', declaration.Index);
        Assert.True(open > 0, $"Body not found for {methodName}");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Unbalanced braces while reading {methodName}.");
    }

    private static string ReadAppSource(string relativePath)
    {
        string full = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Heimdall.App",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Heimdall.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException("Cannot find repository root containing Heimdall.slnx.");
    }

    private sealed class CountingCloseGuard : ICloseGuard
    {
        public int SampleCount { get; private set; }

        public CloseGuardState SampleCloseGuardState()
        {
            SampleCount++;
            return new CloseGuardState(true, 1);
        }

        public CloseDecision PollClose(CloseRequest request) => CloseDecision.Allow(1);

        public Task<bool> ResolveCloseAsync(CloseRequest request, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
}
