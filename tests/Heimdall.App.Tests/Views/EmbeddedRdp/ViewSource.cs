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

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Reads the RDP view's own source and markup.
/// </summary>
/// <remarks>
/// A handler body is read here only to assert that a decision is actually consulted at the site
/// that owns it. The decision itself is always asserted behaviourally, against the extracted
/// function or against a real WPF element, never against the text.
/// </remarks>
internal static class ViewSource
{
    private static readonly XNamespace s_xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    internal static string Code() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "Heimdall.App", "Views", "EmbeddedRdpView.xaml.cs"));

    internal static string MarkupPath() => Path.Combine(
        RepoRoot(), "src", "Heimdall.App", "Views", "EmbeddedRdpView.xaml");

    internal static XDocument Markup() => XDocument.Load(MarkupPath());

    /// <summary>Finds the markup element carrying <paramref name="name"/> as its x:Name.</summary>
    internal static XElement NamedElement(string name)
    {
        XElement? element = Markup()
            .Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(s_xaml + "Name") == name);

        Assert.True(element is not null, $"No element named '{name}' in EmbeddedRdpView.xaml.");
        return element!;
    }

    /// <summary>The value of an attached automation property, or null when it is not declared.</summary>
    internal static string? AutomationAttribute(XElement element, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.Attribute("AutomationProperties." + propertyName);
    }

    /// <summary>The local (prefix-free) markup tag name of an element.</summary>
    internal static string TagName(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.Name.LocalName;
    }

    /// <summary>
    /// The text of one method, from its signature to the next member declaration at class scope.
    /// </summary>
    internal static string HandlerBody(string signature)
    {
        string source = Code();
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"handler not found in the view: {signature}");

        Match next = Regex.Match(
            source[(start + signature.Length)..],
            @"(?m)^    (private|public|internal|protected)\s");

        return next.Success
            ? source.Substring(start, signature.Length + next.Index)
            : source[start..];
    }

    internal static string RepoRoot()
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
            $"Cannot find repository root from test binary directory: {AppContext.BaseDirectory}");
    }
}
