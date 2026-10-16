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
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The "View only" server setting reaches the noVNC client in two hops inside the page: the
/// <c>connect:</c> message hands it to <c>connect()</c>, and <c>connect()</c> sets it on the
/// client. The page used to drop it at the first hop, so keyboard and mouse input reached a host
/// the user had configured as view-only.
/// </summary>
/// <remarks>
/// Proved against three mutants of <c>vnc.html</c>, each red here and green once restored:
/// deleting <c>rfb.viewOnly = viewOnly;</c>, folding it behind <c>if (false)</c>, and calling
/// <c>connect()</c> without <c>params.viewOnly</c>.
/// </remarks>
public sealed class VncViewOnlyTests
{
    private const string ConnectFromMessageSignature = "function connectFromMessage(json) {";
    private const string ConnectSignature = "function connect(wsUrl, password, viewOnly) {";

    [Fact]
    public void ThePage_PassesTheSettingFromTheMessageToConnect()
    {
        string logic = FunctionLogic(ConnectFromMessageSignature);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                logic,
                "connect(params.wsUrl, params.password || null, params.viewOnly === true);"),
            "The connect message no longer hands viewOnly to connect().");
    }

    [Fact]
    public void ThePage_AppliesTheSettingToTheClient()
    {
        string logic = FunctionLogic(ConnectSignature);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, "rfb.viewOnly = viewOnly;"),
            "connect() no longer sets viewOnly on the noVNC client.");
    }

    /// <summary>
    /// One function of the page, blanked of comments and literals and cut at its closing brace,
    /// so a statement written in a later function cannot answer for this one.
    /// </summary>
    private static string FunctionLogic(string signature)
    {
        string page = ReadSource("Assets", "vnc.html");
        string logic = ViewSource.WithoutCommentsAndLiterals(ViewSource.HandlerBody(page, signature));

        int depth = 0;
        for (int index = 0; index < logic.Length; index++)
        {
            if (logic[index] == '{')
            {
                depth++;
            }
            else if (logic[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return logic[..(index + 1)];
                }
            }
        }

        Assert.Fail($"The function never closes: {signature}");
        return logic;
    }

    private static string ReadSource(params string[] relative)
    {
        string full = Path.Combine([ViewSource.RepoRoot(), "src", "Heimdall.App", .. relative]);
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }
}
