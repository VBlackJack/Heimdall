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

namespace Heimdall.App.Tests.Views;

/// <summary>
/// Freezes that the Citrix window-capture paths re-check disposal after every await, and that
/// teardown runs every step even when one of them throws.
/// </summary>
/// <remarks>
/// <para>Window capture polls for up to thirty seconds and hops to the dispatcher several times. A
/// tab closed in the middle disposes the view, yet the dispatcher lambdas went on to touch controls,
/// adopt and embed a window, start the sign-in watch or show the external fallback on a view that
/// no longer existed. Each lambda now opens with a disposal check, and the sign-in watch is only
/// started below one.</para>
/// <para>Read as statements of the lambda bodies: the check must be the FIRST statement of each
/// lambda, so a check folded behind a term that is false by construction, or moved below the work
/// it protects, fails here. Like every text reading this settles that the check stands as a step
/// of the body, not that it runs.</para>
/// </remarks>
public sealed class CitrixDisposalGuardTests
{
    private const string CaptureMember = "private async Task CaptureWindowAsync()";
    private const string WatchMember = "private async Task WatchForSessionAfterAuthAsync(";
    private const string DisposeMember = "public void Dispose()";
    private const string RunnerMember = "private static void RunDisposeStep(";
    private const string LambdaOpening = "Dispatcher.InvokeAsync(() =>";
    private const string Guard = "if (_disposed) return";
    private const string GuardStatement = "if (_disposed) return;";
    private const string StartWatch = "StartAuthWatch(";
    private const string FallbackCall = "await Dispatcher.InvokeAsync(ShowExternalFallbackUnlessDisposed);";
    private const string StepRunner = "RunDisposeStep(";
    private const int DisposeStepCount = 8;

    private const int ExtendedMessageLambda = 0;
    private const int AdoptionLambda = 1;
    private const int SignInLambda = 2;

    [Theory]
    [InlineData(CaptureMember, ExtendedMessageLambda)]
    [InlineData(CaptureMember, AdoptionLambda)]
    [InlineData(CaptureMember, SignInLambda)]
    [InlineData(WatchMember, 0)]
    public void EveryDispatcherLambda_OpensWithADisposalCheck(string member, int occurrence)
    {
        string lambda = DispatcherLambda(Logic(member), occurrence);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(lambda, Guard),
            $"Dispatcher lambda #{occurrence} of {member} does not check _disposed as a statement of its body.");

        Assert.True(
            StartsWithStatement(lambda, Guard),
            $"The disposal check is not the first statement of dispatcher lambda #{occurrence} of {member}.");
    }

    [Fact]
    public void TheSignInWatch_IsStartedBelowADisposalCheck()
    {
        string tail = StatementsAfterLambda(Logic(CaptureMember), SignInLambda);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(tail, GuardStatement),
            "CaptureWindowAsync does not re-check _disposed after the sign-in embed.");

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                "{" + ViewSource.HandlerBody(tail, GuardStatement), StartWatch),
            "StartAuthWatch is not a step below the disposal check, so a torn-down view can start the watch.");
    }

    [Fact]
    public void TheTimeoutFallback_GoesThroughTheDisposedAwareWrapper()
    {
        string logic = Logic(CaptureMember);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, FallbackCall),
            "The capture timeout does not hand the external fallback to ShowExternalFallbackUnlessDisposed.");
    }

    [Fact]
    public void Dispose_RunsEveryTeardownStepThroughItsOwnTryCatch()
    {
        string logic = Logic(DisposeMember);

        Assert.Equal(DisposeStepCount, CountBodyStatements(logic, StepRunner));

        string[] bareSteps =
        [
            "StopAuthWatch();",
            "ReleaseEmbeddedWindow();",
            "_hostPanel?.Dispose();",
            "_sessionHandle?.Terminate();",
            "_sessionHandle?.Dispose();",
            "_session?.Process?.Dispose();",
        ];

        foreach (string step in bareSteps)
        {
            Assert.False(
                ViewSource.IsStatementOfTheMethodBody(logic, step),
                $"Dispose runs '{step}' outside RunDisposeStep, so a throw would skip every later step.");
        }

        string runner = Logic(RunnerMember);
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(runner, "try"),
            "RunDisposeStep no longer wraps the step in a try block.");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(runner, "catch (Exception"),
            "RunDisposeStep no longer catches the step's exception.");
    }

    /// <summary>One method of the Citrix view, comments and literals blanked.</summary>
    private static string Logic(string member)
    {
        string text = ViewSource.HandlerBody(BlankedView(), member);
        int open = text.IndexOf('{');
        Assert.True(open >= 0, $"{member} holds no body.");

        // The last member of a class runs to the end of the file, so the text is cut where the
        // method's own braces balance. Without this, a statement that is absent makes the
        // predicate walk on into the class's closing brace.
        int depth = 0;
        for (int index = open; index < text.Length; index++)
        {
            if (text[index] == '{')
            {
                depth++;
            }
            else if (text[index] == '}' && --depth == 0)
            {
                return text[..(index + 1)];
            }
        }

        return text;
    }

    private static string BlankedView() => ViewSource.WithoutCommentsAndLiterals(File.ReadAllText(
        Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "EmbeddedCitrixView.xaml.cs")));

    /// <summary>The braced body of the n-th dispatcher lambda of the method text.</summary>
    private static string DispatcherLambda(string method, int occurrence)
    {
        (int open, int close) = LambdaSpan(method, occurrence);
        return method[open..(close + 1)];
    }

    /// <summary>The statements that follow the n-th dispatcher lambda, wrapped as one body.</summary>
    private static string StatementsAfterLambda(string method, int occurrence)
    {
        (_, int close) = LambdaSpan(method, occurrence);
        int statementEnd = method.IndexOf(';', close);
        Assert.True(statementEnd > close, "The dispatcher lambda is not followed by a statement end.");
        return "{" + method[(statementEnd + 1)..];
    }

    private static (int Open, int Close) LambdaSpan(string method, int occurrence)
    {
        int cursor = -1;
        for (int seen = 0; seen <= occurrence; seen++)
        {
            cursor = method.IndexOf(LambdaOpening, cursor + 1, StringComparison.Ordinal);
            Assert.True(cursor >= 0, $"Dispatcher lambda #{occurrence} was not found.");
        }

        int open = method.IndexOf('{', cursor);
        int depth = 0;
        for (int index = open; index < method.Length; index++)
        {
            if (method[index] == '{')
            {
                depth++;
            }
            else if (method[index] == '}' && --depth == 0)
            {
                return (open, index);
            }
        }

        throw new InvalidOperationException("The dispatcher lambda never closes.");
    }

    /// <summary>
    /// How many statements of the method body itself (its own brace depth, starting a statement)
    /// begin with the anchor. A call nested in a lambda or a branch is not counted.
    /// </summary>
    private static int CountBodyStatements(string logic, string anchor)
    {
        int count = 0;
        int depth = 0;
        for (int index = logic.IndexOf('{'); index < logic.Length; index++)
        {
            char current = logic[index];
            if (current == '{')
            {
                depth++;
            }
            else if (current == '}')
            {
                depth--;
            }
            else if (depth == 1
                && string.CompareOrdinal(logic, index, anchor, 0, anchor.Length) == 0
                && StartsAStatement(logic, index))
            {
                count++;
            }
        }

        return count;
    }

    private static bool StartsAStatement(string logic, int index)
    {
        for (int back = index - 1; back >= 0; back--)
        {
            char previous = logic[back];
            if (previous is '{' or '}' or ';')
            {
                return true;
            }

            if (!char.IsWhiteSpace(previous))
            {
                return false;
            }
        }

        return true;
    }

    private static bool StartsWithStatement(string body, string statement) =>
        body[1..].TrimStart().StartsWith(statement, StringComparison.Ordinal);
}
