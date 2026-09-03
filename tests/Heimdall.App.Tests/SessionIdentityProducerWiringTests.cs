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
/// Reads the two places that put a minted key on a profile without a running test able to reach
/// them, so each records the profile it is replacing.
/// </summary>
/// <remarks>
/// <para><b>What the DTO's own tests cannot see.</b>
/// <c>ServerProfileTrustIdentityTests</c> pins what <c>AdoptSessionIdentity</c> and
/// <c>InventoryProfileId</c> do, and every consumer test calls <c>AdoptSessionIdentity</c> itself
/// to build its fixture. So a producer that went back to assigning <c>Id</c> directly - which
/// compiles, and which is what the code did before - leaves all of them green while every
/// approval given in that pane is filed under a key that dies with it.</para>
/// <para><b>Two of the three producers are read here; the third is run.</b>
/// <c>SplitServiceTests.SplitSessionWithServerAsync_Rdp_PaneProfilesStillResolveToTheInventoryProfileForTrust</c>
/// exercises <c>SplitService</c> behaviourally. The connect pipeline and the ad-hoc reconnect
/// both sit behind a full session pipeline, so they are read instead - and what is read is the
/// whole statement, because the mutant that matters keeps the identifier and drops the
/// recording.</para>
/// <para><b>What it cannot see.</b> That either statement RUNS. The predicate reads a statement
/// at its body's own brace depth and walks past every conditional early return above it.</para>
/// </remarks>
public sealed class SessionIdentityProducerWiringTests
{
    private const string PipelineMember =
        "internal async Task<BulkConnectOutcome> RunConnectionPipelineAsync(";

    private const string AdHocMember =
        "private static ServerProfileDto CloneAdHocProfileForConnection(ServerProfileDto snapshot)";

    private const string PipelineStatement = "serverDto.AdoptSessionIdentity(sessionId);";

    private const string AdHocStatement =
        "runtimeProfile.AdoptSessionIdentity(SessionIdCodec.Create(snapshot.Id));";

    [Fact]
    public void TheConnectPipelineRecordsTheProfileItsSessionKeyReplaces()
        => Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                Logic(ServerListSource(), PipelineMember), PipelineStatement),
            "The connect pipeline no longer adopts its session key as a step of its own body, so "
                + "the profile a certificate approval belongs to is no longer recorded and the "
                + "approval is filed under a key that dies with the connection.");

    // Positive control 1. The bare assignment, which is exactly what the code did before and what
    // still compiles: the identifier is set, the profile is left saying nothing about its origin.
    [Fact]
    public void ThePipelineStatementIsNotFoundWhenTheIdentifierIsAssignedDirectly()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                Logic(
                    Mutate(ServerListSource(), PipelineStatement, "serverDto.Id = sessionId;"),
                    PipelineMember),
                PipelineStatement),
            "A pipeline that assigns the session key straight onto Id satisfies this file's "
                + "reading, so the reading cannot tell a recorded origin from a lost one.");

    // Positive control 2. The adoption left in a comment.
    [Fact]
    public void ThePipelineStatementIsNotFoundWhenItIsOnlyLeftInAComment()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                Logic(
                    Mutate(ServerListSource(), PipelineStatement, "// " + PipelineStatement),
                    PipelineMember),
                PipelineStatement),
            "A commented-out adoption satisfies this file's reading of the pipeline.");

    [Fact]
    public void TheAdHocReconnectRecordsTheProfileItsSessionKeyReplaces()
        => Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                Logic(CoordinatorSource(), AdHocMember), AdHocStatement),
            "The ad-hoc reconnect no longer adopts its session key as a step of its own body, so "
                + "a reconnected pane files its approvals under an identifier that dies with it.");

    // Positive control 3. The same bare assignment on the other producer.
    [Fact]
    public void TheAdHocStatementIsNotFoundWhenTheIdentifierIsAssignedDirectly()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                Logic(
                    Mutate(
                        CoordinatorSource(),
                        AdHocStatement,
                        "runtimeProfile.Id = SessionIdCodec.Create(snapshot.Id);"),
                    AdHocMember),
                AdHocStatement),
            "An ad-hoc reconnect that assigns the session key straight onto Id satisfies this "
                + "file's reading, so the reading cannot tell a recorded origin from a lost one.");

    /// <summary>Both members still exist, so nothing above can pass by reading an empty body.</summary>
    [Fact]
    public void EveryMemberThisFileMeasuresStillExists()
    {
        Assert.NotEqual(string.Empty, Logic(ServerListSource(), PipelineMember).Trim());
        Assert.NotEqual(string.Empty, Logic(CoordinatorSource(), AdHocMember).Trim());
    }

    private static string ServerListSource() => File.ReadAllText(Path.Combine(
        ViewSource.RepoRoot(), "src", "Heimdall.App", "ViewModels", "ServerListViewModel.cs"));

    private static string CoordinatorSource() => File.ReadAllText(Path.Combine(
        ViewSource.RepoRoot(),
        "src",
        "Heimdall.App",
        "ViewModels",
        "Session",
        "SessionCoordinator.cs"));

    private static string Logic(string source, string member) =>
        ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(source), member);

    /// <summary>One fragment replaced, for a control to measure.</summary>
    private static string Mutate(string source, string original, string replacement)
    {
        Assert.True(
            source.Contains(original, StringComparison.Ordinal),
            $"The fragment this control mutates is no longer in the source: {original}");

        return source.Replace(original, replacement, StringComparison.Ordinal);
    }
}
