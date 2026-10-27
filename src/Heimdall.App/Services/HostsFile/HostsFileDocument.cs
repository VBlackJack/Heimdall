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

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

namespace Heimdall.App.Services.HostsFile;

/// <summary>
/// A row that cannot be written, with the line it would occupy in the saved file.
/// </summary>
/// <param name="LineNumber">One-based line number in the file as it would be written.</param>
/// <param name="Entry">The row.</param>
/// <param name="Problem">Why it cannot be written.</param>
public sealed record HostsFileRowProblem(int LineNumber, HostEntry Entry, HostEntryProblem Problem);

/// <summary>
/// The hosts file as an ordered list of lines, so that saving it changes only what was edited.
/// </summary>
/// <remarks>
/// <para>Every line is kept: an entry line (enabled, or disabled as <c># ip host</c>) becomes a
/// <see cref="HostEntry" /> the grid can edit, and every other line (comments, blank lines,
/// anything that does not parse) is kept verbatim in its place. An entry whose values still
/// match what was read is written back as the exact text it was read from, so a file loaded
/// and saved without an edit comes back byte for byte.</para>
/// <para>The encoding (with its byte order mark only when the file had one) and each line's
/// own terminator are kept. A line added here takes the first terminator the file uses, or
/// CRLF for a file that had none.</para>
/// </remarks>
public sealed class HostsFileDocument
{
    private const string DefaultLineEnding = "\r\n";
    private const string DisabledPrefix = "# ";
    private const string FieldSeparator = "\t";
    private const string CommentSeparator = "\t# ";
    private const int MinimumEntryFields = 2;

    private static readonly char[] FieldDelimiters = [' ', '\t'];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly List<Line> _lines = [];
    private readonly Encoding _encoding;
    private readonly byte[] _preamble;
    private readonly string _lineEnding;
    private string _savedText;
    private bool _isDirty;

    private HostsFileDocument(string text, Encoding encoding, byte[] preamble, byte[]? diskContent)
    {
        _encoding = encoding;
        DiskHash = diskContent is null ? null : SHA256.HashData(diskContent);
        _preamble = preamble;
        SplitLines(text);
        _lineEnding = _lines.Select(l => l.Terminator).FirstOrDefault(t => t.Length > 0) ?? DefaultLineEnding;
        _savedText = text;

        foreach (var line in _lines)
        {
            if (line.Entry is { } entry)
            {
                Track(entry);
                Entries.Add(entry);
            }
        }
    }

    /// <summary>
    /// The entry rows in file order, for the grid. Change the set through
    /// <see cref="AddEntry" /> and <see cref="RemoveEntries" /> so the lines follow.
    /// </summary>
    public ObservableCollection<HostEntry> Entries { get; } = [];

    /// <summary>
    /// True when saving now would write something other than what was last read or saved.
    /// </summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty == value)
            {
                return;
            }

            _isDirty = value;
            IsDirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Raised when <see cref="IsDirty" /> changes.
    /// </summary>
    public event EventHandler? IsDirtyChanged;

    /// <summary>
    /// SHA-256 of the bytes this document was read from or last written as, or null when the
    /// file did not exist. A save compares it with the file on disk to notice another
    /// program (a VPN client, Docker Desktop) rewriting the file in the meantime.
    /// </summary>
    public byte[]? DiskHash { get; private set; }

    /// <summary>
    /// A document for a file that does not exist yet: UTF-8 without a byte order mark.
    /// </summary>
    public static HostsFileDocument CreateEmpty() => new(string.Empty, new UTF8Encoding(false), [], diskContent: null);

    /// <summary>
    /// Reads a hosts file from its raw bytes.
    /// </summary>
    public static HostsFileDocument Parse(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var (encoding, preambleLength) = DetectEncoding(content);
        var text = encoding.GetString(content, preambleLength, content.Length - preambleLength);
        var preamble = content[..preambleLength];

        // The strict decoder only proves the bytes are UTF-8; writing goes through a lenient
        // one so a character it cannot encode is never an exception at save time.
        var writeEncoding = ReferenceEquals(encoding, StrictUtf8) ? new UTF8Encoding(false) : encoding;
        return new HostsFileDocument(text, writeEncoding, preamble, content);
    }

    /// <summary>
    /// Appends an empty, enabled entry at the end of the file.
    /// </summary>
    public HostEntry AddEntry()
    {
        var entry = new HostEntry { Enabled = true };
        _lines.Add(new Line(string.Empty, _lineEnding, entry, Baseline: null));
        Track(entry);
        Entries.Add(entry);
        RefreshDirty();
        return entry;
    }

    /// <summary>
    /// Removes entries and the lines that hold them.
    /// </summary>
    public void RemoveEntries(IEnumerable<HostEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries.ToList())
        {
            int index = _lines.FindIndex(l => ReferenceEquals(l.Entry, entry));
            if (index < 0)
            {
                continue;
            }

            _lines.RemoveAt(index);
            entry.PropertyChanged -= OnEntryPropertyChanged;
            Entries.Remove(entry);
        }

        RefreshDirty();
    }

    /// <summary>
    /// Every row that cannot be written, in file order.
    /// </summary>
    public IReadOnlyList<HostsFileRowProblem> FindProblems()
    {
        var problems = new List<HostsFileRowProblem>();
        for (int i = 0; i < _lines.Count; i++)
        {
            // A row read from the file and left alone is never refused: the loader only turns a line
            // into a row when its address passes the same test, and anything else stays raw text.
            var line = _lines[i];
            if (line.Entry is { } entry && !IsUnedited(line) && entry.Problem is var problem && problem != HostEntryProblem.None)
            {
                problems.Add(new HostsFileRowProblem(i + 1, entry, problem));
            }
        }

        return problems;
    }

    /// <summary>
    /// The file text as it would be written now.
    /// </summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            builder.Append(Render(line));

            // Only the last line may lack a terminator: a line that had none because it
            // ended the file needs one once something is written after it.
            bool isLast = i == _lines.Count - 1;
            builder.Append(line.Terminator.Length == 0 && !isLast ? _lineEnding : line.Terminator);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The file bytes as they would be written now, in the encoding the file was read in.
    /// </summary>
    public byte[] ToBytes()
    {
        var body = _encoding.GetBytes(ToText());
        if (_preamble.Length == 0)
        {
            return body;
        }

        var bytes = new byte[_preamble.Length + body.Length];
        _preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, _preamble.Length);
        return bytes;
    }

    /// <summary>
    /// Records that <paramref name="written" />, produced by <see cref="ToBytes" />, is now
    /// what the file holds.
    /// </summary>
    public void MarkSaved(byte[] written)
    {
        ArgumentNullException.ThrowIfNull(written);
        DiskHash = SHA256.HashData(written);
        for (int i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (line.Entry is { } entry)
            {
                _lines[i] = line with { Text = Render(line), Baseline = EntryValues.Of(entry) };
            }
        }

        _savedText = ToText();
        RefreshDirty();
    }

    private void SplitLines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\r' && c != '\n')
            {
                continue;
            }

            int terminatorLength = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            AddParsedLine(text[start..i], text.Substring(i, terminatorLength));
            i += terminatorLength - 1;
            start = i + 1;
        }

        if (start < text.Length)
        {
            AddParsedLine(text[start..], string.Empty);
        }
    }

    private void AddParsedLine(string text, string terminator)
    {
        var entry = TryParseLine(text);
        _lines.Add(new Line(text, terminator, entry, entry is null ? null : EntryValues.Of(entry)));
    }

    private static HostEntry? TryParseLine(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0)
        {
            return null;
        }

        if (line[0] == HostEntry.CommentMarker)
        {
            // A disabled entry: # <IP> <hostname> [# comment]. Any other comment stays a raw line.
            var disabled = TryParseEntry(line.TrimStart(HostEntry.CommentMarker).Trim());
            if (disabled is not null)
            {
                disabled.Enabled = false;
            }

            return disabled;
        }

        var enabled = TryParseEntry(line);
        if (enabled is not null)
        {
            enabled.Enabled = true;
        }

        return enabled;
    }

    private static HostEntry? TryParseEntry(string line)
    {
        int commentIndex = line.IndexOf(HostEntry.CommentMarker);
        var body = commentIndex >= 0 ? line[..commentIndex] : line;
        var parts = body.Split(FieldDelimiters, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < MinimumEntryFields || !HostEntry.IsValidAddress(parts[0]))
        {
            return null;
        }

        return new HostEntry
        {
            IpAddress = parts[0],
            Hostname = string.Join(' ', parts[1..]),
            Comment = commentIndex >= 0 ? line[(commentIndex + 1)..].Trim() : string.Empty,
        };
    }

    private static bool IsUnedited(Line line) =>
        line.Entry is { } entry && line.Baseline is { } baseline && baseline == EntryValues.Of(entry);

    private static string Render(Line line)
    {
        if (line.Entry is not { } entry || IsUnedited(line))
        {
            return line.Text;
        }

        var builder = new StringBuilder();
        if (!entry.Enabled)
        {
            builder.Append(DisabledPrefix);
        }

        builder.Append(entry.IpAddress.Trim());
        builder.Append(FieldSeparator);
        builder.Append(entry.Hostname.Trim());

        var comment = entry.Comment.Trim();
        if (comment.Length > 0)
        {
            builder.Append(CommentSeparator);
            builder.Append(comment);
        }

        return builder.ToString();
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] content)
    {
        foreach (var candidate in new Encoding[]
                 {
                     new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                     new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
                     new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
                 })
        {
            var preamble = candidate.Preamble;
            if (content.AsSpan().StartsWith(preamble))
            {
                return (candidate, preamble.Length);
            }
        }

        try
        {
            _ = StrictUtf8.GetString(content);
            return (StrictUtf8, 0);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8 (a legacy code page): Latin-1 maps every byte to one character and
            // back, so the lines nobody edits are written exactly as they were read.
            return (Encoding.Latin1, 0);
        }
    }

    private void Track(HostEntry entry) => entry.PropertyChanged += OnEntryPropertyChanged;

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshDirty();

    private void RefreshDirty() => IsDirty = !string.Equals(ToText(), _savedText, StringComparison.Ordinal);

    private sealed record Line(string Text, string Terminator, HostEntry? Entry, EntryValues? Baseline);

    private readonly record struct EntryValues(bool Enabled, string IpAddress, string Hostname, string Comment)
    {
        public static EntryValues Of(HostEntry entry) =>
            new(entry.Enabled, entry.IpAddress, entry.Hostname, entry.Comment);
    }
}
