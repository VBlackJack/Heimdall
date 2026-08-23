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

namespace Heimdall.App.Tests;

/// <summary>Whether a workflow tests every pull request, or only some of them.</summary>
internal enum PullRequestTrigger
{
    /// <summary>The workflow does not run on pull requests at all.</summary>
    Absent,

    /// <summary>It runs on every pull request, whatever the base branch.</summary>
    Unfiltered,

    /// <summary>It runs only on pull requests whose base is on a list.</summary>
    Filtered,
}

/// <summary>
/// Reads the pull-request trigger out of a workflow file, without a YAML library.
/// </summary>
/// <remarks>
/// Indentation-based rather than a text search, because the question is structural: does
/// a <c>branches</c> key sit inside the <c>pull_request</c> mapping. Searching the file
/// for "branches" would also find the one under <c>push</c>, which is legitimate and has
/// to stay.
/// </remarks>
internal static class WorkflowTrigger
{
    private const string PullRequestKey = "pull_request:";

    internal static PullRequestTrigger DescribePullRequest(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        string[] lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        int onIndex = IndexOfOnKey(lines);
        if (onIndex < 0)
        {
            return PullRequestTrigger.Absent;
        }

        // Flow style - "on: [push, pull_request]" carries no filter by construction.
        int colon = lines[onIndex].IndexOf(':', StringComparison.Ordinal);
        string inline = lines[onIndex][(colon + 1)..].Trim();
        if (inline.Length > 0)
        {
            return inline.Contains("pull_request", StringComparison.Ordinal)
                ? PullRequestTrigger.Unfiltered
                : PullRequestTrigger.Absent;
        }

        int pullRequestIndex = IndexOfPullRequestKey(lines, onIndex, out int pullRequestIndent);
        if (pullRequestIndex < 0)
        {
            return PullRequestTrigger.Absent;
        }

        return HasBranchFilter(lines, pullRequestIndex, pullRequestIndent)
            ? PullRequestTrigger.Filtered
            : PullRequestTrigger.Unfiltered;
    }

    private static int IndexOfOnKey(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimEnd();

            // Quoted spellings included because YAML 1.1 reads a bare "on" as the boolean
            // true, so both forms turn up in real workflow files.
            if (trimmed.StartsWith("on:", StringComparison.Ordinal)
                || trimmed.StartsWith("'on':", StringComparison.Ordinal)
                || trimmed.StartsWith("\"on\":", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOfPullRequestKey(string[] lines, int onIndex, out int indent)
    {
        indent = 0;
        for (int i = onIndex + 1; i < lines.Length; i++)
        {
            if (IsSkippable(lines[i]))
            {
                continue;
            }

            if (IndentOf(lines[i]) == 0)
            {
                // Back at the top level: the "on" mapping has ended.
                return -1;
            }

            // The colon is what separates this from "pull_request_target:", a different
            // trigger with different security properties.
            if (lines[i].Trim().StartsWith(PullRequestKey, StringComparison.Ordinal))
            {
                indent = IndentOf(lines[i]);
                return i;
            }
        }

        return -1;
    }

    private static bool HasBranchFilter(string[] lines, int pullRequestIndex, int pullRequestIndent)
    {
        for (int i = pullRequestIndex + 1; i < lines.Length; i++)
        {
            if (IsSkippable(lines[i]))
            {
                continue;
            }

            if (IndentOf(lines[i]) <= pullRequestIndent)
            {
                return false;
            }

            string key = lines[i].Trim();
            if (key.StartsWith("branches:", StringComparison.Ordinal)
                || key.StartsWith("branches-ignore:", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int IndentOf(string line) => line.Length - line.TrimStart().Length;

    private static bool IsSkippable(string line)
        => line.Trim().Length == 0 || line.TrimStart().StartsWith('#');
}

/// <summary>
/// Every pull request must be tested, including one whose base is another branch.
/// </summary>
/// <remarks>
/// Measured on 2026-08-23: a stack of five review-sized pull requests carried checks on
/// exactly one of them. The other four were not red, they were empty - and empty reads
/// like reviewed, which is the worse failure. The base-branch filter that caused it is
/// the kind of line that comes back on the next workflow edit, so it is pinned here
/// rather than only removed.
/// </remarks>
public sealed class WorkflowTriggerGuardTests
{
    [Fact]
    public void EveryWorkflow_TestsPullRequestsWhateverTheirBase()
    {
        string workflows = Path.Combine(FindRepoRoot(), ".github", "workflows");
        Assert.True(Directory.Exists(workflows), $"No workflow directory at {workflows}");

        // Enumerated, not named. A guard that hardcodes "ci.yml" stays green through the
        // day someone adds a second workflow with the filter back in it.
        string[] files = [.. Directory.GetFiles(workflows, "*.yml")
            .Concat(Directory.GetFiles(workflows, "*.yaml"))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];

        Assert.NotEmpty(files);

        List<string> filtered = [];
        int triggered = 0;

        foreach (string file in files)
        {
            PullRequestTrigger trigger =
                WorkflowTrigger.DescribePullRequest(File.ReadAllText(file));

            if (trigger == PullRequestTrigger.Unfiltered)
            {
                triggered++;
            }
            else if (trigger == PullRequestTrigger.Filtered)
            {
                filtered.Add(Path.GetFileName(file));
            }
        }

        // Without this, a repository whose workflows had all stopped running on pull
        // requests would pass the check below by having nothing left to check.
        Assert.True(
            triggered > 0,
            "No workflow runs on pull requests at all, so nothing gates a pull request.");

        Assert.True(
            filtered.Count == 0,
            "These workflows exempt pull requests based on a topic branch, so a stacked "
            + "review carries no checks: "
            + string.Join(", ", filtered));
    }

    [Fact]
    public void DescribePullRequest_BaseBranchList_IsSeenAsFiltered()
        => Assert.Equal(
            PullRequestTrigger.Filtered,
            WorkflowTrigger.DescribePullRequest(
                "on:\n  push:\n    branches: [master]\n  pull_request:\n    branches: [master]\n"));

    [Fact]
    public void DescribePullRequest_BranchesIgnore_IsAlsoAFilter()
        => Assert.Equal(
            PullRequestTrigger.Filtered,
            WorkflowTrigger.DescribePullRequest(
                "on:\n  pull_request:\n    branches-ignore: [wip/**]\n"));

    [Fact]
    public void DescribePullRequest_NoNestedKeys_IsUnfiltered()
        => Assert.Equal(
            PullRequestTrigger.Unfiltered,
            WorkflowTrigger.DescribePullRequest(
                "on:\n  push:\n    branches: [master]\n  pull_request:\n"));

    [Fact]
    public void DescribePullRequest_PushFilterAlone_DoesNotCountAgainstPullRequests()
    {
        // The discriminating case for the whole parser. A text search for "branches"
        // would call this filtered, and the push filter is legitimate: it is what stops
        // the workflow running twice on every topic-branch push.
        Assert.Equal(
            PullRequestTrigger.Unfiltered,
            WorkflowTrigger.DescribePullRequest(
                "on:\n  push:\n    branches: [main, master, develop]\n  pull_request:\n"));
    }

    [Fact]
    public void DescribePullRequest_FilteredPushAfterIt_IsStillUnfiltered()
    {
        // The exit boundary of the pull_request mapping, and the only case that
        // measures it. Order is free in YAML, so the filtered push may sit after the
        // unfiltered pull_request - and a parser that walks one key too far reads the
        // push filter as the pull-request one, failing a workflow that is correct.
        Assert.Equal(
            PullRequestTrigger.Unfiltered,
            WorkflowTrigger.DescribePullRequest(
                "on:\n  pull_request:\n  push:\n    branches: [master]\n"));
    }

    [Fact]
    public void DescribePullRequest_OtherNestedKeys_AreNotMistakenForFilters()
        => Assert.Equal(
            PullRequestTrigger.Unfiltered,
            WorkflowTrigger.DescribePullRequest(
                "on:\n  pull_request:\n    types: [opened, synchronize]\n"));

    [Fact]
    public void DescribePullRequest_CommentInsideTheBlock_DoesNotEndIt()
        => Assert.Equal(
            PullRequestTrigger.Filtered,
            WorkflowTrigger.DescribePullRequest(
                "on:\n  pull_request:\n    # a note\n\n    branches: [master]\n"));

    [Fact]
    public void DescribePullRequest_FlowSequence_IsUnfiltered()
        => Assert.Equal(
            PullRequestTrigger.Unfiltered,
            WorkflowTrigger.DescribePullRequest("on: [push, pull_request]\n"));

    [Fact]
    public void DescribePullRequest_PullRequestTarget_IsNotTheSameTrigger()
    {
        // pull_request_target runs with repository secrets against unreviewed code. It is
        // not a substitute for pull_request and must not be read as one.
        Assert.Equal(
            PullRequestTrigger.Absent,
            WorkflowTrigger.DescribePullRequest(
                "on:\n  pull_request_target:\n    branches: [master]\n"));
    }

    [Fact]
    public void DescribePullRequest_PushOnly_IsAbsent()
        => Assert.Equal(
            PullRequestTrigger.Absent,
            WorkflowTrigger.DescribePullRequest("on:\n  push:\n    branches: [master]\n"));

    [Fact]
    public void DescribePullRequest_KeyBelowTheOnBlock_IsNotReadAsATrigger()
    {
        // "pull_request:" appearing inside a job says nothing about when the workflow
        // runs, and reading it as a trigger would report a gate that does not exist.
        Assert.Equal(
            PullRequestTrigger.Absent,
            WorkflowTrigger.DescribePullRequest(
                "on:\n  push:\n    branches: [master]\n\njobs:\n  build:\n    pull_request: no\n"));
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
            "Cannot find repository root containing Heimdall.slnx from test binary directory: "
            + AppContext.BaseDirectory);
    }
}
