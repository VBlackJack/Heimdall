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

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Reads the two hops that carry a connection's route to the certificate question, so the gateway
/// it names is the one that connection actually went through.
/// </summary>
/// <remarks>
/// <para><b>The defect these two hops close.</b> The pane is built from the profile the connection
/// used, and then read the GATEWAY LIST from the application's current settings. Those are two
/// different instants - the connect, and the materialisation that follows it - and
/// <c>ConfigManager.CurrentSettings</c> hands out a fresh deep clone on every read, so the second
/// carries every edit made in between. Editing a gateway's host during a slow tunnel
/// establishment named the new host under "Reached through" for a certificate that had arrived
/// through the old one. That line exists to tell two same-named machines apart, so one that can
/// name the wrong one is worse than no line.</para>
/// <para><b>And the second defect, which withholding the route did not close.</b> A tunnel that
/// is already open is reused on a hash over gateway IDENTIFIERS, which an edit leaves alone, so
/// the connection reusing it cannot resolve its route at all - and answering that with no line
/// left two identically named profiles reaching two different sites indistinguishable, which is
/// the confusion the line exists to end. The route is therefore composed once at the dial and
/// travels as text; these are the two hops it travels along.</para>
/// <para><b>Why this is read rather than run.</b> Both hops end in a WPF code-behind whose
/// certificate path needs a live <c>Application</c> and an ActiveX host before it reaches any of
/// this. What each hop DECIDES is measured behaviourally elsewhere and against no WPF at all:
/// <c>TunnelReuseIdentityTests</c> pins that a reused tunnel reports the route it was opened
/// through rather than the one the reusing connection resolves, and <c>RdpHandlerTests</c> pins
/// that the session result carries what the tunnel layer settled rather than the settings the
/// pane would re-read. Nothing in either fails if the route stops being passed from one hop to
/// the next, which is the junction this file reads.</para>
/// <para><b>What it cannot see.</b> That any of it RUNS. The predicate reads a statement at its
/// body's own brace depth and walks straight past every conditional early return above it.</para>
/// </remarks>
public sealed class RdpTrustRouteCarrierWiringTests
{
    private const string CreateHostControlMember = "public object CreateHostControl(";
    private const string OriginMember =
        "private RdpTrustPromptOrigin BuildTrustPromptOrigin(RdpCertificatePromptContext context)";

    // The hand-over, carried whole. Resolving a route from the materialisation snapshot instead
    // leaves the property name exactly where a bare Contains would have anchored, while the
    // question goes back to naming whichever gateway the settings happen to hold when the pane
    // is built.
    private const string CarrierStatement = "view.GatewayRoute = rdp.GatewayRoute;";

    // And what the question then does with it, carried whole for the same reason: resolve it
    // here from the pane's own snapshot and the local, its type and its name all stay put.
    private const string RouteStatement = "string? route = GatewayRoute;";

    /// <summary>
    /// Both members still exist, so nothing below can pass by reading an empty body.
    /// </summary>
    [Fact]
    public void EveryMemberThisFileMeasuresStillExists()
    {
        Assert.NotEqual(string.Empty, ManagerLogic(ManagerSource()).Trim());
        Assert.NotEqual(string.Empty, ViewSource.HandlerLogic(OriginMember).Trim());
    }

    [Fact]
    public void ThePaneIsHandedTheSettingsItsConnectionResolvedItsGatewayChainFrom()
        => Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                RdpBranch(ManagerLogic(ManagerSource())), CarrierStatement),
            "The RDP branch no longer hands the pane the route the tunnel carrying it was "
                + "dialled through, as a step of its own block. The certificate question then "
                + "says nothing under \"Reached through\" - or, if the assignment was changed "
                + "rather than dropped, it names whatever gateway the settings held when the "
                + "pane was materialised, which is a later instant than the connect.");

    // Positive control 1. The wiring left in a comment: the statement still reads exactly as it
    // should while the pane is handed nothing.
    [Fact]
    public void TheCarrierIsNotFoundWhenTheHandOverIsOnlyLeftInAComment()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                RdpBranch(
                    ManagerLogic(
                        MutateManager(CarrierStatement, "// " + CarrierStatement))),
                CarrierStatement),
            "A commented-out hand-over satisfies this file's reading of the manager, so the "
                + "reading proves nothing about what the pane is given.");

    // Positive control 2. The materialisation snapshot assigned instead, which is the shipped
    // defect written at the new site and which looks entirely reasonable there.
    [Fact]
    public void TheCarrierIsNotFoundWhenTheMaterialisationSnapshotIsAssignedInstead()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                RdpBranch(
                    ManagerLogic(
                        MutateManager(
                            CarrierStatement,
                            "view.GatewayRoute = RdpTrustPromptRoute.Describe("
                            + "runtimeServer.UseDirectConnection, runtimeServer.SshGatewayId, "
                            + "rdpSettings.SshGateways);"))),
                CarrierStatement),
            "A route resolved from the settings the pane is being materialised with satisfies "
                + "this file's reading, so the reading cannot tell the route the tunnel was "
                + "dialled through from one composed after the fact.");

    [Fact]
    public void TheQuestionReadsItsRouteFromThatCarrierAndNotFromThePanesOwnSnapshot()
        => Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                ViewSource.HandlerLogic(OriginMember), RouteStatement),
            "The certificate question no longer takes its route from what the tunnel layer "
                + "settled, as a step of its own body, so \"Reached through\" can name a gateway "
                + "the certificate did not arrive through.");

    // Positive control 3. The pane's own snapshot passed instead - one identifier changed, the
    // statement still a statement, and the whole defect back.
    [Fact]
    public void TheRouteIsNotFoundWhenThePanesOwnSnapshotIsPassedInstead()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                LogicOf(
                    MutateView(
                        RouteStatement,
                        "string? route = RdpTrustPromptRoute.Describe("
                        + "server?.UseDirectConnection ?? false, server?.SshGatewayId, "
                        + "_settings?.SshGateways);"),
                    OriginMember),
                RouteStatement),
            "A route resolved from the pane's materialisation snapshot satisfies this file's "
                + "reading of the view, so the reading cannot tell the two instants apart.");

    // Positive control 4. The resolution left in a comment.
    [Fact]
    public void TheRouteIsNotFoundWhenItsResolutionIsOnlyLeftInAComment()
        => Assert.False(
            ViewSource.IsStatementOfTheMethodBody(
                LogicOf(MutateView(RouteStatement, "// " + RouteStatement), OriginMember),
                RouteStatement),
            "A commented-out resolution satisfies this file's reading of the view.");

    /// <summary>The session manager's own source.</summary>
    private static string ManagerSource() => File.ReadAllText(Path.Combine(
        ViewSource.RepoRoot(), "src", "Heimdall.App", "Services", "EmbeddedSessionManager.cs"));

    /// <summary>The host-control factory of any version of the manager, blanked.</summary>
    private static string ManagerLogic(string source) =>
        ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(source), CreateHostControlMember);

    /// <summary>One member of any version of the view, blanked of comments and literals.</summary>
    private static string LogicOf(string source, string member) =>
        ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(source), member);

    /// <summary>
    /// The block the factory reserves for an RDP session, brace matched, as a body of its own.
    /// </summary>
    /// <remarks>
    /// <see cref="ViewSource.IsStatementOfTheMethodBody"/> reads a body at its own brace depth,
    /// and the RDP branch lives one level deeper than the factory's. Handing it the block asks
    /// the same question there - is this written as a step of what encloses it - rather than
    /// whether the assignment appears somewhere in the factory. The test is written against the
    /// pattern match rather than against the protocol string, because blanking removes the
    /// string and leaves the match.
    /// </remarks>
    private static string RdpBranch(string logic)
    {
        Match branch = Regex.Match(logic, @"session\s+is\s+RdpSessionResult\s+rdp");
        Assert.True(
            branch.Success,
            "The host-control factory no longer recognises an RDP session result at all, so "
                + "nothing below measures the branch it claims to.");

        return BracedBlockAt(logic, branch.Index);
    }

    /// <summary>The brace-matched block that opens at or after <paramref name="from"/>.</summary>
    private static string BracedBlockAt(string logic, int from)
    {
        int open = logic.IndexOf('{', from);
        Assert.True(open >= 0, "The construct carries no block.");

        int depth = 0;
        for (int index = open; index < logic.Length; index++)
        {
            if (logic[index] == '{')
            {
                depth++;
            }
            else if (logic[index] == '}' && --depth == 0)
            {
                return logic[open..(index + 1)];
            }
        }

        throw new InvalidOperationException("The block read from the manager is unbalanced.");
    }

    private static string MutateManager(string original, string replacement)
        => Mutate(ManagerSource(), original, replacement, "the session manager");

    private static string MutateView(string original, string replacement)
        => Mutate(ViewSource.Code(), original, replacement, "the view");

    /// <summary>
    /// Real source with one single-occurrence fragment replaced.
    /// </summary>
    /// <remarks>
    /// The count is asserted because a replacement that matched nothing would leave the source
    /// intact, and a mutant that never landed reports the unmutated code as rejected.
    /// </remarks>
    private static string Mutate(
        string source,
        string original,
        string replacement,
        string what)
    {
        int occurrences = Regex.Matches(source, Regex.Escape(original)).Count;
        Assert.True(
            occurrences == 1,
            $"Expected exactly one '{original}' in {what}, found {occurrences}. The mutants "
                + "built from it would not measure what they claim.");

        string mutated = source.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(source, mutated);
        return mutated;
    }
}
