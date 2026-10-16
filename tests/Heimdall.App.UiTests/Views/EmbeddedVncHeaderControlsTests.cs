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

using System.Reflection;
using Heimdall.App.Services;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views;

namespace Heimdall.App.UiTests.Views;

/// <summary>
/// The VNC display controls on the constructed view: what each one sends to the page, and that
/// the view can be built at all.
/// </summary>
/// <remarks>
/// The controls moved from the page into the session header (issue #594). The markup tests in
/// <c>VncHeaderDisplayControlsTests</c> read the files as text, so they stayed green while the
/// view threw on construction and no VNC session could open. These build the real view and record
/// what reaches the page through <see cref="EmbeddedVncView.PageMessageSink"/>.
/// </remarks>
[Collection(DesktopUiCollection.Name)]
public sealed class EmbeddedVncHeaderControlsTests
{
    /// <summary>
    /// Both selectors carry their initial selection in the markup, so a selection handler declared
    /// there ran inside InitializeComponent, before the WebView existed, and threw.
    /// </summary>
    [StaFact]
    public void TheView_Constructs()
    {
        Exception? failure = null;

        WpfTestHost.Invoke(() =>
        {
            try
            {
                using EmbeddedVncView view = new();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        Assert.True(failure is null, failure?.ToString());
    }

    [StaFact]
    public void PickingAScalingMode_SendsItToThePage()
    {
        List<string> sent = [];

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = new();
            view.PageMessageSink = sent.Add;

            view.ScalingSelector.SelectedIndex = 0;
        });

        Assert.Equal(["scaling:remote"], sent);
    }

    /// <summary>
    /// Picking "Best quality" sends the JPEG quality that sharpens the picture, not only the
    /// lossless compression level the selector used to send alone.
    /// </summary>
    [StaFact]
    public void PickingAQualityPreset_SendsBothItsLevelsToThePage()
    {
        List<string> sent = [];

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = new();
            view.PageMessageSink = sent.Add;

            view.QualitySelector.SelectedIndex = 0;
        });

        Assert.Equal(["quality:{\"qualityLevel\":9,\"compressionLevel\":0}"], sent);
    }

    /// <summary>
    /// The page starts every client on its own defaults; only the two messages after the connect
    /// make the session open on what the header shows. Non-default picks, so a connect that sent
    /// nothing after itself cannot pass by matching the page's defaults.
    /// </summary>
    [StaFact]
    public void TheConnect_IsFollowedByWhatTheHeaderShows()
    {
        List<string> sent = [];

        WpfTestHost.Invoke(() =>
        {
            using EmbeddedVncView view = new();
            view.ScalingSelector.SelectedIndex = 2;
            view.QualitySelector.SelectedIndex = 3;
            view.PageMessageSink = sent.Add;

            SetPrivateField(view, "_proxy", new WebSocketVncProxy("127.0.0.1", 5900));
            SetPrivateField(view, "_webViewReady", true);
            InvokePrivateMethod(view, "SendConnectCommand");
        });

        Assert.Equal(3, sent.Count);
        Assert.StartsWith("connect:", sent[0], StringComparison.Ordinal);
        Assert.Equal(
            ["scaling:none", "quality:{\"qualityLevel\":3,\"compressionLevel\":9}"],
            sent.Skip(1));
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {fieldName} not found.");
        field.SetValue(target, value);
    }

    private static void InvokePrivateMethod(object target, string methodName)
    {
        MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method {methodName} not found.");
        method.Invoke(target, null);
    }
}
