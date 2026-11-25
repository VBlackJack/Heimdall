<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->

*Ce document est la version française de [../TOOLS.md](../TOOLS.md). / This document is the French version.*

# Référence des outils

Référence développeur pour les outils intégrés de Heimdall, les fournisseurs
d'outils externes et l'infrastructure d'hébergement des outils.

## Vue d'ensemble

Heimdall embarque 58 outils intégrés enregistrés par `ToolRegistry`. Le jeton
brut `Entry(` apparaît plus souvent dans `ToolRegistry.cs` parce qu'il
correspond aussi au record interne `ToolEntry`, au chemin d'enregistrement
dynamique des outils externes et à la méthode d'aide privée. Le nombre effectif
d'outils intégrés correspond à l'ensemble des entrées de registre déclarées dans
le constructeur :

- Réseau : 17
- Sécurité : 15
- Encodage : 6
- Système : 14
- Outils natifs externes : 6

Les outils issus des fournisseurs externes sont détectés à l'exécution et
ajoutés au registre après les scans de démarrage. Ils ne font pas partie du
décompte des 58 outils intégrés.

## Catalogue des outils intégrés

Les noms ci-dessous proviennent de `locales/en.json` ; les identifiants sont les
clés de registre stables utilisées par la palette de commandes, les onglets, les
panneaux divisés et la recherche d'outils.

### Réseau

| ID | Nom | Pack |
|---|---|---|
| `PING` | Ping Monitor | Diagnostic réseau |
| `DNS` | DNS Lookup | Diagnostic réseau |
| `CERT` | Certificate Inspector | Diagnostic réseau |
| `PORTSCAN` | Port Scanner | Audit et reconnaissance |
| `SUBNET` | Subnet Calculator | Essentiels |
| `IPCONV` | IP Address Converter | Essentiels |
| `HTTP` | HTTP Status Codes | Essentiels |
| `WHOIS` | WHOIS Lookup | Diagnostic réseau |
| `HTTPHEADERS` | HTTP Header Analyzer | Diagnostic réseau |
| `BANNER` | Banner Grabber | Audit et reconnaissance |
| `TCPTRACE` | Traceroute | Diagnostic réseau |
| `SNMPWALK` | SNMP Walker | Audit et reconnaissance |
| `ARPMON` | ARP Monitor | Diagnostic réseau |
| `FWTEST` | Firewall Tester | Audit et reconnaissance |
| `NETMAP` | Network Cartography | Audit et reconnaissance |
| `NETCALC` | Network Calculator | Essentiels |
| `TCPPING` | TCP Ping | Diagnostic réseau |

### Sécurité

| ID | Nom | Pack |
|---|---|---|
| `HASH` | Hash Generator | Essentiels |
| `HMAC` | HMAC Generator | Essentiels |
| `PASSWORD` | Password Generator | Essentiels |
| `SSHKEY` | SSH Key Generator | Essentiels |
| `CERTGEN` | Certificate Generator | Essentiels |
| `JWT` | JWT Parser | Essentiels |
| `TOTP` | TOTP Generator | Essentiels |
| `PWDAUDIT` | Password Auditor | Essentiels |
| `SSHAUDIT` | SSH Key Auditor | Essentiels |
| `TLSAUDIT` | TLS Auditor | Audit et reconnaissance |
| `DNSSEC` | DNS Security Checker | Diagnostic réseau |
| `SMBENUM` | SMB Enumerator | Audit et reconnaissance |
| `DEFAULTCREDS` | Default Credential Scanner | Audit et reconnaissance |
| `CVELOOKUP` | CVE Lookup | Essentiels |
| `SECNUMCLOUD` | SecNumCloud Audit | Audit et reconnaissance |

### Encodage

| ID | Nom | Pack |
|---|---|---|
| `BASE64` | Base64 Encoder / Decoder | Essentiels |
| `URLENC` | URL Encoder / Decoder | Essentiels |
| `JSON` | JSON Formatter | Essentiels |
| `REGEX` | Regex Tester | Essentiels |
| `DIFF` | Text Diff | Essentiels |
| `TEXTCASE` | Text Case Converter | Essentiels |

### Système

| ID | Nom | Pack |
|---|---|---|
| `CHMOD` | Chmod Calculator | Essentiels |
| `DATETIME` | DateTime Converter | Essentiels |
| `UUID` | UUID Generator | Essentiels |
| `ULID` | ULID Generator | Essentiels |
| `CRONTAB` | Crontab Builder | Essentiels |
| `LOGVIEW` | Log Viewer | Administration |
| `HOSTS` | Hosts File Editor | Administration |
| `SSHCONFIG` | SSH Config Generator | Essentiels |
| `CRONJOB` | Cron Job Manager | Administration |
| `SERVICES` | Service Status Dashboard | Administration |
| `NOTES` | Notes | Noyau (permanent) |
| `DIAGRAM` | Diagram Editor | Administration |
| `CMDLIB` | Command Library | Noyau (permanent) |
| `PRIVLAUNCH` | Privilege Launcher | Audit et reconnaissance |

### Natifs externes

| ID | Nom | Pack |
|---|---|---|
| `WOL` | Wake-on-LAN | Diagnostic réseau |
| `OPENPORTS` | Open Ports | Diagnostic réseau |
| `NETIF` | Network Interfaces | Diagnostic réseau |
| `ROUTES` | Route Table | Diagnostic réseau |
| `DNSBATCH` | DNS Batch Resolver | Diagnostic réseau |
| `WIFI` | WiFi Networks | Diagnostic réseau |

## Infrastructure des outils

`ToolRegistry` est la source de vérité unique pour les métadonnées des outils
intégrés et pour leur fabrique de création. Ajouter un outil intégré revient à
ajouter une entrée de registre et l'implémentation `IToolView` correspondante.

`ToolDescriptor` porte les métadonnées stables :

- `Id` : clé de recherche courte, par exemple `PING`
- `Category` : `Network`, `Security`, `Encoding`, `System` ou `External`
- `CategoryLabelKey` : clé i18n de l'en-tête de catégorie
- `LabelKey` : clé i18n du nom affiché
- `LabelWithArgKey` : clé i18n optionnelle pour les suggestions de la palette de
  commandes comportant un argument cible
- `CommandPrefixes` : alias pour la palette de commandes
- `IsNetworkTool` : indique si un lancement autonome doit demander une cible
- `IconResourceKey` : clé de ressource de géométrie XAML
- `DescriptionKey` : clé de description explicite optionnelle ; lorsqu'elle est
  nulle, l'interface applique la convention `ToolDesc{Id}`
- `Pack` : le `ToolPack` auquel l'outil appartient (`Core`, `Essentials`,
  `NetworkDiagnostics`, `Administration`, `Audit` ou `Recipes`) ; nul pour
  les outils externes détectés, qui n'appartiennent à aucun pack

La colonne Pack des tableaux du catalogue ci-dessus donne le pack de chaque
outil intégré. Un outil dont le pack est désactivé dans les Paramètres, ou
interdit par la politique de la machine, est masqué de toutes les listes et
refusé quand quelque chose tente de l'ouvrir ; le registre garde lui-même le
catalogue complet. Voir [Packs d'outils](FEATURES.md#packs-doutils) côté
utilisateur, [Architecture des outils](ARCHITECTURE.md#architecture-des-outils)
pour le mécanisme et [SECURITY.md](SECURITY.md) pour la politique de machine et
ses limites.

`IToolView` est le contrat d'exécution des vues d'outil. Il expose :

- `Initialize(ToolContext?, LocalizationManager?)`
- `CanClose()`
- `Dispose()`

`ToolsTabPopulationService` construit à la fois l'onglet Tools complet et
l'arborescence Tools de la barre latérale à partir du registre. Il gère les
favoris, les récents, le filtrage par recherche, le regroupement par catégorie,
les cartes d'outil et l'héritage de contexte.

`SidebarToolCategoryViewModel` et `SidebarToolItemViewModel` alimentent
l'arborescence Tools de la barre latérale. La recherche s'appuie sur un texte
recherchable précalculé en minuscules (`name + aliases`), afin que le filtrage
n'alloue pas de mémoire à répétition.

Les icônes utilisent des ressources de géométrie telles que
`Geo.Tool.PortScanner` et des ressources de pinceau par catégorie telles que
`ToolNetworkBrush`. `ToolRegistry.GetGeometryKey()` et
`ToolRegistry.GetCategoryBrushKey()` existent pour les convertisseurs XAML qui
n'ont pas accès à l'injection de dépendances.

Le routage par passerelle se joue au niveau de la vue, et pas uniquement au
niveau du registre. `ToolGatewayConnector` crée un client SSH pour l'exécution
de commandes distantes à travers une passerelle SSH et exige une clé d'hôte de
passerelle épinglée avant de se connecter. Les vues qui exposent actuellement un
sélecteur de route `CmbRouteVia` sont les suivantes :

- `BannerGrabberView`
- `CertInspectorView`
- `DefaultCredentialView`
- `DnsLookupView`
- `DnsSecurityView`
- `FirewallTesterView`
- `HttpHeaderAnalyzerView`
- `NetworkCartographyView`
- `PingToolView`
- `PortScannerView`
- `SecNumCloudAuditView`
- `SmbEnumeratorView`
- `SnmpWalkerView`
- `TcpTracerouteView`
- `TlsAuditView`
- `WhoisLookupView`

Les outils qui lancent un shell distant à travers une passerelle doivent
conserver des délais d'expiration par sonde et une sélection explicite du shell.
Pour les tests `/dev/tcp`, utilisez `timeout ... bash -c ...` afin que les ports
filtrés ne laissent pas de processus shell distants en cours d'exécution une
fois le canal de commande SSH tué.

## Fournisseurs d'outils externes

Les outils tiers détectés à l'exécution s'appuient sur `IExternalToolProvider`,
`ExternalToolProviderService`, `ExternalToolInfo` et `ExternalToolWrapperView`.

Implémentations de fournisseurs actuelles :

- `SysinternalsToolProvider` : recherche les outils Sysinternals tels que
  PsExec, PsInfo, PsList, PsService, PsPing, Tcpvcon, Autorunsc, Sigcheck,
  AccessChk, Handle, ListDLLs, Disk Usage et Whois.
- `NirSoftToolProvider` : recherche les outils NirSoft tels que PingInfoView,
  CurrPorts, NetworkLatencyView, WakeMeOnLan, FastResolver, CountryTraceRoute,
  DNSDataView, NetResView, NetworkInterfacesView, WifiInfoView,
  Wireless Network Watcher, FullEventLogView, TaskSchedulerView, USBDeview,
  BlueScreenView et ProduKey.
- `NanaRunToolProvider` : recherche les outils du projet NanaRun exploitables en
  ligne de commande, aujourd'hui représentés par MinSudo et SynthRdp.

`ExternalToolProviderService.ScanAll()` agrège les fournisseurs et applique les
chemins de recherche configurés par l'utilisateur dans `AppSettings`
(`SysinternalsPath`, `NirSoftPath`, `NanaRunPath`). Les outils détectés sont
enregistrés dans `ToolRegistry` sous forme d'entrées `ToolDescriptor`
dynamiques, avec des identifiants au format :

```text
EXT:PROVIDER:TOOLID
```

`ExternalToolWrapperView` lance l'exécutable détecté, applique les
substitutions `{Host}` et `{Port}` issues de `ToolContext`, capture stdout et
stderr, puis affiche la sortie sous forme de texte ou de données structurées
selon `OutputFormat`. Les outils marqués `RequiresElevation` affichent un
avertissement d'élévation en amont.

Règle de licence : les binaires tiers sont uniquement détectés et encapsulés. Ne
redistribuez pas les outils NirSoft, Sysinternals, NanaRun ou d'autres outils
tiers dans les paquets Heimdall, sauf si leur licence l'autorise explicitement.

## Recettes

Une recette est un fichier texte qui décrit un outil : un programme déjà présent
sur l'ordinateur, les champs que l'utilisateur remplit, les arguments donnés au
programme et la façon d'afficher sa sortie. Importer le fichier ajoute l'outil à
Heimdall, à côté des outils intégrés. La version courte est dans
[FEATURES.md](FEATURES.md#recettes) ; ce que les règles ci-dessous protègent, et
ce qu'elles ne protègent pas, est dans [SECURITY.md](SECURITY.md#recettes).

### Ce qu'est une recette, et ce qu'elle n'est pas

- C'est un seul fichier JSON, en UTF-8 (une marque d'ordre des octets est
  acceptée), nommé `*.heimdall-recipe.json`, de 64 Kio au plus.
- Elle lance un programme déjà présent sur l'ordinateur. Elle ne fournit aucun
  script ni aucun binaire, et rien n'est téléchargé pour elle.
- Elle ne passe jamais par un interpréteur de commandes. Le programme est lancé
  directement, et chaque élément de la liste d'arguments lui parvient comme un
  argument distinct : rien n'est découpé, mis entre guillemets ou joint en
  route.
- Elle n'est ni signée ni relue. L'auteur écrit dans le fichier est affiché comme
  non vérifié partout où l'outil apparaît.
- Elle est aussi dangereuse que le programme qu'elle nomme et les arguments
  qu'elle donne. Les règles ci-dessous restreignent ce qu'une recette peut
  demander ; elles ne rendent pas une recette sûre. L'aperçu d'import est la
  vraie barrière : lisez-le, et n'importez que ce à quoi vous faites confiance.

### Utiliser une recette

**Activer le pack.** Le pack Recettes est désactivé par défaut, sur une
installation neuve comme sur une installation qui existait déjà (une étape
unique, au premier démarrage de cette version, le range parmi les packs
désactivés, et ne le désactive plus jamais une fois que vous l'avez activé). Le
pack compte comme désactivé tant que cette étape n'a pas été exécutée ou que
vous n'avez pas activé l'interrupteur et enregistré : un démarrage où l'étape n'a
pas pu s'exécuter (un fichier de paramètres écrit par une version plus récente
n'est jamais modifié) ne le laisse donc pas activé. L'interrupteur se trouve
dans
`Paramètres > Avancé > Outils et intégrations > Packs d'outils` et porte une
ligne d'avertissement. Quand l'organisation interdit le pack par une politique,
l'interrupteur est sur arrêt et désactivé, et l'import est refusé (voir
[SECURITY.md](SECURITY.md#recettes)).

Un fichier de paramètres importé avec **Importer des paramètres** n'active jamais
le pack de lui-même. Un fichier exporté par une version antérieure le laisse tel
quel. Un fichier exporté par cette version ne l'active que s'il vient d'une
installation où le pack était activé ; la confirmation montre alors une ligne à
part, "Active le pack Recettes", parmi les lignes à vérifier en premier.

**Importer.** Dans `Paramètres > Avancé > Outils et intégrations > Recettes`,
choisissez **Importer une recette...** et sélectionnez le fichier. L'import est
possible quand le pack est désactivé : la recette est enregistrée, et la
confirmation précise qu'elle n'apparaîtra pas dans l'onglet Outils tant que le
pack n'est pas activé. Heimdall alors :

1. refuse le fichier, en listant chaque problème avec sa ligne et sa colonne,
   quand ce n'est pas une recette valide (voir "Ce qui est refusé" plus bas) ;
2. résout le programme et refuse le fichier quand ce programme n'a pas le droit
   de s'exécuter ;
3. affiche un aperçu et attend une confirmation. Rien n'est écrit avant.

**Lire l'aperçu.** Il montre, dans cet ordre : un avertissement disant que la
recette a été écrite par quelqu'un d'autre et ne peut pas être vérifiée ; pour
un remplacement, ce qui change ; le programme qui sera lancé, sous la forme du
chemin complet trouvé sur cet ordinateur, et le texte du programme tel qu'écrit
dans le fichier ; chaque argument, un par ligne entre crochets, exactement comme
écrit dans le fichier, une liste imbriquée étant signalée comme conservée ou
omise en bloc ; le nom, l'auteur (marqué non vérifié), l'identifiant, la version
et la description ; chaque champ avec son type, son caractère obligatoire ou
facultatif (un interrupteur n'est ni l'un ni l'autre, et un champ doté d'une
valeur par défaut dit "toujours envoyé"), sa valeur par défaut, ses
choix, son motif, sa longueur maximale, sa plage (`plage : 1 à 100`,
`minimum : 1` ou `maximum : 100`) et son aide, et les arguments qu'un
interrupteur ajoute quand il est activé et quand il est désactivé ; chaque
traduction de la recette (la langue, le nom, la description, les libellés des
champs et les aides des champs), puisque l'outil ouvert l'affiche à la place du
texte anglais quand l'interface est dans cette langue (le titre de l'onglet dans
la barre d'onglets, les listes d'outils, la recherche et les Paramètres montrent
le nom écrit dans la recette) ; si la recette lit l'hôte et le port de la
session sélectionnée ; la façon dont la sortie est affichée (pour un tableau lu
depuis du CSV, le caractère qui sépare ses cellules), la façon dont elle est
décodée, ce que devient l'erreur standard, les codes de sortie qui comptent
comme un succès et l'icône (une clé d'icône que cette version de Heimdall ne
connaît pas est signalée, et l'icône par défaut est affichée) ; et le délai. Un
caractère de contrôle ou invisible est écrit `\uXXXX`.
Dans le programme, les arguments et les valeurs par défaut, tout caractère hors
de l'ASCII imprimable est écrit `\uXXXX` lui aussi : une lettre qui ressemble à
une lettre ASCII ne peut donc pas passer pour elle.

**Où l'outil apparaît.** Dans l'onglet Outils sous un titre Recettes, dans
l'arborescence Outils de la barre latérale et dans la palette de commandes, avec
la mention "Recette non vérifiée de {auteur}" sur la carte, la ligne et le titre
de l'onglet. Ouvrez-le, remplissez les champs et choisissez **Exécuter** ;
**Arrêter** interrompt une exécution en cours. La sortie s'affiche en texte ou
en tableau (voir `output`). Quand la recette garde l'erreur standard à part,
elle s'affiche sous "Sortie d'erreur". La ligne d'état dit si l'exécution a
réussi, a échoué avec un code de sortie, a dépassé le délai, a été annulée ou n'a
pas pu être lancée.

**Ce qui se passe à chaque exécution.** Avant que quoi que ce soit démarre,
Heimdall vérifie que le pack est activé et que la recette est toujours importée,
qu'une session est sélectionnée quand la recette en a besoin, et chaque valeur
saisie (voir "Ce qui est refusé" plus bas). Il résout ensuite à nouveau le
programme et compare le résultat au chemin enregistré à l'import : quand le
programme correspond désormais à un autre fichier, ou ne se résout plus,
l'exécution est refusée et le message demande d'importer à nouveau la recette. Il
crée pour l'exécution un dossier de travail neuf et vide sous
`Heimdall-recipe-runs` dans votre dossier temporaire (dans un sous-dossier nommé
d'après le dossier de données utilisé, pour que deux instances ne le partagent
jamais), et non dans le dossier de
données (un programme ne démarre donc pas à côté des paramètres de Heimdall), et le supprime ensuite (les restes d'un arrêt brutal
précédent sont supprimés au démarrage). Le programme démarre sans fenêtre, avec
son entrée standard fermée, avec les droits et l'environnement de Heimdall
lui-même, placé dans un objet de travail (job) Windows juste après son démarrage, qui
arrête les processus qu'il a lancés quand l'exécution prend fin (un processus
lancé dans ce tout premier instant peut y échapper) ; quand Windows refuse de créer le job, seule
une exécution arrêtée ou ayant dépassé le délai arrête l'arbre de processus. Au
plus 1 Mio de sortie est conservé par flux (2 Mio en tout quand l'erreur standard
est gardée à part) : le reste est lu puis jeté, et une ligne indique que la
sortie a été coupée. Le plafond compte des octets, pas des caractères : une
sortie en UTF-16 conserve moitié moins de caractères qu'une sortie en UTF-8 de
même taille. L'exécution prend fin au délai de la recette, et aussi quand le
pack est désactivé ou devient interdit.

**Remplacer ou supprimer une recette dont un onglet est ouvert.** Un onglet garde
la recette avec laquelle il a été ouvert. Une fois cette recette remplacée, ou
supprimée puis importée à nouveau, l'onglet affiche dans son en-tête "Cette
recette a été remplacée ou supprimée après l'ouverture de cet onglet. Fermez cet
onglet et ouvrez de nouveau l'outil.", **Exécuter** est désactivé, et une
exécution refuse de démarrer. Ouvrir l'outil ne remet jamais un tel onglet au
premier plan : cela en ouvre un nouveau.

**Remplacer.** Importer un fichier dont l'`id` est déjà importé est un
remplacement. L'aperçu s'intitule "Remplacer la recette" et indique l'ancienne
et la nouvelle version, et si les arguments, les champs et le programme ont
changé. Chacun de l'auteur, du nom, de la description, des traductions, de la
sortie et de son encodage (et, pour un tableau lu depuis du CSV, de son
séparateur), du mode de conservation de l'erreur standard, des codes de sortie
de succès, du délai et de l'icône est aussi dit sur une ligne à part,
"inchangé" ou "modifié", en comparant les recettes analysées et non les octets.
La ligne des champs compare chaque propriété de chaque champ, plages, aides et
les deux listes d'un interrupteur comprises, et celle des traductions compare
les libellés et les aides des champs.

**Exporter.** Le bouton d'export d'une recette, dans la liste des Paramètres,
écrit une copie du fichier exactement tel qu'il a été importé. Il refuse quand
le fichier enregistré ne correspond plus à ce qui a été importé.

**Supprimer.** Le bouton de suppression demande d'abord confirmation. L'outil
disparaît, et tout ce qui est enregistré et l'utilise devient indisponible.

**Où les recettes sont stockées.** Un fichier par recette dans le dossier
`recipes` du dossier de données (nommé d'après l'identifiant), plus un index qui
garde, pour chaque recette, le SHA-256 des octets importés et le chemin du
programme résolu à l'import. Une recette dont le fichier a été modifié, ou qui a
été déposée à la main dans le dossier, n'est pas chargée. Au plus 200 recettes
peuvent être importées. L'export des paramètres ne transporte pas les recettes.

**Les dossiers d'où les recettes peuvent lancer des programmes.** Une recette
peut toujours lancer un programme depuis le dossier Windows, Program Files et
Program Files (x86). Pour en autoriser un autre, utilisez **Ajouter un
dossier...** dans la même section Recettes des Paramètres, enregistrez les
paramètres puis importez à nouveau la recette. Un dossier doit être un chemin
complet sur une lettre de lecteur (`D:\Outils`) et non la racine d'un lecteur
(`D:\`), de 240 caractères au plus, sans chemin réseau, sans chemin de
périphérique, sans partie `.` ou `..`, sans `%`, sans caractère générique et
sans partie qui se termine par un point ou une espace. Au plus 32 dossiers sont
conservés. Ajouter un dossier qui est le dossier Windows, Program Files ou
Program Files (x86), qui se trouve sous l'un d'eux ou qui en contient un est
accepté, et les Paramètres affichent un avis : les programmes situés sous ces
dossiers restent vérifiés. Un dossier qui mène à la racine d'un lecteur ou à un
emplacement réseau (un lecteur réseau connecté, un lien vers un partage) est
refusé, quelle que soit la façon dont il est écrit. Un fichier de paramètres qui
porte cette liste est signalé comme sensible dans l'aperçu d'import.

### Écrire une recette

Une recette s'écrit à la main, comme un fichier texte, à partir du format
ci-dessous. Heimdall n'a pas d'éditeur pour cela. JSON n'a pas de commentaires
(un commentaire ou une virgule finale est une erreur de syntaxe) : l'exemple est
donc suivi d'un tableau qui l'explique.

```json
{
  "schemaVersion": 1,
  "id": "example.name-lookup",
  "version": "1.0.0",
  "author": "Example",
  "name": "Name lookup",
  "description": "Looks up a DNS record for a name, and can ask a DNS server of your choice.",
  "translations": {
    "fr": {
      "name": "Recherche de nom",
      "description": "Recherche un enregistrement DNS pour un nom, et peut interroger le serveur DNS de votre choix.",
      "fieldLabels": {
        "query": "Nom à rechercher",
        "record": "Type d'enregistrement",
        "server": "Serveur DNS (facultatif)",
        "details": "Afficher les détails"
      },
      "fieldHints": {
        "server": "Laissez vide pour utiliser le serveur par défaut."
      }
    }
  },
  "program": "nslookup.exe",
  "arguments": [
    "-type={in:record}",
    "{in:details}",
    "{in:query}",
    ["{in:server}"]
  ],
  "inputs": [
    { "name": "query", "type": "hostname", "label": "Name to look up", "required": true },
    { "name": "record", "type": "choice", "label": "Record type", "choices": ["A", "AAAA", "MX", "TXT"], "default": "A" },
    { "name": "server", "type": "hostname", "label": "DNS server (optional)", "hint": "Leave empty to use the default server." },
    { "name": "details", "type": "bool", "label": "Show details", "default": false, "whenTrue": ["-debug"] }
  ],
  "output": "text",
  "outputEncoding": "oem",
  "stderr": "merge",
  "successExitCodes": [0],
  "timeoutSeconds": 20,
  "icon": "search"
}
```

| Partie de l'exemple | Ce qu'elle fait |
|---|---|
| `schemaVersion`, `id`, `version`, `author` | La version du format, l'identité `auteur.nom` que Heimdall ne renomme jamais, une version sémantique, et l'auteur, affiché et toujours marqué non vérifié |
| `name`, `description` | Textes en anglais, obligatoires |
| `translations` | Traductions facultatives du nom, de la description, des libellés et des aides des champs, rangées par balise de langue |
| `program` | Un simple nom de fichier : Heimdall trouve lui-même `nslookup.exe`, dans Windows, Program Files ou un dossier autorisé |
| `"-type={in:record}"` | Un argument fait de texte fixe et d'un champ. La valeur de `record` est choisie dans une liste fermée : elle ne peut jamais être lue comme une option |
| `"{in:details}"` | Un interrupteur placé seul comme élément entier. Activé, il ajoute les éléments de son `whenTrue` (ici `-debug`) ; désactivé, il ajoute ceux de son `whenFalse`, ici aucun puisqu'il n'en a pas |
| `"{in:query}"` | Un champ obligatoire qui remplit un argument entier. Une valeur saisie qui commence par `-`, `/` ou `@` est refusée ici |
| `["{in:server}"]` | Une liste imbriquée. `server` est facultatif : toute la liste est omise quand le champ est vide |
| `inputs` | Les champs, dans l'ordre où ils sont affichés. Chaque champ est utilisé par un argument ; `hint` est un texte d'aide affiché sous le champ |
| `output`, `outputEncoding`, `stderr` | La sortie est affichée en texte, décodée avec la page de code OEM de la console, l'erreur standard y étant mêlée |
| `successExitCodes`, `timeoutSeconds` | Les codes de sortie qui comptent comme un succès, et le nombre de secondes au bout duquel l'exécution est arrêtée |
| `icon` | Une clé d'icône : un mot de minuscules, de chiffres et de tirets isolés. `search` est l'une des clés pour lesquelles cette version a un pictogramme |

#### Propriétés de premier niveau

Les noms de propriété sont sensibles à la casse. Une propriété que le format ne
définit pas est refusée, comme une propriété qui apparaît deux fois dans le même
objet.

| Propriété | Type | Obligatoire | Règle |
|---|---|---|---|
| `schemaVersion` | entier | oui | `1` est la seule version que cette version lit. Un nombre plus grand est refusé, et le message nomme la version prise en charge |
| `id` | chaîne | oui | `auteur.nom` : deux parties séparées par un point, chacune de 1 à 32 caractères parmi les minuscules, les chiffres et le tiret, ne commençant ni ne finissant par un tiret. Il identifie la recette (remplacement, favoris, entrées enregistrées) |
| `version` | chaîne | oui | Une version sémantique comme `1.2.3`, avec des parties de préversion et de build facultatives ; 64 caractères au plus. Affichage et aperçu de remplacement seulement |
| `author` | chaîne | oui | 64 caractères au plus. Affiché, toujours comme non vérifié |
| `name` | chaîne | oui | 64 caractères au plus |
| `description` | chaîne | oui | 500 caractères au plus |
| `translations` | objet | non | Traductions, voir plus bas. 16 langues au plus |
| `program` | chaîne | oui | Un simple nom de fichier ou un chemin complet avec une lettre de lecteur ; 260 caractères au plus. Voir "Quel programme peut être lancé" |
| `arguments` | tableau | non | La liste d'arguments, voir plus bas. 64 éléments au plus en tout |
| `inputs` | tableau | non | Les champs, voir plus bas. 16 au plus |
| `output` | chaîne | non | `text` (par défaut), `csv` ou `json`. Les deux derniers s'affichent en tableau |
| `outputEncoding` | chaîne | non | Comment les octets de la sortie deviennent du texte : `utf8` (par défaut), `oem` (la page de code OEM de la console), `ansi` (la page de code ANSI du système) ou `utf16le` (UTF-16 petit-boutiste). Voir "Encodages de la sortie" plus bas |
| `csvSeparator` | chaîne | non | Le caractère entre deux cellules d'un tableau lu depuis du CSV : `comma` (par défaut), `semicolon` ou `tab`. Refusé sauf si `output` vaut `csv`, même quand il dit `comma` |
| `stderr` | chaîne | non | `separate` (par défaut) : gardée à part et affichée comme sortie d'erreur. `merge` : mêlée à la sortie. `merge` est refusé avec `output` `csv` ou `json`, car un tableau ne peut pas se lire dans deux flux mêlés |
| `successExitCodes` | tableau d'entiers | non | `[0]` par défaut. De 1 à 16 entrées, sans doublon. Un code de sortie hors de la liste est signalé comme un échec |
| `timeoutSeconds` | entier | non | 60 par défaut, de 1 à 600. Le programme est arrêté quand ce délai est écoulé |
| `icon` | chaîne | non | La clé de l'icône de l'outil : une lettre minuscule, puis des minuscules, des chiffres et des tirets, 32 caractères au plus, sans tiret final et sans deux tirets de suite. `tool` (par défaut), `terminal`, `network`, `search`, `document`, `shield`, `clock` et `server` ont un pictogramme dans cette version ; toute autre clé de cette forme est acceptée et affiche l'icône par défaut |

Chaque texte affiché (auteur, nom, description, libellés, aides) ne doit pas
être vide et ne doit contenir ni caractère de contrôle, ni caractère de mise en
forme invisible (comme le forçage de l'écriture de droite à gauche), ni
séparateur de ligne ou de paragraphe, ni substitut isolé.

#### Encodages de la sortie

`outputEncoding` dit comment les octets qu'un programme écrit deviennent du
texte. L'erreur standard est décodée avec le même encodage que la sortie
standard. Au plus 1 Mio est conservé par flux, et ce plafond compte des octets,
pas des caractères.

| Valeur | Décode comme | À utiliser pour |
|---|---|---|
| `utf8` | UTF-8. Une marque d'ordre des octets initiale est retirée | Les programmes qui écrivent en UTF-8 |
| `oem` | La page de code OEM de la console | La plupart des outils console de Windows |
| `ansi` | La page de code ANSI du système, lue auprès de Windows (jamais l'encodage par défaut de l'environnement d'exécution) | Les programmes qui écrivent en page de code ANSI quand leur sortie est redirigée. Mesuré sous Windows 11 : `certutil`, `sort` et `findstr` le font |
| `utf16le` | UTF-16 petit-boutiste. Une marque d'ordre des octets initiale est retirée | Les programmes qui écrivent en UTF-16, avec ou sans marque. Mesuré sous Windows 11 : `sfc` et `wsl` l'écrivent sans marque |

Une marque d'ordre des octets ne sert jamais à deviner un encodage, et l'UTF-16
gros-boutiste n'est pas pris en charge. Pour `utf8` et `utf16le`, exactement un
U+FEFF initial est retiré de chaque flux ; un deuxième, ou un autre plus loin,
est conservé comme du texte. Quand la page de code de `oem` ou de `ansi` ne peut
pas être lue, l'UTF-8 est utilisé. La marque est retirée dès que le flux est
réellement décodé en UTF-8 ou en UTF-16, quel que soit le nom donné par la
recette : `utf8`, `utf16le`, `oem` ou `ansi` sur une machine dont la page de
code est 65001, et l'UTF-8 employé quand une page de code ne peut pas être lue.
Sous toute autre page de code, les octets d'une marque sont du texte.

#### Champs

Chaque entrée de `inputs` est un objet doté de ces propriétés :

| Propriété | Type | Obligatoire | Règle |
|---|---|---|---|
| `name` | chaîne | oui | Une lettre, puis des lettres, des chiffres ou des tirets bas ; 32 caractères au plus ; unique dans la recette. C'est ce que désigne `{in:name}` |
| `type` | chaîne | oui | `string`, `int`, `number`, `bool`, `hostname`, `ipaddress`, `path` ou `choice` |
| `label` | chaîne | oui | 64 caractères au plus |
| `required` | booléen | non | `false` par défaut. Refusé sur un champ `bool`, quelle que soit sa valeur, car un interrupteur n'est jamais vide. Refusé avec un `default`, quelle que soit sa valeur (`true` ou `false`) : un champ qui a une valeur par défaut n'est jamais laissé vide, car l'effacer fait utiliser la valeur par défaut, si bien que `required` ne dirait rien |
| `default` | chaîne, nombre ou booléen | non | Une chaîne (tout type sauf `bool`), un nombre (`int` et `number` seulement, écrit comme un nombre JSON ou comme une chaîne, ce qui revient au même) ou `true` ou `false` (`bool` seulement). 512 caractères au plus. Soumise aux mêmes règles de texte fixe qu'un élément d'argument, valide pour le type de son champ, et comprise entre `min` et `max` quand il y en a |
| `min`, `max` | nombre ou chaîne | `int` et `number` seulement | La plus petite et la plus grande valeur acceptées, chacune écrite comme une valeur par défaut et chacune facultative. Une borne d'`int` est un entier sur 32 bits et une borne de `number` un nombre décimal, dans les formes strictes ci-dessous. `min` supérieur à `max` est refusé. Une valeur hors de la plage est refusée à l'exécution, et une valeur par défaut hors de la plage à l'import. Un `number` est comparé exactement, comme un décimal : le signe, puis les chiffres entiers, puis les chiffres de la fraction, les zéros finaux de la fraction ne comptant pas (donc `1` et `1.0` sont égaux, et avec un `max` de `1` la valeur `1.0000000000000001` est refusée). Aucun arrondi en double n'intervient jamais, car le programme reçoit le texte tel qu'il a été saisi |
| `hint` | chaîne | non | Un texte d'aide affiché sous le champ et lu par les technologies d'assistance. 200 caractères au plus, sans caractère de contrôle, sans caractère de mise en forme invisible et sans saut de ligne |
| `pattern` | chaîne | `string` seulement | Une expression régulière que la valeur entière doit satisfaire ; 256 caractères au plus. Une valeur pour laquelle la comparaison dure plus de 250 ms est refusée |
| `maxLength` | entier | `string` seulement | De 1 à 1024. 256 en l'absence de valeur |
| `choices` | tableau de chaînes | `choice` seulement, alors obligatoire | De 1 à 64 valeurs, chacune de 128 caractères au plus, sans doublon |
| `whenTrue` | tableau de chaînes | `bool` seulement | Les éléments que l'interrupteur ajoute quand il est activé. Ils suivent les règles des éléments d'argument |
| `whenFalse` | tableau de chaînes | `bool` seulement | Les éléments que l'interrupteur ajoute quand il est désactivé, selon les mêmes règles |

Un champ `bool` a besoin de `whenTrue`, de `whenFalse` ou des deux, et une liste
écrite ne doit pas être vide. Une propriété qui n'est pas permise pour le type
de son champ est refusée. Un champ `bool` doit figurer dans `arguments` comme
élément entier, et tout autre champ doit être utilisé par un argument : dans un
élément, dans une liste imbriquée ou dans un élément de `whenTrue` ou de
`whenFalse` d'un interrupteur. Un champ que rien n'utilise est refusé.

Ce que chaque type accepte à l'exécution :

| Type | Accepte |
|---|---|
| `string` | Tout texte dans la limite de `maxLength` et conforme à `pattern` s'il y en a un |
| `int` | Un entier sur 32 bits écrit `0` ou `-?[1-9][0-9]*` : `0`, ou des chiffres sans zéro initial avec un signe moins facultatif. Ni signe plus, ni exposant, ni espace, ni zéro avec un signe moins (`-0` est refusé) |
| `number` | Un nombre décimal fini écrit `-?(0\|[1-9][0-9]*)(\.[0-9]+)?` : la forme d'un entier, puis un point et au moins un chiffre, facultatifs. Ni signe plus, ni zéro initial, ni exposant, ni point seul, ni zéro avec un signe moins (`-0`, `-0.0` et `-0.000` sont refusés ; `-0.5` est accepté), 64 caractères au plus. Écrivez une valeur par défaut ou une borne sous forme de chaîne (`"0.00001"`) : un outil qui remet en forme le JSON peut réécrire le nombre `0.00001` en `1e-5`, ce qui est refusé |
| `bool` | Activé ou désactivé. Une valeur vide est désactivée |
| `hostname` | Un nom d'hôte valide, sans espace avant ni après |
| `ipaddress` | Une adresse IPv4 ou IPv6 (chiffres hexadécimaux, deux-points et queue pointée facultative), sans identifiant de zone (`%`), sans crochets et sans espace avant ni après |
| `path` | Un chemin local. Refusés : un chemin réseau, un chemin de périphérique (`\\?\`, `\\.\`), un chemin qui commence par un seul séparateur sans lettre de lecteur, un deux-points ailleurs que juste après la lettre de lecteur, une partie qui se termine par un point ou une espace, et une partie qui est un nom de périphérique DOS (`CON`, `PRN`, `AUX`, `NUL`, `COM1` à `COM9`, `LPT1` à `LPT9`) |
| `choice` | L'une des valeurs de `choices`, comparée exactement |

#### Arguments, marqueurs et listes imbriquées

`arguments` est un tableau. Chaque entrée est soit une chaîne (un élément), soit
un tableau de chaînes (une liste imbriquée d'éléments). Un élément est
exactement un argument du programme : Heimdall ne met rien entre guillemets, ne
découpe rien et ne joint rien, si bien qu'une valeur qui contient des espaces ou
des guillemets parvient quand même au programme comme un seul argument.

Dans un élément, un marqueur est remplacé par une valeur :

| Écrit | Remplacé par |
|---|---|
| `{in:name}` | La valeur du champ `name`, saisie par l'utilisateur ou prise dans sa valeur par défaut |
| `{Host}` | L'hôte de la session sélectionnée |
| `{Port}` | Le port de la session sélectionnée |
| `{{` et `}}` | Une `{` littérale et une `}` littérale |

La syntaxe est fermée et sensible à la casse : tout autre emploi d'une accolade,
un marqueur qui ne nomme aucun champ et un élément vide sont refusés à l'import.
Aucune variable d'environnement n'est jamais développée, et un `%` dans le texte
fixe est refusé.

Un champ est *facultatif* quand ce n'est pas un interrupteur, qu'il n'est pas
obligatoire et qu'il n'a pas de valeur par défaut non vide. Un champ facultatif
ne peut pas servir dans un élément isolé, car la liste d'arguments deviendrait
ambiguë quand il est vide : placez l'élément dans une liste imbriquée. Une liste
imbriquée est omise en bloc dès qu'un champ facultatif qu'elle utilise est vide.
Un champ obligatoire, ou doté d'une valeur par défaut, peut servir partout. Un
champ doté d'une valeur par défaut n'est jamais laissé vide : quand
l'utilisateur l'efface, la valeur par défaut est utilisée.

Les éléments d'un interrupteur (`whenTrue` et `whenFalse`) n'ont pas de liste
imbriquée autour d'eux qui puisse être omise : un champ utilisé là doit être
obligatoire ou doté d'une valeur par défaut. Un champ facultatif sans valeur par
défaut y est refusé. Un champ obligatoire, ou `{Host}` ou `{Port}`, qui n'est
utilisé que dans les éléments de la branche d'un interrupteur qui n'est pas
active (par exemple dans `whenFalse` alors que l'interrupteur est activé) reste
obligatoire à l'exécution : sa valeur, ou la session, doit être fournie quel que
soit l'état de l'interrupteur.

Un champ `bool` se place dans `arguments` comme un élément isolé entier
(`"{in:details}"`) et se développe en éléments de son `whenTrue` quand il est
activé et en éléments de son `whenFalse` quand il est désactivé, en rien quand la
liste de cet état n'a pas été écrite. Il ne peut ni se mêler à d'autre texte
dans un élément, ni se placer dans une liste imbriquée.

64 éléments au plus en tout, chacun de 512 caractères au plus : un pour chaque
élément isolé et chaque membre de liste imbriquée et, pour chaque endroit où un
interrupteur est placé, le nombre d'éléments de sa liste la plus longue
(`whenTrue` ou `whenFalse`). Les éléments des listes ne sont pas comptés pour
eux-mêmes. Le nombre donné dans l'erreur est le pire cas et peut dépasser le
nombre d'éléments écrits dans le fichier.

#### Valeurs de session

`{Host}` et `{Port}` sont les seules valeurs qu'une recette peut lire dans une
session, et elles viennent de la session sélectionnée dans l'arborescence. Elles
peuvent figurer dans un élément, dans une liste imbriquée et dans les éléments
d'un interrupteur (`whenTrue` et `whenFalse`).
Quand une recette utilise l'un des deux et qu'aucune session n'est sélectionnée,
l'outil le dit et ne s'exécute pas : sélectionnez une session, puis rouvrez
l'outil. L'hôte passe par la validation de nom d'hôte la plus stricte et le port
doit être un numéro de port valide.

#### Traductions

`translations` associe une balise de langue (comme `fr` ou `fr-CA` : une
première partie de 2 à 8 lettres, puis des parties de 1 à 8 lettres ou chiffres,
35 caractères au plus) à un objet doté des propriétés facultatives `name`,
`description`, `fieldLabels` et `fieldHints`, les deux dernières étant des tables
qui associent un nom de champ à un libellé traduit et à une aide traduite.
Heimdall utilise l'entrée dont la balise est la langue de l'interface, sinon la
première entrée de même langue principale (la première balise dans l'ordre
alphabétique, sans tenir compte de la casse : `fr-BE` vient avant `fr-CA`, quel
que soit l'ordre dans le fichier), sinon les textes anglais de la recette. L'outil ouvert montre le nom, la description, les libellés et les aides
des champs traduits (un champ sans aide traduite montre le `hint` écrit dans le
champ) ; le titre de l'onglet dans la barre d'onglets, les listes d'outils, la
recherche et les Paramètres montrent le nom écrit dans la recette. Une
traduction d'un champ qui n'existe pas est refusée, dans l'une ou l'autre
table. Deux balises qui ne diffèrent que par la casse (`fr` et `FR`) sont
refusées. Les noms `labels` et `fields`, qu'une ébauche du format employait, sont
des propriétés inconnues.

#### Quel programme peut être lancé

`program` est un simple nom de fichier (`nslookup`, `ping.exe` ; `.exe` est
ajouté quand le nom n'a pas de point) ou un chemin complet qui commence par une
lettre de lecteur. Heimdall le résout lui-même en chemin complet ; l'ordre de
recherche du système d'exploitation n'est jamais utilisé. Il doit satisfaire
tout ceci :

- le fichier se termine par `.exe` (ni `.cmd`, ni `.bat`, ni `.com`, ni `.scr`) ;
- il existe et, une fois tous les liens et jonctions suivis, son emplacement
  final est sous le dossier Windows, Program Files, Program Files (x86) ou un
  dossier autorisé dans les Paramètres. Un lien qui mène en dehors est refusé ;
- sous le dossier Windows, Program Files ou Program Files (x86), ni le fichier
  ni aucun dossier au-dessus (jusqu'à cette racine) ne doit permettre à un
  utilisateur qui n'est pas administrateur de le modifier, par ses permissions
  ou parce qu'il en est propriétaire (un propriétaire autre que le système, les
  administrateurs ou le service d'installation), et les permissions et le
  propriétaire doivent être lisibles. Une entrée OWNER RIGHTS n'est pas prise en
  compte : un propriétaire non approuvé est refusé quoi qu'elle dise. La
  confiance accordée à un dossier autorisé dans les Paramètres dépend de
  l'endroit où se trouve le programme, pas de celui du dossier : un programme
  situé hors de Windows et de Program Files, sous un tel dossier, est approuvé
  en entier et n'est pas vérifié de cette façon, alors qu'un programme sous
  Windows ou Program Files est toujours vérifié, même quand un dossier autorisé
  le couvre ;
- un simple nom n'est cherché que dans les entrées de la variable `PATH` qui sont
  elles-mêmes sous ces dossiers, dans l'ordre de `PATH` ; le premier fichier
  trouvé décide. Le dossier courant et le dossier de Heimdall ne sont jamais
  parcourus ;
- ce n'est pas un interpréteur de commandes ni de scripts (voir la liste
  ci-dessous) ;
- le nom de fichier d'origine inscrit dans la ressource de version du fichier
  n'est pas non plus sur cette liste.

Le texte du programme ne peut contenir ni `%` ni caractère de contrôle, et ne
peut pas être un chemin relatif ou réseau.

La liste des noms refusés est un garde-fou contre une erreur courante, pas une
frontière (voir [SECURITY.md](SECURITY.md#recettes)). Elle contient : `cmd`,
`powershell`, `pwsh`, `bash`, `sh`, `zsh`, `wsl`, `cscript`, `wscript`, `mshta`,
`python`, `pythonw`, `py`, `node`, `perl`, `ruby`, `php`, `java`, `javaw`,
`msiexec`, `rundll32`, `regsvr32`, `regsvcs`, `regasm`, `installutil`,
`msbuild`, `certutil`, `bitsadmin`, `wmic`, `forfiles`, `schtasks`, `sc`, `reg`,
`net`, `net1`, `curl`, `ssh`, `scp`, `sftp`, `ftp`, `tftp`, `git`, `tar`,
`robocopy`, `xcopy`, `expand`, `extrac32`, `makecab`, `hh`, `control`,
`explorer`, `conhost`, `cmstp`, `odbcconf`, `pcalua`, `scriptrunner`, `at`,
`taskkill`, `msedge`, `chrome`, `firefox`, `brave`, `opera`,
`SyncAppvPublishingServer`, `wt`, `tttracer`, `pcwrun`, `esentutl`, `replace`,
`mmc`, `msdt`, `runas`, `dotnet`, `csc`, `vbc`, `jsc`, `ie4uinit` et
`presentationhost`, plus les cibles d'interpréteur que le reste de Heimdall refuse déjà
(comme les extensions de script `.bat`, `.cmd`, `.ps1`, `.vbs`, `.js`, `.wsf` et
`.hta`).

#### Version du schéma

`schemaVersion` vaut `1`, et la version 1 est figée : tout fichier de version 1
valide dans la publication qui l'a figée continue de se charger, avec le même
sens, dans chaque publication suivante. Toute addition au format incrémente la
version : un fichier écrit pour un format plus récent n'est donc jamais compris
à moitié. Une version supérieure à celle que cette version lit est refusée, avec
un message qui donne les deux numéros et demande de mettre Heimdall à jour. La
liste des clés d'icône pour lesquelles cette version a un pictogramme ne fait pas
partie du format : toute clé de la bonne forme est valide, et une clé qu'une
version ne connaît pas affiche l'icône par défaut.

### Ce qui est refusé

**Dans le fichier, à l'import.** Heimdall signale tous les problèmes qu'il
trouve (jusqu'à 50), chacun avec sa ligne et sa colonne quand elles sont
connues.

- Le fichier est vide, plus grand que 64 Kio, n'est pas du JSON ou de l'UTF-8
  valide, ou est imbriqué sur plus de 8 niveaux.
- Une propriété est inconnue ou apparaît deux fois ; une propriété obligatoire
  manque ; une valeur n'a pas le bon type ; un texte, une liste ou un nombre est
  hors de ses limites.
- L'`id`, la `version`, un nom de champ, une balise de langue ou la clé d'icône
  est mal formé ; deux champs portent le même nom ; deux balises de langue ne
  diffèrent que par la casse ; un code de sortie est listé deux fois ; une
  traduction nomme un champ qui n'existe pas.
- Une propriété est employée sur un type qui ne la permet pas (`required` sur un
  `bool` compris), un `choice` n'a pas de liste ou a une valeur par défaut hors de
  cette liste, un `bool` n'a ni `whenTrue` ni `whenFalse` ou a une liste vide, un
  motif ne compile pas, une valeur par défaut n'est pas valide pour son type, un
  nombre n'est pas écrit dans sa forme stricte, un `min` ou un `max` n'est pas
  valide ou le `min` est supérieur au `max`, une valeur par défaut est hors de
  cette plage.
- Un champ est obligatoire et a une valeur par défaut ; un champ n'est utilisé
  par aucun argument, ou un interrupteur n'est jamais placé comme élément entier ;
  un champ facultatif sans valeur par défaut est utilisé dans les éléments d'un
  interrupteur.
- L'erreur standard est mêlée à une sortie affichée en tableau (`csv` ou `json`) ;
  `csvSeparator` est écrit alors que la sortie n'est pas `csv`.
- Un marqueur est inconnu ou mal formé, ne nomme aucun champ, ou un champ
  facultatif se trouve seul dans un élément (placez-le dans une liste
  imbriquée) ; un interrupteur est mêlé à d'autre texte.
- Le texte fixe (le programme, un élément, un élément de `whenTrue` ou de
  `whenFalse`, une valeur par défaut, un choix) contient deux séparateurs de suite (une double barre
  oblique inverse, une double barre oblique, ou une barre oblique inverse à côté
  d'une barre oblique, dans un sens ou dans l'autre : un chemin réseau), `://`
  (une URL), le préfixe de chemin système (un séparateur, deux points
  d'interrogation et un séparateur), un `%` (une variable d'environnement), un
  caractère qui ressemble à un caractère ordinaire (un guillemet typographique,
  un tiret long, une lettre pleine chasse, une espace insécable, ou tout
  caractère que la page de codes ANSI de la machine transforme en caractère
  ASCII), ou commence par `@` (un fichier de réponses). L'entrée en cause est
  nommée. La règle des caractères qui ressemblent à d'autres suit la page de
  codes ANSI de la machine (celle dans laquelle un programme lit sa ligne de
  commande) : certaines majuscules accentuées et certaines ligatures peuvent
  donc être refusées dans un argument sur certaines machines, et acceptées sur
  d'autres.
- Un choix ou une valeur par défaut serait refusé à chaque exécution par la règle
  que rencontre une valeur saisie (voir plus bas) : il contient un caractère de
  contrôle ou qui ressemble à un autre, il commence par `-`, `/`, `@`, une barre
  oblique inverse ou une espace à l'endroit où son champ ouvre un argument, ou il
  forme un séparateur doublé avec le texte voisin. Le fichier est refusé à
  l'import. Gardez le caractère dans le texte fixe de
  l'élément et ne listez que les valeurs nues : écrivez `"-type={in:record}"`
  avec les choix `A` et `MX`, et non `"{in:record}"` avec les choix `-type=A` et
  `-type=MX`.

**Dans le programme, à l'import.** Le programme n'est pas un `.exe`, n'est ni un
simple nom ni un chemin avec lettre de lecteur, est introuvable, se trouve en
dehors de Windows, de Program Files et des dossiers autorisés, est un lien qui
mène en dehors, ou est un interpréteur de commandes ou de scripts, ou un
exécutable relais (d'après son nom de fichier ou le nom d'origine de sa ressource
de version). Le message nomme le programme et, quand la cause est le dossier,
dit d'ajouter le dossier dans les Paramètres, d'enregistrer, puis d'importer à
nouveau.

**Une valeur saisie, à chaque exécution.** Un champ obligatoire vide ; un nombre
hors du `min` et du `max` de son champ (le message nomme les bornes) ; une valeur
avec un caractère de contrôle ou invisible, ou qui ressemble à un autre ; une
valeur qui commence par `-`, `/`, `@`, une barre oblique inverse ou une espace à
l'endroit où son marqueur ouvre l'argument (un `-` initial est la façon dont une
valeur est lue comme une option, un `@` initial comme un fichier de réponses) ; une valeur qui ne convient pas à son type (voir le tableau
ci-dessus) ; une valeur trop longue, hors de ses choix ou qui ne satisfait pas
son motif ; une valeur qui, jointe au texte fixe voisin, formerait un séparateur
doublé (un chemin réseau ou le début d'une URL) ; et tout argument final qui
contient deux séparateurs de suite (sous l'une ou l'autre forme et en toute
combinaison), le préfixe de chemin système ou `://`, quelle que soit son origine.
Le champ affiche la raison.

**Le programme a changé, à chaque exécution.** Quand le programme ne se résout
plus au chemin enregistré à l'import (un autre fichier répond désormais à ce
nom, ou il a été déplacé, supprimé ou n'est plus autorisé), l'exécution ne
démarre pas. Importez à nouveau la recette pour accepter le nouvel emplacement.

### Fichiers sources

- `src/Heimdall.Core/Recipes/` : le format (`RecipeFormat`, `RecipeLimits`,
  `RecipeIcons`), l'analyseur et le validateur stricts, la règle des valeurs,
  l'expanseur, le résolveur de programme, la liste des programmes refusés et
  l'exécuteur.
- `tests/Heimdall.Core.Tests/Recipes/FormatLock/v1/` : les échantillons figés de
  la version 1, acceptés et refusés, avec le texte auquel chacun doit encore
  s'analyser et les erreurs que chacun doit encore donner. Un échantillon peut
  être ajouté, avec sa ligne dans le manifeste, jamais modifié, supprimé ni
  renommé.
- `src/Heimdall.App/Services/RecipeStore.cs` : stockage et chargement.
- `src/Heimdall.App/Services/RecipeImportService.cs` et
  `RecipeImportPreview.cs` : import, remplacement et texte de l'aperçu.
- `src/Heimdall.App/Services/RecipeToolRegistrar.cs` : enregistrement des
  recettes importées comme outils.

## Moteur d'audit SecNumCloud

`SecNumCloudAuditEngine` orchestre l'outil `SECNUMCLOUD`. Il est aligné sur
quatre chapitres orientés SecNumCloud :

- Réseau
- Cryptographie
- Contrôle d'accès
- Exploitation

Le moteur exécute actuellement 15 contrôles couvrant la découverte, l'exposition
réseau, TLS, SSH, SMB, SNMP, les en-têtes HTTP, les enregistrements DNS, les
identifiants par défaut, la correspondance de bannières avec les CVE et la
posture d'exploitation.

La progression est exposée par des évènements :

- `PhaseProgress`
- `StatusChanged`
- `CheckCompleted`

Exports :

- `HtmlReportGenerator` produit un rapport HTML autonome.
- `CsvEvidenceExporter` exporte les lignes de preuve d'audit.
- `DrawIoExporter`, dans `Heimdall.Core.Discovery`, produit des diagrammes de
  topologie Draw.io à partir des données de découverte et de cartographie.

La localisation est injectée dans `SecNumCloudAuditEngine` via un délégué
`Func<string, string>`. Gardez les chaînes de sortie de l'audit localisables et
ne codez pas en dur de texte destiné aux utilisateurs dans le moteur.

## Éditeur de diagrammes

L'outil `DIAGRAM` embarque l'éditeur draw.io dans une surface WebView2.
L'éditeur est vendoré sous `src/Heimdall.App/Assets/drawio/` et n'atteint jamais
le réseau : il est chargé avec `offline=1&stealth=1`, et le fichier `index.html`
vendoré porte une Content-Security-Policy qui maintient toutes les requêtes sur
l'hôte virtuel local `heimdall-drawio.local`.

### Trois entrées

- **Depuis la liste des outils**, avec une toile vide, ou par Fichier > Nouveau
  depuis un modèle. Les modèles sont livrés sous `Assets/diagram-templates/`.
- **Depuis la cartographie réseau**, par le bouton Éditer le diagramme. Le scan
  est converti en document draw.io et remis à l'éditeur comme contenu non
  enregistré. Aucun fichier ne lui correspond, donc le premier enregistrement
  demande où le placer.
- **Depuis un diagramme récent**, sous Fichier > Récents.

### Un scan est dessiné comme une topologie

`DrawIoExporter` transforme un `NetworkScanSnapshot` en un dessin qui dit ce qui
parle à quoi, et pas seulement ce qui existe :

- Un couloir par rôle classifié, plus un pour les hôtes sans port ouvert.
- Un noeud de segment par VLAN détecté, ou un seul pour le sous-réseau scanné
  lorsque aucun VLAN n'a été détecté. Un segment affiche sa passerelle.
  L'appartenance et les passerelles viennent de `VlanInfo`, qui porte déjà les
  deux.
- Une arête de chaque hôte vers son segment. Un hôte qui est lui-même la
  passerelle est relie dans l'autre sens, depuis le segment, afin qu'il se lise
  comme le centre.
- Des formes qui suivent le role : un cylindre pour une base de données, un
  hexagone pour un équipement de routage, un cube pour un hyperviseur. Ce sont
  des formes intégrées a mxGraph, jamais des stencils, car le bundle vendoré est
  élagué et un stencil manquant s'affiche en boite vide.
  `DrawIoExporterTopologyTests` échoue si un style en nomme un autre.

### Les noeuds d'hôte ouvrent des sessions

Un hôte dont les ports ouverts offrent une session que Heimdall sait ouvrir est
écrit comme un `UserObject` portant un lien tel que
`heimdall://session/RDP/10.0.0.5:3389`. La session retenue est décidée par
`SessionProtocolChoice`, la même décision que celle du menu contextuel de la
cartographie.

Sélectionner un tel noeud et choisir Ouvrir la session dans le menu contextuel
de la toile connecte. Le mode edition de draw.io ne suit le lien d'une cellule
sur aucun geste de souris, donc Heimdall donne son entrée propre a l'action
plutôt que de compter sur un clic.

Le format du lien est `SessionLaunchRequest`, qui l'écrit et le relit. Tout ce
qu'un lien contient est non fiable, car un fichier .drawio peut venir de
n'importe ou et le lien est une instruction pour atteindre une machine : seul un
protocole d'une liste fixe est accepte, l'hôte doit être une adresse ou un nom
d'hôte simple, et le port doit être dans la plage. Une demande valide est
ensuite annoncée à l'utilisateur, protocole et destination, avant toute
connexion.

La session est ouverte par `ToolContext.OpenSessionAction`, câblé par
`MainViewModel` vers `ConnectAdHocSessionAsync`. Aucun profil n'est enregistré.

### Le menu de cartographie garde un hôte, ou l'atteint

Un clic droit sur une ligne de la Cartographie réseau propose les deux mêmes choses
au sujet de l'hôte trouvé. Ouvrir la session connecte sans rien enregistrer, via
`ToolContext.OpenSessionAction`. Ajouter aux sessions le garde, via
`ToolContext.AddServerAction`, qui ouvre la boîte d'ajout de serveur habituelle
préremplie avec l'adresse, le nom que le scan a résolu et la session que ses ports
ouverts appellent. Rien n'est écrit tant que la boîte n'est pas validée.

Les deux entrées prennent le protocole dans `SessionProtocolChoice` : elles ne
peuvent donc contredire ni l'une l'autre, ni les liens du diagramme. Un hôte qui
n'offre aucune session mérite quand même d'être enregistré, donc Ajouter aux
sessions retombe sur SSH et son port par défaut et laisse la boîte dire autrement,
tandis qu'Ouvrir la session n'est tout simplement pas proposée.

Ajouter aux sessions demandait auparavant au shell un outil nommé `__ADD_SERVER__`,
qu'aucun registre ne connaissait ; ouvrir un identifiant d'outil inconnu ne lève
rien, cela affiche un onglet portant le texte `Tool: <id>`.
`ToolRouteReachabilityGuardTests` lit désormais tout identifiant d'outil écrit en
dur sur un site d'appel et refuse celui que le registre ne sait pas construire.

### Fusionner un scan ultérieur

Fichier > Fusionner un scan lit un scan fraîchement exporté dans le diagramme à
l'écran au lieu de le remplacer. `DiagramScanMerge` applique trois règles :

1. Une cellule présente dans les deux documents conserve sa géométrie et prend
   le libellé, le style et le lien du scan. La mise en page appartient a
   l'utilisateur, les données au scan.
2. Une cellule dessinée par l'utilisateur n'est jamais touchee. Les cellules de
   Heimdall se reconnaissent à leur préfixe d'identifiant ; tout le reste
   appartient à l'utilisateur.
3. Une cellule Heimdall que le scan ne rapporte plus est conservée et grisée,
   car un hôte disparu est un constat, pas une erreur à effacer.

Cela fonctionne parce que les identifiants de cellule dérivent de l'adresse de
l'hôte et non d'un compteur, si bien que le même hôte garde le même identifiant
d'un export a l'autre.

### Enregistrement, brouillons et reprise

L'en-tête affiche le nom du fichier courant, suivi de `(modifié)` tant que
l'éditeur porte des modifications absentes du disque. Enregistrer écrit dans le
fichier courant ; Enregistrer sous en demande toujours un nouveau. Ctrl+S dans
l'éditeur fait la même chose que le bouton Enregistrer. Fermer l'onglet, ou
remplacer le document, demande quoi faire du travail non enregistré.

Chaque modification signalée par l'éditeur est aussi ecrite comme brouillon sous
le répertoire de données de l'application, indexée par une empreinte du chemin
du document. Un brouillon est supprime dès que le document atteint son vrai
fichier ; un brouillon encore présent à l'ouverture signifie donc que la session
précédente ne s'est pas terminée, et l'éditeur le repropose. L'état lui-même vit
dans `DiagramDocumentState`, qui répond à ce qu'un enregistrement doit faire
ensuite et s'il reste du travail non enregistré.

### Langue, theme et WebView2

La page hôte est ouverte sous la forme
`heimdall-host.html?lang=<en|fr>&theme=<dark|light>` et transmet les deux à
draw.io, afin que l'éditeur suive Heimdall plutôt que la langue du système
d'exploitation. Les raccourcis navigateur sont désactivés : F5 rechargerait la
page hôte et perdrait le diagramme. Sans runtime WebView2, l'outil affiche un
message de repli au lieu d'un éditeur.

Les ressources pèsent environ 44 Mo pour 2300 fichiers et sont exclues des
builds Debug (`Heimdall.App.csproj`) ; l'outil signale alors qu'il n'est
disponible qu'en build Release.

### Mettre à jour l'éditeur vendoré

Exécuter `scripts/Vendor-DrawIo.ps1` sur une copie amont de `src/main/webapp`.
Le script copie le sous-ensemble livré, réapplique les deux modifications
Heimdall (le hook `heimdallDrawioApp` dans `js/bootstrap.js`, la
Content-Security-Policy dans `index.html`) et réécrit les lignes de version de
`VENDORED.md` et des deux fichiers `THIRD-PARTY-NOTICES`.
`DrawioAssetGuardTests` vérifie ensuite que ces manifestes correspondent à la
version que le bundle declare. Faire un test de fumée complet de l'outil avant
de commiter : les gardes lisent le source, pas le comportement.

## Command Library et TwinShell

L'outil `CMDLIB` intègre la bibliothèque de commandes TwinShell au sein de
Heimdall.

Composants principaux :

- `TwinShellBootstrapper` : enregistre dans le conteneur d'injection de
  dépendances de Heimdall la persistance TwinShell, les dépôts, les services, le
  pont de localisation, le pont de paramètres et les services de synchronisation
  Git.
- `CommandLibraryView` : vue d'outil WPF.
- `CommandLibraryViewModel` et ses classes partielles : filtrage, actions de
  commande, historique, favoris, génération et état de l'interface.
- Projets TwinShell : `TwinShell.Core`, `TwinShell.Persistence` et
  `TwinShell.Infrastructure`.

La persistance repose sur SQLite via `TwinShellDbContext`. Le chemin de la base
de données partagée se trouve dans les données d'application locales de
l'utilisateur, dans un répertoire `TwinShell`.

Les données de départ se trouvent dans `data/seed/actions/`. Les fichiers dont
le nom commence par `_` sont ignorés ; le jeu de données actuel contient 527
fichiers JSON d'actions. Leur texte est un pivot anglais avec les autres langues
sous `translations` : le français porte tous les champs, l'espagnol porte le
titre et la description seulement (les notes et les descriptions d'exemples
retombent sur l'anglais, un exemple étant identifié par sa commande exacte), et
`CommandLibraryContentLanguageParityTests` garde chaque bloc en phase avec le
pivot. Leurs catégories sont des clés ASCII que la vue traduit
en libellés localisés. Au démarrage, le répertoire des données de départ est
haché ; quand l'empreinte diffère de celle enregistrée dans la table
`SeedState`, les actions système sont rafraîchies en une seule transaction.

Le pack facultatif d'allègement pour poste de travail se trouve dans
`data/packs/`, au format d'import, et n'est pas chargé au démarrage. Son texte
est en anglais avec des blocs `fr` et `es`, lus par la porte d'import comme les
données de départ.

Principaux parcours utilisateur :

- recherche approximative avec filtres par plateforme, catégorie et niveau de
  risque
- génération de commandes paramétrées
- favoris et historique des commandes
- import/export au format JSON compatible TwinShell
- synchronisation Git via `IGitSyncService`
- envoi vers le terminal via `ToolContext.SendCommandAction`
- runbooks : étapes ordonnées de la bibliothèque ou commandes littérales,
  stockées comme lots de commandes TwinShell et exécutées une étape à la fois
  dans le terminal de la session

Les actions système issues des données de départ sont protégées : les actions
d'édition et de suppression sont masquées pour les commandes qui n'ont pas été
créées par l'utilisateur, et la fusion à l'import ignore les actions système.

## Où trouver quoi

- `src/Heimdall.App/Services/ToolRegistry.cs` : registre des outils intégrés et
  enregistrement dynamique des outils externes.
- `src/Heimdall.Core/Models/ToolDescriptor.cs` : record de métadonnées d'outil.
- `src/Heimdall.Core/Models/IToolView.cs` : contrat des vues d'outil.
- `src/Heimdall.App/Services/ToolsTabPopulationService.cs` : peuplement de
  l'onglet Tools complet et de la section Tools de la barre latérale.
- `src/Heimdall.App/ViewModels/SidebarToolsViewModels.cs` : view-models de
  l'arborescence Tools de la barre latérale.
- `src/Heimdall.App/Services/ToolGatewayConnector.cs` : utilitaire de routage
  par passerelle SSH pour les outils.
- `src/Heimdall.Core/Configuration/ExternalToolProvider.cs` : modèle et
  interface des fournisseurs externes.
- `src/Heimdall.Core/Configuration/SysinternalsToolProvider.cs` : fournisseur
  Sysinternals.
- `src/Heimdall.Core/Configuration/NirSoftToolProvider.cs` : fournisseur
  NirSoft.
- `src/Heimdall.Core/Configuration/NanaRunToolProvider.cs` : fournisseur
  NanaRun.
- `src/Heimdall.App/Services/ExternalToolProviderService.cs` : agrégation des
  fournisseurs et orchestration des scans.
- `src/Heimdall.App/Views/Tools/ExternalToolWrapperView.xaml.cs` : hôte
  générique pour les outils externes.
- `src/Heimdall.App/Services/SecNumCloudAuditEngine.cs` : orchestration de
  l'audit SecNumCloud.
- `src/Heimdall.App/Services/HtmlReportGenerator.cs` : rapport d'audit HTML.
- `src/Heimdall.App/Services/CsvEvidenceExporter.cs` : export CSV des preuves.
- `src/Heimdall.Core/Discovery/DrawIoExporter.cs` : export de topologie Draw.io.
- `src/Heimdall.App/Services/TwinShellBootstrapper.cs` : injection de
  dépendances TwinShell et initialisation des données de départ.
- `src/Heimdall.App/Views/Tools/CommandLibraryView.xaml.cs` : vue de l'outil
  Command Library.
- `src/Heimdall.App/ViewModels/CommandLibraryViewModel*.cs` : tranches de
  view-model de Command Library ; `.Runbooks.cs` porte le panneau Runbooks et
  ses opérations de stockage, `.RunbookRun.cs` lance une exécution et la relie
  au terminal.
- `src/Heimdall.App/ViewModels/CommandLibrary/RunbookRunViewModel.cs` : une
  exécution de runbook, étape par étape, avec ses états et ses enregistrements
  d'historique.
- `src/Heimdall.App/ViewModels/CommandLibrary/RunbookVariables.cs` : la
  substitution `{{nom}}` dans les valeurs des étapes d'un runbook.
- `src/Heimdall.App/ViewModels/Dialogs/RunbookDialogViewModel.cs` et
  `src/Heimdall.App/Views/Dialogs/RunbookDialog.xaml` : l'éditeur de runbook.
- `src/TwinShell.Core/Models/CommandBatch.cs` et `BatchCommandItem.cs` : le
  modèle de runbook, stocké par `BatchService` et `BatchRepository`.
- `data/seed/actions/` : actions de départ de la bibliothèque de commandes.
- `data/packs/` : packs facultatifs de la bibliothèque de commandes, importés par
  l'opérateur.
