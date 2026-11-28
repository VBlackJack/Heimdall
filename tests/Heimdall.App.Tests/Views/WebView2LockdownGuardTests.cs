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
using System.Xml.Linq;
using Heimdall.App.Services;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests.Views;

/// <summary>
/// Freezes the shared WebView2 lockdown, the VNC page policy and the VNC and web console
/// teardown and start-up guards.
/// </summary>
/// <remarks>
/// <para>Every host that starts a browser core is found by scanning the sources for the call, so a
/// sixth host is caught the day it is written. The lockdown call must be a step of the start-up
/// try block, below the call that creates the core.</para>
/// <para>The measures that read source go through <see cref="ViewSource"/>, so a call left in a
/// comment or folded behind a branch does not satisfy them.</para>
/// </remarks>
public sealed class WebView2LockdownGuardTests
{
    private const string CoreCall = "EnsureCoreWebView2Async(";
    private const string LockdownStatement = "_lockdown = WebView2Lockdown.Apply(";
    private const string LockdownStatementQualified = "_lockdown = Services.WebView2Lockdown.Apply(";
    private const string DisposeStatement = "_lockdown?.Dispose();";
    private const string DisposeSignature = "public void Dispose()";

    private const string VncView = "EmbeddedVncView.xaml.cs";
    private const string WebConsoleView = "EmbeddedWebView.xaml.cs";
    private const string DisposedGuard = "if (_disposed) return;";
    private const string DisposedOrHiddenGuard = "if (_disposed || ReconnectOverlay.Visibility != Visibility.Visible)";
    private const string VncStartUp = "private async Task InitializeWebViewAsync()";
    private const int KnownVncAwaits = 3;

    public static TheoryData<string> HostFiles()
    {
        var data = new TheoryData<string>();
        foreach (string file in FindHosts())
        {
            data.Add(Path.GetRelativePath(AppRoot(), file));
        }

        return data;
    }

    [Fact]
    public void EveryKnownHost_IsFoundByTheScan()
    {
        string[] found = FindHosts()
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expected = new[]
        {
            "AsciinemaPlayerDialog.xaml.cs",
            "EmbeddedVncView.xaml.cs", "EmbeddedSshView.xaml.cs", "EmbeddedWebView.xaml.cs",
            "DiagramEditorView.xaml.cs", "MilkdownEditorControl.xaml.cs",
        }.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, found);
    }

    [Theory]
    [MemberData(nameof(HostFiles))]
    public void EveryHost_AppliesTheLockdownRightAfterItsCoreExists(string relativePath)
    {
        string logic = ViewSource.WithoutCommentsAndLiterals(
            File.ReadAllText(Path.Combine(AppRoot(), relativePath)));

        Assert.True(
            LockdownIsAStepOfTheStartUp(logic),
            $"{relativePath} does not call WebView2Lockdown.Apply as a step of the method that creates its browser core.");
    }

    [Theory]
    [MemberData(nameof(HostFiles))]
    public void EveryHost_DisposesTheLockdownWithItself(string relativePath)
    {
        string logic = ViewSource.WithoutCommentsAndLiterals(
            File.ReadAllText(Path.Combine(AppRoot(), relativePath)));
        string dispose = ViewSource.HandlerBody(logic, DisposeSignature);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(dispose, DisposeStatement),
            $"{relativePath} does not dispose its lockdown subscription in Dispose.");
    }

    /// <summary>The mutation: a host without the call must be refused by the measure.</summary>
    [Theory]
    [MemberData(nameof(HostFiles))]
    public void AHostWithoutTheLockdownCall_IsRefused(string relativePath)
    {
        string source = File.ReadAllText(Path.Combine(AppRoot(), relativePath));
        string mutated = source
            .Replace(LockdownStatementQualified, "var unused = Services.WebView2Lockdown.Apply(", StringComparison.Ordinal)
            .Replace(LockdownStatement, "var unused = WebView2Lockdown.Apply(", StringComparison.Ordinal);

        Assert.NotEqual(source, mutated);
        Assert.False(LockdownIsAStepOfTheStartUp(ViewSource.WithoutCommentsAndLiterals(mutated)));
    }

    [Theory]
    [MemberData(nameof(HostFiles))]
    public void AHostThatDoesNotDisposeThePreviousHandleFirst_IsRefused(string relativePath)
    {
        string source = File.ReadAllText(Path.Combine(AppRoot(), relativePath));
        string mutated = source.Replace("            " + DisposeStatement + "\r\n            _lockdown = ", "            _lockdown = ", StringComparison.Ordinal);
        mutated = mutated.Replace("            " + DisposeStatement + "\n            _lockdown = ", "            _lockdown = ", StringComparison.Ordinal);

        Assert.NotEqual(source, mutated);
        Assert.False(LockdownIsAStepOfTheStartUp(ViewSource.WithoutCommentsAndLiterals(mutated)));
    }

    [Fact]
    public void AHostWithTheLockdownInsideABranch_IsRefused()
    {
        string source = File.ReadAllText(Path.Combine(AppRoot(), "Views", VncView));
        string mutated = source.Replace(
            LockdownStatementQualified,
            "if (core is null) _lockdown = Services.WebView2Lockdown.Apply(",
            StringComparison.Ordinal);

        Assert.NotEqual(source, mutated);
        Assert.False(LockdownIsAStepOfTheStartUp(ViewSource.WithoutCommentsAndLiterals(mutated)));
    }

    [Fact]
    public void TheVncPage_DeclaresAPolicyThatKeepsEveryRequestOnTheLoopback()
    {
        string? policy = FindPolicy(ReadVncPage());

        Assert.NotNull(policy);
        Dictionary<string, string[]> directives = ParseDirectives(policy);

        Assert.Equal(new[] { "'none'" }, directives["default-src"]);
        Assert.Equal(new[] { "ws://127.0.0.1:*" }, directives["connect-src"]);
        Assert.Equal(new[] { "'none'" }, directives["object-src"]);
        Assert.Equal(new[] { "'none'" }, directives["base-uri"]);
        Assert.Equal(new[] { "'none'" }, directives["form-action"]);
        Assert.Equal(new[] { "'none'" }, directives["frame-src"]);

        foreach ((string name, string[] sources) in directives)
        {
            Assert.DoesNotContain("*", sources);
            Assert.DoesNotContain("http:", sources);
            Assert.DoesNotContain("https:", sources);
            Assert.DoesNotContain("ws:", sources);
            Assert.DoesNotContain("wss:", sources);
            Assert.True(
                sources.All(s => !s.StartsWith("http", StringComparison.OrdinalIgnoreCase)),
                $"{name} names a remote origin.");
        }
    }

    [Fact]
    public void TheVncPage_LoadsNothingFromARemoteAddress()
    {
        string withoutComments = Regex.Replace(ReadVncPage(), "<!--.*?-->", string.Empty, RegexOptions.Singleline);
        Assert.DoesNotMatch(@"https?://", withoutComments);
    }

    /// <summary>The mutation: a page without the meta must be refused by the measure.</summary>
    [Fact]
    public void ThePageWithoutTheMeta_HasNoPolicy()
    {
        string mutated = Regex.Replace(
            ReadVncPage(),
            @"<meta http-equiv=""Content-Security-Policy""[^>]*>",
            string.Empty,
            RegexOptions.CultureInvariant);

        Assert.NotEqual(ReadVncPage(), mutated);
        Assert.Null(FindPolicy(mutated));
    }

    [Theory]
    [InlineData("var env = await Services.WebView2Helper.CreateEnvironmentAsync(")]
    [InlineData("await VncWebView.EnsureCoreWebView2Async(")]
    [InlineData("await core.AddScriptToExecuteOnDocumentCreatedAsync(")]
    public void TheVncStartUp_ChecksDisposalRightAfterEachAwait(string awaitStatement)
    {
        string scope = TryScopeOf(ViewSource.HandlerBody(VncLogic(), VncStartUp));

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(scope, awaitStatement),
            "The VNC start-up no longer awaits this step as a statement of its try block.");

        string fromStatement = ViewSource.HandlerBody(scope, awaitStatement);
        string afterStatement = fromStatement[(fromStatement.Split(';', 2)[0].Length + 1)..].TrimStart();

        Assert.True(
            afterStatement.StartsWith(DisposedGuard, StringComparison.Ordinal),
            "The VNC start-up does not check _disposed right after this await.");
    }

    [Fact]
    public void TheVncStartUp_AwaitsOnlyTheStepsThatAreGuarded()
    {
        string scope = TryScopeOf(ViewSource.HandlerBody(VncLogic(), VncStartUp));

        Assert.Equal(KnownVncAwaits, scope.Split("await ").Length - 1);
    }

    [Fact]
    public void TheVncStartUpCatch_ChecksDisposalBeforeAnythingElse()
    {
        string method = ViewSource.HandlerBody(VncLogic(), VncStartUp);
        string handler = ViewSource.HandlerBody(method, "catch (Exception ex)");
        string body = handler.Split('{', 2)[1].TrimStart();

        Assert.True(
            body.StartsWith(DisposedGuard, StringComparison.Ordinal),
            "The catch of the VNC start-up does not check _disposed as its first statement.");
    }

    [Theory]
    [InlineData("private void EndAttempt(string text, bool offerEntry)")]
    [InlineData("private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)")]
    public void TheVncEntryPoints_LeaveFirstWhenDisposed(string signature)
    {
        string method = ViewSource.HandlerBody(VncLogic(), signature);
        string body = method.Split('{', 2)[1].TrimStart();

        Assert.True(
            body.StartsWith(DisposedGuard, StringComparison.Ordinal),
            $"{signature} does not check _disposed as its first statement.");
    }

    [Fact]
    public void TheVncReconnectOverlay_IsFocusableAndTakesTheFocusItself()
    {
        XDocument markup = XDocument.Load(Path.Combine(AppRoot(), "Views", "EmbeddedVncView.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement overlay = markup.Descendants()
            .Single(e => (string?)e.Attribute(xaml + "Name") == "ReconnectOverlay");

        Assert.Equal("True", (string?)overlay.Attribute("Focusable"));

        string method = ViewSource.HandlerBody(VncLogic(), "public void ShowReconnectOverlay(string? message = null)");
        string fromDeferral = ViewSource.HandlerBody(method, "Dispatcher.BeginInvoke(");
        string lambda = BlockAfter(fromDeferral, "() =>");
        string afterLambda = fromDeferral.Split(lambda, 2)[1];

        // The overlay itself takes the focus, as a step of the deferred lambda, below the guard.
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(lambda, DisposedOrHiddenGuard),
            "The deferred focus does not check disposal and visibility first.");

        // The focus is a step that follows the guard, and the guard's block leaves the lambda.
        string fromGuard = ViewSource.HandlerBody(lambda, DisposedOrHiddenGuard);
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody("{" + fromGuard, "ReconnectOverlay.Focus();"),
            "The reconnect overlay does not take the focus below the guard of its deferred lambda.");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(BlockAfter(fromGuard, DisposedOrHiddenGuard), "return;"),
            "The guard of the deferred focus does not return.");
        Assert.StartsWith(
            "System.Windows.Threading.DispatcherPriority.Input);",
            afterLambda.TrimStart(',', ' ', '\r', '\n'),
            StringComparison.Ordinal);

        // A button never takes it: a Space typed as the session drops would reconnect.
        Assert.DoesNotMatch(@"ReconnectButton\.Focus\(", method);
    }

    [Fact]
    public void TheWebConsole_StartsInitialisationOnlyWhileNoneIsInFlight()
    {
        string logic = ViewSource.WithoutCommentsAndLiterals(
            File.ReadAllText(Path.Combine(AppRoot(), "Views", WebConsoleView)));
        string start = ViewSource.HandlerBody(logic, "private Task StartInitializationAsync()");
        const string Guard = "if (_initTask is { IsCompleted: false })";

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(start, Guard),
            "The web console starts initialisation without looking at an attempt still in flight.");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                "{" + ViewSource.HandlerBody(start, Guard),
                "_initTask = InitializeWebViewAsync();"),
            "The web console does not start initialisation below the in-flight check.");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                BlockAfter(ViewSource.HandlerBody(start, Guard), Guard),
                "return _initTask;"),
            "The in-flight check does not hand back the running attempt.");

        // The only caller of the real initialisation is the guarded starter (plus its declaration).
        Assert.Equal(2, logic.Split("InitializeWebViewAsync(").Length - 1);

        // A flag that is never reset would make Reload useless after a failed start.
        Assert.DoesNotMatch(@"_webViewInitStarted|_initialized\s*=\s*true", logic);
    }

    [Fact]
    public void TheWebConsole_SubscribesTheCertificateRemovalOnce()
    {
        string logic = ViewSource.WithoutCommentsAndLiterals(
            File.ReadAllText(Path.Combine(AppRoot(), "Views", WebConsoleView)));
        string scope = TryScopeOf(ViewSource.HandlerBody(logic, "private async Task InitializeWebViewAsync()"));
        const string Condition = "if (CertificateTrust is not null && !_certificateRemovedSubscribed)";

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(scope, Condition),
            "The certificate removal subscription is not conditional on a flag.");

        string block = BlockAfter(ViewSource.HandlerBody(scope, Condition), Condition);
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(block, "CertificateTrust.CertificateRemoved += OnCertificateRemoved;"),
            "The guarded block no longer subscribes.");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(block, "_certificateRemovedSubscribed = true;"),
            "The guarded block does not record the subscription.");
    }

    /// <summary>The first brace-delimited block after <paramref name="needle"/>, braces included.</summary>
    private static string BlockAfter(string text, string needle)
    {
        int start = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(start >= 0, $"not found: {needle}");

        int open = start + needle.Length;
        while (open < text.Length && text[open] != '{')
        {
            open++;
        }

        int depth = 0;
        for (int index = open; index < text.Length; index++)
        {
            if (text[index] == '{')
            {
                depth++;
            }
            else if (text[index] == '}' && --depth == 0)
            {
                return text[open..(index + 1)];
            }
        }

        throw new InvalidOperationException($"unbalanced block after: {needle}");
    }

    /// <summary>The try block that opens a method, up to its catch, as a body of its own.</summary>
    private static string TryScopeOf(string method)
    {
        Match tryKeyword = Regex.Match(method, @"\btry\b");
        Assert.True(tryKeyword.Success, "The method holds no try block.");
        return BlockAfter(method[tryKeyword.Index..], "try");
    }

    private static bool LockdownIsAStepOfTheStartUp(string logic)
    {
        int core = logic.IndexOf(CoreCall, StringComparison.Ordinal);
        Assert.True(core >= 0, "The source holds no call that creates a browser core.");

        // The member that holds the call: the last member declaration at class scope above it.
        MatchCollection members = Regex.Matches(
            logic[..core],
            @"(?m)^    (?:private|public|internal|protected)[^\r\n]*");
        Assert.True(members.Count > 0, "No enclosing member found for the core call.");
        string method = ViewSource.HandlerBody(logic, members[^1].Value.Trim());

        // Narrow to the try block that holds the call, so the statement is read at its own depth.
        int tryStart = -1;
        foreach (Match m in Regex.Matches(method, @"\btry\b"))
        {
            if (m.Index < method.IndexOf(CoreCall, StringComparison.Ordinal))
            {
                tryStart = m.Index;
            }
        }

        string scope = method;
        if (tryStart >= 0)
        {
            int catchStart = method.IndexOf("catch", tryStart, StringComparison.Ordinal);
            scope = catchStart > tryStart ? method[tryStart..catchStart] : method[tryStart..];
        }

        if (!ViewSource.IsStatementOfTheMethodBody(scope, DisposeStatement))
        {
            return false;
        }

        bool plain = ViewSource.IsStatementOfTheMethodBody(scope, LockdownStatement);
        bool qualified = ViewSource.IsStatementOfTheMethodBody(scope, LockdownStatementQualified);
        if (!plain && !qualified)
        {
            return false;
        }

        int apply = Math.Max(
            scope.IndexOf(LockdownStatement, StringComparison.Ordinal),
            scope.IndexOf(LockdownStatementQualified, StringComparison.Ordinal));
        // The previous handle goes first, so a second start never leaves the first one subscribed.
        int dispose = scope.LastIndexOf(DisposeStatement, apply, StringComparison.Ordinal);
        return apply > scope.IndexOf(CoreCall, StringComparison.Ordinal)
            && dispose >= 0
            && scope[(dispose + DisposeStatement.Length)..apply].Trim().Length == 0;
    }

    private static List<string> FindHosts()
    {
        string root = AppRoot();
        var hosts = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            if (ViewSource.WithoutCommentsAndLiterals(File.ReadAllText(file))
                .Contains(CoreCall, StringComparison.Ordinal))
            {
                hosts.Add(file);
            }
        }

        return hosts;
    }

    private static string AppRoot() => Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App");

    private static string VncLogic() => ViewSource.WithoutCommentsAndLiterals(
        File.ReadAllText(Path.Combine(AppRoot(), "Views", VncView)));

    private static string ReadVncPage() => File.ReadAllText(Path.Combine(AppRoot(), "Assets", "vnc.html"));

    private static string? FindPolicy(string html)
    {
        Match match = Regex.Match(
            html,
            @"http-equiv=""Content-Security-Policy""\s+content=""(?<policy>[^""]+)""",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["policy"].Value : null;
    }

    private static Dictionary<string, string[]> ParseDirectives(string policy)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (string part in policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            result[tokens[0]] = tokens[1..];
        }

        return result;
    }
}

/// <summary>The parts of the lockdown that need no browser runtime.</summary>
public sealed class WebView2LockdownTests
{
    [Fact]
    public void OnlyAMissingFeature_CountsAsAnOlderRuntime()
    {
        Assert.True(WebView2Lockdown.IsRuntimeTooOld(new NotImplementedException()));
        Assert.True(WebView2Lockdown.IsRuntimeTooOld(new InvalidCastException()));
        Assert.False(WebView2Lockdown.IsRuntimeTooOld(new InvalidOperationException()));
        Assert.False(WebView2Lockdown.IsRuntimeTooOld(new UnauthorizedAccessException()));
    }

    [Fact]
    public void TheSubscriptionHandle_UnsubscribesOnceWhateverTheNumberOfDisposals()
    {
        int calls = 0;
        var handle = new WebView2Lockdown.Subscription(new List<Action> { () => calls++, () => calls++ });

        handle.Dispose();
        handle.Dispose();

        Assert.Equal(2, calls);
    }

    [Fact]
    public void TheSubscriptionHandle_KeepsGoingWhenTheBrowserIsAlreadyGone()
    {
        int calls = 0;
        var handle = new WebView2Lockdown.Subscription(new List<Action>
        {
            () => throw new InvalidOperationException(),
            () => calls++,
        });

        handle.Dispose();

        Assert.Equal(1, calls);
    }
}
