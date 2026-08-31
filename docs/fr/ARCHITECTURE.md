<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->

*Ce document est la version française de [../ARCHITECTURE.md](../ARCHITECTURE.md). / This document is the French version.*

# Architecture

Heimdall est une application WPF .NET 10 organisée en solution multi-projets, avec des frontières de dépendances strictes. Elle prend en charge les types de connexion RDP, SSH, SFTP, FTP, VNC, Telnet, Citrix et Local Shell, avec environ 5 491 clés i18n par langue (EN/FR), 58 outils sysops intégrés dotés d'une aide contextuelle, une navigation croisée entre outils, et 10 529 tests automatisés (dont 10 404 bloquants en CI, tous verts, les 125 restants dans les lignes informatives CIUnstable et RequiresDesktop). Le moniteur de santé interroge en parallèle (Task.WhenAll), les importateurs XML sont durcis contre XXE, et tous les Debug.WriteLine ont été remplacés par FileLogger. Design System conforme WCAG AA avec 45 design tokens (typographie de 11 px minimum, espacements, rayons d'angle, opacités, tailles d'icônes, famille de police), micro-animations, FocusIndicatorBrush pour l'accessibilité clavier, système d'icônes unifié à deux niveaux (géométries vectorielles + MDL2), code couleur par catégorie d'outil, i18n déclarative via l'extension de balisage `{loc:Translate}` et ServerDialog à divulgation progressive.

## Structure de la solution

```
Heimdall.slnx (14 projects)
├── src/
│   ├── Heimdall.Core          net10.0         Models, session diagnostics, security, config, state machine, i18n, network scanner, utilities
│   ├── Heimdall.Ssh           net10.0         SSH engine, tunnels, Pageant, TOFU, failure classifier, health monitor
│   ├── Heimdall.Rdp           net10.0-windows RDP + Citrix engine (ActiveX, StoreBrowse), credential autofill
│   ├── Heimdall.Sftp          net10.0         SFTP/FTP browser (SSH.NET + FluentFTP), remote file editing
│   ├── Heimdall.Terminal      net10.0-windows Terminal sessions (pipe mode, ConPTY, Telnet)
│   ├── TwinShell.Core         net10.0         Terminal emulator core abstractions
│   ├── TwinShell.Persistence  net10.0         Terminal persistence primitives
│   ├── TwinShell.Infrastructure net10.0       Terminal infrastructure services
│   └── Heimdall.App           net10.0-windows WPF application (MVVM, views, themes, DI)
│       ├── Views: MainWindow, SessionPaneControl, SplitContainerControl,
│       │          EmbeddedRdpView, EmbeddedSshView, EmbeddedSftpView,
│       │          EmbeddedCitrixView, EmbeddedVncView, FloatingSessionWindow
│       ├── Views/Tools: 58 built-in sysops tools (IToolView interface)
│       └── Services: ConnectionService (.Rdp/.Ssh/.Sftp/.Ftp/.Vnc/.Telnet/.Citrix/.Local/.Tunnel),
│                     SplitService, SessionWindowService, EmbeddedSessionManager, ToolRegistry,
│                     TaskSchedulerService, MacroService, EphemeralFileServer, FileShareService,
│                     X11ServerManager, WebSocketVncProxy, KeyboardShortcutService,
│                     ContextMenuFactory, SessionTabContextMenuFactory, ToolsTabPopulationService,
│                     SessionHealthMonitor (inventory TCP reachability probes), HealthReasonLocalizer
└── tests/
    ├── Heimdall.Core.Tests    State machine, HMAC integrity, input validation, PIN manager, config manager tests
    ├── Heimdall.Ssh.Tests     SSH engine tests (failure classifier, preflight, TOFU, Pageant, Plink)
    ├── Heimdall.App.Tests     SplitService, SessionDiagnostic, NotesStorage, theming wrapper/bridge, Migration, EphemeralFileServer, tool coherence
    ├── Heimdall.Rdp.Tests     RDP credential autofill and broker-selection tests
    └── Heimdall.App.UiTests   Desktop UIAutomation smoke and accessibility coverage
```

## Graphe de dépendances

```
                    +-----------------+
                    |  Heimdall.App   |  WPF, MVVM, DI container
                    +--------+--------+
                             |
          +------------------+------------------+
          |         |        |        |         |
     +----v---+ +--v---+ +--v--+ +---v----+ +--v-------+
     |  Core  | |  Ssh | |  Rdp| |  Sftp  | | Terminal |
     +--------+ +--+---+ +--+--+ +---+----+ +----+-----+
                   |         |        |           |
                   +----+----+    +---+---+       |
                        |         | Core  |       |
                   +----v----+    | + Ssh |  +----v----+
                   |  Core   |    +-------+  |  Core   |
                   +---------+               +---------+
```

- **Heimdall.Core** n'a aucune dépendance interne vers un autre projet (uniquement des paquets NuGet : CommunityToolkit.Mvvm, ProtectedData, abstractions DI)
- **Heimdall.Ssh** dépend de Core + SSH.NET ; il embarque `ServerHealthMonitor` pour l'interrogation multiplexée CPU/RAM/disque sur une session SSH active (voir section 21). A ne pas confondre avec `Heimdall.App.Services.SessionHealthMonitor` (voir section 21b), qui sonde l'accessibilité de l'inventaire en TCP brut **avant / sans** se connecter
- **Heimdall.Rdp** dépend de Core (utilise WPF + WinForms pour l'hébergement ActiveX ; inclut l'intégration Citrix StoreBrowse)
- **Heimdall.Sftp** dépend de Core + Ssh (réutilise la fabrique de connexions SSH.NET). `SftpSessionBundle`, dans ConnectionService, regroupe SftpClient + SshClient pour les opérations sudo. `FtpBrowser` implémente `IRemoteBrowser` pour les connexions FTP
- **Heimdall.Terminal** dépend de Core (utilise les API Win32 pour ConPTY, le mode pipe et Telnet en TCP brut)
- **Heimdall.App** référence les bibliothèques Heimdall et porte la racine de composition DI

## Décisions de conception majeures

### 1. Double stratégie SSH.NET + Plink

**Problème** : SSH.NET 2025.1.0 ne prend pas en charge l'agent Pageant nativement. De nombreux environnements d'entreprise utilisent des clés PPK chargées exclusivement dans Pageant.

**Solution** : une approche à deux volets.

1. **SSH.NET (voie principale)** : authentification programmatique par mot de passe, fichier de clé privée ou keyboard-interactive. Un `PageantClient` maison dialogue avec Pageant via la mémoire partagée Win32 (`CreateFileMapping` + `WM_COPYDATA`) et enveloppe les clés en `IPrivateKeySource` pour SSH.NET.

2. **Repli Plink** : lorsque `AuthPreflightChecker.RequiresPageantFallback()` détecte que la seule méthode d'authentification viable est Pageant, `PlinkTunnelRunner` prend en charge les tunnels et `PipeModeSession` la session SSH interactive. Plink dialogue nativement avec Pageant, mais Heimdall reste maître de la confiance des clés d'hôte. `PlinkHostKeyDecider` accepte une empreinte stockée, ou utilise l'`IPlinkHostKeyProbe` injectable plus `IHostKeyVerifier` pour résoudre la confiance de premier usage avant le lancement. Si aucune empreinte approuvée par Heimdall ne peut être résolue, le chemin échoue avec `SshFailureCode.HostKeyUnavailable` au lieu de retomber sur le cache de PuTTY/Plink.

**Corrections de l'intégration Pageant** (3 bogues critiques résolus) :
- `AGENT_COPYDATA_ID` doit valoir `0x804e50ba` - toute autre valeur pousse Pageant à ignorer silencieusement la requête
- Les algorithmes RSA-SHA2 (`rsa-sha2-256`, `rsa-sha2-512`) doivent être enregistrés sur la `ConnectionInfo` pour les serveurs modernes qui rejettent l'ancien `ssh-rsa`
- `PageantHostAlgorithm.Sign()` doit renvoyer le blob de signature SSH complet (longueur du nom d'algorithme + nom d'algorithme + longueur de signature + signature), et non les octets bruts de la signature - SSH.NET attend le blob au format fil. `PageantClient.SignData()` renvoie déjà ce blob tel quel.

### 2. Mode pipe pour les terminaux SSH (et NON ConPTY)

**Problème** : ConPTY convertit l'entrée VT en événements clavier console Windows, puis la reconvertit en VT. Cette double conversion casse les flèches, les touches de fonction et les autres séquences d'échappement lorsque le flux passe par plink.

**Solution** : `PipeModeSession` redirige stdin/stdout en pipes bruts, sans pseudo-console. Combiné à l'option `-t` de plink (qui force l'allocation d'un PTY distant même quand stdin n'est pas un terminal), les séquences VT transitent sans modification :

```
xterm.js  -->  stdin pipe  -->  plink -t  -->  remote PTY  -->  bash
                                                    |
xterm.js  <--  stdout pipe <--  plink     <---------+
```

ConPTY (`ConPtySession`) est conservé uniquement pour les scénarios de shell local.

### 2b. Stratégie d'élévation du shell local

**Problème** : le `ServiceHelper.StartService` de gsudo plante lorsque des gestionnaires de privilèges (AdminByRequest, CyberArk, BeyondTrust) interceptent l'invite UAC et invalident les handles de processus en cours d'élévation.

**Solution** : une énumération `ElevationMode` configurable avec chaîne de repli :

| Mode | Mécanisme | Terminal intégré | Compatible AdminByRequest |
|------|-----------|-------------------|---------------------------|
| `None` | Pas d'élévation | Oui | Sans objet |
| `Auto` | gsudo `--direct` → repli en fenêtre externe | Oui (gsudo) / Non (repli) | Oui |
| `Gsudo` | gsudo `--direct` uniquement | Oui | Partiel |
| `Runas` | `ShellExecute` avec le verbe `runas` | Non (fenêtre externe) | Oui |

Décisions de conception clés :
- L'option `--direct` contourne le mécanisme de service/cache de gsudo, ce qui évite le plantage de `ServiceHelper.StartService`
- Le mode `Auto` tente gsudo en premier (meilleure UX : terminal intégré), attrape `InvalidOperationException` et réessaie en fenêtre externe
- Le mode `Runas` utilise `Process.Start` avec `Verb="runas"` et `UseShellExecute=true` - impossible de rediriger stdin/stdout (limitation Windows), donc le terminal s'ouvre dans une fenêtre séparée
- L'annulation UAC (erreur Win32 1223) est interceptée et signalée par un message compréhensible
- Rétrocompatibilité : l'ancien `LocalShellElevated=true` est mappé sur `Auto` via la propriété calculée `EffectiveElevationMode`

### 3. WebView2 + xterm.js pour le rendu du terminal

**Problème** : WPF n'a pas de contrôle terminal natif. Microsoft.Terminal.Control exige ConPTY, qui casse SSH (voir ci-dessus).

**Solution** : WebView2 héberge xterm.js, le moteur de rendu terminal de référence du marché :

- Données binaires sûres grâce à un encodage base64 entre C# et JavaScript
- `PostWebMessageAsString` (C# vers JS) et `WebMessageReceived` (JS vers C#)
- xterm.js prend en charge tout le rendu VT100/xterm : couleurs, curseur, historique, souris, sélection
- Fréquence de clignotement du curseur CSS fixée à 1,2 s pour éviter le conflit de focus WPF/WebView2

#### Modèle de sécurité WebView2

La page du terminal (`terminal.html`) est chargée via `NavigateToString` (aucune origine externe). Durcissement :

- **CSP** : `default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'none'; frame-src 'none'` - tous les scripts sont inlinés, aucun chargement de ressource externe n'est autorisé
- **Blocage de navigation** : le gestionnaire `NavigationStarting` annule toute navigation en dehors des origines `about:` ou `data:`
- **Validation de l'origine des messages** : `OnWebMessageReceived` rejette les messages provenant de sources inattendues
- **Ouverture d'URL** : seules les URI `http://` et `https://` sont transmises à `Process.Start` avec `UseShellExecute`

### 4. ActiveX RDP et protocole de vidange de layout

**Problème** : le `WindowsFormsHost` de WPF souffre d'un problème d'"airspace" : la surface de rendu n'est pas correctement liée au HWND visible si le layout n'a pas été vidangé avant `Connect()`. De plus, le HWND Win32 se dessine toujours au-dessus du contenu WPF de la même fenêtre - `Panel.ZIndex` n'a aucun effet.

**Solution** : vidange de layout obligatoire avant chaque `Connect()` :

```
UpdateLayout() -> DoEvents() -> Dispatcher.Invoke(Render) -> EnsureHandle -> Connect()
```

**Règle de surimpression airspace** : toute interface WPF qui doit s'afficher au-dessus d'une surface `WindowsFormsHost` (RDP, VNC) DOIT utiliser un `Popup` WPF. Un Popup crée son propre HWND de premier niveau, que l'OS compose au-dessus de la surface ActiveX embarquée. La palette de commandes utilise ce motif - à l'origine c'était une surimpression `Grid` avec `Panel.ZIndex="9999"`, invisible au-dessus des sessions RDP.

Garde-fous supplémentaires :
- Les mises à jour de résolution sont bloquées après `OnConnected` (évite le code de déconnexion 4360). Le délai est `AppSettings.RdpResizeEnableDelayMs`, valeur par défaut 10000 ms, qu'une valeur par profil peut remplacer
- La libération COM suit un ordre strict : replier la visibilité, détacher de l'arbre, déconnecter, détacher le puits d'événements, libérer - ne PAS appeler `Marshal.ReleaseComObject` (laisser AxHost gérer le nettoyage du RCW)
- Reconnexion automatique à nombre de tentatives borné (`MaxReconnectAttempts = 20`), annulable via le puits d'événements COM
- Décodeur de motif de déconnexion : `GetDisconnectReasonKey()` associe 26 codes MsTscAx à des clés i18n, et
  `GetExtendedDisconnectReasonKey()` associe par-dessus les familles de motifs étendus
- La priorité des messages de déconnexion vit en UN SEUL endroit, `RdpActiveXHost.ResolveDisconnectReasonKey(reason, extendedReason)`, appelé aussi bien par le diagnostic affiché à l'écran que par l'événement de session persisté. Le motif étendu l'emporte, sauf qu'un rejet générique d'identifiants n'écrase pas un code primaire désignant un état de compte précis (verrouillé, expiré, mot de passe expiré). Les deux surfaces composaient auparavant les mêmes décodeurs dans des ordres opposés et pouvaient nommer des causes différentes pour une même déconnexion. Cette priorité n'est délibérément PAS dérivée de la table de sévérité : la sévérité répond à la question de savoir si un code impose une action sur les identifiants et pilote le veto de reconnexion automatique, ce qui est une décision distincte

**Optimisations de performance** (atténuation du démarrage à froid) :
- **Préchauffage COM** : un thread STA d'arrière-plan crée puis libère un `RdpActiveXHost` jetable au démarrage, ce qui force le chargement en mémoire de mstscax.dll et de 22 dépendances statiques (environ 400 ms gagnés sur la première connexion)
- **Pré-résolution DNS** : `Dns.GetHostEntryAsync()` en fire-and-forget dès la sélection d'un serveur dans l'arborescence
- **Keep-alive TCP** : `KeepAliveIntervalMs = 60_000` pour détecter les coupures réseau
- **Indicateurs d'expérience par serveur** : masque de bits `AdvancedSettings9.PerformanceFlags` (fond d'écran, thèmes, animations, glisser, ombre du curseur, composition) configurable dans la boîte de dialogue Serveur
- **Suppression de la sonde UDP** : `BandwidthDetection = false` + `NetworkConnectionType = 6` (LAN) arrête la sonde UDP qui expire derrière les pare-feu. Cela ne force pas TCP : aucun réglage côté client ne le peut, seule la stratégie machine `fClientDisableUDP` le fait

### 4b. Résolution du profil RDP et surcharge de mode ponctuelle

**Problème** : deux sources de confusion liées dans la chaîne de lancement RDP. (1) Le chemin externe (mstsc) retombait sur `AppSettings.DefaultResolutionWidth/Height` et ignorait les valeurs par serveur `RdpResolutionMode`, `RdpFixedWidth/Height`, le multi-écran et le smart sizing - le profil configuré par l'utilisateur ne s'appliquait donc pas, silencieusement. (2) Le mode est une propriété par serveur (`RdpMode = "Embedded" | "External"`), sans possibilité de le basculer pour un unique lancement de triage sans éditer le profil.

**Solution** : centraliser les décisions de résolution par serveur dans `RdpProfileResolver.ResolveResolution(server, settings)` (dans `Heimdall.App/Services/`), qui renvoie un enregistrement `RdpResolvedResolution` `(Width, Height, MultiMonitor, SmartSizing, SelectedMonitorIndices)`. Il reproduit la règle de priorité existante de `RdpProfileResolver.ResolveColorDepth` (serveur > réglages > repli). Les deux chemins, embarqué (via `RdpDisplayResolver` et les setters de propriétés COM) et externe (via `RdpFileGenerator.RdpFileOptions`), consomment le même enregistrement résolu, ce qui élimine la divergence silencieuse.

Pour les sessions embarquées, `RdpDisplayResolver` résout `RdpResolutionMode.FitWindow` avec `smartSizing: true` (`reason: explicit-fit-window-scaled`), de sorte que Fit Window met à l'échelle vers la zone hôte au lieu de déclencher les barres de défilement non clientes de MsTscAx. Fixed et Multimon conservent un rendu non smart pour les scénarios pixel-perfect ou multi-écran.

Le mode Auto possède un contrat embarqué/externe explicite. Auto embarqué fait référence : taille pilotée par la zone d'affichage, Smart Sizing activé. Le mode Auto du `.rdp` externe reflète ce contrat en écrivant Smart Sizing activé, Multimon désactivé, `screen mode id:i:1` (fenêtré) et des dimensions déterministes de la zone de travail principale alignées sur un multiple de 4 via `RdpDisplayHelper` ; cela maintient `RdpFileGenerator` dans un rôle d'écrivain tandis que `RdpProfileResolver` porte la politique (`ae0dd70`).

**Surcharge de mode ponctuelle** : l'énumération `RdpModeOverride` (`UseProfile` / `ForceEmbedded` / `ForceExternal`) circule en paramètre optionnel de `IConnectionService.ConnectRdpAsync` → `IProtocolHandler.ConnectAsync` → `RdpHandler.ConnectAsync` → `ResolveEffectiveMode`. La surcharge ne modifie jamais `server.RdpMode` ; elle ne vit que dans la pile d'appels. L'entrée de menu contextuel serveur *Se connecter avec...* propose `Connect (embedded)` et `Connect (external mstsc)` pour les profils RDP uniquement. Lorsqu'une surcharge est active, le titre de l'onglet de session résultant reçoit un suffixe `(forced embedded)` / `(forced external)`.

**Règle RD Gateway** : `ServerProfileDto.RdpGateway` est distinct de la chaîne de tunnels de passerelle SSH. La prise en charge de la passerelle par MsTscAx embarqué n'est volontairement pas exposée pour l'instant ; tout `RdpGateway` non vide force le mode de lancement effectif à External, y compris les surcharges `ForceEmbedded` et les imports `.rdp` contenant `gatewayhostname`. La boîte de dialogue Serveur désactive le mode Embedded et affiche une explication localisée, afin que les réglages de passerelle ne soient pas ignorés en silence.

**Suivi des onglets mstsc externes** : les lancements RDP externes renvoient désormais un `ExternalRdpSessionResult` au lieu d'une session nulle. `SessionCoordinator` crée un onglet léger pour chaque lancement de mstsc, y compris pour les profils enregistrés dont le `RdpMode` vaut déjà `External`. `ExternalRdpSessionModel` expose des états de statut sans mot de passe (`Launched`, `AutofillSearching`, `AutofillFilled`, `AutofillTimedOut`, `AutofillFailed`, `Closed`), mis à jour par `RdpHandler`, tandis que le vrai processus mstsc reste non géré par la fermeture de l'onglet.

**Points de couture pour les tests** :

- `IMonitorEnumerator` (implémentation `WinFormsMonitorEnumerator`) encapsule `Screen.AllScreens` pour que le ViewModel de ServerDialog soit testable unitairement sans écran interactif. Utilisé par le sélecteur *Ecrans sélectionnés* par profil introduit pour `RdpResolutionMode.Multimon`.
- `IRdpExternalClientLauncher` abstrait le lancement de mstsc afin que le gestionnaire External soit testable sans démarrer un vrai processus.
- `RdpSelectedMonitorValidator` valide les index d'écran persistés face au nombre d'écrans à l'exécution et écarte silencieusement les entrées hors plage (repli sur "tous les écrans" si la liste résultante est vide).

La validation de topologie Multimon au moment de la connexion vit dans `RdpDisplayResolver.ValidateMultimon`. Le helper est pur et vérifie les `RdpDisplayCapabilities` réelles avant que les réglages ne soient appliqués à l'hôte ActiveX : Multimon sur un hôte mono-écran, ou tout index d'écran sélectionné supérieur ou égal à `MonitorCount`, est ramené au mode mono-écran. Un `selectedmonitors` vide reste "utiliser tous les écrans". Le repli est non bloquant, journalisé en Warning et acheminé vers la surface de texte de reconnexion/statut existante (`EmbeddedRdpView.StatusTextBlock`) plutôt que vers une fenêtre modale (`2e9b938`).

**Motif de propriété COM** : `selectedmonitors` est une propriété RDP documentée, mais ce n'est pas un membre de premier ordre de `IMsRdpClientNonScriptable5`. `RdpActiveXHost.TrySetSelectedMonitors` écrit via `MsRdpClientShell.SetRdpProperty("selectedmonitors", "0,2")` (chemin documenté) et ne retombe sur `IMsRdpClientNonScriptable5.SelectedMonitors` qu'en cas d'échec. Réutiliser ce motif `SetRdpProperty + repli non-scriptable` chaque fois qu'une propriété RDP documentée n'a pas de liaison C# de premier ordre sur l'interface COM.

### 4c. Aides UX de la barre d'outils RDP et épinglage du letterbox

**Indicateur de mode de résolution** (`RdpResolutionModeIndicator`, `Heimdall.App/Views/EmbeddedRdp/`) : un helper statique sans état qui résout le mode de résolution effectif en direct à partir de `(profileMode, manualWidth, manualHeight, profileFixedWidth, profileFixedHeight)`. Une surcharge manuelle par session (définie via le menu Résolution de la barre d'outils) l'emporte toujours et est rapportée comme `Fixed (W,H)` ; sinon le mode de profil persisté est renvoyé. Le même helper produit le glyphe Segoe MDL2 (5 points de code distincts - Auto/FitWindow/SmartSizing/Fixed/Multimon), la clé i18n par mode (en réutilisant `ServerDialogResolutionMode*` pour éviter la duplication) ainsi que les chaînes d'en-tête et d'infobulle formatées. `EmbeddedRdpView.GetEffectiveResolutionState()` expose le résultat en `internal` pour que `SessionTabContextMenuFactory.AppendActiveModeHeader` puisse reproduire le même en-tête `Active mode: <mode>` dans le sous-menu Résolution du clic droit - les deux menus restent synchronisés sans dupliquer la logique.

**Politique de visibilité des redirections** (`RdpRedirectionVisibilityPolicy`, même dossier) : des helpers purs derrière la pastille d'expansion `+N`. `IsIndicatorVisible(isActive, alwaysExpanded, sessionExpandedOverride)` décide de la visibilité par icône, `ShouldShowExpandBadge(disabledCount, alwaysExpanded, sessionExpandedOverride)` décide de la visibilité du badge. `EmbeddedRdpView` conserve un `_redirectionExpandedOverride` local à la session que le clic sur le badge inverse. L'option `AppSettings.RdpRedirectionIndicatorsAlwaysExpanded` (`false` par défaut) préserve l'ancien comportement "tout afficher" pour ceux qui le préfèrent ; ce réglage n'est pas encore exposé dans l'interface des Paramètres - les utilisateurs éditent directement `settings.json`.

**Epinglage du HWND letterbox** (`RdpRegionFrameLayout`, même dossier) : lorsque le letterbox est actif, `HostWidth` / `HostHeight` sont désormais fixés à la taille du cadre au lieu de `double.NaN`, de sorte que le `WindowsFormsHost` WPF se voit allouer exactement le rectangle de la région RDP. Sans cet épinglage, le HostVisual de l'hôte pouvait déborder du cadre et le gris par défaut du HWND Win32 transparaissait dans les bandes du letterbox. Avec l'épinglage, les bandes retombent sur le `SurfaceContainer.Background` parent (soit `SurfaceBrush`), en accord avec le reste de la surface. Le cas sans letterbox conserve `HostWidth = double.NaN` (Stretch) - aucun changement sur le chemin plein écran connecté.

**Politique d'action de la surimpression de déconnexion** (`RdpDisconnectActionPolicy`) : `ShouldOfferEditProfile(disconnectCode)` renvoie toujours `true` (éditer le profil est rarement nuisible et coûteux à manquer). L'ancien comportement (masquer le bouton sur les codes sans remédiation) est désormais cantonné à `ResolvePrimaryAction`, qui utilise toujours le helper privé `IsProfileRemediationCode` (codes 2055/2308/2311/2825/3080/3848/4360) pour décider si Editer le profil ou Reconnecter est l'action *primaire*, pré-focalisée.

**Réinitialisation intelligente du mode Avancé** (`ServerDialogAdvancedModePolicy.ResolveAdvancedDefault`) : à la réouverture d'un profil RDP existant, l'indicateur global `RdpDialogAdvancedDefault` n'est honoré que si le profil personnalise réellement au moins un champ avancé (`AdvancedRdpSnapshot` couvrant UseGlobalDefaults, AntiIdle, BitmapCaching, Compression, AutoReconnect, AdminMode, FullScreen). Les profils dont l'état avancé reste aux valeurs par défaut conservatrices replient automatiquement l'expander Avancé, même lorsque la préférence utilisateur "ouvrir en mode avancé" est active.

### 5. Remplissage automatique des identifiants via EnumThreadWindows

**Problème** : `EnumWindows` ne trouve que les fenêtres de premier niveau. Les boîtes CredUI issues de contrôles ActiveX embarqués sont des fenêtres enfants appartenant au thread, invisibles pour l'énumération standard.

**Solution** : parcourir tous les threads du processus courant avec `EnumThreadWindows` en complément de `EnumWindows`. Utiliser UI Automation (`System.Windows.Automation`) pour les CredUI modernes en XAML, avec un repli Win32 `SendMessage`/`BM_CLICK` pour les boîtes classiques.

### 6. Vérification TOFU des clés d'hôte

La confiance des clés d'hôte SSH est orchestrée par `HostKeyTrustService`, qui compose le `HostKeyStore` de plus bas niveau et persiste des métadonnées `HostKeyEntry` enrichies dans `settings.json` sous `trustedHostKeysV2` (`Fingerprint`, `FirstSeen`, `LastSeen`, `Algorithm`, `Source`, `PublicKeyBase64` optionnel). Les entrées `trustedHostKeys` héritées sont lues de façon additive et migrées en mémoire sans supprimer l'ancienne clé, ce qui préserve la sûreté d'un retour arrière. Les chemins SSH.NET résolvent les décisions de premier usage et de non-concordance avant la vraie tentative de connexion via `IHostKeyVerifier` ; le rappel `HostKeyReceived` de SSH.NET reste synchrone et ne reçoit qu'un `PinnedFingerprintVerifier` déjà résolu. Les points d'entrée de production SSH/SFTP/tunnel/sudo exigent `HostKeyStore` et `IHostKeyVerifier` comme dépendances non nullables. `RejectingHostKeyVerifier` est le vérificateur sûr en fail-closed, tandis que `AutoAcceptHostKeyVerifier` est réservé aux tests explicites.

Le repli Plink suit le même modèle de confiance via `PlinkHostKeyDecider` : les empreintes stockées sont passées en `-hostkey` ; les sondes de premier usage passent par le vérificateur normal ; les empreintes non résolues échouent avec `HostKeyUnavailable`. Les correspondances en régime établi rafraîchissent `LastSeen` silencieusement. Les non-concordances affichent côte à côte l'empreinte stockée et l'empreinte présentée, autorisent un remplacement délibéré après vérification hors bande, et rejettent avec `SshFailureCode.HostKeyMismatch` lorsque l'utilisateur refuse. L'import/export known_hosts optionnel est explicite, à l'exception de l'indicateur d'import au démarrage, qui est opt-in.

Les échecs de clé d'hôte en cours de session ne se réduisent pas à un texte de déconnexion générique. `SshSessionFailureDispatcher` associe `HostKeyRejectedException` à `SshSessionSecurityEvent` pour `SftpBrowser` et `SshShellSession` ; l'interface SSH supprime la reconnexion automatique sur signal de MITM. Une `HostKeyRejectedException` imbriquée dans une exception externe est reconnue à chaque site de classification via `HostKeyRejectionFinder`, de sorte qu'un rejet encapsulé se classe toujours en fail-closed et ne devient jamais éligible à la reconnexion automatique (`SshReconnectPolicy` n'autorise que les codes réseau transitoires). `RemoteFileEditor` lève `HostKeyRotatedDuringUpload` lorsqu'une session d'édition sudo voit la clé d'hôte changer pendant un téléversement automatique.

### 7. Classification des échecs SSH

`FailureClassifier` associe les exceptions SSH.NET (et les motifs de stderr de Plink) à 29 valeurs structurées `SshFailureCode`. Cela permet à l'interface d'afficher des messages d'erreur ciblés et localisés (par exemple `ErrorSshKeyRejected`, `ErrorSshNetworkTimedOut`, `ErrorSshHostKeyUnavailable`) plutôt que le texte brut de l'exception.

### 8. Intégration Citrix StoreBrowse

**Problème** : les applications et bureaux publiés Citrix exigent une authentification StoreFront et la génération d'un fichier ICA avant de lancer une session.

**Solution** : `ConnectionService.Citrix.cs` utilise la ligne de commande `storebrowse.exe` fournie par Citrix Workspace App :
1. Détection automatique de `storebrowse.exe` dans `%ProgramFiles(x86)%\Citrix\ICA Client\SelfServicePlugin\`
2. Authentification auprès de StoreFront pour énumérer les ressources publiées
3. Génération du fichier ICA de la ressource sélectionnée
4. `EmbeddedCitrixView` héberge la session dans un onglet, en suivant le même cycle de vie que les sessions RDP

### 9. Hiérarchie de types ISessionResult

Toutes les opérations de connexion renvoient un `ISessionResult` (défini dans `Heimdall.Core/Models/`). Les implémentations concrètes portent l'état de session propre à chaque protocole :
- `RdpSessionResult` - handle ActiveX, informations de résolution
- `SshSessionResult` - flux shell ou référence de session en mode pipe
- `SftpSessionResult` - `SftpSessionBundle` (SftpClient + SshClient pour sudo)
- `CitrixSessionResult` - handle de session ICA
- `LocalSessionResult` - référence de session ConPTY
- `VncSessionResult` - handle du proxy WebSocket, informations de connexion noVNC
- `TelnetSessionResult` - référence `TelnetSession` (TCP brut)
- `FtpSessionResult` - référence `FtpBrowser` (IRemoteBrowser)

`ConnectionResult.Warning` est un message de statut optionnel et non bloquant, destiné aux connexions réussies qui appellent tout de même une mise en garde visible, comme un FTP avec identifiants sans TLS. Il est acheminé vers la surface de statut plutôt que vers une fenêtre modale.

### 10. Diffusion multi-exécution

**Flux de données** : le terminal source de la diffusion capture la saisie utilisateur au niveau de l'événement `onData` de xterm.js. Lorsque la diffusion est active, l'événement de saisie est relayé via `PostWebMessageAsString` vers l'instance WebView2 de chaque terminal inscrit, qui le transmet à son propre pipe stdin. Chaque terminal renvoie l'écho de la saisie par son PTY distant, de sorte que la sortie reste propre à chaque session.

### 11. Connexion rapide (Ctrl+K)

**Biais d'historique par hôte** : `IRecentConnectionTracker` (singleton DI, `Heimdall.App/Services/RecentConnectionTracker.cs`) tient en mémoire un journal des triplets `(host, protocol, timestamp)` réussis (50 entrées maximum, dédoublonnées par `(host, protocol)`). Il est alimenté depuis `ServerListViewModel.OnConnectionStateChanged` chaque fois qu'une session atteint `Connected` ou `LaunchedExternalClient`. `CommandPaletteViewModel` l'exploite pour deux gains UX ponctuels : (1) lorsque l'utilisateur saisit une simple IP ou un nom d'hôte, les suggestions SSH et RDP sont réordonnées pour que le protocole utilisé en dernier sur cet hôte apparaisse en premier ; (2) lorsque la palette est ouverte avec une requête vide, les serveurs persistés dont le `RemoteServer` correspond à un hôte récent remontent en tête de la liste de suggestions, du plus récent au plus ancien. Le tracker est limité au processus - aucune persistance disque pour l'instant ; un travail ultérieur pourra ajouter le chargement/l'enregistrement sans modifier l'interface publique.

**Architecture** : une palette de commandes basée sur `Popup` (HWND propre, s'affiche au-dessus des surfaces ActiveX/WindowsFormsHost) analyse les chaînes de connexion de la forme `[protocol://]user@host[:port]`. L'analyseur déduit le protocole du port s'il est omis (22=SSH, 3389=RDP, 1494=Citrix, 5900=VNC, 23=Telnet, 21=FTP). Un `ServerProfileDto` est créé de façon transitoire (non persisté) et transmis à `ConnectionService.ConnectAsync()`. Les connexions récentes sont stockées dans `settings.json` pour un réemploi rapide. Ouverte en mode split, la palette force le mode de connexion Embedded et rattache la nouvelle session au panneau secondaire de l'onglet actif.

**Classement flou unifié** : `CommandPaletteViewModel.Search.cs` note les outils (libellé localisé + alias `CommandPrefixes` + catégorie), les outils externes, les serveurs (`DisplayName`, `RemoteServer`, `Group`, `Username`, `ConnectionType`, `Environment`, `Tags`, `ProjectName`) et les extraits TwinShell en une seule passe, avant tri et sélection des 20 meilleurs. Les seuls chemins de retour anticipé sont (1) les invocations d'outils explicitement porteuses d'un argument - `<prefix> <argument>` lorsque `LabelWithArgKey` est défini, par exemple `ping 8.8.8.8` ou `subnet 10.0.0.0/8` - et (2) la requête littérale `tool`/`tools`, qui liste tous les outils enregistrés. Tout le reste mélange les résultats, si bien que des requêtes comme `calculator`, `formatter` ou `encoder` font remonter l'outil correspondant aux côtés des serveurs qui correspondent également.

**Indexation des extraits** : la palette rafraîchit à chaque ouverture un instantané de la bibliothèque d'actions TwinShell via un `IActionService` de portée limitée, résolu par `IServiceProvider.CreateAsyncScope` - en fire-and-forget à chaque `Open()`/`OpenSplit()`, de sorte que le cache est toujours frais sans bloquer le popup. Les extraits sont notés sur le Titre (poids plein), les Tags (poids plein, important pour des requêtes sysops comme `disk` ou `df`), la Description et la Catégorie (poids réduit de moitié). A la sélection, `HandleSnippetSelection` résout la meilleure charge utile à copier-coller (`ResolveSnippetCommand` : modèle Windows → modèle Linux → premier exemple → titre de l'action), l'écrit dans `System.Windows.Clipboard` et affiche un message de statut - les extraits sont exclusivement destinés au presse-papiers, interceptés avant tout routage split/connect, si bien qu'un Id `snippet-*` ne peut jamais ouvrir un onglet ni fusionner un panneau par accident.

**En-têtes de section visuels** : la ListBox à plat consomme un `CollectionViewSource` avec un `PropertyGroupDescription` sur `Group`. `PaletteGroupHeaderConverter` normalise les valeurs de Group vides vers un repli localisé `Servers` / `Quick Connect`, de sorte qu'aucune section sans titre ne s'affiche. Les éléments ad hoc (`adhoc-ssh-...`, `adhoc-rdp-...`) prennent explicitement le groupe `PaletteQuickConnectHeader` afin de se regrouper sous leur propre en-tête au lieu de retomber sur le repli.

### 12. Panneau des tunnels (rétractable)

**Architecture** : un panneau latéral basé sur `GridSplitter`, lié à `TunnelPanelViewModel`. Le panneau observe `TunnelManager.ActiveTunnels` (une `ObservableCollection<TunnelSession>`) et affiche le statut en temps réel. La fermeture d'un tunnel envoie une demande d'annulation à la `TunnelSession` concernée, sans affecter les autres tunnels ni la connexion SSH parente. L'état d'expansion est résolu par onglet/session actif, et non par un unique indicateur global : la surcharge manuelle par onglet l'emporte, puis la persistance nullable par profil (`ServerProfileDto.TunnelsPanelExpanded`), puis la valeur par défaut de l'application (`AppSettings.CollapseTunnelsPanelByDefault`). Les profils enregistrés persistent leur préférence de panneau dans le DTO de profil serveur, tandis que les onglets ad hoc gardent la surcharge locale à l'onglet. Les en-têtes d'onglet de session affichent également un badge de tunnel agrégé lorsqu'une feuille de panneau splitté de l'onglet possède un état de tunnel actif.

### 13. VNC via noVNC dans WebView2 + proxy WebSocket

**Problème** : WPF n'a pas de contrôle VNC natif. Le VNC (protocole RFB) fonctionne en TCP brut, alors que noVNC exige un transport WebSocket.

**Solution** : `WebSocketVncProxy` est un proxy léger in-process qui écoute sur un port local aléatoire, accepte une unique connexion WebSocket depuis noVNC et achemine les trames binaires dans les deux sens vers la socket TCP du serveur VNC. `EmbeddedVncView` héberge noVNC dans un contrôle WebView2 pointant sur `ws://localhost:{ListenPort}`. Cela réutilise la même infrastructure WebView2 que les vues terminal.

### 14. Telnet en TCP brut avec négociation IAC

**Problème** : les équipements réseau anciens (commutateurs, routeurs, consoles série) exigent un accès Telnet.

**Solution** : `TelnetSession`, dans `Heimdall.Terminal`, implémente `ITerminalSession` sur une socket TCP brute avec une négociation Telnet IAC minimale (gestion WILL/WONT/DO/DONT, sous-négociation NAWS pour la taille du terminal). Elle se branche sur la même chaîne de rendu WebView2 + xterm.js que le mode pipe SSH, si bien que l'expérience utilisateur est identique. Aucun client Telnet externe n'est requis.

### 15. FTP via l'abstraction IRemoteBrowser

**Problème** : certains serveurs exposent du FTP plutôt que du SFTP. L'interface de l'explorateur de fichiers doit fonctionner à l'identique quel que soit le protocole.

**Solution** : `IRemoteBrowser` définit la surface commune (`Connect`, `ListDirectory`, `Upload`, `Download`, `Disconnect`, événements). `SftpBrowser` (SSH.NET) et `FtpBrowser` (FluentFTP `AsyncFtpClient`) implémentent tous deux cette interface. `EmbeddedSftpView` se lie à `IRemoteBrowser` sans connaître le protocole sous-jacent. `RemoteFileEditor` fonctionne avec les deux via la même interface.

`FtpHandler` valide l'hôte et le port avant la connexion. Lorsque `FtpUseSsl` est faux et que des identifiants sont présents, il renvoie un `ConnectionResult` réussi avec `Warning = WarnFtpCleartext` ; l'interface l'affiche comme un texte de statut non bloquant. `FtpBrowser` utilise FluentFTP pour les opérations FTP/FTPS asynchrones, délègue à FluentFTP l'analyse des listages de répertoire, et active les connexions de données chiffrées pour le FTPS explicite. Heimdall valide et épingle le certificat du canal de contrôle FTPS. Le `FtpDataStream` de FluentFTP accepte le certificat du canal de données indépendamment, en dehors du rappel de Heimdall, si bien que l'application ne peut pas garantir l'identité de ce canal ; une session FTPS active affiche cette limite sous forme d'avis permanent.

**Deux modes d'édition** : clic droit sur un fichier pour choisir entre :
- **Editer (intégré)** : ouvre AvalonEdit dans l'application avec coloration syntaxique. L'enregistrement déclenche le téléversement.
- **Editer avec un éditeur externe** : téléchargement dans un dossier temporaire, lancement de l'éditeur configuré (Paramètres > Avancé > Editeur externe), `FileSystemWatcher` avec anti-rebond de 2 secondes pour un téléversement automatique à l'enregistrement.

### 15b. Système d'élévation sudo pour SFTP

**Problème** : SFTP s'exécute avec les droits de l'utilisateur connecté. Les fichiers et répertoires appartenant à root (par exemple `/etc/shadow`, `/root/`) sont inaccessibles. Contrairement à SSH, où `sudo su -` donne un accès complet, le protocole SFTP n'a **aucune escalade de privilèges intégrée**.

**Solution** : une approche à deux niveaux, utilisant des canaux d'exécution SSH en parallèle de la session SFTP.

**Niveau 1 - repli automatique** (transparent pour l'utilisateur) :
Chaque opération de fichier attrape les exceptions typées de permission refusée (`SftpPermissionDeniedException`, plus `UnauthorizedAccessException` en local pour les chemins de fichiers temporaires), puis réessaie via une exécution SSH :
- Téléversement : SFTP vers `/tmp/` → `sudo tee` vers la cible ; le nettoyage s'exécute en commande `sudo rm -f` distincte depuis un bloc `finally`
- Téléchargement : `sudo cat` via exécution SSH
- Edition : délègue à `RemoteFileEditor.EditFileSudoAsync`
- Chmod/Renommage/Suppression/Mkdir : `sudo chmod`/`mv`/`rm`/`mkdir` via exécution SSH

**Niveau 2 - bascule "Naviguer en root"** (déclenchée par l'utilisateur) :
Un bouton bascule de la barre d'outils fait passer le listage de répertoire du `ListDirectory` SFTP à `sudo ls -la --time-style=long-iso` via exécution SSH. Cela permet de naviguer dans N'IMPORTE QUEL répertoire, quelles que soient les permissions.

**Décisions de conception et pièges rencontrés** :

- **L'authentification SSH doit correspondre à la session principale** : les helpers sudo doivent utiliser `SshConnectionFactory.Create()` avec la même authentification Pageant/clé/mot de passe que la connexion d'origine. La première implémentation utilisait un `new SshClient(connInfo)` brut, qui contournait l'intégration Pageant - la connexion SSH échouait avec "Permission denied (publickey,password)" et l'utilisateur voyait une erreur incompréhensible.
- **Vérification de clé d'hôte obligatoire** : les clients SSH sudo doivent utiliser `SshConnectionFactory.Create()` avec le `HostKeyStore` partagé et l'`IHostKeyVerifier` de production, afin de recevoir la même résolution de confiance en préflight et le même vérificateur épinglé que les sessions SSH/SFTP normales. Contourner la fabrique fait sauter le flux fail-closed de clé d'hôte.
- **L'escalade doit rester typée** : SSH.NET peut remonter des messages `SshException("Failure")` très larges pour de nombreuses conditions sans rapport avec les permissions. Le chemin sudo ne fait délibérément aucune correspondance par sous-chaîne sur les échecs génériques ; les faux négatifs sont plus sûrs que des opérations privilégiées sur des erreurs sans rapport.
- **Les sessions d'édition distante mettent la confiance en cache** : le téléversement d'une édition sudo utilise le `PinnedFingerprintVerifier` résolu à l'ouverture du fichier. Une rotation de clé d'hôte pendant l'édition lève `HostKeyRotatedDuringUpload`, ferme la session d'édition, et ne relance pas le TOFU à chaque enregistrement.
- **Les tâches de téléversement ont un propriétaire** : `RemoteFileEditor` suit la tâche de téléversement active par session d'édition, propage l'annulation via `CloseEdit`/`Dispose` et observe les défaillances de tâche.
- **Analyse de la sortie `ls -la`** : le format `--time-style=long-iso` produit **8 colonnes** (permissions, liens, propriétaire, groupe, taille, date, heure, nom). Le premier analyseur en attendait 9 et ignorait silencieusement toutes les entrées. La colonne du nom de fichier doit être la dernière portion du découpage pour gérer les espaces.
- **Bascule sudo masquée pour FTP** : les sessions FTP n'ont pas de canal SSH, donc le bouton sudo est replié.

### 16. Système de split récursif à N panneaux

**Architecture** : la disposition en split est modélisée comme un arbre binaire de noeuds `ISplitContent` :

```
ISplitContent (marker interface)
├── SessionPaneModel     Leaf: PaneId (GUID), HostControl, ServerId, OriginalServerId, Title, Status, FailureDetails, ...
└── SplitContainerModel  Branch: First, Second (ISplitContent), Orientation, SplitRatio
                         Constants: MinRatio (0.1), MaxRatio (0.9), DefaultRatio (0.5), SplitterThickness (4)
                         Auto-clamping: SplitRatio setter clamps to [MinRatio, MaxRatio] BEFORE PropertyChanged
```

**Identité de panneau** - deux identifiants distincts pour deux usages :
- `ServerId` (portée session) : attribué APRES une connexion réussie ; sert de clé de machine à états et de clé de suivi des tunnels. Vide pendant la phase de connexion.
- `OriginalServerId` (stable) : défini à la création du panneau à partir de l'identifiant d'inventaire du serveur ; ne change jamais. Utilisé pour les recherches de reconnexion, l'historique de déconnexion et l'appariement `SplitLayoutMemory`. Défini tôt dans `SplitSessionWithServerAsync` pour un nettoyage correct si le panneau est fermé pendant la connexion.

`SessionTabViewModel.RootContent` porte la racine de l'arbre. Un panneau unique est un `SessionPaneModel`. Un split est un `SplitContainerModel` dont les enfants peuvent eux-mêmes être splittés - ce qui permet des dispositions arbitraires (2x2, en L, 3 côte à côte, etc.) jusqu'à 8 panneaux par onglet. `SplitTreeHelper` fournit des helpers statiques de parcours et de mutation : `EnumerateLeaves`, `FindPane`, `FindPaneByHostControl`, `FindParent`, `RemovePane`, `ReplacePane`, `CountLeaves`, `FirstLeaf`. `ReplacePaneRecursive` s'arrête dès la première correspondance de panneau. Les diagnostics d'échec à l'échelle du panneau vivent désormais sur `SessionPaneModel` (`FailureDetails` plus les helpers de visibilité dérivés), de sorte que la divulgation des échecs SSH/RDP reste attachée au bon panneau, y compris sur des onglets splittés.

**SplitService** (extrait de MainViewModel) : toute l'orchestration split/merge vit dans `Heimdall.App.Services.SplitService`, un service DI singleton qui possède :
- `SplitSessionWithServerAsync` - connexion asynchrone + insertion dans l'arbre, avec CancellationToken propagé aux gestionnaires de protocole
- `SplitSessionWithTool` - ancrage synchrone d'un outil
- `MergeExistingSession` - reparentage à chaud avec vérification `CanClose()` sur toutes les feuilles de l'arbre source (pas seulement le shim primaire) ; retour utilisateur lorsqu'un outil occupé bloque l'opération
- `ClosePane` - nettoyage typé : déconnexion/libération de l'hôte → `HostControl=null` → retrait du panneau de l'arbre
- `CloseAllPanes` - démontage centralisé de l'onglet : contrôle `CanClose()`, annulation, historique, libération des tunnels, remise à zéro de l'état, libération des ressources (appelé par `ConnectionViewModel.CloseSessionInternal`)
- `ReconnectPaneAsync` - nettoyage différé de l'ancienne machine à états (libérée seulement après le succès de la nouvelle connexion) ; ne crée plus d'entrées LayoutMemory auto-référentielles
- `SwapSplitPanesAsync` - échange asynchrone en deux phases : détacher les contrôles hôtes → attendre l'arbre visuel → échanger le modèle → attendre de nouveau → restaurer (évite la course de reparentage WebView2/ActiveX)
- `ToggleSplitOrientation` - mutation d'arbre sur place
- `ConnectByProtocolAsync` - aiguillage unifié des 8 protocoles avec passage du CancellationToken à tous les gestionnaires `ConnectionService.Connect*Async`
- Cycle de vie du `CancellationTokenSource` par session (`RegisterSession`/`CancelSession` avec libération différée pour éviter les fuites)
- Instance `SplitLayoutMemory` pour la persistance des dispositions

`ConnectionViewModel` n'est plus qu'une coquille fine : `CloseSessionInternal` délègue entièrement à `SplitService.CloseAllPanes`, ne conservant que la gestion de la collection d'onglets. Les rappels vers `ConnectionViewModel` (ActiveSessions, ActiveSession, HasActiveSessions, StatusText) sont câblés par `MainViewModel` à la construction, selon le même motif qu'`EmbeddedSessionManager`.

**Rendu** : des `DataTemplate` WPF implicites dans `Window.Resources` instancient récursivement `SessionPaneControl` (feuille) et `SplitContainerControl` (branche avec `GridSplitter`). Chaque feuille gère ses propres surimpressions (indicateur de chargement, déconnexion avec boutons Reconnecter/Fermer, libellés accessibles). Accent de focus (`IsKeyboardFocusWithin`) sur le panneau actif + bordure au survol (`IsMouseOver`) pour le retour visuel. Les deux contrôles s'abonnent dans le constructeur et détachent tous leurs gestionnaires d'événements dans `Unloaded` (PropertyChanged, Click, DragCompleted, MouseDoubleClick) pour éviter les fuites mémoire. Taille minimale de panneau imposée (MinWidth=120, MinHeight=80). Un double-clic sur le séparateur remet le ratio à 50/50. Garde NaN/Infini à la fin du glisser. Le curseur du GridSplitter est mis à jour dynamiquement (`SizeNS` pour Horizontal, `SizeWE` pour Vertical) dans `ApplyLayout()`.

**Split d'une nouvelle connexion** : clic droit → "Split..." → Horizontal | Vertical → palette de commandes en mode split → sélection du serveur → nouveau `SessionPaneModel` inséré dans l'arbre par encapsulation dans un `SplitContainerModel`. Surimpression de chargement visible pendant la connexion asynchrone. Une garde post-await abandonne si le panneau a été retiré ou l'onglet fermé pendant la connexion. Le CancellationToken par session assure un abandon propre à la fermeture de l'onglet. La palette de split affiche TOUS les serveurs de l'inventaire (pas seulement les récents).

**Split avec un outil** : lorsqu'un outil intégré est sélectionné en mode split (recherche dans la palette ou outils récents), `SplitSessionWithTool()` crée le contrôle de l'outil de façon synchrone via `EmbeddedSessionManager.CreateToolControl()` et l'ancre directement dans le panneau splitté - ni surimpression de chargement, ni connexion asynchrone. Les panneaux d'outil utilisent `ConnectionType = "TOOL:<ID>"` et un `ServerId` basé sur un GUID pour l'adressage dans l'arbre.

**Fusion d'une session ou d'un outil existant** : clic droit → "Merge with..." → session ou outil → Horizontal | Vertical → `MergeExistingSession()` reparente le `HostControl` vivant dans un nouveau panneau, sans reconnexion. Vérifie `CanClose()` sur tous les panneaux d'outil source avant de poursuivre (un outil occupé bloque la fusion). Fonctionne symétriquement pour les onglets de connexion et les onglets d'outil. Utilise `OriginalServerId` comme clé de recherche stable (repli depuis `ServerId`, qui peut être vide pendant la connexion ; les onglets d'outil utilisent `ServerId` directement). Consulte `SplitLayoutMemory` pour restaurer le ratio antérieur des serveurs déjà appariés. Annule toute opération en cours pour la session source. Les entrées de machine à états sont préservées pendant la fusion (les connexions restent vivantes, elles sont seulement reparentées) - le nettoyage a lieu à la fermeture de l'onglet.

**Glisser pour splitter** : faire glisser un onglet sur la zone de contenu d'un autre onglet. L'orientation est détectée automatiquement à partir de la position de dépôt (bord le plus proche). Fonctionne sur des sessions déjà splittées pour créer des dispositions à 3 panneaux ou plus.

**Opérations** : échanger les panneaux, basculer l'orientation (Ctrl+Shift+O), détacher n'importe quel panneau vers une `FloatingSessionWindow`, fermer un panneau individuel (promotion du frère dans l'arbre), annuler le split (restaure le panneau en onglet indépendant). La fermeture de panneau est typée : les panneaux de connexion reçoivent historique de déconnexion + libération de tunnel + remise à zéro de la machine à états ; les panneaux d'outil vérifient `IToolView.CanClose()` et sautent le démontage machine à états/tunnel. L'ordre de fermeture est figé : déconnecter/libérer l'hôte via `EmbeddedSessionManager`, mettre `HostControl=null`, puis retirer le panneau de l'arbre. L'ordre de démontage propre à RDP/ActiveX est pris en charge à l'intérieur de `RdpDisconnectTeardownSequence`.

**Ratio du séparateur** : le modèle borne automatiquement `SplitRatio` à `[0.1, 0.9]` dans le setter (avant l'émission de PropertyChanged) - la vue lit le ratio directement, sans bornage redondant. Capturé via `GridSplitter.DragCompleted` par `SplitContainerControl` avec garde NaN/Infini, puis persisté dans le modèle d'arbre. Restauré au changement d'onglet par reconstruction de la disposition. Le double-clic sur le séparateur remet à `DefaultRatio` (0.5).

**Persistance des dispositions de split** : `SplitLayoutMemory` enregistre les associations de paires de serveurs dans `config/split-layouts.json`, avec un schéma JSON versionné (`{ "version": 1, "entries": [...] }`). Rétrocompatible avec l'ancien format de tableau brut. Sûr vis-à-vis des threads via un `lock` sur toutes les méthodes publiques. Enregistrement atomique via un fichier temporaire unique (suffixé par un `Guid`) + `File.Move(overwrite: true)` avec nettoyage en `finally`. A l'ouverture de la palette de commandes en mode split, les serveurs déjà appariés sont remontés en tête des résultats.

**Gardes contre les conditions de course** :
- `CancellationToken` par session propagé à travers `ConnectByProtocolAsync` vers tous les gestionnaires de protocole - fermer un onglet annule la véritable tentative de connexion, pas seulement l'enveloppe externe
- `CancelSession` libère le CTS après un délai de 5 secondes (libération différée), afin que les opérations en vol puissent observer l'annulation avant que la source ne soit récupérée
- Le contrôle post-await `!ActiveSessions.Contains(session) || FindPane(...) is null` empêche les connexions orphelines
- La barrière `CountLeaves >= 8` empêche une croissance non bornée de l'arbre
- Anti double reconnexion via le contrôle `pane.HostControl is null` (la surimpression masque le bouton dès le début de la connexion)
- Garde de sous-arbre nul dans `RemovePane` : promeut le frère au lieu d'assigner null aux enfants du conteneur
- Nettoyage différé de la machine à états lors d'une reconnexion : l'ancien tunnel/état n'est libéré qu'après le succès ou l'échec définitif de la nouvelle connexion (évite la perte d'état en cas d'échec de reconnexion)
- `OriginalServerId` défini à la création du panneau (et non après connexion), pour un nettoyage correct si le panneau est fermé pendant la connexion asynchrone
- `MergeExistingSession` vérifie la présence de HostControl sur toutes les feuilles de l'arbre source (pas seulement le shim primaire), ce qui évite un refus de fusion erroné sur des onglets splittés dont le panneau primaire est déconnecté

**Rétrocompatibilité** : `SessionTabViewModel` expose des propriétés shim (`ServerId`, `Title`, `Status`, `HostControl`, `IsSplit`, `SplitOrientation`, etc.) qui délèguent à `PrimaryPane` (première feuille). Les propriétés shim `Secondary*` visent la première feuille du second enfant au niveau racine. `NotifyTreeDependentProperties()` (méthode partagée) est appelée après les changements de `RootContent` comme après les mutations d'arbre sur place (échange). `_emptyPane` est propre à chaque instance (et non statique), pour éviter les fuites d'état entre sessions.

### 16b. Détachement d'onglet en fenêtre flottante

**Problème** : les utilisateurs ont besoin de voir plusieurs sessions côte à côte, ou de déplacer une session vers un second écran.

**Solution** : `FloatingSessionWindow` héberge un unique `SessionTabViewModel` détaché. N'importe quel panneau individuel peut être détaché d'un arbre de split via `DetachPaneToFloatingWindow(paneId)` - le panneau est extrait de l'arbre, promu en onglet indépendant, puis détaché en fenêtre flottante. La fenêtre applique le thème courant via `WindowThemeHelper`, affiche les métadonnées de session (titre, route de tunnel) et propose un bouton de rattachement. A la fermeture, si aucun rattachement explicite n'a eu lieu, la session est rendue à la fenêtre principale pour un nettoyage correct.

### 17. Comptage de références des tunnels partagés

**Problème** : plusieurs connexions peuvent emprunter le même tunnel SSH (par exemple deux sessions RDP via la même passerelle). Démonter le tunnel à la fermeture d'une connexion tuerait les autres.

**Solution** : `TunnelManager` maintient un `ConcurrentDictionary<int, int>` de compteurs de références indexés par port local. `AddReference()` incrémente le compteur lorsqu'une nouvelle connexion réutilise un tunnel existant. `ReleaseReference()` le décrémente et n'appelle `CloseTunnel()` que lorsque le compteur atteint zéro. `CloseTunnel()` vérifie lui-même le compteur avant de démonter, ce qui constitue une double garde.

### 18. Héritage de connexion (GroupDefaultsDto)

**Problème** : les entreprises organisent des centaines de serveurs en groupes qui partagent la même passerelle, le même utilisateur SSH, le même chemin de clé ou le même type de connexion. Configurer chaque serveur individuellement est fastidieux.

**Solution** : `GroupDefaultsDto` définit des réglages de connexion par défaut (passerelle, nom d'utilisateur SSH, chemin de clé, port, type de connexion) au niveau du groupe/dossier. Les serveurs héritent de ces valeurs lorsque leurs propres champs sont nuls ou vides. La résolution est hiérarchique : un serveur dans `PROD/Linux` hérite d'abord de `PROD/Linux`, puis retombe sur `PROD` si le groupe imbriqué ne surcharge pas le champ.

### 19. Fournisseurs d'identifiants externes (fabrique + commande / Gestionnaire d'identifiants Windows)

**Problème** : les environnements soucieux de sécurité stockent les identifiants dans des gestionnaires de mots de passe externes (KeePassXC, KeePass2, Bitwarden CLI, 1Password CLI, `pass`) ou dans le Gestionnaire d'identifiants Windows natif, et non dans le coffre DPAPI de l'application.

**Solution** : `ICredentialProviderFactory` (`CredentialProviderFactory`) construit le fournisseur choisi dans les réglages (`CredentialProviderType`) : le fournisseur en ligne de commande ou le Gestionnaire d'identifiants Windows natif. `CommandCredentialProvider` exécute un modèle de ligne de commande configuré par l'utilisateur - les emplacements `{Host}`, `{Port}`, `{User}`, `{Title}`, `{Database}` sont substitués avec un assainissement sensible au contexte (`InputValidator.IsShellTarget()` choisit un filtrage strict pour les interpréteurs de commandes et un filtrage relâché pour les exécutables ordinaires comme keepassxc-cli, bw, op). Un **secret de déverrouillage** optionnel est fourni à l'outil via stdin (mot de passe maître KeePassXC, phrase de passe GPG), une **commande de nom d'utilisateur séparée** optionnelle résout le login, un **délai d'expiration configurable** borne chaque appel, et un mode **"première ligne uniquement"** supprime le bruit final tel que la ligne `OK:` de KPScript (également utile pour `pass`). `WindowsCredentialManagerProvider` lit les identifiants génériques via `CredReadW` (Win32), renvoie à la fois le nom d'utilisateur et le mot de passe, et s'indexe sur le nom d'entrée de coffre par profil (`VaultEntryName`) ou sur le nom d'affichage. Les mots de passe récupérés sont rechiffrés avec DPAPI avant injection dans le profil en mémoire, si bien que tous les protocoles en aval (SSH/SFTP, RDP/Citrix, WinRM, FTP, Telnet, VNC) fonctionnent sans modification. Les échecs souples (code de retour non nul, sortie vide) remontent un avertissement à l'utilisateur. Cela permet une récupération d'identifiants à divulgation nulle, où Heimdall ne persiste jamais le mot de passe.

**Authentification par fichier de clé KeePassXC** : les bases KeePassXC d'entreprise s'authentifient avec un fichier de clé (`.keyx`/`.key`), utilisé seul ou avec un mot de passe maître. Un emplacement `{KeyFile}` et le réglage `CredentialProviderKeyFile` (un chemin de fichier en clair, pas un secret) injectent le fichier de clé dans la commande via `-k "{KeyFile}"`. Les deux emplacements de chemin `{Database}` et `{KeyFile}` utilisent un **assainisseur sensible aux chemins** pour les cibles non-shell, qui ne supprime que le guillemet double et les CR/LF, au lieu du jeu relâché plus large. La justification : le fournisseur s'exécute avec `UseShellExecute=false`, donc aucun shell n'interprète les arguments, et le guillemet double (illégal dans un nom de fichier Windows) est le seul métacaractère de délimitation d'argument ; ne supprimer que lui préserve tous les caractères de chemin légaux. Cela corrige également une corruption latente des chemins `{Database}` contenant des caractères tels que `&` ou `$`. Trois préréglages KeePassXC sont livrés : `"KeePassXC"` (mot de passe maître, désormais avec `-q`), `"KeePassXC (key file)"` (mot de passe maître + fichier de clé) et `"KeePassXC (key file only)"` (`--no-password`, fichier de clé seul). Dans les Paramètres, un champ **Fichier de clé** avec une boîte de dialogue Parcourir (`*.keyx;*.key`) se place à côté du champ de base de données, et le bouton **Test** avertit d'emblée lorsqu'un modèle `{KeyFile}` est sélectionné sans chemin de fichier de clé, avant de lancer l'outil. Les clés de locale EN/FR sont ajoutées à parité. Les préréglages sont validés contre le vrai `keepassxc-cli` 2.7.11.

Une **barrière Windows Hello** complémentaire (`IWindowsHelloService` au-dessus de `UserConsentVerifier`) peut exiger une vérification biométrique/PIN avant toute résolution d'identifiant stocké, aussi bien en connexion unitaire qu'en connexion groupée. Elle est fail-closed (bloque si Hello est activé mais indisponible ou non enrôlé) et retient une vérification réussie pendant une fenêtre de grâce en mémoire configurable (`RequireWindowsHelloOnConnect`, `WindowsHelloGraceMinutes`), afin de ne pas solliciter l'utilisateur à chaque connexion.

### 20. Moteur de tâches planifiées (TaskSchedulerService)

**Problème** : les connexions automatisées (scripts de sauvegarde SSH quotidiens, fenêtres de maintenance) doivent s'exécuter selon une planification, sans intervention manuelle.

**Solution** : `TaskSchedulerService` exécute un `System.Threading.Timer` d'arrière-plan qui tique toutes les 60 secondes. A chaque tic, il évalue les entrées `ScheduledTaskDto` (fournies par le rappel `TasksProvider`) au regard de l'heure courante, déclenche `TaskDueCallback` pour les tâches échues, et appelle `PersistCallback` pour enregistrer les horodatages de dernière exécution. Le timer est protégé par un `SemaphoreSlim` afin d'éviter le chevauchement de tics.

### 21. Supervision de santé serveur (canal SSH multiplexé)

**Problème** : les administrateurs veulent des données de santé en un coup d'oeil (CPU, RAM, disque) pour les serveurs connectés, sans ouvrir un outil de supervision séparé.

**Solution** : `ServerHealthMonitor`, dans `Heimdall.Ssh`, réutilise le `SshClient` existant d'une session shell active pour exécuter des commandes de supervision légères (`top -bn1`, `free -m`, `df -h /`) sur des canaux SSH multiplexés, à un intervalle configurable (15 secondes par défaut). Les commandes passent par la surface APM de SSH.NET (`BeginExecute`/`EndExecute`) encapsulée avec `Task.Factory.FromAsync`, et les trois sondes s'exécutent simultanément avec `Task.WhenAll`. Les résultats sont analysés par expressions régulières compilées vers un enregistrement `ServerHealthData` et affichés dans l'interface.

### 21b. Moniteur d'accessibilité de session (sondes TCP sur tout l'inventaire)

**Problème** : la section 21 ne voit CPU/RAM/disque qu'**après** connexion de l'utilisateur. L'utilisateur veut aussi une vue d'ensemble des serveurs de l'inventaire joignables sur le réseau **avant** d'ouvrir une session - des pastilles vertes/rouges dans la barre latérale, plutôt que de découvrir au clic qu'un hôte est tombé.

**Solution** : `Heimdall.Core.SessionHealth` définit le modèle de données (énumération `HealthStatus`, enregistrement immuable `HealthState`, couture de test `IHealthProbe`, implémentation par défaut `TcpHealthProbe`) et `Heimdall.App.Services.SessionHealthMonitor` l'orchestre. Un `System.Threading.Timer` se déclenche toutes les `SessionHealthCheckIntervalSeconds` (60 par défaut), charge le dernier inventaire depuis `IConfigManager.LoadServersAsync` (de sorte que les ajouts/suppressions via la boîte de dialogue serveur sont pris en compte automatiquement, sans hook de rafraîchissement séparé) et lance des sondes TCP parallèles limitées par un `SemaphoreSlim` (10 simultanées par défaut). Le résolveur protocole → port associe RDP->`RemotePort`, SSH/SFTP->`SshPort`, VNC->`VncPort`, FTP->`FtpPort`, Telnet->`TelnetPort` ; Citrix et Local Shell ne sont volontairement pas sondés. Les serveurs derrière une passerelle (`SshGatewayId != null`) court-circuitent vers `Unknown` sans consommer de créneau de sonde - une sonde directe échouerait toujours puisque la passerelle est le seul saut joignable. L'état des serveurs retirés de l'inventaire entre deux cycles est évincé au cycle suivant.

**Intégration aux réglages** : `IConfigManager.SettingsChanged` est souscrit dans le constructeur ; basculer `SessionHealthMonitorEnabled` ou modifier l'un des quatre réglages `SessionHealth*` réarme le Timer sans redémarrage. La désactivation vide le dictionnaire d'état en mémoire.

**Intégration à l'interface** : `ServerListViewModel` s'abonne à `StatusChanged` et achemine chaque verdict vers le `ServerItemViewModel.HealthState` correspondant via `IUiDispatcher.InvokeAsync` (le thread du Timer ne touche jamais aux liaisons WPF). `ServerStatusToColorConverter` est passé de 2/3 à 3/4 valeurs de liaison, en acceptant un `HealthState` optionnel ; lorsque la session est dans un état de connexion non actif, la couleur de la pastille de la barre latérale reflète le verdict de santé. La conversion reste rétrocompatible : les appels à 2 et 3 valeurs retombent sur l'ancienne palette par type de connexion, si bien que tout appelant hors barre latérale continue de fonctionner à l'identique.

**Levée d'ambiguïté** : ce service est **distinct** de `Heimdall.Ssh.ServerHealthMonitor` (section 21), qui interroge l'usage des ressources sur une unique session SSH déjà connectée. Les deux noms sont proches mais désignent des choses différentes : *Server*Health = "comment va cette machine connectée ?", *Session*Health = "une session vers cette machine aboutirait-elle maintenant ?".

### 22. Enregistreur de macros (capture de frappes avec délais)

**Problème** : les enchaînements de terminal répétitifs (séquences de connexion, commandes de configuration) doivent pouvoir être enregistrés et rejoués.

**Solution** : `TerminalMacro` (dans `Heimdall.Core.Models`) stocke une séquence d'enregistrements `MacroEntry`, chacun contenant le texte saisi et le délai (en millisecondes) écoulé depuis l'entrée précédente. `MacroService` persiste les macros sous forme de fichiers JSON individuels dans un répertoire `macros/`. A la relecture, les entrées sont envoyées à la session de terminal en préservant les délais inter-frappes enregistrés.

### 23. Scanner réseau (balayage ICMP + sonde de ports)

**Problème** : les administrateurs ont besoin de découvrir les hôtes d'un sous-réseau avant de les ajouter à l'inventaire de serveurs.

**Solution** : `NetworkScanner` (dans `Heimdall.Core.Security`) accepte un sous-réseau CIDR (par exemple `192.168.1.0/24`), réalise des balayages ping ICMP parallèles avec un délai d'expiration d'une seconde, puis sonde les ports courants (22, 3389, 80, 443, 5900) sur les hôtes qui répondent, avec un délai de 500 ms. Les résultats contiennent l'adresse IP, le nom d'hôte (DNS inverse), le temps d'aller-retour et les ports ouverts. Un rappel de progression permet de mettre à jour l'interface pendant le balayage.

### 24. Serveur de fichiers rapide (HTTP/TFTP éphémère)

**Problème** : certains serveurs n'ont ni SFTP ni SCP (serveurs durcis, conteneurs minimaux, équipements réseau). Les utilisateurs ont besoin d'un moyen rapide de rendre des fichiers locaux accessibles à `wget`/`curl`/`tftp` depuis une session SSH distante.

**Solution** : `EphemeralFileServer` fournit toujours un serveur HTTP en lecture seule (via `HttpListener` avec listage de répertoire), tandis que TFTP (RFC 1350 RRQ minimal sur `UdpClient`) est optionnel, activable dans Paramètres > Avancé > Partage de fichiers. A l'activation, l'interface affiche des commandes de téléchargement prêtes à l'emploi pour les transports actifs et copie automatiquement l'URL du serveur dans le presse-papiers, pour un collage direct dans le terminal SSH actif. L'extrait de commande `tftp` n'apparaît dans la barre de statut que lorsque TFTP est activé. Tous les transports actifs sont libérés lorsque l'utilisateur clique sur "Stop File Server".

### 25. Détection et gestion automatiques du serveur X11

**Problème** : le renvoi X11 sur SSH exige un serveur X local (VcXsrv, Xming, X410, XWin). Les utilisateurs oublient d'en démarrer un, ou la variable `DISPLAY` est mal configurée.

**Solution** : `X11ServerManager` détecte les processus de serveur X en cours d'exécution en balayant les noms de processus connus. Si aucun n'est trouvé, il recherche les chemins d'installation connus et démarre automatiquement le premier serveur disponible. La variable d'environnement `DISPLAY` est fixée à `localhost:0.0` pour la session SSH. Le gestionnaire libère le processus démarré à l'arrêt.

### 26. Stratégie de découpage du code-behind de MainWindow

**Problème** : `MainWindow.xaml.cs` grossit naturellement : il est propriétaire d'environ 300 éléments XAML nommés, des gestionnaires d'événements, du câblage de localisation, des menus contextuels, du peuplement des onglets et de l'orchestration des sessions. Sans discipline, il dépasse 5 000 lignes, ce qui rend la navigation, la relecture et les tests unitaires difficiles.

**Solution** : deux motifs complémentaires appliqués comme des découpages purement structurels (aucun changement de logique, aucun renommage, aucune modification de signature) :

1. **Extraire vers des services enregistrés en DI** quand la logique n'a pas besoin d'accéder aux éléments XAML nommés - un service prend quelques dépendances par constructeur, est enregistré en singleton dans `App.xaml.cs` et est injecté dans `MainWindow` à côté de `MainViewModel`. Le retour vers la fenêtre passe soit par une petite interface de rappel (quand beaucoup de méthodes ont besoin de l'état de la fenêtre), soit par de simples délégués `Action<T>` (quand un ou deux rappels suffisent).
   - **`ContextMenuFactory`** (647 lignes) - construit les quatre menus contextuels du `TreeView` de sessions et le sous-menu "Detected Tools". Revient vers `MainWindow` via `IContextMenuCallbacks`.
   - **`SessionTabContextMenuFactory`** (335 lignes) - construit le menu contextuel de la barre d'onglets de session (19 éléments conditionnels : fermer/fermer les autres/tout fermer/renommer/dupliquer/détacher/splitter/fusionner/annuler le split/reconnecter/...). Revient via `ISessionTabContextCallbacks`.
   - **`ToolsTabPopulationService`** (605 lignes) - possède la reconstruction complète de l'onglet Outils pleine page ainsi que les données et le filtre du `TreeView` Outils de la barre latérale. Revient via `Action<ToolDescriptor>` (clic sur une carte) + `Action<string>` (clic sur l'épingle). Les jetons de thème sont résolus via `Application.Current.FindResource`, ce qui garde le service découplé de tout `FrameworkElement`.
   - **`FileShareService`** - cycle de vie du partage de dossier HTTP/TFTP éphémère (auparavant inline dans `OnShareFolderClick`). API à base d'événements (`ShareStarted` / `ShareStopped`), `IAsyncDisposable` - `App.OnExit` passe par `IAsyncDisposable.DisposeAsync` sur le fournisseur de services pour libérer correctement les services exclusivement asynchrones.
   - **`KeyboardShortcutService`** (18 raccourcis) - enregistrement fluide des raccourcis avec conditionnement `canExecute`, en remplacement du `switch` monolithique d'`OnPreviewKeyDown`. Enregistré dans le constructeur de `MainWindow`.
   - **`SessionWindowService`** - orchestration split/merge/détachement/annulation de split sortie de MainWindow. Expose l'événement `SplitPaletteRequested` pour que MainWindow ouvre la palette en mode split.

2. **Découper en fichiers `partial class`** quand la logique *doit* toucher directement des éléments XAML nommés (une extraction en service imposerait alors de passer des dizaines de paramètres `FrameworkElement` à chaque appel). Le nouveau fichier déclare `public partial class MainWindow` et regroupe un sous-ensemble de méthodes thématiquement cohérent. L'accès inter-fichiers est libre (même classe, même assembly), si bien que les helpers statiques et les champs privés restent partagés sans modification de visibilité. Des POCO colocalisés avec les partiels (`WindowUIState`, `TreeInteractionState`, `TabInteractionState`) possèdent les champs et indicateurs auparavant dispersés dans le monolithe.
   - **`MainWindow.Localization.cs`** (519 lignes) - les 8 méthodes `Apply*Localization` (l'orchestrateur `ApplyLocalization` + Navigation / Toolbar / Tunnel / Scheduled / Settings / About / Accessibility). Les phases 5A/5B ont depuis migré Navigation/Toolbar/Accessibility vers `{loc:Translate}` - ces helpers d'application sont désormais des stubs vides, en attente de suppression après les phases 5C/5D.
   - **`MainWindow.WindowUI.cs`** + le POCO `WindowUIState` - bascule plein écran, repli de la barre latérale, persistance du défilement de l'arbre, mémoire d'expansion des dossiers, sauvegarde/restauration des dimensions de fenêtre.
   - **`MainWindow.TreeInteractions.cs`** + le POCO `TreeInteractionState` - glisser-déposer dans le `TreeView` de sessions, champ de filtre, renommage inline, plomberie des menus contextuels. L'UX de déplacement vers un groupe achemine désormais le menu contextuel et le glisser-déposer par la même méthode centrale de `ServerListViewModel`, valide les cibles de glisser-déposer face au même ensemble de groupes délimité par projet, préserve `_expandedNodes` en évitant les rechargements `LoadServers`, et expose une zone de dépôt dédiée "sans groupe" pour la parité du glisser vers la racine.
   - **`MainWindow.TabInteractions.cs`** + le POCO `TabInteractionState` - réorganisation d'onglets par glisser, détachement par glisser, résolution des cibles de dépôt, suivi du survol de la barre d'onglets.

**Règle de décision** : si le corps entier de la méthode se résume à `Mw_X.Text = vm.Localize(...)` sur des éléments nommés, utiliser une classe partielle. Si elle manipule l'arbre ou construit des contrôles à partir de données et peut être remodelée pour accepter un paramètre `Panel`/`Control`, l'extraire en service. Le même `ConnectionService` de `Heimdall.App/Services/` utilise déjà ce motif de classe partielle, avec 10 fichiers pour ses flux de connexion par protocole.

**Résultat** : `MainWindow.xaml.cs` est passé de **4 895 à 2 123 lignes (-57 %)** sur le Chantier 1 + les phases 1 à 3, chaque unité extraite étant désormais relisible indépendamment et la porte étant ouverte à des tests unitaires ciblés là où c'est pertinent. La phase 1 a extrait `OnboardingFlowViewModel`, `FileShareService` et le partiel `WindowUI`. La phase 2 a extrait `KeyboardShortcutService`, `SidebarViewModel`, `ToolsTabViewModel`, et supprimé un gestionnaire mort `OnWindowDeactivated` de la palette de commandes qui fermait la palette à son ouverture. La phase 3 a extrait les partiels `TreeInteractions`/`TabInteractions`, `SessionTabContextMenuFactory` et `SessionWindowService`.

### 27. Composition de MainViewModel en sous-ViewModels

**Problème** : `MainViewModel.cs` avait atteint 1 917 lignes en tant que point d'orchestration unique de la barre latérale, de l'onglet Outils, de la palette de commandes, du panneau des tunnels, des tâches planifiées, du cycle de vie des sessions, du mode diffusion et de la restauration d'espace de travail. Chaque nouvelle fonctionnalité ajoutait quelques `[ObservableProperty]` / `[RelayCommand]` / gestionnaires d'événements de plus dans la même classe, brouillant les frontières de domaine et rendant le VM difficile à tester isolément.

**Solution** : des sous-VM composés, instanciés dans le constructeur de `MainViewModel` (aucun enregistrement DI, aucune recherche par localisateur de services). Chaque sous-VM prend `MainViewModel` comme premier paramètre de constructeur et accède à l'état des frères via `_main.X` (le motif déjà utilisé par `TunnelsViewModel` et `ScheduledTasksViewModel`). Les sous-VM qui possèdent des abonnements à des événements implémentent `IDisposable` et sont libérés depuis `MainViewModel.Dispose`.

Quatre sous-VM extraits en phase 4 :

- **`CommandPaletteViewModel`** (palette Ctrl+K) - 14 méthodes couvrant le classement de la recherche floue, l'analyse des commandes d'outil (`tools`, `ping 10.0.0.1`), l'analyse des chaînes de connexion ad hoc (`user@host:port` avec inférence de protocole), la remontée des outils récents et les flux connect/split. Possède l'état `IsCommandPaletteOpen` et la recherche d'appariement `SplitLayoutMemory`.
- **`TunnelsViewModel`** - collection du panneau des tunnels, onglet des tunnels, résolveur de route (`ResolveRoute(sessionId)` pour l'affichage de l'en-tête de session). S'abonne au `CollectionChanged` de `TunnelManager.ActiveTunnels` et s'en désabonne dans `Dispose`.
- **`ScheduledTasksViewModel`** - possession de `TaskSchedulerService`, câblage de `TasksProvider`/`TaskDueCallback`/`PersistCallback`, indicateur idempotent `_started` pour survivre à une réentrance de `LoadAsync`.
- **`SessionCoordinator`** - plaque tournante du cycle de vie des sessions : 8 câblages externes (5 fournisseurs/setters `Split.*` + 3 rappels `EmbeddedSessionManager` : `BroadcastCallback`, `IsBroadcastActive`, `ReconnectRequestedCallback`), la grappe du mode diffusion (bascule + diffusion + indicateurs par vue), `OnSessionReady` (matérialiser les onglets de session, résoudre la route de tunnel, enregistrer l'historique, ouvrir automatiquement le panneau compagnon SFTP) et `OnReconnectRequestedAsync` (fermer l'onglet obsolète + relancer le flux de connexion). `OpenToolCallback` est resté sur `MainViewModel` parce qu'`OpenToolTabAsync` relève de la coquille applicative, partagée avec les consommateurs barre latérale/onglet Outils/palette.

Deux sous-VM supplémentaires de la couche coquille ont été extraits en phase 2, pour refléter le modèle de liaison XAML de la barre latérale gauche :

- **`SidebarViewModel`** - bascule des onglets Sessions/Outils, texte du filtre d'outils, arbre `SidebarToolCategoryViewModel`, peuplement paresseux à la première activation, sélection de la cible de bascule `Ctrl+Shift+T` (le RadioButton frère doit être coché explicitement - voir le piège `ToggleSidebarTab()` dans la section Barre latérale).
- **`ToolsTabViewModel`** - état du VM du navigateur d'outils pleine page (favoris, récents, filtre, visibilité des sections). Le rendu des sections reste dans `ToolsTabPopulationService` (qui écrit dans des panneaux XAML nommés), câblé par un événement d'injection de Panel, de sorte que le VM ne touche jamais directement un `FrameworkElement`.

**Résultat** : `MainViewModel.cs` est passé de **1 917 à 628 lignes (-67 %)**. La classe coquille orchestre désormais l'instanciation des sous-VM, les réglages partagés, l'unique `OpenToolCallback` et le pipeline `LoadAsync` composé. Chaque sous-VM est navigable indépendamment, testable isolément (ceux qui ne touchent pas `Application.Current.Dispatcher` s'exécutent proprement sous xUnit) et possède son propre cycle de vie d'abonnement aux événements via `IDisposable`.

### 28. Migration vers l'i18n déclarative (phase 5)

**Problème** : avant la phase 5, `MainWindow.Localization.cs` portait la localisation sous forme d'une passe impérative de 523 lignes en code-behind. `ApplyLocalization()` s'exécutait au démarrage et à chaque notification `LocalizationManager.LocaleChanged`, en aiguillant vers 7 méthodes `Apply*Localization` (Navigation, Toolbar, Tunnel, Scheduled, Settings, About, Accessibility) qui touchaient plus de 300 éléments XAML nommés avec des affectations telles que `Mw_X.Text = vm.Localize("Key")`, `AutomationProperties.SetName(Mw_X, vm.Localize("Key"))`, `Mw_X.Tag = vm.Localize("Key")`, ainsi que les équivalents infobulle et en-tête. Chaque changement de langue relançait la passe complète et réécrivait libellés, infobulles, noms d'accessibilité et filigranes par leur nom.

**Solution** : la phase 5 a déplacé environ 307 sites de localisation impérative vers du XAML déclaratif, en utilisant l'extension de balisage `{loc:Translate Key}` existante. `TranslateExtension` et `LocalizationSource` sont volontairement restés inchangés : l'extension crée un `Binding` WPF vers `LocalizationSource.Instance[Key]`, et `LocalizationSource` lève `PropertyChanged("Item[]")` au changement de langue, de sorte que les DependencyProperty liées se rafraîchissent sans passe de rendu en code-behind.

La migration a été découpée par motif d'interface :

- **5A - libellés Navigation + Toolbar (58 sites)** : en-têtes de la barre d'onglets, contenus/infobulles des boutons de la barre d'outils, Quick Connect / Quick File Server, libellé de la bascule de diffusion, texte "prêt" de la barre de statut et indice de raccourcis. Les correspondances directes ont suivi le motif mécanique `Mw_X.Text = vm.Localize("Key")` → `Text="{loc:Translate Key}"`.
- **5B - attributs d'accessibilité (39 sites)** : tous les appels impératifs `AutomationProperties.SetName(Mw_X, vm.Localize("Key"))` sont passés à `AutomationProperties.Name="{loc:Translate Key}"` sur l'élément XAML propriétaire. `ApplyAccessibilityLocalization` a été supprimée entièrement.
- **5C.1 - Tunnel + Scheduled + About (40 sites)** : en-têtes de colonnes des DataGrid tunnels/tâches planifiées, en-têtes de menu contextuel, boutons d'action et libellés de champs migrés un pour un vers le XAML. `ApplyScheduledLocalization` n'avait plus de travail résiduel et a été supprimée.
- **5C.2 - onglet Paramètres (160 sites)** : la passe la plus dense, couvrant 6 sous-onglets de paramètres avec boutons radio, cases à cocher, libellés, filigranes, infobulles et groupes d'options. Elle inclut 24 migrations jumelées `Content` + `AutomationProperties.Name` couvrant les variantes de thème, la persistance de session, les modes de transport, les options d'affichage/audio RDP, les actions de passerelle, les boutons de mode d'application et les actions de fournisseur d'identifiants. `ApplySettingsLocalization` ne subsiste plus que comme stub de peuplement d'interface à l'exécution.
- **5D.1 - composites via `<Run>` inline (8 sites)** : les composites de la barre de statut (`" " + key + " " + key`) et les puces de fonctionnalités de la fenêtre A propos (`"\u2022 " + key`) ont été découpés en éléments `Run` inline anonymes contenant du texte littéral et des liaisons `{loc:Translate Key}`. `ApplyNavigationLocalization` et `ApplyAboutLocalization` ont été supprimées.
- **5D.2 - cas à forte logique (2 sites + extraction d'un helper)** : le libellé conditionnel de partage de dossier est sorti d'`ApplyToolbarLocalization` vers `UpdateShareFolderLabel()` sur `MainWindow`, appelé depuis `SharingStarted`, `SharingStopped`, le gestionnaire de langue et le démarrage, tandis que `FileShareService` reste non-INPC. Le découpage du `{0}` de l'en-tête du panneau des tunnels est passé dans `TunnelsViewModel.TunnelPanelHeaderPrefix` / `TunnelPanelHeaderSuffix`, avec des liaisons `Run` inline en `Mode=OneWay` et une re-notification `LocalizationManager.LocaleChanged`. `ApplyToolbarLocalization` et `ApplyTunnelLocalization` ont été supprimées.

**Résultat** : `MainWindow.Localization.cs` est passé de **523 à 122 lignes (-77 %)**. Ses responsabilités restantes ne relèvent délibérément pas de la localisation pure de libellés XAML :

- `ApplyLocalization()` - désormais un aiguillage à un seul appel vers `ApplySettingsLocalization(vm)`.
- `ApplySettingsLocalization()` - peuplement résiduel d'interface à l'exécution : `PopulateCredProvPresets`, `PopulateExtToolPlaceholderList`, `UpdateExtToolPreview`, `UpdateExternalToolProviderStatus` et le contrôle asynchrone du statut de jeton. Ces helpers génèrent ou mettent à jour une interface dynamique à partir de l'état d'exécution, ils restent donc impératifs jusqu'à une extraction dédiée de helpers de paramètres.
- `RefreshVmDrivenLocalization(vm)` - helper appelé depuis le constructeur et depuis le gestionnaire de changement de langue pour rafraîchir les libellés de l'onglet Outils pilotés par le VM, auparavant cascadés par `ApplyLocalization()`. Cela préserve le comportement de rafraîchissement des sous-VM après réduction de l'aiguilleur à son unique appel Paramètres.

Le test de fumée final de la phase 5 a également révélé une régression latente de la palette de commandes issue du chemin de refactorisation des phases 2A/4A : un simple clic dans la palette fermait le popup avant que le double-clic ne puisse se déclencher. `OnWindowPreviewMouseDown` protège désormais les clics issus de `CommandPalettePopup.Child`, avec des contrôles de repli sur `IsMouseOver` et sur les limites, ce qui préserve la fermeture au clic extérieur tout en autorisant la sélection normale dans la ListBox et l'exécution au double-clic.

**Travaux futurs** :

- `FileShareService` peut implémenter `INotifyPropertyChanged` afin que `Mw_ShareFolderLabel` devienne une pure liaison et que `UpdateShareFolderLabel()` disparaisse.
- Huit clés d'accessibilité (cinq onglets de navigation issus de la phase 5B et trois boutons de passerelle issus de la phase 5C.2) conservent pour l'instant le comportement impératif au lieu des variantes `Access*`, plus descriptives. Si des tests NVDA signalent des problèmes de formulation, il s'agira d'un simple échange de clés en XAML.
- Les 5 helpers de paramètres résiduels peuvent être déplacés vers des services dédiés CredProv / outils externes, ce qui éliminerait entièrement `MainWindow.Localization.cs`. C'est un nettoyage architectural, pas une exigence de migration i18n.

### 29. Résolution de la bibliothèque de commandes après connexion

Le lot 57 a introduit des étapes structurées post-connexion pour les sessions SSH
embarquées. Le lot 58 conserve un exécuteur sans état mais ajoute un pont de
résolution à l'exécution vers TwinShell, de sorte qu'une étape peut soit rester
littérale (`Input`), soit référencer une action de la bibliothèque de commandes
par son identifiant. Le contrat de données reste additif sur
`Heimdall.Core.Models.PostConnectStep` :

- `Input` reste la commande littérale et est préservé pour l'UX de dissociation.
- `CommandLibraryId` identifie l'action TwinShell liée.
- `CommandLibraryParams` stocke les valeurs de paramètres indexées par
  `TemplateParameter.Name`.

`SessionCoordinator` reste propriétaire du point de déclenchement SSH embarqué,
mais il passe désormais un `IPostConnectStepResolver` optionnel à
`IPostConnectSequenceRunner.RunAsync(...)`. Le résolveur est implémenté dans App
parce qu'il a besoin des services TwinShell, tandis que la logique de migration
reste dans Core, aux côtés de `ServerProfileDto` et `ConfigManager`.

La chaîne de résolution est la suivante :

1. `SessionCoordinator.OnSessionReady` démarre la séquence post-connexion pour les
   onglets SSH embarqués uniquement.
2. `PostConnectSequenceRunner` inspecte chaque étape.
3. Une étape littérale (`CommandLibraryId == null`) exécute `Input` exactement
   comme au lot 57.
4. Une étape liée ouvre une portée DI neuve via `IServiceScopeFactory`, résout
   `IActionService` et `ICommandGeneratorService`, et tente de résoudre le
   modèle de commande Linux à l'exécution.
5. Une résolution réussie émet `Resolved` et la commande Linux générée est
   écrite dans le rappel de session.
6. Les défauts de configuration (`action missing`, `no Linux template`,
   `invalid parameters`) émettent `Broken`, incrémentent `StepsBroken` et
   poursuivent la séquence sans honorer `OnFailure.Stop`.

Cela maintient le lien vers la bibliothèque de commandes à jour à chaque connexion,
évite de mettre en cache des services TwinShell à portée limitée, et empêche
qu'une entrée de bibliothèque obsolète ou supprimée n'exécute silencieusement le
repli littéral dormant.

Le lot 59 laisse l'exécution intacte et n'améliore que la rédaction. La
`ServerDialog` capture un `AutoPrefillContext` minimal (`Host`, `Port`,
`Username`, `ConnectionType`) à l'ouverture du sélecteur de bibliothèque de
commandes. Le sélecteur applique une table d'alias stricte (`host`/`hostname`/...,
`port`/`sshPort`/..., `user`/`username`/...) pour préremplir une seule fois les
paramètres correspondants, au moment de la sélection. Le préremplissage est un
simple instantané, jamais lié en direct aux champs du serveur, et les valeurs de
paramètres existantes l'emportent toujours. Les paramètres dont le nom technique
correspond à la liste noire des secrets (`password`, `token`, `secret`, etc.) sont
structurellement exclus du préremplissage, même si de futures tables d'alias
s'élargissent.

### 29b. Chemin d'import de profils unifié

**Problème** : deux flux divergents servaient à importer un fichier de configuration `.rdp` ou `.json`. Le glisser-déposer sur la fenêtre principale passait par le flux riche `ServerListViewModel.ImportRdpFilesAsync` (aperçu + résolution de conflits par élément + fusion / remplacement / ignorer). Le bouton `Paramètres -> Importer` analysait le fichier en ligne, sans aperçu ni résolution de conflits. L'utilisateur ne savait pas quel point d'entrée utiliser, et les deux chemins pouvaient diverger avec le temps.

**Solution** : `IProfileImportService` (dans `Heimdall.App/Services/Import/`) est l'orchestrateur unique des deux points d'entrée. Il branche selon l'extension du fichier, délègue l'analyse `.rdp` à `IRdpImportService`, traite nativement les charges utiles de configuration `.json` et remonte un `ProfileImportResult` à l'appelant une fois que l'utilisateur a résolu les conflits via `RdpImportDialogViewModel`. `ServerListViewModel.ImportRdpFilesAsync` n'est plus qu'une enveloppe fine qui transmet la liste de fichiers au service, si bien que l'UX du glisser-déposer est préservée à l'identique. `SettingsViewModel.ImportConfigAsync` est également une enveloppe fine autour du même service - les anciennes méthodes `ImportRdpFileAsync` et `ImportJsonAsync` ont été supprimées (pas de code mort).

Les formats historiques (`MobaXterm`, `RDCMan`, `mRemoteNG`) conservent leurs analyseurs dédiés et ne sont pas routés par le nouveau service. Leurs entrées de filtre d'import restent dans l'OpenFileDialog afin de ne pas régresser les usages existants.

## Design System (CommonControls.xaml - plus de 1 880 lignes, 45 tokens, WCAG AA)

L'application utilise un Design System centralisé défini dans `CommonControls.xaml`, adossé au paquet NuGet `ThemeForge.Theme` et au pont de brosses applicatives `HeimdallThemeBridge.xaml`. Les 17 palettes ThemeForge fournissent les emplacements de couleurs canoniques ; le pont réexprime les 74 clés de brosses applicatives de Heimdall sur ces emplacements. Le changement de thème est porté par `HeimdallThemeService` (singleton DI) - voir `docs/TROUBLESHOOTING.md` ("Theme Switching - Stale Colors After Swap") pour les motifs de réactivité.

**Tokens de typographie (10)** - ressources `sys:Double` pour un dimensionnement de police cohérent :
- `FontSizeSmallCaption` (11), `FontSizeCaption` (12), `FontSizeBody` (13), `FontSizeBodyLarge` (14), `FontSizeSubtitle` (15), `FontSizeLarge` (17), `FontSizeTitle` (20), `FontSizeDisplay` (22), `FontSizeHeadline` (24), `FontSizeHero` (64)
- Usage : `FontSize="{StaticResource FontSizeBody}"` au lieu de `FontSize="12"`

**Tokens de famille de police** :
- `FontFamilyMonospace` (`Consolas, Courier New, monospace`) - utilisé pour les champs de chemin, les éditeurs de code, le texte de terminal

**Tokens d'espacement (5 uniformes + 3 asymétriques)** - ressources `Thickness` pour marges et remplissages :
- Uniformes : `SpacingXs` (4), `SpacingSm` (8), `SpacingMd` (12), `SpacingLg` (20), `SpacingXl` (24)
- Asymétriques : `ContentAreaMargin` (16,0,16,16) pour les zones de contenu d'outil, `SessionHeaderPadding` (8,4) pour les bandeaux d'en-tête de session, `ToolHeaderPadding` (12,8) / `ToolFooterPadding` (12,8) pour les en-têtes/pieds des panneaux d'outil
- Remplissage de bouton par rôle : `PaddingButtonHelp` (6,2), `PaddingButtonCopy` (10,4), `PaddingButtonPrimary` (12,6), `PaddingButtonPreset` (8,2) - les boutons Copier/Exporter doivent utiliser `PaddingButtonCopy`, pas `PaddingButtonPrimary`
- Remplissage des champs de saisie : `PaddingInput` (8,6) pour toutes les TextBox
- Les marges asymétriques réellement ponctuelles (`Margin="0,0,8,0"`) restent en dur - pratique WPF standard

**Tokens de rayon d'angle (5)** : `CornerRadiusXs` (2), `CornerRadiusSm` (4), `CornerRadiusMd` (8), `CornerRadiusLg` (10), `CornerRadiusXl` (12)

**Tokens d'opacité (4)** : `OpacityDisabled` (0.55), `OpacityReadOnly` (0.75), `OpacityOverlay` (0.20), `OpacityAccentOverlay` (0.20)

**Tokens de taille d'icône (6)** : `IconSizeSmall` (12), `IconSizeMedium` (16), `IconSizeLarge` (20), `IconSizeXLarge` (36), `IconSizeEmptyState` (32), `IconSizeHero` (48)

**Brosses de catégorie d'outil** - 5 couleurs distinctes par catégorie d'outil (définies dans le pont sur les emplacements ThemeForge) :
- `ToolNetworkBrush` (bleu), `ToolSecurityBrush` (orange), `ToolEncodingBrush` (violet), `ToolSystemBrush` (cyan), `ToolExternalBrush` (rose)
- Chaque outil possède un glyphe propre (Segoe MDL2 Assets) + la couleur de sa catégorie, dans l'arborescence comme dans la palette

**Micro-animations** - transitions discrètes pour les panneaux qui apparaissent ou disparaissent :
- `FadeInPanelStyle` : `DoubleAnimation` d'opacité de 0 à 1 en 150 ms sur `Visibility=Visible`
- Tokens de durée : `AnimationFast` (150 ms), `AnimationMedium` (250 ms)
- Appliqué à : surimpression de chargement de session, surimpressions de reconnexion SSH/RDP/VNC

**Accessibilité** :
- `FocusIndicatorBrush` (cyan en thème sombre, bleu en thème clair) - anneau de focus clavier dédié sur tous les styles de bouton
- `TextOnAccentBrush` (blanc) - utilisé sur les surfaces à couleur d'accent (boutons, sélections de DataGrid, cases à cocher)
- Toutes les paires premier plan/arrière-plan vérifiées pour WCAG AA (rapport de contraste minimal de 4,5:1)
- `AutomationProperties.Name` sur tous les contrôles interactifs de l'ensemble des 57 vues d'outil + toutes les vues de dialogue, via le motif `SetName()` localisé à l'exécution dans `ApplyLocalization()` - aucun emplacement XAML vide
- `Focusable="False"` sur les TextBlock d'icônes décoratives (glyphes MDL2 des états vides), pour les exclure du focus clavier et de la navigation par lecteur d'écran
- `ToolAsyncStateController` : gestion centralisée de la visibilité chargement/erreur/état vide/résultats pour les outils asynchrones (13 outils l'ont adopté)
- `ToolLoadingBarStyle` (indéterminée, 4 px) et `ToolDeterminateProgressBarStyle` (déterminée, 20 px) - toutes les ProgressBar d'outil utilisent des styles partagés
- Motif d'en-tête d'outil : ligne 0 = titre + bouton d'aide uniquement ; les contrôles de saisie vont dans un bandeau de saisie dédié (ligne 2)

**Icônes de protocole** - géométries vectorielles `Geo.Protocol.*` uniques par type de protocole dans le TreeView :
- RDP, SSH, SFTP, Local Shell, Citrix, VNC, Telnet, FTP

**19 styles de contrôle thématisés** avec couverture complète des états (survol, pressé, focus, désactivé) :
- Window, PrimaryButton, SecondaryButton, ToolbarGhostButton, TextBox, PasswordBox, ComboBox, TabControl, TabItem, TreeView, ContextMenu, MenuItem, CheckBox, RadioButton, ToolTip, ListBox, Expander, ProgressBar, Slider, DataGrid

**Valeurs par défaut globales** :
- `DataGrid.ClipboardCopyMode="IncludeHeader"` - active le Ctrl+C natif sur toutes les DataGrid
- Déclencheur `TextBox.IsReadOnly` - fond `SurfaceBrush` + `Opacity=0.75` pour les champs en lecture seule
- `TreeViewItem`/`ListBoxItem` - déclencheur `IsKeyboardFocused` avec bordure `FocusIndicatorBrush`

## Flux de connexion

```
User clicks Connect
        |
        v
ConnectionService.ConnectAsync(server)
        |
        +-- Resolve gateway chain (GatewayChainResolver)
        |       |
        |       +-- For each gateway: establish SSH tunnel
        |       |       |
        |       |       +-- AuthPreflightChecker: validate credentials
        |       |       +-- SshConnectionFactory: create SSH.NET client or Plink fallback
        |       |       +-- TunnelManager: start port forward
        |       |
        +-- Determine connection type
        |
        +-- RDP?
        |       +-- EmbeddedRdpView: ActiveX MsTscAx
        |       |       +-- Layout flush -> Connect() -> OnConnected -> credential autofill
        |       +-- OR mstsc.exe (external) + CredentialAutofill polling
        |
        +-- SSH?
        |       +-- EmbeddedSshView: WebView2 + xterm.js
        |               +-- PipeModeSession: plink -t -> stdin/stdout pipes
        |               +-- OR SshShellSession: SSH.NET shell stream
        |
        +-- SFTP?
        |       +-- EmbeddedSftpView: file browser panel
        |               +-- SftpSessionBundle: SftpClient (file ops) + SshClient (sudo exec)
        |               +-- RemoteFileEditor: FileSystemWatcher auto-upload (2s debounce)
        |               +-- Sudo fallback: permission denied -> sudo cat/sudo tee via SSH exec
        |
        +-- Citrix?
        |       +-- EmbeddedCitrixView: StoreBrowse session tab
        |               +-- storebrowse.exe: StoreFront auth + resource enumeration
        |               +-- ICA file generation -> Citrix Workspace launch
        |
        +-- VNC?
        |       +-- EmbeddedVncView: WebView2 + noVNC
        |               +-- WebSocketVncProxy: WS-to-TCP bridge on random local port
        |               +-- noVNC connects to ws://localhost:{port}
        |
        +-- Telnet?
        |       +-- EmbeddedSshView (reused): WebView2 + xterm.js
        |               +-- TelnetSession: raw TCP + IAC negotiation
        |
        +-- FTP?
                +-- EmbeddedSftpView (reused): file browser panel
                        +-- FtpBrowser: IRemoteBrowser over FluentFTP
```

## Machines à états

### Machine à états de connexion

Etats : `Disconnected` → `Initializing` → `ValidatingConfig` → `EstablishingTunnel` → `TunnelEstablished` → `LaunchingRdp` / `LaunchingSsh` / `LaunchingSftp` / `LaunchingCitrix` / `LaunchingVnc` / `LaunchingTelnet` / `LaunchingFtp` → `Connected` → `Disconnecting` → `Disconnected`

L'état d'erreur est atteignable depuis n'importe quel état actif. Les transitions sont validées avant application.

### Machine à états applicative

Etats : `Initializing` → `Ready` <-> `Busy` → `Shutdown`

L'état d'erreur est atteignable depuis Ready ou Busy.

## Architecture de sécurité

| Couche | Mécanisme |
|---|---|
| Stockage des identifiants | DPAPI (portée utilisateur) + intégrité HMAC-SHA256 via le `CredentialProtector` unifié |
| Migration de l'existant | `CredentialProtector.Unprotect` accepte les blobs protégés par HMAC comme les blobs DPAPI simples |
| Gestion de la clé HMAC | Générée automatiquement au premier lancement, protégée par DPAPI, stockée dans `settings.json` |
| Protection par code PIN | PBKDF2-SHA256, 100 000 itérations, sel de 128 bits via `PinManager` |
| Protection des fichiers | ACL Windows (utilisateur + Admins + SYSTEM) via `AclEnforcer` sur les répertoires de configuration, les journaux et les fichiers temporaires |
| Validation des entrées | Expressions régulières compilées contre l'injection (CWE-78) via `InputValidator` : `EscapeShellArg()`, `EscapeForDoubleQuotedString()`, `ValidateDomain()`, `SanitizeCsvCell()` |
| Prévention de l'injection shell | `InputValidator.EscapeShellArg()` appliqué à tous les appels `CreateCommand()` des tunnels SSH et des outils (plus de 16 vues d'outil) |
| Injection de formule CSV | `InputValidator.SanitizeCsvCell()` dans 10 exportateurs + le `ToolContextMenuHelper` générique |
| Assainissement CRLF | Construction brute de l'en-tête HTTP Host assainie contre l'injection d'en-têtes |
| Construction de commandes | Listes d'arguments structurées pour Plink/gsudo (aucune concaténation de chaînes issues de l'utilisateur) |
| Assainissement des emplacements | Sensible au contexte : `InputValidator.IsShellTarget()` détecte les interpréteurs (cmd, powershell, bash, wsl, cscript, mshta + .bat/.cmd/.ps1/.vbs/.js/.wsf/.hta) ; les cibles shell subissent un filtrage strict des métacaractères, les cibles .exe ordinaires un filtrage relâché qui préserve `()`, `'`, `%` dans les valeurs légitimes |
| Traversée HTTP/TFTP | Contrôle du séparateur final + comparaison exacte à la racine dans EphemeralFileServer |
| Concurrence de configuration | Verrou d'écriture SemaphoreSlim dans ConfigManager pour éviter le dernier-écrivain-gagne |
| Durcissement WebView2 | CSP (`default-src 'none'`), blocage de navigation, validation de la source des `WebMessage` |
| IPC Pageant | Vérification de l'identité du propriétaire du processus avant tout accès à la mémoire partagée |
| Remplissage d'identifiants | Limité à la lignée du processus mstsc + correspondance d'indice d'hôte, classe `#32770` exclue |
| CredMan RDP | Persistance à la portée de la session, nettoyage déterministe après lancement de la session |
| Sécurité des fichiers temporaires | ACL appliquées sur les fichiers .rdp, Plink -pwfile (ACL atomique, sans repli), répertoires d'édition SFTP |
| Prévention XXE | `DtdProcessing.Prohibit` + `XmlResolver = null` sur tous les importateurs XML |
| Validation des arguments Citrix | Contrôle des métacaractères shell sur `CitrixLaunchCommandLine` avant `Process.Start` |
| Confiance des hôtes SSH | TOFU confirmé par l'utilisateur via `IHostKeyVerifier` ; dépendances de clé d'hôte non nulles sur les points d'entrée de production ; empreintes persistées dans `settings.json`, chargées au démarrage et appliquées sur les chemins SSH.NET et Plink |
| Repli Plink | `PlinkHostKeyDecider` échoue en fail-closed avec `HostKeyUnavailable` lorsqu'aucune empreinte approuvée par Heimdall ne peut être résolue |
| Réutilisation de tunnel | Cible distante + mode de renvoi + `GatewayChainKey` empêchent une réutilisation inter-passerelles sur des plages privées qui se chevauchent |
| Escalade sudo SFTP | Uniquement sur permission refusée typée ; pas d'escalade sudo par sous-chaîne `Failure` générique |
| FTP en clair | Un FTP avec identifiants sans TLS émet un `ConnectionResult.Warning` non bloquant |
| Ecritures de fichiers | UTF-8 sans BOM via `SecureFileWriter` |
| Mémoire | Identifiants effacés après injection COM, `SecureString` sur les chemins de transmission |
| Gestion des exceptions | Gestionnaires globaux enregistrés avant le premier await, exceptions de tâches non observées interceptées |
| Identifiants externes | `CredentialProviderFactory` sélectionne le fournisseur en ligne de commande (KeePassXC, KeePass2/KPScript, Bitwarden, 1Password, pass) ou le Gestionnaire d'identifiants Windows natif ; délai configurable, secret de déverrouillage optionnel via stdin, rechiffrement DPAPI avant usage |
| Barrière Windows Hello | Vérification biométrique/PIN optionnelle en fail-closed (`UserConsentVerifier`) avant usage des identifiants stockés, en connexion unitaire comme groupée, avec fenêtre de grâce configurable |
| Journalisation | `FileLogger.Dispose()` vide le tampon avant de se marquer libéré (aucun diagnostic perdu) |

## Architecture du panneau Paramètres

Le panneau Paramètres utilise un `TabControl` à navigation latérale gauche, avec 6 sous-onglets :

| Sous-onglet | Réglages |
|---------|----------|
| **Général** | Apparence : langue, thème, nombre maximal de sessions, empêcher la mise en veille, repli des tunnels par défaut |
| **Terminal** | Famille de police, taille de police, jeu de couleurs |
| **SSH & SFTP** | Chemin de Plink, mode par défaut, anti-inactivité, réinitialisation de TMOUT, ouverture automatique du SFTP, X11, passerelles |
| **RDP** | Mode par défaut, résolution, profondeur de couleur, audio, NLA, résolution dynamique, multi-écran, redirection de périphériques, cache, réglage de la reconnexion et du keep-alive, préréglages de résolution modifiables |
| **Sécurité** | Fournisseur d'identifiants externe (commande/base de données/parcourir/préréglages/test), Credential Guard |
| **Avancé** | Journalisation, journalisation de session, délais d'expiration (tunnel/RDP/outils externes), Partage de fichiers (activation TFTP + avertissement), Editeur externe (chemin + parcourir), détection des outils tiers, synchronisation de la bibliothèque de commandes, liste des outils externes (éditer/aperçu/test/valider) |

Les boutons d'action (Enregistrer / Réinitialiser / Exporter / Importer) sont épinglés en bas, toujours visibles quel que soit le sous-onglet.

Persistance des réglages : ViewModel → AppSettings → ConfigManager → settings.json (UTF-8 sans BOM). Les écritures de ConfigManager sont protégées par un `SemaphoreSlim` afin d'éviter la corruption par enregistrements concurrents.

Les réglages par profil qui reflètent une valeur globale d'`AppSettings` se résolvent dans un ordre fixe : la valeur du profil quand elle est non nulle, puis le réglage global, puis une valeur codée en dur en dernier filet de sécurité. `RdpResizeEnableDelayMs` est l'implémentation de référence, via le helper pur `EmbeddedSessionManager.ResolveRdpResizeEnableDelayMs` : un profil à `0` désactive le verrouillage de redimensionnement post-connexion, les valeurs de profil négatives sont ramenées à `0` à l'exécution alors que la validation de schéma et de dialogue les rejette, et un réglage global négatif retombe sur la valeur par défaut de 10 000 ms avec un journal en Warning (`038992f`).

## Stratégie de déploiement de WebView2

WebView2 est requis pour les terminaux SSH embarqués (xterm.js) et les sessions VNC (noVNC). `WebView2Helper` centralise la détection du runtime :

1. **Runtime Fixed Version embarqué** dans `runtimes/webview2/` (édition Self-Contained, environ 436 Mo)
2. **Runtime Evergreen système** via Edge ou l'installeur autonome (édition Standard)
3. **Indisponible** - affiche un message d'erreur localisé, sans plantage

Editions de build :

| Edition | Option de build | Taille (zip / installeur) | WebView2 |
|---------|-----------|----------------------|----------|
| **Standard** | `-Variant Standard` | environ 177 Mo / 127 Mo | Nécessite Edge (préinstallé sur Windows 10/11) |
| **Self-Contained** | `-Variant SelfContained` | environ 397 Mo / 288 Mo | Runtime Fixed Version embarqué pour les environnements isolés ou déconnectés |

`Build.ps1 -Variant Both` (valeur par défaut) produit les deux variantes + les installeurs Inno Setup. `Build.ps1 -Mode Release -Publish` crée une release GitHub avec tous les artefacts. `Build.ps1 -DryRun` simule la release sans toucher à git/GitHub. Raccourcis batch : `Run.bat`, `Test.bat`, `Build.bat`, `Release.bat`.

### Baseline de tests

`dotnet test Heimdall.slnx --no-build` découvre 5 578 tests répartis sur les cinq projets de test (`Heimdall.App.Tests`, `Heimdall.App.UiTests`, `Heimdall.Core.Tests`, `Heimdall.Rdp.Tests`, `Heimdall.Ssh.Tests`) : 5 578 verts et 0 ignoré. Les fichiers TRX partiels par projet peuvent rapporter des totaux plus faibles et être pris à tort pour une régression - toujours lancer la commande agrégée pour obtenir une baseline correcte.

## Architecture des outils

### ToolRegistry (source unique de vérité)

Les 58 outils intégrés sont enregistrés dans `ToolRegistry` (singleton). Chaque outil est décrit par un enregistrement `ToolDescriptor` :

```csharp
public record ToolDescriptor(
    string Id,                  // "PING", "CERTGEN", etc.
    ToolCategory Category,      // Network, Security, Encoding, System
    string CategoryLabelKey,    // i18n key for category header
    string LabelKey,            // i18n key for tool name
    string? LabelWithArgKey,    // i18n key for "tool with argument" variant
    string[] CommandPrefixes,   // Palette aliases: ["ping"], ["dns","dig"]
    bool IsNetworkTool,         // Prompts for host when opened standalone
    string? IconResourceKey);   // XAML BitmapImage key: "Icon.Tool.PortScanner"
```

Le registre fusionne en une unique collection ordonnée trois listes auparavant dupliquées (définitions de menu, commandes de palette, switch de fabrique de vues). Ajouter un nouvel outil demande :
1. Un fichier XAML + code-behind implémentant `IToolView`
2. Une ligne `Entry()` dans `ToolRegistry`
3. Des clés i18n dans les deux fichiers de locale

### Interface IToolView

```csharp
public interface IToolView : IDisposable
{
    void Initialize(ToolContext? context, LocalizationManager? localizer);
    bool CanClose() => true; // default implementation, override to prevent close during async ops
}
```

Toutes les vues d'outil respectent ce contrat. `EmbeddedSessionManager.CreateToolControl()` utilise le délégué de fabrique du registre pour instancier les vues sans aucun aiguillage propre à un protocole. `SplitService.CloseAllPanes()` vérifie `CanClose()` panneau par panneau avant libération - ce qui fonctionne pour les onglets d'outil autonomes comme pour les panneaux d'outil au sein de splits mixtes (par exemple SSH + outil dans le même onglet). `SplitService.ClosePane()` vérifie également `CanClose()` à la fermeture d'un panneau d'outil individuel dans un arbre de split. `MergeExistingSession` affiche un message dans la barre de statut lorsqu'un outil occupé bloque la fusion.

### ToolContextMenuHelper (actions DataGrid partagées)

Actions de menu contextuel standard partagées entre les DataGrid des outils :
- `BuildHostActions()` : navigation croisée entre outils (Ping, PortScan, DNS, Whois, Cert, Navigateur, Ajouter aux serveurs)
- `BuildCopyRowAction()` : copier la ligne sélectionnée en texte séparé par tabulations
- `BuildCopyAllAction()` : copier toutes les lignes avec les en-têtes
- `BuildExportCsvAction()` : exporter la DataGrid en fichier CSV via SaveFileDialog
- `SelectRowOnRightClick()` : sélectionner la ligne sous le curseur au clic droit

### ToolContext (contexte serveur enrichi)

```csharp
public record ToolContext(
    string? TargetHost, int? TargetPort, string? Argument,
    string? DisplayName, string? Username, string? ConnectionType,
    string? ProjectName, string? GroupName, string? SourceServerId);
```

Lorsqu'un outil est ouvert depuis le menu contextuel d'un serveur, toutes les métadonnées serveur disponibles lui sont transmises. Les outils réseau préremplissent leur champ hôte ; les outils de sécurité peuvent exploiter le contexte d'identifiants.

### Navigation entre outils

- **Ctrl+Shift+T** : bascule entre les onglets Serveurs et Outils de la barre latérale gauche (RadioButton groupés `SidebarTabServers` / `SidebarTabTools`)
- **Ctrl+K → "tools"** : la palette de commandes liste tous les outils regroupés par catégorie
- **Ctrl+K → "ping 10.0.0.1"** : ouvre l'outil avec l'argument prérempli
- **Outils récents** : les 5 derniers outils utilisés sont affichés en tête de palette à l'ouverture
- **Comportement singleton** : les outils sans contexte (UUID, Password, Chmod) réutilisent l'onglet existant
- **Outils externes** : également cherchables dans la palette Ctrl+K
- **Système d'aide** : un bouton "?" sur les 49 outils affiche une description localisée, un mode d'emploi et des exemples (motif de clé i18n : `ToolHelp<ID_EN_MAJUSCULES>`, par exemple `ToolHelpBASE64`)
- **Panneau de détail** : sélectionner un outil dans le TreeView affiche un panneau dédié (nom, catégorie, description, "Ouvrir dans un onglet")
- **Préréglages de mot de passe** : les préréglages personnalisés sont enregistrés dans `config/password-presets.json`, restaurés au clic et supprimés par clic droit
- **Couleurs de protocole** : les brosses sensibles au thème sont définies dans `HeimdallThemeBridge.xaml` sur les emplacements ThemeForge - résolues via `DynamicResource` lorsque c'est possible, et réévaluées au changement de thème via les déclencheurs `HeimdallThemeService.ThemeRevision` pour les liaisons à base de convertisseur
- **Navigation croisée** : `ToolContextMenuHelper` avec le rappel `OpenToolAction` permet un clic droit → ouvrir un autre outil avec le contexte prérempli

### Outil Notes (façon Obsidian)

L'outil Notes (n° 34) offre une expérience d'édition Markdown local-first inspirée d'Obsidian :

**Pile d'édition** :
- **Principal** : éditeur WYSIWYG Milkdown (basé sur ProseMirror, licence MIT) hébergé dans WebView2. Empaqueté en un unique `Assets/milkdown/index.html` (Vite + vite-plugin-singlefile). Sources dans `Assets/milkdown-editor/`.
- **Repli** : AvalonEdit avec `MarkdownHighlighting` (XSHD) + `MarkdownLivePreviewTransformer` (mise à l'échelle des titres, décorations barrées, caractères de syntaxe estompés).
- **Sélection** : `MilkdownEditorControl.IsAvailable` vérifie la présence de l'asset `index.html` ; `IsHostInitialized` vérifie que l'hôte WebView2 a bien été créé ; l'initialisation de WebView2 est différée à l'événement `Loaded` via un rendu de main au dispatcher `WaitUntilLoadedAsync()`. Repli sur AvalonEdit si `!IsHostInitialized` après `InitializeAsync()`.

**Pont C# <-> JS** (`MilkdownEditorControl`) :
- JS → C# : `ready`, `change { markdown, dirty }`, `open-link { payload }`
- C# → JS : `set-content`, `set-theme`, `set-readonly`, `focus`, `insert`, `set-menu-labels`
- Synchronisation du contenu via l'événement `ContentChanged` (anti-rebond de 200 ms côté JS)
- Thème : palette Dracula en mode sombre via les jetons CSS Crepe `--crepe-*` (l'ancien `@milkdown/theme-nord` a été retiré). La coloration AvalonEdit utilise des couleurs Dracula assorties

**Gestion des fichiers** (`NotesStorageService`) :
- Stockage : `config/notes/` (configurable via `AppSettings.NotesDirectory`)
- `NoteTreeNode.BuildTree()` : construit une arborescence calquée sur le système de fichiers à partir d'une liste plate de notes, dossiers vides inclus via `AddEmptyFolders()`
- Liens entre notes : `FindNotePathAsync()` résout par titre → nom de fichier → slug → chemin relatif, avec repli insensible aux accents (`RemoveDiacritics`) ; `ResolveOrCreateNoteAsync()` crée la note en cas d'échec
- Tags : extraits des lignes de métadonnées `> tags: x, y` dans les blocs de citation
- Traversée de chemin : `ValidatePathWithinRoot()` sur toutes les opérations d'entrée/sortie
- Enregistrement synchrone : la méthode synchrone `SaveNote()` pour `CanClose()`/`Dispose()` (évite le sync-over-async)

**Bascule de la barre latérale** : le bouton hamburger de l'en-tête replie/déplie le panneau TreeView. La largeur est persistée dans `AppSettings.NotesSidebarWidth` via `ConfigManager.MergeSettingAsync()` (charger-muter-enregistrer atomique sous verrou d'écriture).

**Localisation des modèles** : `NotesTemplateFactory.Create()` accepte un `LocalizationManager` optionnel - tous les intertitres utilisent des clés i18n `ToolNotesTpl*`. `Slugify()` supprime les diacritiques par normalisation Unicode, de sorte que les titres français produisent des noms de fichiers compatibles ASCII.

**Menu contextuel de l'éditeur** : un clic droit dans l'éditeur affiche 17 actions de mise en forme Markdown (gras, italique, titres, listes, liens, blocs de code, tableau, filet horizontal). Dans Milkdown : menu contextuel natif JS avec libellés localisés via le message `set-menu-labels`. Dans AvalonEdit : `ContextMenu` WPF construit dynamiquement avec les helpers `WrapEditorSelection`, `PrefixEditorLines`, `InsertInEditor`.

**Menu contextuel du TreeView** : `OnTreeViewContextMenuOpening` construit le menu dynamiquement ; `OnTreeViewPreviewRightClick` stoppe la propagation descendante pour éviter l'interception par `MainWindow.OnSessionTabRightClick`. `MainWindow` exclut également les `TreeView` situés dans des sessions `TOOL:*` du menu d'onglet de session.

**Glisser-déposer** : interne (déplacer une note entre dossiers via `MoveNoteToFolderAsync`) et externe (import de fichiers .md par copie vers la racine des notes)
- **Moteur de cartographie réseau** : l'espace de noms `Heimdall.Core.Discovery/` avec CartographyEngine (balayage ping + capture de TTL, scan de ports, capture de bannière, extraction d'en-têtes HTTP/HTTPS, inspection de certificat TLS, sondes UDP NetBIOS/SNMP/mDNS, empreinte d'OS, saut de cache KB), UdpProbeEngine (NetBIOS NBSTAT brut + GET SNMPv2c + découverte de services mDNS), OsFingerprinter (TTL + 33 motifs de bannière), RoleClassifier (plus de 46 motifs de port, plus de 96 empreintes de bannière, CnRegex compilé, ClassifyEnriched multi-sources), OuiDatabase (plus de 300 préfixes MAC), VlanDetector, DrawIoExporter, ScanHistoryManager (écriture atomique, ACL, rétention, diff HostChange typé), KnowledgeBaseManager (helpers purs et statiques pour les horodatages persistants Observation\<T\> par champ, fusion au scan, accélération de cache par TTL, purge d'hôte) et `INetworkKnowledgeBaseStore` (couture de persistance pour le ViewModel ; `FileNetworkKnowledgeBaseStore` par défaut, `InMemoryNetworkKnowledgeBaseStore` pour les tests). `CartographyEngine` continue d'utiliser la surface de helpers purs, tandis que le ViewModel achemine ses entrées/sorties par la couture de stockage introduite pour corriger la course au chargement initial de la phase 3.6.
- **Stratégie d'exécution PowerShell** : configurable dans Paramètres > Terminal, appliquée comme option `-ExecutionPolicy` au lancement du shell local
- **Modes d'élévation** : `None` / `Auto` (gsudo `--direct` → repli en fenêtre externe) / `Gsudo` / `Runas` - `Auto` par défaut pour la compatibilité AdminByRequest/CyberArk/BeyondTrust, configurable par profil serveur

### Sérialisation de l'initialisation de la cartographie réseau

`NetworkCartographyViewModel.Initialize()` reste synchrone parce qu'il implémente le contrat `IToolView`. Le chargement asynchrone des statistiques de la KB est capturé dans un `_initialLoadTask` interne, au lieu d'être laissé en fire-and-forget non suivi. `WaitForInitialLoadAsync()` expose cette tâche aux appelants et aux tests, et les opérations destructrices telles que `ClearKbAsync` l'attendent en toute première étape. Cela empêche des données d'initialisation obsolètes d'écraser une KB fraîchement vidée, et constitue le motif de référence pour les futurs ViewModels d'outil qui doivent sérialiser une initialisation asynchrone derrière une surface `Initialize()` synchrone.

### Système de thèmes (`HeimdallThemeService` + ThemeForge)

**Problème** : le changement de thème à l'exécution parmi les 17 palettes ThemeForge doit maintenir toutes les surfaces de Heimdall synchronisées - y compris les brosses propres à l'application, les convertisseurs qui résolvent des brosses au moment de la conversion (icônes de serveur, pastilles de statut), l'éditeur de fichiers AvalonEdit et le chrome de barre de titre DWM.

**Solution** : `Services/HeimdallThemeService.cs` est l'enveloppe de compatibilité de Heimdall autour de `ThemeForge.Theme.ThemeService`.

- **Source du paquet** : Heimdall consomme `ThemeForge.Theme` depuis le flux public nuget.org (restauration anonyme, sans jeton). ThemeForge possède les 17 dictionnaires de palette et injecte la palette active dans `Application.Resources.MergedDictionaries`.
- **Singleton DI** : enregistré une seule fois dans `App.xaml.cs`, injecté dans `MainWindow`, `MainViewModel` et `EmbeddedEditorView`.
- **`ApplyTheme(string? themeName)`** : résout la valeur persistée vers un identifiant ThemeForge. Les valeurs inconnues retombent sur `Drakul` et sont persistées via `ConfigManager.MergeSettingAsync`. Le changement lui-même est délégué à ThemeForge, après quoi Heimdall réapplique le mode de barre de titre DWM à chaque `Window` ouverte via `WindowThemeHelper.ApplyCurrentTheme`.
- **Rafraîchissement du pont** : `Themes/HeimdallThemeBridge.xaml` associe 74 clés de brosse Heimdall à des emplacements de couleur ThemeForge. `RefreshHeimdallBridge` réintègre ce dictionnaire après chaque changement ThemeForge, car une ressource `SolidColorBrush` partagée ne met pas à jour à chaud sa `Color` en `DynamicResource`.
- **Compteur `ThemeRevision`** : exposé au travers de l'enveloppe depuis ThemeForge. Les `MultiBinding` XAML qui dépendent de convertisseurs résolvant des brosses ajoutent `DataContext.ThemeRevision` (`ElementName=MainWindowRoot`) comme valeur de déclenchement finale, afin de forcer WPF à réexécuter le convertisseur à chaque changement. `ElementName` (et non `RelativeSource AncestorType=Window`) est obligatoire pour que la liaison se résolve depuis l'intérieur du `Popup` de la palette de commandes, dont le contenu possède sa propre racine visuelle.
- **`event Action<string> ThemeChanged`** : traduit depuis la forme d'événement de ThemeForge, consommé par les vues en aval qui reconstruisent leurs caches de brosses (`EmbeddedEditorView.ApplyTheme` relit les couleurs de chrome d'AvalonEdit dans le dictionnaire actif) et par `MainViewModel` pour répercuter la révision dans les liaisons XAML.

**Convertisseurs résolvant des brosses** : `ConnectionTypeToColorConverter`, `ConnectionTypeToBrushConverter`, `ConnectionStateToBrushConverter`, `ServerStatusToColorConverter`, `TunnelBadgeStateToBrushConverter` et `ResourceKeyToBrushConverter` résolvent les brosses de thème via `TryFindResource`. Les liaisons multi-valeurs transmettent le déclencheur `ThemeRevision` là où une réévaluation à chaud est nécessaire.

**Convertisseurs génériques de clé de ressource** : `ResourceKeyToBrushConverter` (double `IValue`/`IMulti`, utilisé par le navigateur d'outils de la barre latérale et par les vues d'outil pour résoudre les brosses de catégorie/statut à partir de propriétés du VM) et `ResourceKeyToGeometryConverter` (simple `IValue`, résout les géométries `Geo.Tool.*` - immuables d'un thème à l'autre, aucun déclencheur nécessaire).

**Réactivité des interfaces construites en code** : au lieu de mettre en cache des instances de `Brush` issues de `FindResource`, des constructeurs comme `ToolsTabPopulationService` utilisent `element.SetResourceReference(<DP>, "BrushKey")`. Les appels directs résiduels à `FindResource("<Name>Brush")` concernent des surfaces à usage unique, des ressources dérivées locales, ou des vues qui se reconstruisent explicitement sur `ThemeChanged`.

### Barre latérale (onglets Serveurs / Outils)

**Problème** : l'ancien `ToolsQuickPanel` repliable (`MaxHeight=350`, ancré en bas de la barre latérale Serveurs) était à l'étroit et se disputait l'espace vertical avec le `TreeView` des serveurs. Le rendu des mini-cartes en code-behind figeait les brosses au moment de la construction et exigeait un filet de sécurité de reconstruction paresseuse après chaque changement de thème.

**Solution** : la barre latérale gauche est désormais une zone à onglets. Deux `RadioButton` (`SidebarTabServers` / `SidebarTabTools`, `GroupName=SidebarTabs`) sont placés en haut de la barre latérale, stylés par `SidebarTabStyle` dans `CommonControls.xaml` (onglet plat avec soulignement d'accent sur `IsChecked`, survol `HighlightBrush`, focus clavier `FocusIndicatorBrush`, toutes les couleurs en `DynamicResource`). La `Visibility` de `SidebarServersContent` et de `SidebarToolsContent` est liée à l'`IsChecked` de chaque RadioButton via `BoolToVisibilityConverter`, de sorte que les deux conteneurs de contenu occupent toute la hauteur restante de la barre latérale, l'un après l'autre.

**Onglet Serveurs** : inchangé - barre d'outils (recherche, ajout, déplier/replier) au-dessus du `ServerTreeView`.

**Onglet Outils** : `TextBox` de filtre + libellé de contexte (miroir de `Mw_ToolsTabContextText` - "Network tools open without gateway" / "...with <host>") + `TreeView` pleine hauteur peuplé paresseusement depuis `ToolRegistry.All` au premier `SidebarTabTools.Checked`. L'arbre insère désormais toujours une catégorie Favoris localisée à l'index 0, peuplée depuis `AppSettings.FavoriteToolIds` et triée alphabétiquement sur le `Name` localisé affiché dans l'interface. Modèle de données :
- `SidebarToolCategoryViewModel` (`ObservableObject` via CommunityToolkit.Mvvm) : `CategoryName`, `BrushKey`, `Tools`, `VisibleCount` (pilote le badge de l'en-tête), `IsExpanded` (bidirectionnel), `IsVisible`
- `SidebarToolItemViewModel` : `Id`, `Name`, `BrushKey`, `IconGeometryKey`, et un blob `Searchable` pré-minusculé (`name + aliases`) pour un filtrage sans allocation. L'état de favori n'est pas stocké sur le VM feuille ; il est résolu à chaud depuis `FavoriteToolIds`.

Un `HierarchicalDataTemplate` rend les en-têtes de catégorie (pastille d'accent + nom + badge de comptage) et les feuilles (icône vectorielle 14x14 + nom). Les liaisons de brosse utilisent un `MultiBinding` sur `[BrushKey, DataContext.ThemeRevision]` acheminé par `ResourceKeyToBrushConverter` - la réactivité au changement de thème est automatique, aucune reconstruction nécessaire. Les géométries d'icône passent par `ResourceKeyToGeometryConverter` (immuables d'un thème à l'autre).

**Filtre** : `OnSidebarToolsFilterChanged` met à jour `IsVisible` par élément (via `Searchable.Contains(filterLower)`) ainsi que `VisibleCount` / `IsExpanded` par catégorie. Dépliage automatique quand un filtre est actif, repli quand il est effacé. La catégorie Favoris obéit aux mêmes règles de filtrage que toutes les autres. Un libellé d'état vide apparaît lorsqu'aucune catégorie n'a d'enfant visible.

**Flux de lancement** : `OnSidebarToolsSelectedItemChanged` → `LaunchSidebarTool(item)` → résolution du descripteur via `ToolRegistry.All.FirstOrDefault(Id)` → réutilisation des mêmes primitives `CreateInheritedToolContext` / `ResolveToolTabTitle` / `vm.OpenToolTabAsync` / `vm.TrackRecentTool` que l'onglet Outils pleine page. Avant l'ouverture, l'onglet principal Serveurs est activé pour que le panneau de session soit visible. Le clic droit pose une garde `_suppressSidebarLaunch` avant le changement de sélection, afin que l'outil ne s'ouvre pas pendant que le ContextMenu des favoris est visé. Le lanceur redondant `MouseDoubleClick` de la barre latérale a été retiré, car le simple clic ouvre déjà l'outil et pouvait sinon produire des onglets en double pour les outils contextuels/réseau.

**Synchronisation des favoris** : `MainViewModel.ToggleFavoriteToolAsync` reste l'unique écrivain de `FavoriteToolIds` et lève un événement `FavoritesChanged` après persistance. `SidebarViewModel` s'abonne à ce signal et applique une mutation ciblée d'ajout/retrait à la catégorie Favoris, puis invalide le filtre de la barre latérale. Cela garde la barre latérale synchronisée, que la bascule provienne du ContextMenu de la barre latérale ou du bouton d'épinglage de l'onglet Outils pleine page. Un outil mis en favori est représenté par deux instances `SidebarToolItemViewModel` indépendantes : une dans Favoris et une dans sa catégorie d'origine.

**ContextMenu des favoris** : attaché aux seuls éléments feuille et construit par programmation en code-behind lors du clic droit. Le libellé et l'`AutomationProperties.Name` sont résolus à l'ouverture d'après l'appartenance courante à `FavoriteToolIds`, en utilisant les clés de localisation existantes `TreeCtxAddFavorite` / `TreeCtxRemoveFavorite` et `A11yPinTool` / `A11yUnpinTool`.

**Piège Ctrl+Shift+T** : `RadioButton.IsChecked = !IsChecked` sur un bouton groupé ne coche **pas** automatiquement son frère - les deux finissent décochés, les deux conteneurs de contenu se replient, et la barre latérale devient vide. `ToggleSidebarTab()` fixe donc explicitement la cible : `if (SidebarTabTools.IsChecked == true) SidebarTabServers.IsChecked = true; else SidebarTabTools.IsChecked = true;`.

**Persistance** : réutilise le réglage booléen existant `ShowToolsPanel` (`true` = onglet Outils actif au démarrage). Restauré dans le gestionnaire `Loaded` de la fenêtre.

**Rafraîchissement des outils externes** : `ToolRegistry.ExternalToolsChanged` invalide `_sidebarToolsPopulated` et reconstruit immédiatement si l'onglet Outils de la barre latérale est actif ; sinon la reconstruction est différée au prochain basculement.

### Onglet Outils dédié (pleine page)

Navigateur pleine page sur le rail de navigation principal, indépendant de l'onglet Outils de la barre latérale. Il contient 3 sections - Favoris (outils épinglés, persistés dans `AppSettings.FavoriteToolIds`), Récemment utilisés (`_recentToolIds`, 5 maximum) et Tous les outils par catégorie. Les cartes font 280 px de large, avec bouton d'épinglage et fond d'icône coloré par catégorie. La recherche filtre sur le nom, les alias et les descriptions.

**Flux de lancement** : `OnToolsTabCardClick` → `vm.OpenToolTabAsync` → `EmbeddedSessionManager.CreateToolControl` → `ToolRegistry.CreateView` (lambda de fabrique) → `view.Initialize(context, localizer)`. Les outils non réseau utilisent le comportement d'onglet singleton. Les outils réseau reçoivent directement le serveur sélectionné en `TargetHost` (sans invite intermédiaire). `OpenToolTabAsync` nettoie les onglets orphelins en cas d'échec de `CreateToolControl`.

**Prise en main** : surimpression de premier lancement en 3 étapes (`OnboardingOverlay`, `Panel.ZIndex=500`). Etapes : Se connecter aux serveurs → Outils intégrés → Connexion rapide. Chaque étape amène à la zone d'interface concernée (onglet Serveurs → onglet Paramètres → bascule de la barre latérale vers l'onglet Outils). Accessible au clavier (Echap, cycle Tab, gestion du focus). Persistée via `AppSettings.OnboardingCompleted`.

**NetworkCartography responsive** : les colonnes utilisent des largeurs proportionnelles (`*`) avec `MinWidth`. Le gestionnaire `SizeChanged` masque les colonnes de détail en dessous de 1100 px et les colonnes secondaires en dessous de 800 px, pour la prise en charge des panneaux splittés.

**Piège des design tokens** : `SpacingRowGap` est un `sys:Double` (pour Margin/Height). `RowDefinition.Height` exige un `GridLength` - utiliser `SpacingRowGapGrid` pour les espaceurs de lignes de grille.

### Catégories d'outils (52 outils)

| Catégorie | Nombre | Outils |
|----------|-------|-------|
| **Réseau** | 17 | **Network Cartography** (balayage ping, scan de ports, capture de bannière, inspection de certificat TLS, empreinte d'OS à partir de 5 sources (TTL/bannière/ports/SNMP/NTLM), **extraction du challenge NTLM SMB2** (nom d'hôte/domaine/build d'OS/GUID/uptime), **empreinte HASSH SSH**, **hachage de favicon compatible Shodan** (plus de 30 équipements connus), **sondage d'URL produit HTTP** (13 chemins d'éditeurs), **détection de framework par cookie/page d'erreur**, requête SNMPv2c sur 6 OID + décodage du PEN IANA de l'éditeur, NetBIOS NBSTAT, mDNS/Bonjour, **SSDP + récupération de UPnP rootDesc.xml**, plus de 320 OUI MAC + détection d'adresse MAC aléatoire, plus de 50 motifs de rôle + plus de 100 empreintes de bannière + 6 règles de conflit, détection dynamique de VLAN par CIDR, export de topologie Draw.io, historique de scan avec diff typé, scan de sous-réseau distant via passerelle SSH (sondes groupées), **base de connaissances persistante avec cache par TTL + backfill KB**, **scan par tunnel : balayage ping + découverte ARP + sondes `/dev/tcp` parallèles avec délai par sonde**), Ping, DNS (serveur personnalisé, via tunnel), Cert Inspector (chaîne + TLS, via tunnel), Port Scanner (capture de bannière, via tunnel), Subnet (IPv4+IPv6), IP Converter, HTTP Status, Whois, HTTP Header Analyzer, Banner Grabber, TCP Traceroute, SNMP Walker, ARP Monitor, Firewall Rule Tester, Network Calculator (supernet + VLAN) |
| **Sécurité** | 15 | Password (3 modes, temps de cassage, historique, préréglages personnalisés, effacement automatique du presse-papiers), SSH Key (RSA+Ed25519), Hash (SHA3 + progression), HMAC, JWT (vérification de signature), Certificate Generator (CA + feuille), TOTP (RFC 6238), Password Policy Checker, SSH Key Auditor, SSL/TLS Auditor, DNS Security Checker (SPF/DKIM/DMARC), SMB Enumerator, Default Credential Scanner, CVE Lookup, **SecNumCloud Audit** (15 contrôles, 4 chapitres, constructeur `Func<string,string> localize`, export HTML/CSV/Draw.io) |
| **Encodage** | 6 | Base64 (variante URL-safe), URL Encoder, JSON (position d'erreur), Regex (surlignage des correspondances), Text Diff (au mot près), Text Case (8 formats) |
| **Système** | 14 | Chmod, Crontab Builder, DateTime (fuseau horaire + relatif), UUID (v4+v7), ULID, Hosts Editor, SSH Config Generator, Log Viewer/Tail, Cron Job Manager, Service Status Dashboard, **Notes** (Markdown façon Obsidian), **Diagram Editor** (draw.io embarqué hors ligne), **Command Library**, **Privilege Launcher** |

### i18n déclarative (extension de balisage `{loc:Translate}`)

**Problème** : l'ancien motif `ApplyLocalization()` en code-behind exige environ 385 appels manuels `L("key")`, fait apparaître les contrôles vides dans le concepteur WPF, et ajoute du code répétitif à chaque nouvelle vue.

**Solution** : une `MarkupExtension` maison qui permet l'i18n déclarative directement en XAML :
```xml
<TextBlock Text="{loc:Translate StatusReady}"/>
<Button AutomationProperties.Name="{loc:Translate BtnUnlock}"/>
```

**Architecture** (`src/Heimdall.App/Localization/`) :
- `TranslateExtension` - `MarkupExtension` qui crée un `Binding` vivant vers `LocalizationSource.Instance[Key]` pour les cibles DependencyProperty (mise à jour automatique au changement de langue). Repli sur une chaîne statique pour les cibles non-DP. Affiche `[Key]` en mode concepteur.
- `LocalizationSource` - pont singleton implémentant `INotifyPropertyChanged`. Encapsule l'indexeur de `LocalizationManager` et lève `PropertyChanged("Item[]")` sur `LocaleChanged`, ce qui provoque la réévaluation de toutes les liaisons.
- Initialisé dans `App.xaml.cs` après le chargement de la langue : `LocalizationSource.Instance.Initialize(localization)`

**Stratégie de migration** : coexiste avec `ApplyLocalization()`. Les nouvelles vues utilisent `{loc:Translate}`, les vues existantes migrent progressivement. PinDialog est entièrement migrée en preuve de concept.

### Système d'icônes à deux niveaux

**Problème** : trois systèmes d'icônes parallèles (BitmapImage, géométries vectorielles, glyphes MDL2) compliquaient la maintenance et causaient des incohérences visuelles entre l'arborescence, les onglets et les outils.

**Solution** : unification en deux niveaux :
1. **Niveau 1 - géométries vectorielles** (`IconGeometries.xaml`) : ressources nommées `Geo.<Categorie>.<Nom>` (Protocol.Rdp, Status.Connected, Tool.Ping, Tree.Group, etc.). Consommées via des éléments `Path` + `ConnectionTypeToGeometryConverter` / `ConnectionStateToGeometryConverter`.
2. **Niveau 2 - Segoe MDL2 Assets** : en ligne dans le XAML pour le chrome d'interface standard (barre d'outils, navigation, menus). Non centralisé - utilisé sous forme de `TextBlock` avec la famille de police.

**Changements clés** : `ToolRegistry` stocke une clé `Geo.Tool.*` par outil, avec des recherches par `FrozenDictionary`. Les convertisseurs résolvent les types de connexion `TOOL:*` via `ToolRegistry.GetGeometryKey()` / `GetCategoryBrushKey()`. Le TreeView utilise 2 liaisons de convertisseur au lieu d'environ 180 lignes de DataTriggers.

### Divulgation progressive (ServerDialog)

**Problème** : la boîte de dialogue d'ajout/édition de serveur présentait 5 onglets d'options dès l'ouverture, ce qui submergeait les nouveaux utilisateurs pour une opération le plus souvent limitée à "nom + hôte + port".

**Solution** : une boîte de dialogue à deux modes :
- **Mode simple** (par défaut) : n'affiche que les champs essentiels - Nom, Type de connexion, Hôte, Port, Projet, Passerelle.
- **Mode avancé** (bascule) : un déroulé animé (ScaleY + Opacity, 300 ms ease-out / 250 ms ease-in) révèle le TabControl complet avec les options propres au protocole.
- La préférence de mode est persistée dans `AppSettings.ServerDialogAdvancedMode` via `ConfigManager.MergeSettingAsync()`.

### DialogCommonStyles.xaml

Dictionnaire de ressources partagé (`src/Heimdall.App/Themes/DialogCommonStyles.xaml`) regroupant 8 styles réutilisables extraits de ServerDialog/GatewayDialog/ProjectDialog : `DialogLabelStyle`, `DialogSectionTitleStyle`, `DialogSectionDescriptionStyle`, `DialogHintTextStyle`, `DialogSectionCardStyle`, `DialogFormTextBoxStyle`, `DialogFormComboBoxStyle`, `DialogFormPasswordBoxStyle`.
