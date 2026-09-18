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
/// Guards the vendored draw.io bundle and the page that hosts it: the version
/// the manifests declare, the embed parameters that keep the editor offline,
/// the content policy that keeps its requests local, and the separation of
/// autosave from an explicit save.
/// </summary>
/// <remarks>
/// <para>The bundle shipped 24.8.6 for two years while every manifest claimed
/// 26.0.9, so the version is read from the bundle and the manifests are checked
/// against it rather than against a number written here. Upstream's own content
/// policy allows any image host and the diagrams.net endpoints, which a crafted
/// diagram can use as a beacon; Heimdall replaces it, and re-vendoring without
/// re-applying that edit is what this notices.</para>
/// <para>These read web assets, not C#, so the statement predicate that the
/// C#-facing guards use does not apply. What they can settle is that the text
/// is written; behaviour is settled by the smoke test named in VENDORED.md.
/// The view's own decisions live in <c>DiagramEditorGuardTests</c>.</para>
/// </remarks>
public sealed class DrawioAssetGuardTests
{
    private static string AssetPath(params string[] parts)
    {
        string full = Path.Combine(
            RepositoryRoot(),
            Path.Combine("src", "Heimdall.App", "Assets", "drawio"),
            Path.Combine(parts));

        Assert.True(File.Exists(full), $"Expected draw.io asset not found: {full}");
        return full;
    }

    private static string ReadAsset(params string[] parts) => File.ReadAllText(AssetPath(parts));

    private static string ReadRepositoryFile(string relativePath)
    {
        string full = Path.Combine(
            RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(full), $"Expected file not found: {full}");
        return File.ReadAllText(full);
    }

    private static string RepositoryRoot()
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

        throw new DirectoryNotFoundException(
            "Cannot find repository root containing Heimdall.slnx from test binary directory: "
                + AppContext.BaseDirectory);
    }

    /// <summary>The version every manifest states must be the one the bundle reports.</summary>
    [Fact]
    public void VendoredVersion_IsTheOneTheBundleReports()
    {
        var shipped = Regex.Match(
            ReadAsset("js", "app.min.js"),
            @"EditorUi\.VERSION\s*=\s*""(?<version>[0-9][0-9.]*)""",
            RegexOptions.CultureInvariant);

        Assert.True(shipped.Success,
            "Could not read EditorUi.VERSION from the vendored draw.io bundle.");

        string version = shipped.Groups["version"].Value;

        Assert.True(
            ReadAsset("VENDORED.md").Contains(version, StringComparison.Ordinal),
            $"VENDORED.md does not mention the shipped draw.io version {version}.");

        foreach (string notices in new[] { "THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES.fr.md" })
        {
            var row = Regex.Match(
                ReadRepositoryFile(notices),
                @"^\|\s*draw\.io embed\s*\|\s*(?<version>[^|\s]+)\s*\|",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);

            Assert.True(row.Success, $"{notices} has no draw.io embed row.");
            Assert.Equal(version, row.Groups["version"].Value);
        }
    }

    /// <summary>
    /// draw.io honours offline and stealth. Without them it reaches for
    /// app.diagrams.net, and the tool's own help promises an offline editor.
    /// </summary>
    [Fact]
    public void HostPage_LoadsTheEditorOffline()
    {
        var editorParams = Regex.Match(
            ReadAsset("heimdall-host.html"),
            @"var editorParams = new URLSearchParams\((?<body>.*?)\);",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        Assert.True(editorParams.Success,
            "heimdall-host.html no longer builds the editor URL through editorParams.");

        string body = editorParams.Groups["body"].Value;
        var missing = new List<string>();

        foreach (string required in new[] { "offline", "stealth" })
        {
            if (!Regex.IsMatch(body, $@"\b{required}:\s*'1'", RegexOptions.CultureInvariant))
            {
                missing.Add(required);
            }
        }

        Assert.True(missing.Count == 0,
            "The embedded editor URL no longer sets " + string.Join(" and ", missing)
                + "=1, so draw.io may call out.");
    }

    /// <summary>
    /// Heimdall's content policy on the vendored page, which a re-vendoring
    /// without the edit would silently replace with upstream's permissive one.
    /// </summary>
    [Fact]
    public void VendoredIndexPage_KeepsEveryRequestLocal()
    {
        var policy = Regex.Match(
            ReadAsset("index.html"),
            @"http-equiv=""Content-Security-Policy""\s+content=""(?<policy>[^""]+)""",
            RegexOptions.CultureInvariant);

        Assert.True(policy.Success, "index.html no longer declares a Content-Security-Policy.");

        string value = policy.Groups["policy"].Value;

        Assert.True(value.Contains("default-src 'self'", StringComparison.Ordinal),
            "The vendored Content-Security-Policy no longer defaults to the local origin.");

        var allowed = new List<string>();
        foreach (string forbidden in new[] { "img-src *", "media-src *", "font-src *", "diagrams.net", "draw.io", "googleapis" })
        {
            if (value.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            {
                allowed.Add(forbidden);
            }
        }

        Assert.True(allowed.Count == 0,
            "The vendored Content-Security-Policy still allows: " + string.Join(", ", allowed));
    }

    /// <summary>
    /// The editor reports both an autosave and an explicit save. Handling them
    /// with one branch is what made Ctrl+S write nothing.
    /// </summary>
    [Fact]
    public void HostPage_SeparatesAutosaveFromAnExplicitSave()
    {
        string host = ReadAsset("heimdall-host.html");

        var autosave = Regex.Match(host, @"case 'autosave':(?<body>.*?)break;",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        var save = Regex.Match(host, @"case 'save':(?<body>.*?)break;",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        Assert.True(autosave.Success, "heimdall-host.html no longer handles the autosave event.");
        Assert.True(save.Success, "heimdall-host.html no longer handles the save event.");

        string autosaveBody = autosave.Groups["body"].Value;
        string saveBody = save.Groups["body"].Value;

        // A shared branch shows up as one label falling through to the other,
        // which leaves the first label with an empty body.
        Assert.False(string.IsNullOrWhiteSpace(autosaveBody),
            "The autosave case falls through to another case instead of having a body of its own, "
                + "which is how an explicit save became indistinguishable from an autosave.");

        Assert.True(autosaveBody.Contains("'save:'", StringComparison.Ordinal),
            "Autosave no longer reports the document to the host.");
        Assert.True(saveBody.Contains("'save-request:'", StringComparison.Ordinal),
            "An explicit save no longer asks the host to write to disk.");
        Assert.False(autosaveBody.Contains("'save-request:'", StringComparison.Ordinal),
            "Autosave asks for a write on every change.");
    }

    /// <summary>
    /// The context menu must describe the cell the user aimed at, and must be the
    /// only menu that opens.
    /// </summary>
    /// <remarks>
    /// Found by a manual pass, because nothing else could find it. A right click
    /// leaves draw.io's selection untouched, and this menu is built from the DOM
    /// contextmenu event, which arrives first. Reading the selection therefore
    /// built the menu against whatever was selected earlier, usually nothing, so
    /// the session entry was permanently disabled. draw.io also opened its own
    /// menu over this one and swallowed the click. Both are behaviour no source
    /// guard can prove; what is pinned here is that the two lines addressing them
    /// are still written.
    /// </remarks>
    [Fact]
    public void HostPage_DrivesItsContextMenuFromThePointer()
    {
        string host = ReadAsset("heimdall-host.html");

        var handler = Regex.Match(host,
            @"function onFrameContextMenu\(evt\)\s*\{(?<body>.*?)\n        \}",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        Assert.True(handler.Success, "heimdall-host.html no longer handles the frame's context menu.");
        Assert.True(
            handler.Groups["body"].Value.Contains("selectCellUnderPointer(", StringComparison.Ordinal),
            "The context menu is built without selecting the cell under the pointer, so its "
                + "entries describe an earlier selection and the session entry stays disabled.");

        Assert.True(
            host.Contains("popupMenuHandler.setEnabled(false)", StringComparison.Ordinal),
            "draw.io's own context menu is left enabled and opens over Heimdall's, swallowing "
                + "the click meant for it.");
    }

    /// <summary>
    /// draw.io's actions must reach the menu translated, not as their keys.
    /// </summary>
    /// <remarks>
    /// An action's <c>label</c> is its resource key, not its text: 'undo' carries
    /// "undo" and 'insertEdge' carries "line". Returning it put the key on screen,
    /// so this menu read "undo / redo / cut" beside a draw.io menu reading
    /// "Annuler / Refaire / Couper". The key must go through mxResources.
    /// </remarks>
    [Fact]
    public void HostPage_TranslatesTheEditorsOwnMenuEntries()
    {
        string host = ReadAsset("heimdall-host.html");

        var resolver = Regex.Match(host,
            @"function getDrawioLabel\([^)]*\)\s*\{(?<body>.*?)\n        \}",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        Assert.True(resolver.Success, "heimdall-host.html no longer resolves draw.io's labels.");

        string body = resolver.Groups["body"].Value;

        Assert.True(
            body.Contains("mxResources.get(key)", StringComparison.Ordinal),
            "The label is not looked up through mxResources, so the menu shows resource keys.");

        // The defect: handing back action.label as if it were the text.
        Assert.False(
            Regex.IsMatch(body, @"return\s+action\.label\s*;", RegexOptions.CultureInvariant),
            "An action's label is its resource key, not its text; returning it puts "
                + "\"undo\" and \"cut\" on screen instead of the translated entries.");
    }

    /// <summary>
    /// Every action the context menu offers must have a key the shipped
    /// catalogues can translate. Re-vendoring must preserve those catalogues.
    /// </summary>
    [Theory]
    [InlineData("dia.txt")]
    [InlineData("dia_fr.txt")]
    [InlineData("dia_es.txt")]
    public void HostPage_ContextMenuActionsAreTranslatableInShippedLanguages(string catalogue)
    {
        string host = ReadAsset("heimdall-host.html");

        var items = Regex.Match(host,
            @"var contextMenuItems = \[(?<body>.*?)\n        \];",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        Assert.True(items.Success, "heimdall-host.html no longer declares its context menu items.");

        // The resource key an entry resolves to: its explicit labelKey when it has
        // one, and otherwise the action name, which is what draw.io's own label
        // carries for all but insertEdge.
        var keys = new List<string>();
        foreach (Match entry in Regex.Matches(items.Groups["body"].Value,
            @"\{\s*action:\s*'(?<action>[^']+)'(?<rest>[^}]*)\}", RegexOptions.CultureInvariant))
        {
            var explicitKey = Regex.Match(entry.Groups["rest"].Value, @"labelKey:\s*'(?<key>[^']+)'");
            keys.Add(explicitKey.Success ? explicitKey.Groups["key"].Value : entry.Groups["action"].Value);
        }

        Assert.NotEmpty(keys);

        // insertEdge is the one action whose resource key is not its name; draw.io
        // carries "line" on it, and that is what the host resolves.
        keys = keys.Select(key => key == "insertEdge" ? "line" : key).ToList();

        string text = ReadAsset("resources", catalogue);
        var missing = keys
            .Where(key => !Regex.IsMatch(text, $@"^{Regex.Escape(key)}=", RegexOptions.Multiline))
            .ToList();

        Assert.True(missing.Count == 0,
            $"{catalogue} cannot translate: {string.Join(", ", missing)}. "
                + "Those entries would show their resource key.");
        Assert.Contains($"'resources/{catalogue}'", ReadRepositoryFile("scripts/Vendor-DrawIo.ps1"));
    }

    /// <summary>
    /// The iframe host reaches the editor through a global the bundle does not
    /// define on its own; losing that edit disables the whole WPF toolbar.
    /// </summary>
    [Fact]
    public void VendoredBootstrap_ExposesTheEditorToTheHostPage()
    {
        string bootstrap = ReadAsset("js", "bootstrap.js");

        Assert.True(
            bootstrap.Contains("window.heimdallDrawioApp = app;", StringComparison.Ordinal),
            "js/bootstrap.js no longer exposes the editor instance, so the host page cannot "
                + "drive undo, redo, zoom or the context menu.");

        Assert.True(
            ReadAsset("heimdall-host.html").Contains("heimdallDrawioApp", StringComparison.Ordinal),
            "The host page no longer reads the editor instance the bundle exposes.");
    }
}
