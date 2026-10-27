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

using System.Text;
using FluentAssertions;
using Heimdall.App.Services.HostsFile;

namespace Heimdall.App.Tests.Services.HostsFile;

/// <summary>
/// The hosts file model: every line survives, an edit touches only its own line, and the
/// encoding and line endings come back as they were read.
/// </summary>
public sealed class HostsFileDocumentTests
{
    // The shape of a stock Windows hosts file, plus the things the old loader dropped:
    // a comment between entries, a blank line between entries, a line that does not parse,
    // aliases, odd spacing, and no final newline.
    private const string Sample =
        "# Copyright (c) 1993-2009 Microsoft Corp.\r\n" +
        "#\r\n" +
        "#      102.54.94.97     rhino.acme.com          # source server\r\n" +
        "\r\n" +
        "127.0.0.1   localhost   # loopback\r\n" +
        "# --- lab machines below ---\r\n" +
        "\r\n" +
        "10.0.0.5\tlab1 lab1.local\r\n" +
        "not an entry at all\r\n" +
        "fe80::1  link-local\r\n" +
        "  10.0.0.6    lab2";

    [Fact]
    public void Loaded_And_Not_Edited_Is_Written_Back_Byte_For_Byte()
    {
        var bytes = Encoding.UTF8.GetBytes(Sample);

        var document = HostsFileDocument.Parse(bytes);

        document.ToBytes().Should().Equal(bytes);
        document.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Parse_Reads_Enabled_Disabled_And_Alias_Entries()
    {
        var document = HostsFileDocument.Parse(Encoding.UTF8.GetBytes(Sample));

        document.Entries.Select(e => (e.Enabled, e.IpAddress, e.Hostname, e.Comment)).Should().Equal(
            (false, "102.54.94.97", "rhino.acme.com", "source server"),
            (true, "127.0.0.1", "localhost", "loopback"),
            (true, "10.0.0.5", "lab1 lab1.local", ""),
            (true, "fe80::1", "link-local", ""),
            (true, "10.0.0.6", "lab2", ""));
    }

    [Fact]
    public void Editing_An_Entry_Rewrites_Only_That_Line_And_Keeps_Interior_Comments()
    {
        var document = HostsFileDocument.Parse(Encoding.UTF8.GetBytes(Sample));

        document.Entries.Single(e => e.Hostname == "localhost").IpAddress = "127.0.0.2";

        document.ToText().Should().Be(Sample.Replace(
            "127.0.0.1   localhost   # loopback",
            "127.0.0.2\tlocalhost\t# loopback"));
    }

    [Fact]
    public void Deleting_An_Entry_Removes_Its_Line_Only()
    {
        var document = HostsFileDocument.Parse(Encoding.UTF8.GetBytes(Sample));

        document.RemoveEntries([document.Entries.Single(e => e.IpAddress == "10.0.0.5")]);

        document.ToText().Should().Be(Sample.Replace("10.0.0.5\tlab1 lab1.local\r\n", string.Empty));
        document.Entries.Should().NotContain(e => e.IpAddress == "10.0.0.5");
    }

    [Fact]
    public void Added_Entry_Is_Empty_And_Appended_At_The_End_With_The_File_Line_Ending()
    {
        var document = HostsFileDocument.Parse(Encoding.UTF8.GetBytes(Sample));

        var entry = document.AddEntry();
        entry.IpAddress.Should().BeEmpty();
        entry.Hostname.Should().BeEmpty();
        entry.Enabled.Should().BeTrue();
        entry.IpAddress = "192.168.1.10";
        entry.Hostname = "nas";

        document.ToText().Should().Be(Sample + "\r\n192.168.1.10\tnas\r\n");
    }

    [Fact]
    public void Lf_Only_File_Keeps_Lf_For_New_Lines()
    {
        const string text = "# header\n127.0.0.1 localhost\n";
        var document = HostsFileDocument.Parse(Encoding.UTF8.GetBytes(text));

        var entry = document.AddEntry();
        entry.IpAddress = "10.1.1.1";
        entry.Hostname = "box";

        document.ToText().Should().Be(text + "10.1.1.1\tbox\n");
    }

    [Fact]
    public void File_Without_Bom_Is_Written_Without_Bom_After_An_Edit()
    {
        var document = HostsFileDocument.Parse(Encoding.UTF8.GetBytes(Sample));

        document.Entries[0].Comment = "edited";

        document.ToBytes().Take(3).Should().NotEqual(new UTF8Encoding(true).GetPreamble());
    }

    [Fact]
    public void File_With_Bom_Keeps_Its_Bom()
    {
        var bom = new UTF8Encoding(true).GetPreamble();
        var bytes = bom.Concat(Encoding.UTF8.GetBytes(Sample)).ToArray();

        var document = HostsFileDocument.Parse(bytes);
        document.ToBytes().Should().Equal(bytes);

        document.Entries[0].Comment = "edited";
        document.ToBytes().Take(bom.Length).Should().Equal(bom);
        document.Entries[0].Comment.Should().Be("edited");
    }

    [Fact]
    public void Legacy_Code_Page_Bytes_Round_Trip()
    {
        // 0xE9 is "e acute" in Windows-1252 and not valid UTF-8 on its own.
        var bytes = Encoding.ASCII.GetBytes("# caf?\r\n127.0.0.1 localhost\r\n");
        bytes[5] = 0xE9;

        HostsFileDocument.Parse(bytes).ToBytes().Should().Equal(bytes);
    }

    [Theory]
    [InlineData("fe80::1")]
    [InlineData("::1")]
    [InlineData("2001:db8::8a2e:370:7334")]
    [InlineData("192.168.0.1")]
    [InlineData("fe80::1%12")]
    public void Compressed_Ipv6_And_Ipv4_Are_Accepted(string ip)
    {
        HostEntry.Validate(ip, "host").Should().Be(HostEntryProblem.None);
    }

    [Theory]
    [InlineData("", "host", HostEntryProblem.MissingIpAddress)]
    [InlineData("   ", "host", HostEntryProblem.MissingIpAddress)]
    [InlineData("999.1.1.1", "host", HostEntryProblem.InvalidIpAddress)]
    [InlineData("not-an-ip", "host", HostEntryProblem.InvalidIpAddress)]
    [InlineData("127.1", "host", HostEntryProblem.InvalidIpAddress)]
    [InlineData("0x7f.1", "host", HostEntryProblem.InvalidIpAddress)]
    [InlineData("1", "host", HostEntryProblem.InvalidIpAddress)]
    [InlineData("010.0.0.1", "host", HostEntryProblem.InvalidIpAddress)]
    [InlineData("10.0.0.1", "", HostEntryProblem.MissingHostname)]
    [InlineData("10.0.0.1", "a#b", HostEntryProblem.InvalidHostname)]
    public void Invalid_Rows_Are_Named(string ip, string host, HostEntryProblem expected)
    {
        HostEntry.Validate(ip, host).Should().Be(expected);
    }

    [Fact]
    public void Problem_Notifies_When_The_Address_Changes()
    {
        var entry = new HostEntry { IpAddress = "10.0.0.1", Hostname = "a" };
        var changed = new List<string?>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        entry.IpAddress = "nope";

        entry.Problem.Should().Be(HostEntryProblem.InvalidIpAddress);
        changed.Should().Contain([nameof(HostEntry.Problem), nameof(HostEntry.ProblemDisplay)]);
    }

    [Fact]
    public void FindProblems_Lists_Every_Invalid_Row_With_Its_Line_Number()
    {
        var document = HostsFileDocument.Parse(Encoding.UTF8.GetBytes("# h\n10.0.0.1 a\n10.0.0.2 b\n"));
        document.Entries[1].IpAddress = "10.0.0.300";
        var added = document.AddEntry();

        var problems = document.FindProblems();

        problems.Select(p => (p.LineNumber, p.Problem)).Should().Equal(
            (3, HostEntryProblem.InvalidIpAddress),
            (4, HostEntryProblem.MissingIpAddress));
        problems[1].Entry.Should().BeSameAs(added);
    }

    [Fact]
    public void Shorthand_Address_Lines_Stay_Raw_And_Are_Not_Rows()
    {
        const string text = "127.1 shorthand\r\n# 1 not-a-disabled-entry\r\n127.0.0.1 localhost\r\n";

        var document = HostsFileDocument.Parse(Encoding.UTF8.GetBytes(text));

        document.Entries.Select(e => e.IpAddress).Should().Equal("127.0.0.1");
        document.FindProblems().Should().BeEmpty();
        document.ToText().Should().Be(text);
    }

    [Fact]
    public void Problems_Are_Reported_Only_For_Edited_Or_Added_Rows()
    {
        var document = HostsFileDocument.Parse(Encoding.UTF8.GetBytes("10.0.0.1 a\n10.0.0.2 b\n"));

        document.Entries[0].Hostname = string.Empty;

        document.FindProblems().Should().ContainSingle()
            .Which.Entry.Should().BeSameAs(document.Entries[0]);
    }

    [Fact]
    public void Dirty_Follows_Edits_And_Is_Cleared_By_MarkSaved()
    {
        var document = HostsFileDocument.Parse(Encoding.UTF8.GetBytes(Sample));
        var transitions = new List<bool>();
        document.IsDirtyChanged += (_, _) => transitions.Add(document.IsDirty);
        var entry = document.Entries[1];

        document.IsDirty.Should().BeFalse();

        entry.Enabled = false;
        document.IsDirty.Should().BeTrue();

        // Putting the value back makes the file what it was, so there is nothing to save.
        entry.Enabled = true;
        document.IsDirty.Should().BeFalse();

        entry.Hostname = "renamed";
        document.IsDirty.Should().BeTrue();
        document.MarkSaved(document.ToBytes());
        document.IsDirty.Should().BeFalse();

        document.AddEntry();
        document.IsDirty.Should().BeTrue();

        transitions.Should().Equal(true, false, true, false, true);
    }

    [Fact]
    public void Deleting_An_Entry_Makes_The_Document_Dirty()
    {
        var document = HostsFileDocument.Parse(Encoding.UTF8.GetBytes(Sample));

        document.RemoveEntries([document.Entries[0]]);

        document.IsDirty.Should().BeTrue();
    }
}
