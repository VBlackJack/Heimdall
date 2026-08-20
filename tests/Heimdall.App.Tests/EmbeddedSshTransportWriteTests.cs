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
using System.Net.Sockets;
using Heimdall.App.Views;

namespace Heimdall.App.Tests;

/// <summary>
/// A keystroke aimed at a transport that has gone is dropped, not thrown.
/// </summary>
/// <remarks>
/// <para>The terminal's input arrives from a WebView2 message handler whose only guard was for a
/// malformed payload, so an exception from the session write left it unhandled. The local terminal
/// already tolerated a dead process and the resize path already tolerated a dead session; the SSH
/// write was the one that did not, and typing after a drop took the exception all the way out.</para>
/// <para>Tolerated is not the same as swallowed. The transport failing is a fact about the
/// connection; an argument or reference fault is a defect and has to keep travelling.</para>
/// </remarks>
public sealed class EmbeddedSshTransportWriteTests
{
    [Fact]
    public void ADeliveredWriteReportsSuccess()
    {
        int calls = 0;

        bool delivered = EmbeddedSshView.TryTransportWrite(() => calls++, "ssh");

        Assert.True(delivered);
        Assert.Equal(1, calls);
    }

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public void ATransportThatHasGoneDropsTheInput(Exception failure)
    {
        Assert.True(EmbeddedSshView.IsTransportWriteFailure(failure));

        bool delivered = EmbeddedSshView.TryTransportWrite(() => throw failure, "ssh");

        Assert.False(delivered);
    }

    /// <summary>
    /// The two the session raises by name, asserted with the message it actually uses.
    /// </summary>
    /// <remarks>
    /// <c>SshShellSession.Write</c> throws <see cref="ObjectDisposedException"/> once disposed and
    /// <see cref="InvalidOperationException"/> when its stream has gone. Those are the two a user
    /// reaches by typing after a disconnect, so they are named here rather than left implied by
    /// the type list.
    /// </remarks>
    [Fact]
    public void TheTwoTheSessionRaisesByNameAreTolerated()
    {
        Assert.False(EmbeddedSshView.TryTransportWrite(
            () => throw new ObjectDisposedException(nameof(Heimdall.Ssh.SshShellSession)),
            "ssh"));

        Assert.False(EmbeddedSshView.TryTransportWrite(
            () => throw new InvalidOperationException("Session is not connected."),
            "ssh"));
    }

    /// <summary>
    /// The disposed arm is redundant, and that is recorded rather than discovered again.
    /// </summary>
    /// <remarks>
    /// Removing <see cref="ObjectDisposedException"/> from the tolerated list changes no
    /// behaviour, because it already is an <see cref="InvalidOperationException"/>. A mutation
    /// that removes it therefore cannot be killed - it is equivalent, not uncovered. Asserting the
    /// relation keeps the arm a documented choice instead of an assumption about the framework.
    /// </remarks>
    [Fact]
    public void TheDisposedArmIsSubsumedByTheInvalidOperationArm()
    {
        Assert.True(typeof(InvalidOperationException)
            .IsAssignableFrom(typeof(ObjectDisposedException)));
    }

    /// <summary>
    /// Guards the guard: a blanket catch would pass every test above while hiding defects.
    /// </summary>
    [Theory]
    [MemberData(nameof(Defects))]
    public void ADefectKeepsTravelling(Exception defect)
    {
        Assert.False(EmbeddedSshView.IsTransportWriteFailure(defect));

        Exception thrown = Assert.ThrowsAny<Exception>(
            () => EmbeddedSshView.TryTransportWrite(() => throw defect, "ssh"));

        Assert.Same(defect, thrown);
    }

    [Fact]
    public void AMissingWriteIsRejectedRatherThanReportedAsDelivered()
    {
        Assert.Throws<ArgumentNullException>(
            () => EmbeddedSshView.TryTransportWrite(null!, "ssh"));
    }

    /// <summary>
    /// Both session writes have to go through the guard, or it defends nothing.
    /// </summary>
    /// <remarks>
    /// Read from source because the view needs a desktop to construct. Coarse, and deliberately
    /// so: what it rules out is a guard that exists and is wired to nothing, which is how the
    /// tests above would keep passing while the defect returned.
    /// </remarks>
    [Theory]
    [InlineData("private void WriteToSession(byte[] data, bool marksTerminalInput = true)")]
    [InlineData("private void WriteToSession(string text, bool marksTerminalInput = true)")]
    public void EverySessionWriteGoesThroughTheGuard(string signature)
    {
        string body = ExtractMethodBody(ReadViewSource(), signature);

        Assert.Contains("TryTransportWrite(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_session.Write(", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Guards the guard above: it only means something if it is reading the real file.
    /// </summary>
    [Fact]
    public void TheSourceBeingReadIsTheViewThatCarriesTheWrites()
    {
        string source = ReadViewSource();

        Assert.Contains("internal static bool TryTransportWrite(", source, StringComparison.Ordinal);
        Assert.Contains("private void WriteToSession(byte[] data", source, StringComparison.Ordinal);
        Assert.Contains("private void WriteToSession(string text", source, StringComparison.Ordinal);
    }

    private static string ReadViewSource() => File.ReadAllText(Path.Combine(
        FindRepoRoot(),
        "src",
        "Heimdall.App",
        "Views",
        "EmbeddedSshView.xaml.cs"));

    private static string ExtractMethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Signature not found: {signature}");

        int open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"No body for: {signature}");

        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Unbalanced body for: {signature}");
    }

    private static string FindRepoRoot()
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
            $"Cannot find repository root containing Heimdall.slnx from: {AppContext.BaseDirectory}");
    }

    public static TheoryData<Exception> TransportFailures() =>
    [
        new ObjectDisposedException("session"),
        new InvalidOperationException("Session is not connected."),
        new IOException("the pipe has ended"),
        new SocketException(),
        new Renci.SshNet.Common.SshConnectionException("client not connected"),
        new Renci.SshNet.Common.SshOperationTimeoutException("timed out"),
    ];

    public static TheoryData<Exception> Defects() =>
    [
        new ArgumentNullException("data"),
        new ArgumentOutOfRangeException("length"),
        new NullReferenceException(),
        new IndexOutOfRangeException(),
        new NotSupportedException(),
    ];
}
