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
/// Reads the supply-chain and run-safety properties out of a workflow file, without a
/// YAML library: the questions are structural (a key at a given depth) or per line.
/// </summary>
internal static class WorkflowPolicy
{
    private const string TimeoutKey = "timeout-minutes:";
    private const string UsesKey = "uses:";
    private const string JobsKey = "jobs:";
    private const string PermissionsKey = "permissions:";
    private const string CancelKey = "cancel-in-progress:";
    private const int JobKeyIndent = 2;
    private const int JobPropertyIndent = 4;

    // The only expression allowed to switch cancellation on: it is true for a pull
    // request run and false for a push, which is what keeps a run on master finishing.
    private const string PullRequestOnlyCancel = "${{github.event_name=='pull_request'}}";

    // A full-length commit id: a tag or a branch can be moved after the review, this cannot.
    private static readonly Regex CommitPin = new(
        @"^[^\s@]+@[0-9a-f]{40}\s+#\s*v\d+\.\d+\.\d+\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The <c>uses:</c> entries that are not a 40-hex commit with a version comment.</summary>
    internal static IReadOnlyList<string> UnpinnedActions(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        List<string> unpinned = [];
        foreach (string line in Lines(yaml))
        {
            string trimmed = line.Trim().TrimStart('-').Trim();
            if (!trimmed.StartsWith(UsesKey, StringComparison.Ordinal))
            {
                continue;
            }

            string reference = trimmed[UsesKey.Length..].Trim();

            // A local action lives in this repository, at the commit under test.
            if (reference.StartsWith("./", StringComparison.Ordinal))
            {
                continue;
            }

            if (!CommitPin.IsMatch(reference))
            {
                unpinned.Add(reference);
            }
        }

        return unpinned;
    }

    /// <summary>How many <c>uses:</c> entries the file has, so a guard can prove it saw some.</summary>
    internal static int ActionCount(string yaml)
        => Lines(yaml).Count(line => line.Trim().TrimStart('-').Trim()
            .StartsWith(UsesKey, StringComparison.Ordinal));

    /// <summary>Whether a <c>permissions:</c> key sits at the top level of the file.</summary>
    internal static bool HasTopLevelPermissions(string yaml)
        => Lines(yaml).Any(line => line.StartsWith(PermissionsKey, StringComparison.Ordinal));

    /// <summary>
    /// Whether a push run can never be cancelled: no <c>cancel-in-progress</c> at all,
    /// the literal <c>false</c>, or an expression that is only true for a pull request.
    /// </summary>
    internal static bool NeverCancelsPushRuns(string yaml)
    {
        foreach (string line in Lines(yaml))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith(CancelKey, StringComparison.Ordinal))
            {
                continue;
            }

            string value = trimmed[CancelKey.Length..].Trim();
            int comment = value.IndexOf(" #", StringComparison.Ordinal);
            if (comment >= 0)
            {
                value = value[..comment].Trim();
            }

            bool safe = value == "false"
                || value.Replace(" ", string.Empty, StringComparison.Ordinal) == PullRequestOnlyCancel;
            if (!safe)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The names of the jobs that declare no <c>timeout-minutes</c>.</summary>
    internal static IReadOnlyList<string> JobsWithoutTimeout(string yaml)
    {
        Dictionary<string, int?> jobs = Jobs(yaml);
        return [.. jobs.Where(job => job.Value is null).Select(job => job.Key)];
    }

    /// <summary>The <c>timeout-minutes</c> of each job, or null for a job that has none.</summary>
    internal static Dictionary<string, int?> Jobs(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        Dictionary<string, int?> jobs = [];
        bool inJobs = false;
        string? current = null;
        foreach (string line in Lines(yaml))
        {
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            int indent = line.Length - line.TrimStart().Length;
            if (indent == 0)
            {
                inJobs = line.TrimEnd() == JobsKey;
                current = null;
                continue;
            }

            if (!inJobs)
            {
                continue;
            }

            string trimmed = line.Trim();
            if (indent == JobKeyIndent && trimmed.EndsWith(':'))
            {
                current = trimmed[..^1];
                jobs[current] = null;
            }
            else if (indent == JobPropertyIndent
                && current is not null
                && trimmed.StartsWith(TimeoutKey, StringComparison.Ordinal)
                && int.TryParse(
                    trimmed[TimeoutKey.Length..].Trim(),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int minutes))
            {
                jobs[current] = minutes;
            }
        }

        return jobs;
    }

    private static IEnumerable<string> Lines(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }
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

    // The longest the test job's own steps can add up to, with the restore and the
    // builds on top: a job bound below this would cut a healthy run short.
    private const int MinimumTestJobTimeoutMinutes = 90;

    private const string TestJobName = "build-and-test";

    private static readonly string[] ValidWorkflow =
    [
        "name: CI",
        "on:",
        "  pull_request:",
        "permissions:",
        "  contents: read",
        "concurrency:",
        "  group: g",
        "  cancel-in-progress: ${{ github.event_name == 'pull_request' }}",
        "jobs:",
        "  build:",
        "    runs-on: x",
        "    timeout-minutes: 90",
        "    steps:",
        "      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1",
    ];

    private static string Valid(params (string From, string To)[] mutations)
    {
        string yaml = string.Join('\n', ValidWorkflow);
        foreach ((string from, string to) in mutations)
        {
            Assert.Contains(from, yaml, StringComparison.Ordinal);
            yaml = yaml.Replace(from, to, StringComparison.Ordinal);
        }

        return yaml;
    }

    private static List<(string Name, string Yaml)> ReadWorkflows()
    {
        string workflows = Path.Combine(FindRepoRoot(), ".github", "workflows");
        string[] files = [.. Directory.GetFiles(workflows, "*.yml")
            .Concat(Directory.GetFiles(workflows, "*.yaml"))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        Assert.NotEmpty(files);
        return [.. files.Select(file => (Path.GetFileName(file), File.ReadAllText(file)))];
    }

    [Fact]
    public void EveryWorkflow_PinsEveryActionToACommit()
    {
        List<(string Name, string Yaml)> workflows = ReadWorkflows();

        // A guard that read no `uses:` at all would pass for the wrong reason.
        Assert.True(
            workflows.Sum(workflow => WorkflowPolicy.ActionCount(workflow.Yaml)) > 0,
            "No workflow uses an action: the pin check read nothing.");

        foreach ((string name, string yaml) in workflows)
        {
            Assert.True(
                WorkflowPolicy.UnpinnedActions(yaml).Count == 0,
                $"{name} references actions by tag or branch (a moving target) or without a "
                + "version comment: "
                + string.Join(", ", WorkflowPolicy.UnpinnedActions(yaml)));
        }
    }

    [Fact]
    public void EveryWorkflow_DeclaresTopLevelPermissions()
    {
        foreach ((string name, string yaml) in ReadWorkflows())
        {
            Assert.True(
                WorkflowPolicy.HasTopLevelPermissions(yaml),
                $"{name} has no top-level permissions: block, so its token takes the "
                + "repository default instead of read-only.");
        }
    }

    [Fact]
    public void EveryWorkflow_NeverCancelsARunOnPush()
    {
        foreach ((string name, string yaml) in ReadWorkflows())
        {
            Assert.True(
                WorkflowPolicy.NeverCancelsPushRuns(yaml),
                $"{name} can cancel a run on push: the release preflight reads the CI "
                + "conclusion of the master commit, and a cancelled run reads as no verdict.");
        }
    }

    [Fact]
    public void EveryWorkflowJob_HasATimeout()
    {
        List<(string Name, string Yaml)> workflows = ReadWorkflows();
        Assert.True(
            workflows.Sum(workflow => WorkflowPolicy.Jobs(workflow.Yaml).Count) > 0,
            "No workflow job was read, so the timeout check saw nothing.");

        foreach ((string name, string yaml) in workflows)
        {
            Assert.True(
                WorkflowPolicy.JobsWithoutTimeout(yaml).Count == 0,
                $"{name} has jobs with no timeout-minutes (GitHub's default is six hours): "
                + string.Join(", ", WorkflowPolicy.JobsWithoutTimeout(yaml)));
        }
    }

    [Fact]
    public void TheTestJob_IsBoundedAboveItsStepTimeouts()
    {
        string ci = ReadWorkflows().Single(workflow => workflow.Name == "ci.yml").Yaml;
        int? timeout = WorkflowPolicy.Jobs(ci)[TestJobName];
        Assert.NotNull(timeout);
        Assert.True(
            timeout >= MinimumTestJobTimeoutMinutes,
            $"The {TestJobName} job is bound to {timeout} minutes; its test steps alone "
            + "are allowed 55, before the restore and both builds.");
    }

    [Fact]
    public void UnpinnedActions_ValidWorkflow_HasNone()
        => Assert.Empty(WorkflowPolicy.UnpinnedActions(Valid()));

    [Theory]
    [InlineData("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1", "actions/checkout@v7")]
    [InlineData("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1", "actions/checkout@main")]
    [InlineData("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1", "actions/checkout@3d3c42e # v7.0.1")]
    [InlineData("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1", "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1")]
    public void UnpinnedActions_TagBranchShortShaOrMissingComment_IsRefused(string from, string to)
        => Assert.Single(WorkflowPolicy.UnpinnedActions(Valid((from, to))));

    [Fact]
    public void UnpinnedActions_LocalAction_IsNotAMovingTarget()
        => Assert.Empty(WorkflowPolicy.UnpinnedActions(
            Valid(("      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1",
                "      - uses: ./.github/actions/local"))));

    [Fact]
    public void HasTopLevelPermissions_PermissionsOnlyInsideAJob_IsNotEnough()
        => Assert.False(WorkflowPolicy.HasTopLevelPermissions(
            Valid(("permissions:\n  contents: read\n", string.Empty),
                ("    runs-on: x", "    runs-on: x\n    permissions:\n      contents: read"))));

    [Fact]
    public void NeverCancelsPushRuns_PullRequestOnlyExpression_IsAccepted()
        => Assert.True(WorkflowPolicy.NeverCancelsPushRuns(Valid()));

    [Theory]
    [InlineData("${{ github.event_name == 'pull_request' }}", "true")]
    [InlineData("${{ github.event_name == 'pull_request' }}", "${{ github.event_name != 'pull_request' }}")]
    [InlineData("${{ github.event_name == 'pull_request' }}", "${{ github.ref != 'refs/heads/master' }}")]
    public void NeverCancelsPushRuns_AnythingThatCanCancelAPush_IsRefused(string from, string to)
        => Assert.False(WorkflowPolicy.NeverCancelsPushRuns(Valid((from, to))));

    [Fact]
    public void NeverCancelsPushRuns_LiteralFalse_IsAccepted()
        => Assert.True(WorkflowPolicy.NeverCancelsPushRuns(
            Valid(("${{ github.event_name == 'pull_request' }}", "false"))));

    [Fact]
    public void JobsWithoutTimeout_NamesTheJobThatHasNone()
        => Assert.Equal(
            ["build"],
            WorkflowPolicy.JobsWithoutTimeout(Valid(("    timeout-minutes: 90\n", string.Empty))));

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
