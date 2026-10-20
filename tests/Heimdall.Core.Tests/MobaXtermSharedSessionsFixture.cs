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

    /// <summary>
    /// How many sessions of <see cref="ProtocolsContent"/> become profiles: the measured Telnet,
    /// VNC and SFTP sessions (two Telnet, two VNC, one SFTP) and the six synthetic ones.
    /// </summary>
    public const int ProtocolsImportedCount = 11;

    /// <summary>
    /// How many sessions of <see cref="ProtocolsContent"/> are skipped: FTP, WSL, two with a jump
    /// host, one measured and five synthetic with a proxy.
    /// </summary>
    public const int ProtocolsSkippedCount = 10;

    // The terminal font and colours MobaXterm writes after the '%' fields of a session.
    private const string Tail = "#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,192%0%-1%0%%xterm%-1%0%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%0%-1#0#";

    /// <summary>
    /// Telnet, VNC, SFTP, FTP and WSL sessions of a real MobaXterm, each created in its interface.
    /// </summary>
    /// <remarks>
    /// <para>The sessions named after a protocol (<c>tel-a</c>, <c>vnc-b</c>, <c>sftp-px</c>,
    /// <c>telnet-gw</c>, <c>ftp-a</c>, <c>wsl</c>...) are measured: the fields are what MobaXterm
    /// wrote, with fake hosts and users, and the value after the equal sign starts with a space as
    /// in the file. The ports are not the protocol defaults, so a constant standing for the port
    /// would fail.</para>
    /// <para>The <c>syn-</c> and <c>noport-</c> sessions are SYNTHETIC: they are not copied from
    /// MobaXterm. Every field the layout of their type does not read holds a unique sentinel
    /// (<c>u3</c>, <c>u4</c>...), and the jump host and proxy slots are empty or "0", so a layout
    /// shifted by one in either direction lands on a sentinel or on a refused slot. The
    /// <c>noport-</c> sessions have no port at all. The <c>-proxy</c> sessions are the same with
    /// the proxy slot filled and no jump host, to tell the two refusals apart.</para>
    /// </remarks>
    public const string ProtocolsContent = $"""
        [Bookmarks_1]
        SubRep=Protocols
        ImgNum=41
        ftp-a= #130#6%ftp-a.example.test%2121%ftpusera%-1%/srv/ftpa%0%-1%0%0%%21%%%0%0%-1%0%-1%0%%0{Tail}
        sftp-a= #140#7%sftp-a.example.test%2202%sftpusera%-1%0%/srv/sftpa%0%0%%0%%1080%%%%0{Tail}
        sftp-px= #140#7%sftp-px.example.test%22%sftpuserpx%-1%0%%0%0%%1%proxys.example.test%3130%proxyusers%%%0{Tail}
        tel-a= #98#1%telnet-a.example.test%2323%telusera%%2%%%%%0%0%%1080%%%0%-1%0{Tail}
        tel-b= #98#1%telnet-b.example.test%2324%teluserb%%2%%%%%0%0%%1080%%%0%-1%0{Tail}
        telnet-gw= #98#1%telnet-gw.example.test%23%telusergw%%1%jumpt.example.test%2299%jumpusert%%0%1%proxyt.example.test%3128%proxyusert%%0%-1%0{Tail}
        vnc-a= #128#5%vnc-a.example.test%5911%-1%0%%%%%-1%-1%0%vncusera%0%%1080%%%0{Tail}
        vnc-b= #128#5%vnc-b.example.test%5912%-1%0%%%%%-1%-1%0%vncuserb%0%%1080%%%0{Tail}
        vnc-gw= #128#5%vnc-gw.example.test%5900%-1%0%jumpv.example.test%2298%jumpuserv%%-1%-1%0%%1%proxyv.example.test%3129%proxyuserv%%0{Tail}
        wsl= #151#14%AlmaLinux-9%
        syn-telnet= #98#1%syn-telnet.example.test%2325%syntelu%u3%u4%%%%u8%u9%0%%u12%u13%u14{Tail}
        syn-vnc= #128#5%syn-vnc.example.test%5913%u2%u3%%%%u7%u8%u9%u10%u11%0%%u14%u15{Tail}
        syn-sftp= #140#7%syn-sftp.example.test%2203%synsftpu%u3%u4%u5%u6%u7%u8%0%%u11%u12{Tail}
        noport-telnet= #98#1%noport-telnet.example.test%%syntelu%u3%u4%%%%u8%u9%0%%u12%u13%u14{Tail}
        noport-vnc= #128#5%noport-vnc.example.test%%u2%u3%%%%u7%u8%u9%u10%u11%0%%u14%u15{Tail}
        noport-sftp= #140#7%noport-sftp.example.test%%synsftpu%u3%u4%u5%u6%u7%u8%0%%u11%u12{Tail}
        syn-telnet-proxy= #98#1%syn-telnet-proxy.example.test%2326%syntelu%u3%u4%%%%u8%u9%1%proxyt2.example.test%3128%u13{Tail}
        syn-vnc-proxy= #128#5%syn-vnc-proxy.example.test%5914%u2%u3%%%%u7%u8%u9%u10%u11%1%proxyv2.example.test%3129{Tail}
        syn-vnc-proxytype= #128#5%syn-vnc-proxytype.example.test%5915%u2%u3%%%%u7%u8%u9%u10%u11%2%{Tail}
        syn-telnet-proxytype= #98#1%syn-telnet-proxytype.example.test%2327%syntelu%u3%u4%%%%u8%u9%2%%u12%u13%u14{Tail}
        syn-sftp-proxytype= #140#7%syn-sftp-proxytype.example.test%2204%synsftpu%u3%u4%u5%u6%u7%u8%2%%u11%u12{Tail}
        """;

    /// <summary>
    /// SSH sessions with and without a proxy, three created in the MobaXterm interface and three
    /// synthetic.
    /// </summary>
    /// <remarks>
    /// <para>The <c>ssh-proxy-</c> sessions are measured: <c>a</c> goes through a Socks4 proxy
    /// (type 1) with a login and port 3181, <c>c</c> through an Http proxy (type 3) with a user on
    /// the session itself, <c>b</c> has no proxy and shows what MobaXterm writes then: type "0", no
    /// host, port 1080. Fields are as written, hosts and users are fake.</para>
    /// <para>The <c>syn-ssh</c> sessions are SYNTHETIC: every field the SSH layout does not read
    /// holds a unique sentinel, the login command and jump host are empty, so a proxy slot shifted
    /// by one in either direction lands on a sentinel and refuses the session that has no proxy.</para>
    /// </remarks>
    public const string SshProxyContent = $"""
        [Bookmarks_1]
        SubRep=SshProxy
        ImgNum=41
        ssh-proxy-a.example.test= #109#0%ssh-proxy-a.example.test%22%%%-1%-1%%%%%0%-1%0%%%-1%-1%0%1%proxy-ssh-a.example.test%3181%pxusera%0%0%1%%0%%%%0%-1%-1%0%%%0{Tail}
        ssh-proxy-b.example.test= #109#0%ssh-proxy-b.example.test%22%%%-1%-1%%%%%0%-1%0%%%-1%-1%0%0%%1080%%0%0%1%%0%%%%0%-1%-1%0%%%0{Tail}
        ssh-proxy-c.example.test (sshuserc)= #109#0%ssh-proxy-c.example.test%22%sshuserc%%-1%-1%%%%%0%0%0%%%-1%-1%0%3%proxy-ssh-c.example.test%3183%%0%0%1%%0%%%%0%-1%-1%0%%%0{Tail}
        syn-ssh= #109#0%syn-ssh.example.test%2224%synsshu%u3%u4%u5%%%%%u10%u11%u12%u13%u14%u15%u16%u17%0%%u20%u21%u22{Tail}
        syn-ssh-proxy= #109#0%syn-ssh-proxy.example.test%2225%synsshu%u3%u4%u5%%%%%u10%u11%u12%u13%u14%u15%u16%u17%1%proxyssh2.example.test%u20%u21%u22{Tail}
        syn-ssh-proxytype= #109#0%syn-ssh-proxytype.example.test%2226%synsshu%u3%u4%u5%%%%%u10%u11%u12%u13%u14%u15%u16%u17%2%%u20%u21%u22{Tail}
        """;

    /// <summary>
    /// RDP sessions with and without an RD Gateway, three created in the MobaXterm interface and
    /// two synthetic.
    /// </summary>
    /// <remarks>
    /// <para>MobaXterm has no proxy for an RDP session; its other route is the RD Gateway of the
    /// Expert RDP settings. The <c>rdp-proxy-</c> sessions are measured with the gateway set to
    /// Always: <c>a</c> authenticates as the end server, <c>b</c> prompts and carries a user on the
    /// session. The server lands at field 19 and again in the packed field 30.
    /// <c>rdp-direct-c</c> has no gateway: field 19 is empty and field 30 is absent.</para>
    /// <para>The <c>syn-rdp</c> sessions are SYNTHETIC: every field the RDP layout does not read
    /// holds a unique sentinel and the SSH jump host is empty, so a gateway index shifted by one
    /// lands on a sentinel and refuses the session that has no gateway.</para>
    /// </remarks>
    public const string RdpGatewayContent = $"""
        [Bookmarks_1]
        SubRep=RdpGateway
        ImgNum=41
        rdp-proxy-a.example.test= #91#4%rdp-proxy-a.example.test%3389%%0%0%0%0%-1%0%0%-1%%%%%0%0%%-1%rdgw-a.example.test%-1%-1%0%-1%0%-1%0%0%0%0%10000010000000,2,rdgw-a.example.test,,0,0,{Tail}
        rdp-proxy-b.example.test (rdpuserb)= #91#4%rdp-proxy-b.example.test%3389%rdpuserb%0%0%0%0%-1%0%0%-1%%%%%0%0%%-1%rdgw-b.example.test%-1%-1%0%-1%0%-1%0%0%0%0%10000010000000,2,rdgw-b.example.test,,0,1,{Tail}
        rdp-direct-c.example.test= #91#4%rdp-direct-c.example.test%3389%%0%0%0%0%-1%0%0%-1%%%%%0%0%%-1%%-1%-1%0%-1%0%-1%0%0%0%0%{Tail}
        syn-rdp= #91#4%syn-rdp.example.test%3390%synrdpu%u3%u4%u5%u6%u7%u8%u9%u10%u11%%%%u15%u16%u17%u18%%u20%u21%u22{Tail}
        syn-rdp-gw= #91#4%syn-rdp-gw.example.test%3391%synrdpu%u3%u4%u5%u6%u7%u8%u9%u10%u11%%%%u15%u16%u17%u18%rdgw2.example.test%u20%u21%u22{Tail}
        """;
}
