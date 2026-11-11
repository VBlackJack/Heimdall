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

using System.Runtime.InteropServices;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// A copied private key is taken back after a delay, when the tool closes and when the
/// application exits, but only while the clipboard still holds that key.
/// </summary>
public sealed class TimedSecretClipboardTests
{
    private const string Key = "-----BEGIN PRIVATE KEY-----AAAA-----END PRIVATE KEY-----";
    private const string OtherText = "something the user copied since";

    [Fact]
    public void TryCopy_ArmsTheTimerForTheClearDelay()
    {
        Fixture f = new();

        Assert.True(f.Subject.TryCopy(Key));

        Assert.Equal([Key], f.Clipboard.SensitiveCopies);
        Assert.Equal(TimeSpan.FromSeconds(TimedSecretClipboard.ClearDelaySeconds), f.Timer.LastDelay);
        Assert.True(f.Timer.Running);
    }

    [Fact]
    public void WhenTheDelayElapses_AndTheClipboardIsUnchanged_ItIsCleared()
    {
        Fixture f = new();
        f.Subject.TryCopy(Key);

        f.Timer.Fire();

        Assert.Equal(1, f.Clipboard.ClearCalls);
        Assert.Null(f.Clipboard.Text);
        Assert.False(f.Timer.Running);
    }

    [Fact]
    public void WhenTheDelayElapses_AndTheUserCopiedSomethingElse_TheClipboardIsLeftAlone()
    {
        Fixture f = new();
        f.Subject.TryCopy(Key);
        f.Clipboard.Text = OtherText;

        f.Timer.Fire();

        Assert.Equal(0, f.Clipboard.ClearCalls);
        Assert.Equal(OtherText, f.Clipboard.Text);
    }

    [Fact]
    public void Dispose_ClearsAtOnceWithoutWaitingForTheDelay()
    {
        Fixture f = new();
        f.Subject.TryCopy(Key);

        f.Subject.Dispose();

        Assert.Equal(1, f.Clipboard.ClearCalls);
        Assert.False(f.Timer.Running);
        Assert.Equal(0, f.Exit.SubscriberCount);
    }

    [Fact]
    public void Dispose_DoesNotTouchAClipboardTheUserChanged()
    {
        Fixture f = new();
        f.Subject.TryCopy(Key);
        f.Clipboard.Text = OtherText;

        f.Subject.Dispose();

        Assert.Equal(0, f.Clipboard.ClearCalls);
        Assert.Equal(OtherText, f.Clipboard.Text);
    }

    [Fact]
    public void ApplicationExit_ClearsTheKeyEvenWhenTheToolWasNeverClosed()
    {
        Fixture f = new();
        f.Subject.TryCopy(Key);

        f.Exit.Raise();

        Assert.Equal(1, f.Clipboard.ClearCalls);
        Assert.Null(f.Clipboard.Text);
    }

    [Fact]
    public void ApplicationExit_WithAClipboardThatFailsUnexpectedly_DoesNotThrow()
    {
        Fixture f = new();
        f.Subject.TryCopy(Key);
        f.Clipboard.GetFailure = new InvalidOperationException("clipboard broke");

        f.Exit.Raise();
        f.Subject.Dispose();
    }

    [Fact]
    public void TryCopy_WithALockedClipboard_ReturnsFalseAndArmsNothing()
    {
        Fixture f = new();
        f.Clipboard.SetFailure = new ExternalException("locked");

        Assert.False(f.Subject.TryCopy(Key));

        Assert.False(f.Timer.Running);
        f.Subject.Dispose();
        Assert.Equal(0, f.Clipboard.ClearCalls);
    }

    [Fact]
    public void ClearingAClipboardThatIsLockedAtThatMoment_IsSwallowed()
    {
        Fixture f = new();
        f.Subject.TryCopy(Key);
        f.Clipboard.GetFailure = new ExternalException("locked");

        f.Timer.Fire();
        f.Exit.Raise();
    }

    [Fact]
    public void AfterDispose_TheExitHookNoLongerClears()
    {
        Fixture f = new();
        f.Subject.TryCopy(Key);
        f.Subject.Dispose();
        f.Clipboard.Text = Key;

        f.Exit.Raise();

        Assert.Equal(Key, f.Clipboard.Text);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Subject = new TimedSecretClipboard(Clipboard, Timer, Exit);
        }

        public FakeClipboard Clipboard { get; } = new();

        public FakeTimer Timer { get; } = new();

        public FakeExit Exit { get; } = new();

        public TimedSecretClipboard Subject { get; }
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public List<string> SensitiveCopies { get; } = [];

        public string? Text { get; set; }

        public int ClearCalls { get; private set; }

        public Exception? GetFailure { get; set; }

        public Exception? SetFailure { get; set; }

        public void SetText(string text) => Text = text;

        public void SetSensitiveText(string text)
        {
            if (SetFailure is not null)
            {
                throw SetFailure;
            }

            SensitiveCopies.Add(text);
            Text = text;
        }

        public string? GetText() => GetFailure is null ? Text : throw GetFailure;

        public void Clear()
        {
            ClearCalls++;
            Text = null;
        }
    }

    private sealed class FakeTimer : ISecretClearTimer
    {
        public event EventHandler? Elapsed;

        public TimeSpan LastDelay { get; private set; }

        public bool Running { get; private set; }

        public void Restart(TimeSpan delay)
        {
            LastDelay = delay;
            Running = true;
        }

        public void Stop() => Running = false;

        public void Fire() => Elapsed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeExit : IApplicationExitSource
    {
        private EventHandler? _handlers;

        public event EventHandler? Exiting
        {
            add => _handlers += value;
            remove => _handlers -= value;
        }

        public int SubscriberCount => _handlers?.GetInvocationList().Length ?? 0;

        public void Raise() => _handlers?.Invoke(this, EventArgs.Empty);
    }
}
