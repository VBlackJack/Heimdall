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
/// The updater's HttpClient is a process-lifetime singleton, and a singleton HttpClient
/// keeps its pooled sockets - and with them the DNS answer they were opened against - for
/// as long as the process lives. A session left open across a GitHub address change would
/// keep dialling an address that has moved, and the update check would fail for a reason
/// no log explains.
/// </summary>
/// <remarks>
/// <para>
/// These are source-reading assertions, and the reason is worth stating rather than
/// hiding: the registration lives in the container build of a WPF application that cannot
/// be constructed here, and a pooled-connection lifetime has no observable effect inside a
/// test run. A behavioural oracle would be a fiction.
/// </para>
/// <para>
/// Every anchor is a whole statement carried through
/// <see cref="ViewSource.IsStatementOfTheMethodBody"/>, so a statement folded behind a
/// term that is false by construction is not mistaken for one that stands. That is also
/// why the client is built by a named factory rather than by an initialiser inside the
/// container build: it gives the handler and its lifetime a method body to be statements
/// of.
/// </para>
/// </remarks>
public sealed class UpdaterHttpClientWiringTests
{
    private const string ConfigureServicesMember =
        "private void ConfigureServices(IServiceCollection services, string dataRoot)";

    private const string FactoryMember = "private static HttpClient CreateUpdateHttpClient()";

    private const string RegistrationStatement = "services.AddSingleton(_ => CreateUpdateHttpClient());";

    private const string HandlerStatement =
        "var handler = new SocketsHttpHandler { PooledConnectionLifetime = UpdateConnectionLifetime };";

    private const string ClientStatement = "return new HttpClient(handler) { Timeout = UpdateHttpTimeout };";

    [Fact]
    public void TheUpdaterClientIsRegisteredThroughTheFactory()
    {
        string logic = Logic(ConfigureServicesMember);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, RegistrationStatement),
            "the updater HttpClient registration is not a step of the container build");
    }

    [Fact]
    public void TheFactoryBuildsTheClientOnAHandlerThatRecyclesPooledConnections()
    {
        string logic = Logic(FactoryMember);

        // One call per constant, not a loop: the assertion guard associates the reading
        // with the predicate by the anchor it was given, and a loop variable is not that.
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, HandlerStatement),
            "the pooled-connection lifetime is not a step of the updater client factory");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, ClientStatement),
            "the client is not built on that handler");
    }

    [Fact]
    public void NoPlainHttpClientRegistrationSurvives()
    {
        string source = ReadAppSource("App.xaml.cs");

        // The mutant this catches is a reversion rather than a deletion: an edit that
        // registers the bare client again, beside the factory instead of instead of it.
        // The two readings above would still pass with that line back in the file.
        Assert.DoesNotMatch(
            new Regex(@"new\s+HttpClient\s*\{", RegexOptions.None, TimeSpan.FromSeconds(5)),
            source);
    }

    private static string Logic(string signature)
        => ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(ReadAppSource("App.xaml.cs")), signature);

    private static string ReadAppSource(string relativePath)
    {
        string full = Path.Combine(
            ViewSource.RepoRoot(),
            "src",
            "Heimdall.App",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }
}
