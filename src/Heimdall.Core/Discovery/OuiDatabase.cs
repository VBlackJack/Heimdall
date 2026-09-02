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

namespace Heimdall.Core.Discovery;

/// <summary>
/// Embedded OUI (Organizationally Unique Identifier) database for MAC address
/// manufacturer lookup. Contains common prefixes covering enterprise, network,
/// IoT, and consumer devices.
/// </summary>
public static class OuiDatabase
{
    /// <summary>
    /// Looks up the manufacturer name from a MAC address.
    /// Accepts formats: "AA:BB:CC:DD:EE:FF", "AA-BB-CC-DD-EE-FF", "AABBCCDDEEFF".
    /// </summary>
    public static string? LookupManufacturer(string macAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress)) return null;

        // Normalize to uppercase, remove separators, take first 6 chars (OUI)
        var normalized = macAddress.ToUpperInvariant()
            .Replace(":", "").Replace("-", "").Replace(".", "");
        if (normalized.Length < 6) return null;
        var oui = normalized[..6];

        if (OuiTable.TryGetValue(oui, out var manufacturer))
            return manufacturer;

        // Detect locally administered MAC (bit 1 of first byte set)
        // - indicates randomized MAC from smartphones, tablets, or modern OS privacy features
        var firstByte = Convert.ToByte(oui[..2], 16);
        if ((firstByte & 0x02) != 0)
            return "Private (Randomized MAC)";

        return null;
    }

    private static readonly Dictionary<string, string> OuiTable = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── VMware / Hypervisors ─────────────────────────────────────────
        ["000C29"] = "VMware",
        ["005056"] = "VMware",
        ["000569"] = "VMware",
        ["00155D"] = "Microsoft Hyper-V",
        ["080027"] = "Oracle VirtualBox",
        ["525400"] = "KVM/QEMU",

        // ── Network Equipment ────────────────────────────────────────────
        ["B4FBE4"] = "Ubiquiti",
        ["245A4C"] = "Ubiquiti",
        ["F09FC2"] = "Ubiquiti",
        ["788A20"] = "Ubiquiti",
        ["000F66"] = "Cisco",
        ["0050BA"] = "D-Link",
        ["001E58"] = "D-Link",
        ["C0A0BB"] = "D-Link",
        ["AC84C6"] = "TP-Link",
        ["50C7BF"] = "TP-Link",
        ["E894F6"] = "TP-Link",
        ["001F33"] = "Netgear",
        ["A42B8C"] = "Netgear",
        ["C43DC7"] = "Netgear",
        ["0024B2"] = "Netgear",
        ["B8060D"] = "Arlo Technologies",
        ["9C7B6B"] = "Arlo Technologies",
        ["001A2B"] = "Arlo Technologies",
        ["0023C1"] = "Securitas Direct (Verisure)",
        ["207918"] = "Juniper",
        ["001BED"] = "Juniper",
        ["406C8F"] = "Apple",
        ["A4B197"] = "Apple",
        ["3C15C2"] = "Apple",
        ["D8CF9C"] = "Apple",
        ["000D93"] = "Apple",
        ["00A040"] = "Apple",
        ["000A27"] = "Apple",
        ["009333"] = "Apple",

        // ── NAS ──────────────────────────────────────────────────────────
        ["001132"] = "Synology",
        ["00089B"] = "ICP Electronics (QNAP)",
        ["246511"] = "QNAP",
        ["0011D8"] = "Asustor",
        ["00D0B7"] = "Intel (NAS/Compute)",

        // ── Cameras ──────────────────────────────────────────────────────
        ["70B3D5"] = "Hikvision",
        ["C0568D"] = "Hikvision",
        ["3CEF8C"] = "Dahua",
        ["A0BD1D"] = "Dahua",
        ["ACCC8E"] = "AXIS Communications",
        ["001A07"] = "AXIS Communications",
        ["0002D1"] = "Vivotek",
        ["000E8F"] = "Foscam",
        ["B0C554"] = "Reolink",

        // ── Printers ─────────────────────────────────────────────────────
        ["002590"] = "HP (Printer)",
        ["3C2AF4"] = "HP (Printer)",
        ["00215A"] = "HP Enterprise",
        ["001BA9"] = "Brother",
        ["0001E6"] = "Hewlett-Packard",
        ["000085"] = "Canon",
        ["001599"] = "Samsung Electronics",
        ["000400"] = "Lexmark",
        ["00137F"] = "Cisco-Linksys",
        ["001E0B"] = "Hewlett-Packard",

        // ── IoT / Smart Home ─────────────────────────────────────────────
        ["001788"] = "Philips Hue",
        ["ECB5FA"] = "Philips Hue",
        ["B8D7AF"] = "Murata (IoT modules)",
        ["5CCF7F"] = "Espressif (ESP8266/ESP32)",
        ["240AC4"] = "Espressif",
        ["A020A6"] = "Espressif",
        ["40F520"] = "ESPHome",
        ["B827EB"] = "Raspberry Pi",
        ["DCA632"] = "Raspberry Pi",
        ["E45F01"] = "Raspberry Pi",
        ["D83ADD"] = "Raspberry Pi",

        // ── Smartphones / Tablets ────────────────────────────────────────
        ["286C07"] = "Xiaomi",
        ["7811DC"] = "Xiaomi",
        ["643415"] = "Xiaomi",
        ["9C2EA1"] = "Samsung",
        ["B47443"] = "Samsung",
        ["D0176A"] = "Samsung",
        ["3C5AB4"] = "Google",
        ["F4F5D8"] = "Google",
        ["54608B"] = "Google Nest",
        ["A4771A"] = "Google Nest",
        ["7C2EBD"] = "Google",

        // ── Server Hardware ──────────────────────────────────────────────
        ["001E67"] = "Intel AMT (iDRAC/iLO)",
        ["D4AE52"] = "Dell",
        ["B083FE"] = "Dell",
        ["246E96"] = "Dell",
        ["002655"] = "Hewlett-Packard",
        ["3C4A92"] = "Hewlett-Packard",
        ["94B866"] = "Hewlett-Packard Enterprise",
        ["D89EF3"] = "Lenovo",

        // ── Firewalls / Security ─────────────────────────────────────────
        ["001A8C"] = "Sophos",
        ["002615"] = "Fortinet",
        ["00163E"] = "Xensource (Citrix)",

        // ── Audio / Media ────────────────────────────────────────────────
        ["B8E937"] = "Sonos",
        ["5CDAD4"] = "Sonos",
        ["949359"] = "Sonos",
        ["7071BC"] = "Sonos",
        ["48D6D5"] = "Google Chromecast",

        // ── AVM / ISP boxes ──────────────────────────────────────────────
        ["3810D5"] = "AVM (Fritz!Box)",
        ["C80E14"] = "AVM (Fritz!Box)",
        ["B0487A"] = "AVM (Fritz!Box)",
        ["2C3AE8"] = "Cisco Meraki",
        ["0018FE"] = "Hewlett-Packard",

        // ── MikroTik ─────────────────────────────────────────────────────
        ["D4CA6D"] = "MikroTik",
        ["6C3B6B"] = "MikroTik",
        ["E48D8C"] = "MikroTik",
        ["48A98A"] = "MikroTik",
        ["C4AD34"] = "MikroTik",

        // ── Additional Cisco ────────────────────────────────────────────
        ["00265A"] = "Cisco",
        ["001BD4"] = "Cisco",
        ["0023EB"] = "Cisco",
        ["001F9E"] = "Cisco",
        ["0025B5"] = "Cisco",
        ["001D70"] = "Cisco",
        ["005080"] = "Cisco",
        ["000164"] = "Cisco",
        ["00264A"] = "Cisco",
        ["7CFC5A"] = "Cisco",
        ["00271B"] = "Cisco Wireless",
        ["0019E7"] = "Cisco Wireless",

        // ── Arista / Extreme / Brocade ──────────────────────────────────
        ["001C73"] = "Arista Networks",
        ["444CA8"] = "Arista Networks",
        ["0023E8"] = "Extreme Networks",
        ["B40131"] = "Extreme Networks",
        ["000131"] = "Extreme Networks",
        ["001B0D"] = "Brocade",
        ["008048"] = "Brocade",
        ["00E06F"] = "Brocade",

        // ── Aruba / Ruckus / Cambium ────────────────────────────────────
        ["000B86"] = "Aruba Networks",
        ["D8C7C8"] = "Aruba Networks",
        ["24DEC6"] = "Aruba Networks",
        ["001EE5"] = "Cisco Meraki",
        ["0018AE"] = "Ruckus Wireless",
        ["C8664B"] = "Ruckus Wireless",
        ["04BD88"] = "Cambium Networks",

        // ── Fortinet / Check Point / SonicWall ──────────────────────────
        ["00090F"] = "Fortinet",
        ["70E4E0"] = "Fortinet",
        ["000C86"] = "Check Point",
        ["000B06"] = "SonicWall",
        ["0006B1"] = "SonicWall",

        // ── ISP Routers / CPE ───────────────────────────────────────────
        ["E8D0FC"] = "Sagemcom (ISP Router)",
        ["CC7669"] = "Sagemcom (ISP Router)",
        ["B8A386"] = "Sagemcom (ISP Router)",
        ["3C81D8"] = "Sagemcom (ISP Router)",
        ["002376"] = "Technicolor (ISP Router)",
        ["B4F0AB"] = "Technicolor (ISP Router)",
        ["D4636D"] = "Technicolor (ISP Router)",
        ["E8F1B0"] = "Orange (Livebox)",
        ["68A3C4"] = "Orange (Livebox)",
        ["AC9B0A"] = "SFR (Box)",
        ["E003AB"] = "SFR (Box)",
        ["F40F24"] = "Free (Freebox)",
        ["0024D4"] = "Free (Freebox)",
        ["70FC8F"] = "Free (Freebox)",
        ["000E6F"] = "Freebox (Free SAS)",
        ["DC00B0"] = "Free (Freebox)",
        ["000F0B"] = "Huawei (CPE/Router)",
        ["0025AB"] = "Huawei",
        ["B4A984"] = "Huawei",
        ["0015B9"] = "ZTE",
        ["001BBF"] = "ZTE",
        ["F496C4"] = "ZTE",
        ["ACB313"] = "Arcadyan (ISP Router)",
        ["00271A"] = "Arcadyan (ISP Router)",
        ["F4EC38"] = "TP-Link",
        ["1086FE"] = "TP-Link",
        ["28EE52"] = "TP-Link",
        ["A842A1"] = "TP-Link",
        ["B0BE76"] = "TP-Link",
        ["0024A5"] = "Buffalo",
        ["001601"] = "Buffalo",
        ["0050FC"] = "Edimax",

        // ── Additional Printers ─────────────────────────────────────────
        ["0021B7"] = "Lexmark",
        ["001E62"] = "Lexmark",
        ["C8D015"] = "Konica Minolta",
        ["0000F0"] = "Samsung (Printer)",
        ["10064B"] = "Samsung (Printer)",
        ["00DA0C"] = "Sharp",
        ["0024E8"] = "Dell (Printer)",
        ["005056"] = "VMware",
        ["001B96"] = "Toshiba TEC",
        ["00066C"] = "Zebra Technologies",
        ["002244"] = "Ricoh",
        ["0020A6"] = "Kyocera",
        ["006087"] = "Konica Minolta",
        ["00C0EE"] = "Xerox",
        ["0000AA"] = "Xerox",

        // ── Additional IoT / Smart Home ─────────────────────────────────
        ["3C6A2C"] = "Espressif",
        ["BCDDC2"] = "Espressif",
        ["AC67B2"] = "Espressif",
        ["D8BFC0"] = "Espressif",
        ["2462AB"] = "Espressif",
        ["483FDA"] = "Espressif",
        ["C44F33"] = "Espressif",
        ["8C4B14"] = "Espressif",
        ["E09806"] = "Shelly",
        ["EC6464"] = "Shelly (Allterco)",
        ["98CDAC"] = "Shelly (Allterco)",
        ["E8DB84"] = "Espressif (Tuya)",
        ["D8F15B"] = "Espressif (Tuya)",
        ["10D561"] = "Tuya",
        ["7CF666"] = "Ring (Amazon)",
        ["3C9A77"] = "Ring (Amazon)",
        ["B0728E"] = "Amazon (Echo)",
        ["74C246"] = "Amazon (Echo/Fire)",
        ["FCA183"] = "Amazon (Echo/Fire)",
        ["F0D2F1"] = "Amazon (Echo/Fire)",
        ["40F520"] = "ESPHome",
        ["34A395"] = "Yeelight",
        ["04CF8C"] = "Xiaomi (Smart Home)",
        ["64DBA0"] = "Xiaomi (Smart Home)",
        ["78110B"] = "Xiaomi (Smart Home)",
        ["D072DC"] = "LIFX",
        ["50C2E8"] = "TP-Link (Kasa)",
        ["705A0F"] = "Wemo (Belkin)",
        ["EC1A59"] = "Belkin (Wemo)",
        ["002250"] = "Control4",
        ["002161"] = "Crestron",
        ["0017D1"] = "Nortel",
        ["C0A53E"] = "Wyze",
        ["7C2EBD"] = "Google",
        ["1440E5"] = "eufy (Anker)",
        ["54E43A"] = "Apple HomePod",

        // ── Additional Raspberry Pi / SBC ───────────────────────────────
        ["2CCFA1"] = "Raspberry Pi",
        ["DC2632"] = "Raspberry Pi",
        ["E8FF1E"] = "Raspberry Pi (Broadcom)",

        // ── Industrial / SCADA ──────────────────────────────────────────
        ["000E8C"] = "Siemens",
        ["0015BE"] = "Siemens",
        ["00D033"] = "Siemens",
        ["007093"] = "Siemens",
        ["0000E8"] = "Rockwell Automation",
        ["0002AB"] = "Rockwell Automation",
        ["00805F"] = "Schneider Electric",
        ["00C01F"] = "Schneider Electric",
        ["000E9B"] = "Honeywell",
        ["001199"] = "ABB",
        ["00106F"] = "ABB",
        ["002096"] = "Beckhoff",
        ["000135"] = "Advantech",
        ["00D060"] = "Advantech",
        ["001CF0"] = "Moxa",
        ["00908F"] = "Moxa",
        ["000B49"] = "WAGO",

        // ── Additional Server Hardware ──────────────────────────────────
        ["0025B5"] = "Cisco",
        ["002590"] = "HP (Printer)",
        ["AC1F6B"] = "Supermicro",
        ["003048"] = "Supermicro",
        ["002264"] = "Hewlett-Packard",
        ["0021F6"] = "Apple (Xserve)",
        ["002211"] = "Intel (Server NIC)",
        ["001517"] = "Intel",
        ["001B21"] = "Intel",
        ["7CDF4A"] = "NVIDIA (Mellanox)",
        ["0002C9"] = "Mellanox",
        ["E41F13"] = "Mellanox",
        ["000F1F"] = "Dell (DRAC/iDRAC)",
        ["001E68"] = "Quanta (Server)",
        ["0025DC"] = "Intel (vPro AMT)",

        // ── Media / Entertainment ───────────────────────────────────────
        ["3C8D20"] = "LG Electronics",
        ["001CF6"] = "Samsung (Smart TV)",
        ["5C497D"] = "Samsung (Smart TV)",
        ["F4428F"] = "Samsung (Smart TV)",
        ["7C2F80"] = "Samsung (Smart TV)",
        ["B47AF1"] = "Sony",
        ["001331"] = "Sony",
        ["001ADB"] = "Sony",
        ["08CC68"] = "Sony (PlayStation)",
        ["F8D0AC"] = "Sony",
        ["0005CD"] = "Denon",
        ["000903"] = "Yamaha",
        ["0026AB"] = "Seiko Epson",
        ["001E4C"] = "Samsung",
        ["04180F"] = "Samsung",
        ["340B40"] = "Bose",
        ["046176"] = "Bose",
        ["9004F5"] = "Bose",
        ["D86216"] = "Roku",
        ["B0A737"] = "Roku",
        ["8C3DC9"] = "Panasonic",
        ["008030"] = "Panasonic",

        // ── Additional Apple ────────────────────────────────────────────
        ["3CAA35"] = "Apple",
        ["A860B6"] = "Apple",
        ["6C4009"] = "Apple",
        ["F8E079"] = "Apple",
        ["70CD60"] = "Apple",
        ["34363B"] = "Apple",
        ["DC2B2A"] = "Apple",

        // ── Additional Mobile ───────────────────────────────────────────
        ["FC1910"] = "Samsung",
        ["A08E78"] = "Samsung",
        ["00215C"] = "Samsung",
        ["501AC5"] = "Samsung",
        ["58B568"] = "Samsung",
        ["1CBFCE"] = "Xiaomi",
        ["34802D"] = "Xiaomi",
        ["F8E9EF"] = "Xiaomi",
        ["C07184"] = "Xiaomi (Redmi)",
        ["2C411A"] = "OnePlus",
        ["C0EE40"] = "OnePlus",
        ["3C7843"] = "OPPO",
        ["2CFD9E"] = "OPPO",
        ["D47BEE"] = "vivo",
        ["A41163"] = "vivo",
        ["8C68C8"] = "Realme",
        ["5AC85C"] = "Motorola",
        ["64D154"] = "Motorola",
        ["001ECA"] = "Nokia",

        // ── Surveillance Additional ─────────────────────────────────────
        ["44A842"] = "Hikvision",
        ["B4B88D"] = "Hikvision",
        ["BC3400"] = "Hikvision",
        ["20F173"] = "Dahua",
        ["E00ECE"] = "Dahua",
        ["D87CF7"] = "Dahua",
        ["BCBAC2"] = "Hikvision",
        ["4CF5DC"] = "Hikvision",
        ["54C4A5"] = "Hikvision",
        ["C4A36E"] = "Hikvision",
        ["00408C"] = "AXIS Communications",
        ["001A07"] = "AXIS Communications",
        ["00129A"] = "Vivotek",
        ["18C2BF"] = "Reolink",

        // ── Cloud / Hosting ─────────────────────────────────────────────
        ["FA163E"] = "Amazon AWS (ENI)",
        ["060EAE"] = "Google Cloud",
        ["001DD8"] = "Microsoft Azure",
    };
}
