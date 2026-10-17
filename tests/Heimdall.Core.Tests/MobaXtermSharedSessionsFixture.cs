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

namespace Heimdall.Core.Tests;

/// <summary>
/// An anonymized copy of the structure of a real MobaXterm shared session file (.mxtsessions).
/// </summary>
/// <remarks>
/// <para>Reproduces what the real file showed: one [Bookmarks_N] section per folder including a
/// nested <c>Old\PROD</c>, custom session icons (126, 117, 130, 140) in front of SSH type 0, the
/// per-user <c>[DAS]</c> login on both the session and its gateway, two gateways shared by most
/// sessions, one direct session, login commands, comments with commas, a session whose name is
/// not its host, the same session name in two folders, and a trailing [SharedSettings] section.</para>
/// <para>Every host is fake.</para>
/// </remarks>
internal static class MobaXtermSharedSessionsFixture
{
    public const int SessionCount = 8;

    public const string FirstGatewayHost = "gw1.example.test";
    public const int FirstGatewayPort = 22;
    public const int FirstGatewaySessionCount = 4;

    public const string SecondGatewayHost = "gw2.example.test";
    public const int SecondGatewayPort = 2222;
    public const int SecondGatewaySessionCount = 3;

    public const string DirectSessionName = "srv-tools01";
    public const string RenamedSessionName = "web-front (primary)";
    public const string RenamedSessionHost = "srv-web01";

    public const string Content = """
        [Bookmarks]
        SubRep=
        ImgNum=42
        [Bookmarks_1]
        SubRep=PROD
        ImgNum=41
        srv-db01=#126#0%srv-db01%22%[DAS]%%0%-1%sudo su - www%gw1.example.test%22%[DAS]%-1%0%0%%%-1%0%0%0%%1080%%0%0%1#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,192%0%-1%0%%xterm%-1%0%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%0%-1#0#database
        srv-db02=#126#0%srv-db02%22%[DAS]%%0%-1%sudo su - www%gw1.example.test%22%[DAS]%-1%0%0%%%-1%0%0%0%%1080%%0%0%1#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,192%0%-1%0%%xterm%-1%0%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%0%-1#0#database
        web-front (primary)=#117#0%srv-web01%22%[DAS]%%0%-1%sudo su - app_prod%gw1.example.test%22%[DAS]%-1%0%0%%%-1%0%0%0%%1080%%0%0%1#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,192%0%-1%0%%xterm%-1%0%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%0%-1#0#ssl-front-x, email-x
        [Bookmarks_2]
        SubRep=PROD\Proxies
        ImgNum=41
        srv-hap01=#117#0%srv-hap01%22%[DAS]%%0%-1%%gw2.example.test%2222%[DAS]%-1%0%0%%%-1%0%0%0%%1080%%0%0%1#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,192%0%-1%0%%xterm%-1%0%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%0%-1#0#ha-proxy
        srv-hap02=#117#0%srv-hap02%22%[DAS]%%0%-1%%gw2.example.test%2222%[DAS]%-1%0%0%%%-1%0%0%0%%1080%%0%0%1#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,192%0%-1%0%%xterm%-1%0%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%0%-1#0#ha-proxy
        [Bookmarks_3]
        SubRep=Old\PROD
        ImgNum=41
        srv-db01=#130#0%srv-db01-old%22%[DAS]%%0%-1%sudo su - www%gw1.example.test%22%[DAS]%-1%0%0%%%-1%0%0%0%%1080%%0%0%1#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,192%0%-1%0%%xterm%-1%0%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%0%-1#0#
        srv-app01=#140#0%srv-app01%2201%[DAS]%%0%-1%sudo su - app_prod%gw2.example.test%2222%[DAS]%-1%0%0%%%-1%0%0%0%%1080%%0%0%1#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,192%0%-1%0%%xterm%-1%0%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%0%-1#0#database, legacy
        [Bookmarks_4]
        SubRep=Tools
        ImgNum=41
        srv-tools01=#140#0%srv-tools01%22%[DAS]%%0%-1%%%%%-1%0%0%%%-1%0%0%0%%1080%%0%0%1#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,192%0%-1%0%%xterm%-1%0%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%0%-1#0#tools
        [SharedSettings]
        AllowedEditors=editor-a,editor-b
        """;
}
