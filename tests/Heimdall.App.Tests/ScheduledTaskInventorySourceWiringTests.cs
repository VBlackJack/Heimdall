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
/// Reads which collection the scheduler resolves a task's profile from.
/// </summary>
/// <remarks>
/// <para><b>What the resolver's own tests cannot see.</b>
/// <c>ScheduledTaskServerResolverTests</c> pins the rule - identifier first, name only when
/// exactly one profile carries it - and passes it whatever list the test builds. It says nothing
/// about which list the application hands it, and that is where the defect was: the scheduler
/// passed <c>ServerList.Servers</c>, which applying a filter replaces with the visible results
/// only.</para>
/// <para><b>The failure the difference produces.</b> Two profiles named "Production", one of them
/// the machine a task names by identifier. Turn on a filter that hides it. The identifier now
/// matches nothing, the name matches exactly one of the profiles still visible - the other
/// machine - and the scheduler connects to it, unattended. No deletion, no migration, no stale
/// reference: a filter was enough.</para>
/// <para><b>Why this is read rather than run.</b> The resolution sits inside
/// <c>OnTaskDueAsync</c>, behind a live main view-model, a session coordinator and a timer. What
/// the rule DECIDES is measured behaviourally next door; what is read here is the one token that
/// decides which inventory it decides over.</para>
/// </remarks>
public sealed class ScheduledTaskInventorySourceWiringTests
{
    private const string Member = "private async Task OnTaskDueAsync(ScheduledTaskDto task)";

    // Carried whole. The difference between the right answer and the shipped defect is one
    // identifier inside an argument list, which no reading anchored on the call's name could see.
    private const string ResolveStatement =
        "var server = ScheduledTaskServerResolver.Resolve(task.ServerId, task.ServerName, "
        + "_main.ServerList.AllServers, s => s.Id, s => s.DisplayName);";

    [Fact]
    public void TheSchedulerResolvesAgainstTheWholeInventory()
        => Assert.True(
            ViewSource.IsStatementOfTheMethodBody(Logic(Source()), Normalise(ResolveStatement)),
            "The scheduler no longer resolves a task's profile from the full inventory as a step "
                + "of its own body. Reading the filtered view lets a filter hide the profile a "
                + "task names, after which the name rule can find a different profile of that "
                + "name and connect to it.");

    // Positive control 1. The filtered view, which is exactly what shipped and what compiles
    // identically: same call, same argument count, one identifier changed.
    [Fact]
    public void TheResolutionIsNotFoundWhenItReadsTheFilteredView()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                Logic(Mutate("_main.ServerList.AllServers", "_main.ServerList.Servers")),
                Normalise(ResolveStatement)),
            "A scheduler resolving from the sidebar's filtered view satisfies this file's "
                + "reading, so the reading cannot tell the inventory from what the user happens "
                + "to be looking at.");

    // Positive control 2. The resolution left in a comment.
    [Fact]
    public void TheResolutionIsNotFoundWhenItIsOnlyLeftInAComment()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                Logic(Mutate("var server = ScheduledTaskServerResolver.Resolve(",
                             "var server = (ServerItemViewModel?)null; _ = ScheduledTaskServerResolver.Resolve(")),
                Normalise(ResolveStatement)),
            "A resolution whose result is discarded satisfies this file's reading.");

    /// <summary>The member still exists, so nothing above can pass by reading an empty body.</summary>
    [Fact]
    public void TheMemberThisFileMeasuresStillExists()
        => Assert.NotEqual(string.Empty, Logic(Source()).Trim());

    private static string Source() => File.ReadAllText(Path.Combine(
        ViewSource.RepoRoot(),
        "src",
        "Heimdall.App",
        "ViewModels",
        "Scheduled",
        "ScheduledTasksViewModel.cs"));

    /// <summary>The member's body, blanked of comments and literals and of its own line breaks.</summary>
    /// <remarks>
    /// The statement spans several lines in the source, and the predicate compares raw text. Both
    /// sides are collapsed to single-spaced form so the assertion is about the tokens rather than
    /// about where the formatter chose to wrap them.
    /// </remarks>
    private static string Logic(string source) =>
        Normalise(ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(source), Member));

    private static string Normalise(string text)
    {
        string collapsed = string.Join(
            ' ',
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        // A wrapped argument list leaves a space just inside its parentheses once the line breaks
        // collapse. Removing it lets the constant above read as the statement does rather than as
        // an artefact of where the formatter chose to wrap.
        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
            .Replace(" )", ")", StringComparison.Ordinal);
    }

    private static string Mutate(string original, string replacement)
    {
        string source = Source();
        Assert.True(
            source.Contains(original, StringComparison.Ordinal),
            $"The fragment this control mutates is no longer in the source: {original}");

        return source.Replace(original, replacement, StringComparison.Ordinal);
    }
}
