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
using System.Runtime.InteropServices;
using System.Windows;
using Heimdall.App.Services;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// How a secret reaches the clipboard and when it is taken back: the payload carries the three
/// Windows opt-out formats, a pending clear is finished when the tool closes, and a clear never
/// touches a value the guard did not put there.
/// </summary>
public sealed class SecretClipboardGuardTests
{
    private const string Secret = "c0rrect-h0rse-battery-staple";

    [Fact]
    public void SensitivePayload_CarriesTheTextAndTheThreeOptOutFormats()
    {
        DataObject data = SensitiveClipboardData.Create(Secret);

        Assert.Equal(Secret, data.GetText(System.Windows.TextDataFormat.UnicodeText));
        Assert.True(data.GetDataPresent(SensitiveClipboardData.ExcludeFromMonitorFormat));
        Assert.Equal(0, SensitiveClipboardData.ReadDword(data, SensitiveClipboardData.CanIncludeInHistoryFormat));
        Assert.Equal(0, SensitiveClipboardData.ReadDword(data, SensitiveClipboardData.CanUploadToCloudFormat));
    }

    [Theory]
    [InlineData(SensitiveClipboardData.ExcludeFromMonitorFormat)]
    [InlineData(SensitiveClipboardData.CanIncludeInHistoryFormat)]
    [InlineData(SensitiveClipboardData.CanUploadToCloudFormat)]
    public void SensitivePayload_EachOptOutFormatIsAFourByteValue(string format)
    {
        DataObject data = SensitiveClipboardData.Create(Secret);

        Assert.NotNull(SensitiveClipboardData.ReadDword(data, format));
    }

    [Fact]
    public void Copy_GoesThroughTheSensitivePath_NotPlainText()
    {
        var clipboard = new FakeClipboard();
        var guard = new SecretClipboardGuard(clipboard);

        Assert.True(guard.TryCopy(Secret, armAutoClear: true));

        Assert.Equal([Secret], clipboard.SensitiveCopies);
        Assert.Empty(clipboard.PlainCopies);
    }

    [Fact]
    public void Close_WithAPendingClearAndOurValueStillThere_ClearsTheClipboard()
    {
        var clipboard = new FakeClipboard();
        var guard = new SecretClipboardGuard(clipboard);
        guard.TryCopy(Secret, armAutoClear: true);

        guard.Close();

        Assert.Equal(1, clipboard.ClearCalls);
        Assert.Null(clipboard.Text);
        Assert.False(guard.HasPendingClear);
    }

    [Fact]
    public void Close_WithAutoClearOff_LeavesTheClipboardAlone()
    {
        var clipboard = new FakeClipboard();
        var guard = new SecretClipboardGuard(clipboard);
        guard.TryCopy(Secret, armAutoClear: false);

        guard.Close();

        Assert.Equal(0, clipboard.ClearCalls);
        Assert.Equal(Secret, clipboard.Text);
    }

    [Fact]
    public void Close_WhenSomethingElseWasCopiedSince_LeavesThatAlone()
    {
        var clipboard = new FakeClipboard();
        var guard = new SecretClipboardGuard(clipboard);
        guard.TryCopy(Secret, armAutoClear: true);
        clipboard.Text = "the user copied this afterwards";

        guard.Close();

        Assert.Equal(0, clipboard.ClearCalls);
        Assert.Equal("the user copied this afterwards", clipboard.Text);
    }

    [Fact]
    public void ACopyWithAutoClearOff_CancelsTheClearOwedByAnEarlierCopy()
    {
        var clipboard = new FakeClipboard();
        var guard = new SecretClipboardGuard(clipboard);
        guard.TryCopy(Secret, armAutoClear: true);
        guard.TryCopy(Secret, armAutoClear: false);

        guard.Close();

        Assert.Equal(0, clipboard.ClearCalls);
    }

    [Fact]
    public void Close_CalledTwice_ClearsOnce()
    {
        var clipboard = new FakeClipboard();
        var guard = new SecretClipboardGuard(clipboard);
        guard.TryCopy(Secret, armAutoClear: true);

        guard.Close();
        guard.Close();

        Assert.Equal(1, clipboard.ClearCalls);
    }

    [Fact]
    public void ClearIfStillHeld_WhenTheClipboardIsLocked_DoesNotThrow()
    {
        var clipboard = new FakeClipboard { ThrowOnGet = true };
        var guard = new SecretClipboardGuard(clipboard);
        guard.TryCopy(Secret, armAutoClear: true);

        guard.ClearIfStillHeld();

        Assert.False(guard.HasPendingClear);
    }

    [Fact]
    public void Copy_WhenTheClipboardCannotBeOpened_ReportsFailureAndOwesNothing()
    {
        var clipboard = new FakeClipboard { ThrowOnSet = true };
        var guard = new SecretClipboardGuard(clipboard);

        Assert.False(guard.TryCopy(Secret, armAutoClear: true));
        Assert.False(guard.HasPendingClear);
    }

    [Fact]
    public void ThePasswordGeneratorView_CopiesOnlyThroughTheGuard_AndFinishesTheClearOnDispose()
    {
        // The view cannot be built outside the running application, so the call sites are guarded
        // by reading the source: a plain clipboard write would put a password back in the history.
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Heimdall.App", "Views", "Tools", "PasswordGeneratorView.xaml.cs"));

        Assert.DoesNotContain("Clipboard.SetText(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard.SetDataObject(", source, StringComparison.Ordinal);

        string dispose = ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(source), "public void Dispose()");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(dispose, "_secretClipboard.Close();"),
            "Dispose does not finish a pending clipboard clear");
    }

    private static string FindRepoRoot()
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

        throw new DirectoryNotFoundException("Cannot find Heimdall.slnx above " + AppContext.BaseDirectory);
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public List<string> SensitiveCopies { get; } = [];

        public List<string> PlainCopies { get; } = [];

        public string? Text { get; set; }

        public int ClearCalls { get; private set; }

        public bool ThrowOnGet { get; init; }

        public bool ThrowOnSet { get; init; }

        public void SetText(string text)
        {
            PlainCopies.Add(text);
            Text = text;
        }

        public void SetSensitiveText(string text)
        {
            if (ThrowOnSet)
            {
                throw new ExternalException("clipboard is held open by another process");
            }

            SensitiveCopies.Add(text);
            Text = text;
        }

        public string? GetText()
        {
            if (ThrowOnGet)
            {
                throw new ExternalException("clipboard is held open by another process");
            }

            return Text;
        }

        public void Clear()
        {
            ClearCalls++;
            Text = null;
        }
    }
}
