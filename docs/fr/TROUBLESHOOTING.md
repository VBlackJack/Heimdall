<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->
*Ce document est la version française de [../TROUBLESHOOTING.md](../TROUBLESHOOTING.md). / This document is the French version.*

# Heimdall - Guide de diagnostic

Index de tous les problèmes rencontrés pendant le développement et de leurs solutions.

## Sommaire

1. [RDP embarqué - écran blanc](#rdp-embedded-white-screen)
2. [RDP embarqué - code de déconnexion 4360](#rdp-embedded-disconnect-code-4360)
3. [RDP embarqué - plantage du RCW COM à la fermeture d'un onglet](#rdp-embedded-com-rcw-crash-on-tab-close)
4. [RDP embarqué - erreur HRESULT au redimensionnement](#rdp-embedded-resize-hresult-error)
5. [Remplissage automatique CredUI RDP - boîte de dialogue non détectée](#rdp-credui-autofill-dialog-not-detected)
6. [SSH - clés Pageant non reconnues par SSH.NET](#ssh-pageant-keys-not-recognized)
7. [Terminal SSH - touches fléchées inopérantes](#ssh-terminal-arrow-keys-not-working)
8. [Terminal SSH - aucune couleur, caractères étranges](#ssh-terminal-no-colors-strange-characters)
9. [Terminal SSH - le curseur clignote trop vite](#ssh-terminal-cursor-blinks-too-fast)
10. [WebView2 - DLL introuvable](#webview2-dll-not-found)
11. [Navigation par onglets - onglets bloqués par les sessions actives](#tab-navigation-blocked-by-sessions)
12. [WPF - DynamicResource dans BasedOn](#wpf-dynamicresource-in-basedon)
13. [WPF - gestionnaire Click XAML dans un Setter de Style](#wpf-xaml-click-handler-in-style-setter)
14. [Build - débordement du numéro de version](#build-version-number-overflow)
15. [Build - références de types ambiguës](#build-ambiguous-type-references)
16. [TOFU - HostKeyFingerprint sur un PSCustomObject](#tofu-hostkeyfingerprint-on-pscustomobject)
17. [Mots de passe - non enregistrés après modification](#passwords-not-saved-after-edit)
18. [Pageant - mauvaise valeur d'AGENT_COPYDATA_ID](#pageant-agent_copydata_id-wrong-value)
19. [Pageant - enregistrement des algorithmes RSA-SHA2](#pageant-rsa-sha2-algorithm-registration)
20. [Pageant - Sign() renvoie des octets bruts au lieu du blob](#pageant-sign-returns-raw-bytes-instead-of-blob)
21. [SFTP - la CheckBox se déclenche pendant l'analyse du XAML](#sftp-checkbox-fires-during-xaml-parse)
22. [SFTP - menu contextuel intercepté par MainWindow](#sftp-context-menu-intercepted-by-mainwindow)
23. [Citrix - storebrowse.exe introuvable](#citrix-storebrowseexe-not-found)
24. [Changement de thème - couleurs obsolètes après la bascule](#theme-switching-stale-colors-after-swap)
25. [Multi-Exec - la diffusion atteint les mauvais terminaux](#multi-exec-broadcast-sends-to-wrong-terminals)
26. [RDP embarqué - scintillement au redimensionnement](#rdp-embedded-resize-flicker)
27. [VNC - bibliothèque noVNC indisponible](#vnc-novnc-unavailable)
28. [VNC - conflit de port du proxy WebSocket](#vnc-websocket-port-conflict)
29. [Redirection X11 - aucun affichage](#x11-no-display)
30. [Telnet - la connexion se fige](#telnet-connection-hangs)
31. [FTP - échecs en mode passif](#ftp-passive-mode)
32. [Détachement d'onglet - session WebView2 perdue](#tab-detach-webview2)
33. [Serveur éphémère - accès refusé sur le port 69](#tftp-port-access-denied)
34. [Connexion rapide - échec d'une session SSH ad hoc](#quick-connect-ad-hoc-ssh-fails)
35. [Redimensionnement RDP - reconnexions persistantes (réglage delta/anti-rebond)](#rdp-resize-still-reconnecting-deltadebounce-tuning)
36. [SFTP - permission refusée sur le repli sudo (échec d'authentification)](#sftp-sudo-fallback-auth-failure)
37. [SFTP - le parseur de sudo ls affiche un répertoire vide](#sftp-sudo-ls-parser-empty)
38. [SFTP - le repli sudo n'utilise que les erreurs typées de permission refusée](#sftp-typed-permission-denied-only)
39. [WebView2 - erreur de configuration side-by-side (0x800736B1)](#webview2-sxs-error)
40. [Traversée HTTP - contournement par préfixe frère](#http-traversal-sibling-prefix)
41. [Scan par tunnel des outils - peu ou pas d'hôtes trouvés](#tool-tunnel-scan-few-hosts)
42. [SSH - liste des passerelles TestEnv vide après import](#ssh-testenv-gateway-dropdown-empty)
43. [RDP - IMsRdpExtendedSettings inaccessible par un accès de propriété dynamic](#rdp-extendedsettings-dynamic-access)
44. [SSH - clé d'hôte indisponible sur le repli Plink](#ssh-host-key-unavailable-plink)
45. [SSH/SFTP - clé d'hôte discordante en cours de session](#ssh-sftp-host-key-mismatch-mid-session)
46. [FTP - avertissement d'identifiants en clair](#ftp-cleartext-credential-warning)
47. [Passerelle WinRM - réponse serveur invalide HTTP 12152](#winrm-gateway-12152)
48. [Fournisseur d'identifiants KeePassXC - pièges courants](#keepassxc-credential-provider)
49. [RDP embarqué - session coupée peu après la connexion](#rdp-slow-server-cutoff)
50. [Tâche planifiée - exécutée mais rien de connecté](#scheduled-task-connected-nothing)

---

## RDP embarqué - écran blanc {#rdp-embedded-white-screen}

**Symptôme** : le contrôle ActiveX RDP se connecte (OnConnected se déclenche) mais la zone d'affichage reste blanche.

**Cause racine** : problème d'airspace du `WindowsFormsHost` WPF. La surface de rendu du contrôle ActiveX n'est pas correctement liée au HWND visible parce que WPF n'a pas vidé son pipeline de layout avant l'appel à `Connect()`.

**Solution** : appliquer le motif éprouvé de vidage du layout avant ET après `Connect()` :
```csharp
// Before Connect()
FormsHost.UpdateLayout();
SurfaceContainer.UpdateLayout();
WinForms.Application.DoEvents();
Dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));

// EnsureHostHandle - force handle creation
if (!_rdpHost.IsHandleCreated) { _ = _rdpHost.Handle; }

// After Connect()
FormsHost.UpdateLayout();
WinForms.Application.DoEvents();
```

**Point clé** : le `FormsHost` DOIT se trouver dans l'arbre visuel visible avec une taille valide AVANT `Connect()`. Utiliser une boucle de nouvelles tentatives si la surface n'est pas prête (jusqu'à 10 tentatives, intervalles de 120 ms).

**Fichiers** : `EmbeddedRdpView.xaml.cs` - `FlushLayoutPipeline()`, `BeginConnect()`

---

## RDP embarqué - code de déconnexion 4360 {#rdp-embedded-disconnect-code-4360}

**Symptôme** : RDP se connecte puis se déconnecte au bout de quelques secondes avec le code de raison 4360.

**Cause racine** : le code 4360 signifie "session disconnected" - il peut être provoqué par :
1. `UpdateResolution()` appelé trop tôt après `Connect()`, ce qui fait planter l'objet COM
2. des problèmes de licence ou de stratégie côté serveur
3. un redimensionnement dynamique de la résolution pendant la négociation initiale de connexion

**Solution** : bloquer les appels à `UpdateResolution` pendant 5 secondes après le déclenchement d'`OnConnected`. Ignorer les mises à jour de taille identique.

```csharp
_allowResolutionUpdates = false;
// In OnConnected:
_connectedAtUtc = DateTime.UtcNow;
// Enable after 5 second delay:
await Task.Delay(TimeSpan.FromSeconds(5));
_allowResolutionUpdates = true;
```

**Fichiers** : `EmbeddedRdpView.xaml.cs` - `EnableResolutionUpdatesAsync()`

---

## RDP embarqué - plantage du RCW COM à la fermeture d'un onglet {#rdp-embedded-com-rcw-crash-on-tab-close}

**Symptôme** : `COM object that has been separated from its underlying RCW cannot be used` lors de la fermeture d'un onglet de session.

**Cause racine** : l'`ArrangeOverride` de WPF tente de redimensionner le contrôle ActiveX APRÈS sa libération. Le `WindowsFormsHost` référence encore l'objet COM pendant le layout.

**Solution** : masquer le `FormsHost` et retirer son enfant AVANT de libérer l'objet COM :
```csharp
// CRITICAL ORDER:
FormsHost.Visibility = Visibility.Collapsed;  // Stop layout
FormsHost.Child = null;                       // Remove COM from tree
_rdpHost.Disconnect();                        // Then disconnect
_rdpHost.DetachEventSink();                   // Remove event sink
_rdpHost.Dispose();                           // Finally dispose
```

**Fichiers** : `EmbeddedRdpView.xaml.cs` - `Dispose()`

---

## RDP embarqué - erreur HRESULT au redimensionnement {#rdp-embedded-resize-hresult-error}

**Symptôme** : `Unexpected HRESULT has been returned from a call to a COM component` pendant un redimensionnement.

**Cause racine** : `SetDisplay()` ou `UpdateResolution()` appelé alors que la session RDP est en cours de connexion (pas encore totalement établie).

**Solution** : n'appeler `UpdateResolution` que lorsque `IsConnected == true` ET après le délai de stabilisation.

**Fichiers** : `EmbeddedRdpView.xaml.cs` - `OnResizeTimerTick()`

---

## Remplissage automatique CredUI RDP - boîte de dialogue non détectée {#rdp-credui-autofill-dialog-not-detected}

**Symptôme** : les balayages du remplissage automatique CredUI ne trouvent que 8 fenêtres de premier niveau et ne détectent jamais la boîte de dialogue d'identifiants "Windows Security".

**Cause racine** : la boîte de dialogue CredUI issue d'un contrôle ActiveX embarqué n'est PAS une fenêtre de premier niveau - c'est une fenêtre enfant/possédée créée par le thread du contrôle RDP. `EnumWindows` ne trouve que les fenêtres de premier niveau.

**Solution** : en complément d'`EnumWindows`, balayer aussi tous les threads du processus courant avec `EnumThreadWindows` :
```csharp
foreach (ProcessThread thread in Process.GetCurrentProcess().Threads)
{
    EnumThreadWindows((uint)thread.Id, callback, IntPtr.Zero);
}
```

Utiliser également UI Automation (`System.Windows.Automation`) pour les boîtes de dialogue CredUI modernes basées sur XAML, avec `SendMessage`/`BM_CLICK` Win32 en repli pour les boîtes de dialogue classiques.

Lorsque le remplissage automatique échoue silencieusement, activez la journalisation de niveau Debug pour `CredentialAutofill` et examinez l'entrée d'énumération des brokers ajoutée dans `1d7c78c` : les titres des fenêtres candidates, les noms de processus et les motifs de rejet expliquent pourquoi une invite a été rejetée. Ces diagnostics ne contiennent que des métadonnées ; les champs d'identifiants et le contenu des champs de saisie n'apparaissent pas dans le journal.

**Fichiers** : `CredentialAutofill.cs` - `GetVisibleWindows()`, `InjectPassword()`

---

## SSH - clés Pageant non reconnues {#ssh-pageant-keys-not-recognized}

**Symptôme** : `Server rejected the SSH key` ou `No suitable authentication method found` alors même que Pageant tourne avec des clés chargées.

**Cause racine** : SSH.NET 2025.1.0 n'intègre pas de prise en charge de l'agent Pageant. Le repli `NoneAuthenticationMethod` ne déclenche pas la négociation Pageant.

**Solution** : approche en deux volets :
1. **Client IPC Pageant** : le `PageantClient` maison dialogue avec Pageant via la mémoire partagée Win32 (`CreateFileMapping` + `WM_COPYDATA`). Il encapsule les clés en `IPrivateKeySource` pour SSH.NET via `PageantKeyWrapper` + `PageantHostAlgorithm`.
2. **Repli Plink** : quand `RequiresPageantFallback()` détecte une authentification Pageant seule, utiliser `PlinkTunnelRunner` pour les tunnels et `PipeModeSession` pour le SSH interactif. Plink dialogue nativement avec Pageant.

**Fichiers** : `Pageant/PageantClient.cs`, `SshConnectionFactory.cs`, `ConnectionService.cs`

---

## Terminal SSH - touches fléchées inopérantes {#ssh-terminal-arrow-keys-not-working}

**Symptôme** : les touches fléchées ne parcourent pas l'historique des commandes dans bash. Appuyer sur Haut affiche `^[[A` à la place.

**Cause racine** : ConPTY (`CreatePseudoConsole`) convertit les séquences d'entrée VT en événements clavier de la console Windows, puis les reconvertit. Cette double conversion casse les séquences d'échappement des touches fléchées.

**Solution** : utiliser le **mode pipe** (PAS ConPTY) pour les terminaux SSH. `PipeModeSession` redirige stdin/stdout directement, sans pseudo-console. Combiné à l'option `-t` de plink (qui force l'allocation d'un PTY distant), les séquences VT passent en brut.

```
xterm.js → ESC[A → stdin pipe → plink -t → remote PTY → bash
bash → ESC[A response → stdout pipe → xterm.js
```

**Règle clé** : ne JAMAIS utiliser ConPTY pour des terminaux SSH qui passent par plink. ConPTY est réservé aux shells locaux.

**Fichiers** : `PipeModeSession.cs`, `ConnectionService.cs` - `ConnectSshViaPlinkAsync()`

---

## Terminal SSH - aucune couleur, caractères étranges {#ssh-terminal-no-colors-strange-characters}

**Symptôme** : le terminal affiche des codes d'échappement ANSI bruts comme `[?2004h`, `[0;32m` au lieu des couleurs. Pas de curseur.

**Cause racine** : l'implémentation initiale utilisait un `TextBlock` WPF avec suppression des séquences ANSI. Un TextBlock ne sait pas rendre les séquences d'échappement d'un terminal.

**Solution** : remplacer par **WebView2 + xterm.js**, le moteur de rendu de terminal standard du marché :
- xterm.js prend en charge TOUT le rendu VT100/xterm (couleurs, curseur, historique de défilement, souris)
- transfert de données binaire-safe en base64 entre le processus et xterm.js
- `PostWebMessageAsString` pour C# → JS, `WebMessageReceived` pour JS → C#

**Fichiers** : `EmbeddedSshView.xaml`, `EmbeddedSshView.xaml.cs`, `Assets/terminal.html`

---

## Terminal SSH - le curseur clignote trop vite {#ssh-terminal-cursor-blinks-too-fast}

**Symptôme** : le curseur xterm.js clignote extrêmement vite, bien plus rapidement que la normale.

**Cause racine** : WPF et WebView2 se disputent le focus. Les gestionnaires `GotFocus` et `PreviewMouseDown` du UserControl appellent `FocusTerminal()`, qui donne le focus à WebView2, ce qui déclenche `LostFocus` côté WPF, qui redéclenche `GotFocus` → boucle de focus infinie.

**Solution** :
1. Supprimer les gestionnaires `GotFocus` et `PreviewMouseDown`
2. N'appliquer le focus qu'UNE SEULE fois, après l'envoi du message `ready:` par xterm.js
3. Ralentir le clignotement du curseur via CSS : `animation-duration: 1.2s`

**Fichiers** : `EmbeddedSshView.xaml.cs`, `Assets/terminal.html`

---

## WebView2 - DLL introuvable {#webview2-dll-not-found}

**Symptôme** : `Unable to load DLL 'WebView2Loader.dll'` à l'exécution.

**Cause racine** : `WebView2Loader.dll` est une DLL native (non managée) que `dotnet publish` ne copie pas dans le répertoire de sortie. Elle est placée dans le sous-dossier `lib/webview2/` au lieu d'être à côté de l'exécutable.

**Solution** : la copier explicitement dans `Build.ps1` après la publication :
```powershell
Copy-Item "src\Heimdall.App\lib\webview2\WebView2Loader.dll" $outputDir -Force
```

**Fichiers** : `Build.ps1`

---

## Navigation par onglets - onglets bloqués par les sessions actives {#tab-navigation-blocked-by-sessions}

**Symptôme** : lorsqu'une session SSH ou RDP est ouverte, cliquer sur les onglets Tunnels/Scheduled/Settings ne produit aucun effet.

**Cause racine** : plusieurs architectures de layout ont été essayées :
1. **Sessions en superposition globale** (`Panel.ZIndex=10`) : bloque tous les onglets situés dessous
2. **Sessions dans une Grid séparée** : les sessions sont masquées au changement d'onglet mais jamais restaurées

**Solution** (issue de l'audit d'architecture Gemini) : les sessions vivent À L'INTÉRIEUR de la colonne 2 de la Grid Servers. Au changement d'onglet, la Grid Servers entière est masquée (`Visibility=Collapsed`) - les sessions ne sont PAS détruites, seulement suspendues visuellement. Le retour sur Servers les restaure.

Correctifs complémentaires :
- `Panel.ZIndex=100` sur la barre d'outils garantit que les clics atteignent les RadioButtons au-dessus de WebView2
- `ClipToBounds=True` sur la Grid de contenu empêche le débordement de WebView2
- Gestion du focus : pas de va-et-vient de focus entre WPF et WebView2

**Règle clé** : les sessions sont enfants de l'onglet Servers, jamais une superposition globale.

**Fichiers** : `MainWindow.xaml`, `MainWindow.xaml.cs`

---

## WPF - DynamicResource dans BasedOn {#wpf-dynamicresource-in-basedon}

**Symptôme** : `A 'DynamicResourceExtension' cannot be set on the 'BasedOn' property of type 'Style'`

**Cause racine** : limitation de WPF - `BasedOn` n'accepte que `StaticResource`, pas `DynamicResource`.

**Solution** : remplacer `BasedOn="{DynamicResource ...}"` par `BasedOn="{StaticResource ...}"`.

---

## WPF - gestionnaire Click XAML dans un Setter de Style {#wpf-xaml-click-handler-in-style-setter}

**Symptôme** : `Set connectionId threw an exception` au chargement d'une fenêtre contenant un ContextMenu défini dans un Setter de Style qui utilise des gestionnaires d'événement `Click`.

**Cause racine** : WPF ne sait pas résoudre les gestionnaires d'événement en XAML lorsque le ContextMenu est défini à l'intérieur d'un `<Setter.Value>` - la méthode du gestionnaire n'est pas dans la portée.

**Solution** : construire le ContextMenu par programmation dans le code-behind plutôt qu'en XAML.

**Fichiers** : `MainWindow.xaml.cs` - `OnSessionTabRightClick()`

---

## Build - débordement du numéro de version {#build-version-number-overflow}

**Symptôme** : `Arithmetic operation resulted in an overflow` pendant la génération des ressources Win32.

**Cause racine** : `<Version>2026.031614</Version>` - le segment `031614` dépasse la limite de 65535 des champs de version Win32.

**Solution** : utiliser des propriétés de version distinctes :
- `<Version>1.0.MMDD.xx</Version>` pour la compatibilité Win32 (AssemblyVersion)
- `<InformationalVersion>YYYY.MMDDxx</InformationalVersion>` pour l'affichage

**Fichiers** : `Build.ps1`, `Heimdall.App.csproj`

---

## Build - références de types ambiguës {#build-ambiguous-type-references}

**Symptôme** : `'Point' is an ambiguous reference between 'System.Drawing.Point' and 'System.Windows.Point'`

**Cause racine** : `UseWindowsForms=true` dans le csproj importe les types System.Drawing en plus des types System.Windows.

**Solution** : qualifier complètement les types ambigus : `System.Windows.Point`, `System.Windows.DataObject`.

---

## Build - Build.ps1 -SkipTests avec dotnet test --no-build

**Symptôme** : après un `Build.ps1 -SkipTests`, l'appel à `dotnet test --no-build` exécute des assemblies de test obsolètes, ou ne les trouve pas du tout.

**Cause racine** : `-SkipTests` saute à la fois la passe de tests et la reconstruction des assemblies de test. `--no-build` réutilise ensuite ce qui se trouve déjà sur le disque.

**Solution** : lorsque vous itérez sur les tests après un build `-SkipTests`, lancez explicitement `dotnet build Heimdall.slnx -c Debug -p:nodeReuse=false` avant `dotnet test`.

---

## Build - verrous de fichiers MSB3026 dus à Heimdall / testhost

**Symptôme** : `dotnet build` ou `dotnet test` émet `MSB3026: Could not copy ...` ou `MSB3027: Could not copy ...` sur `Heimdall.App.dll`, `Heimdall.App.Tests.dll` ou d'autres assemblies de sortie. Le verrou persiste malgré les nouvelles tentatives.

**Cause racine** : l'un de ces deux processus maintient le fichier de sortie ouvert :
- une instance de `Heimdall.exe` encore en cours, issue d'une session de débogage ou d'exécution précédente ;
- un `testhost.exe` résiduel laissé par un `dotnet test` antérieur (fréquent quand la suite a été annulée, a planté, ou quand un build et une exécution de tests se sont chevauchés).

**Solution** :
1. Tuer les fautifs :
   ```powershell
   Get-Process Heimdall, testhost -ErrorAction SilentlyContinue | Stop-Process -Force
   ```
2. Reconstruire explicitement :
   ```bash
   dotnet build Heimdall.slnx -c Debug -p:nodeReuse=false
   ```
3. Relancer les tests séquentiellement :
   ```bash
   dotnet test Heimdall.slnx --no-build
   ```

**Prévention** : évitez de lancer `Build.ps1` ou `dotnet test` pendant qu'une instance de débogage de Heimdall tourne encore. Si la suite a été annulée en cours d'exécution, faites le ménage des `testhost.exe` avant la tentative suivante.

---

## TOFU - HostKeyFingerprint sur un PSCustomObject {#tofu-hostkeyfingerprint-on-pscustomobject}

**Symptôme** : `The property 'HostKeyFingerprint' cannot be found on this object` (problème hérité de Heimdall v1).

**Cause racine** : les objets passerelle/serveur issus de la désérialisation JSON sont des `PSCustomObject`, pas des instances de classes C#. Les nouvelles propriétés ajoutées au modèle C# n'existent pas sur les objets désérialisés.

**Solution** : ajouter une garde `Add-Member` avant l'affectation :
```powershell
if (-not $gateway.PSObject.Properties['HostKeyFingerprint']) {
    $gateway | Add-Member -MemberType NoteProperty -Name 'HostKeyFingerprint' -Value $null
}
$gateway.HostKeyFingerprint = $fingerprint
```

**Fichiers** : `ConnectionManager.psm1`, `EmbeddedSsh.psm1`, `HeimdallSftpPanel.ps1`

---

## Mots de passe - non enregistrés après modification {#passwords-not-saved-after-edit}

**Symptôme** : le champ mot de passe apparaît vide à la réouverture de la boîte de dialogue d'édition du serveur après enregistrement.

**Cause racine** : `ServerDialogViewModel.ToDto()` ne mappait pas `RdpPassword`/`SshPassword` vers `RdpPasswordEncrypted`/`SshPasswordEncrypted` via DPAPI.

**Solution** :
1. Chiffrer les nouveaux mots de passe dans `ToDto()` : `DpapiProvider.Protect(password)`
2. Conserver les mots de passe chiffrés existants à l'édition (si l'utilisateur n'a pas modifié le champ)
3. Stocker `ExistingRdpPasswordEncrypted`/`ExistingSshPasswordEncrypted` dans le ViewModel

**Fichiers** : `ServerDialogViewModel.cs`

---

## Pageant - mauvaise valeur d'AGENT_COPYDATA_ID {#pageant-agent_copydata_id-wrong-value}

**Symptôme** : `PageantClient` envoie une requête à Pageant mais ne reçoit aucune réponse. Pageant semble en cours d'exécution avec des clés chargées, mais l'authentification SSH.NET échoue avec "no suitable method found".

**Cause racine** : le champ `COPYDATASTRUCT.dwData` doit valoir exactement `0x804e50ba` (`AGENT_COPYDATA_ID`). Toute autre valeur amène Pageant à ignorer silencieusement le message `WM_COPYDATA`.

**Solution** : vérifier que la constante est correcte :
```csharp
private const uint AGENT_COPYDATA_ID = 0x804e50ba;
```

**Fichiers** : `Pageant/PageantClient.cs`

---

## Pageant - enregistrement des algorithmes RSA-SHA2 {#pageant-rsa-sha2-algorithm-registration}

**Symptôme** : les clés Pageant sont chargées dans SSH.NET mais le serveur refuse l'authentification. Les journaux du serveur indiquent "no matching host key type found" ou un message similaire.

**Cause racine** : les serveurs SSH modernes désactivent l'ancien `ssh-rsa` (SHA-1) et exigent `rsa-sha2-256` ou `rsa-sha2-512`. SSH.NET n'enregistre pas automatiquement ces algorithmes lorsqu'on utilise une implémentation personnalisée d'`IPrivateKeySource`.

**Solution** : enregistrer les algorithmes RSA-SHA2 sur la `ConnectionInfo` avant la connexion :
```csharp
connectionInfo.HostKeyAlgorithms["rsa-sha2-256"] = ...;
connectionInfo.HostKeyAlgorithms["rsa-sha2-512"] = ...;
```

**Fichiers** : `SshConnectionFactory.cs`, `Pageant/PageantHostAlgorithm.cs`

---

## Pageant - Sign() renvoie des octets bruts au lieu du blob {#pageant-sign-returns-raw-bytes-instead-of-blob}

**Symptôme** : l'authentification SSH démarre (la clé est proposée) mais échoue pendant l'échange de signature. Le serveur rejette la signature.

**Cause racine** : SSH.NET attend le blob de signature complet au format SSH : `[4 bytes: algorithm name length][algorithm name][4 bytes: signature length][signature bytes]`. `PageantClient.SignData()` renvoie déjà ce blob complet en provenance de Pageant. Retirer le préfixe d'algorithme, réencapsuler le blob une seconde fois ou ne renvoyer que les octets bruts de signature corrompt la signature.

**Solution** : `PageantHostAlgorithm.Sign()` doit renvoyer le blob de Pageant inchangé. La documentation XML et `PageantHostAlgorithmTests.Sign_ReturnsBlobFromAgentUnchanged` figent ce contrat.

**Fichiers** : `Pageant/PageantHostAlgorithm.cs`

---

## SFTP - la CheckBox se déclenche pendant l'analyse du XAML {#sftp-checkbox-fires-during-xaml-parse}

**Symptôme** : `NullReferenceException` au démarrage lors du chargement d'`EmbeddedSftpView`, provenant d'un gestionnaire d'événement `CheckBox.Checked`.

**Cause racine** : une `CheckBox` XAML avec `IsChecked="True"` déclenche l'événement `Checked` pendant `InitializeComponent()`, avant l'initialisation des champs de classe et des autres contrôles.

**Solution** : protéger les gestionnaires d'événement par des tests de nullité sur les champs qui ne sont peut-être pas encore initialisés :
```csharp
private void OnShowHiddenChecked(object sender, RoutedEventArgs e)
{
    if (_sftpClient == null) return; // Not yet initialized
    RefreshDirectory();
}
```

**Fichiers** : `EmbeddedSftpView.xaml.cs`

---

## SFTP - menu contextuel intercepté par MainWindow {#sftp-context-menu-intercepted-by-mainwindow}

**Symptôme** : le clic droit sur un onglet de session SFTP ouvre le menu contextuel générique de session (déconnexion/fermeture) au lieu du menu contextuel propre au SFTP, ou provoque un comportement inattendu.

**Cause racine** : `MainWindow.OnSessionTabRightClick()` intercepte le clic droit sur tous les onglets de session et construit un menu contextuel générique. Les onglets SFTP possèdent leur propre menu contextuel (avec les opérations sur fichiers), mais le gestionnaire de MainWindow se déclenche en premier et le supplante.

**Solution** : dans `OnSessionTabRightClick()`, tester le type de session et sauter la création du menu contextuel pour les onglets SFTP :
```csharp
if (sessionTab.ConnectionType == ConnectionType.Sftp)
    return; // SFTP view handles its own context menu
```

**Fichiers** : `MainWindow.xaml.cs` - `OnSessionTabRightClick()`

---

## Citrix - storebrowse.exe introuvable {#citrix-storebrowseexe-not-found}

**Symptôme** : la connexion Citrix échoue immédiatement avec une erreur indiquant que `storebrowse.exe` est introuvable.

**Cause racine** : Citrix Workspace App n'est pas installé, ou l'est dans un emplacement non standard. Le chemin de détection par défaut est `%ProgramFiles(x86)%\Citrix\ICA Client\SelfServicePlugin\storebrowse.exe`.

**Solution** :
1. Vérifier que Citrix Workspace App est installé (téléchargement chez Citrix)
2. En cas d'installation dans un emplacement personnalisé, renseigner `CitrixStoreBrowsePath` dans Settings > Paths avec le chemin complet de `storebrowse.exe`
3. Vérifier que la version de Citrix Workspace App est à jour - les versions anciennes peuvent ne pas fournir la CLI `storebrowse.exe`

**Fichiers** : `ConnectionService.Citrix.cs`

---

## Changement de thème - couleurs obsolètes après la bascule {#theme-switching-stale-colors-after-swap}

**Symptôme** : après un changement de thème à l'exécution, certains contrôles apparaissent avec des couleurs incorrectes, des bordures manquantes ou des fonds non stylés.

**Cause racine** : l'ordre de fusion des `ResourceDictionary` WPF compte. ThemeForge injecte le dictionnaire de palette actif à l'exécution, puis le dictionnaire pont de Heimdall mappe les clés de pinceaux applicatives sur les emplacements de couleurs ThemeForge. Une ressource `SolidColorBrush` partagée ne met pas à jour en direct un `DynamicResource` affecté à sa `Color` ; il faut la recréer après la bascule de palette. Les convertisseurs qui résolvent des pinceaux via `TryFindResource` au moment de la conversion figent également le résultat et ne sont pas réexécutés lors d'un changement de thème, sauf si leurs entrées de binding changent.

**Solution** :
1. Laisser la palette ThemeForge être injectée par `ThemeForge.Theme.ThemeService` ; ne pas fusionner statiquement une palette ThemeForge dans `App.xaml`
2. Garder `HeimdallThemeBridge.xaml` fusionné après la palette active, et laisser `HeimdallThemeService.RefreshHeimdallBridge` le refusionner après chaque `ApplyTheme`
3. Vérifier que tous les styles dépendants du thème dans `CommonControls.xaml` et `DialogCommonStyles.xaml` utilisent `DynamicResource` pour les propriétés de pinceau
4. `BasedOn` doit toujours utiliser `StaticResource` (limitation WPF), mais le style référencé doit lui-même utiliser `DynamicResource` pour ses propriétés de pinceau
5. Pour les convertisseurs qui résolvent des pinceaux via `TryFindResource`, les brancher au travers d'un `MultiBinding` qui ajoute `DataContext.ThemeRevision` (ElementName=`MainWindowRoot`) comme valeur de déclenchement finale, afin que WPF réexécute le convertisseur après chaque bascule
6. Pour l'interface construite en code-behind, utiliser `element.SetResourceReference(DP, "BrushKey")` plutôt que d'affecter un `Brush` concret obtenu par `FindResource`

**Fichiers** : `Services/HeimdallThemeService.cs`, `Themes/HeimdallThemeBridge.xaml`, `Themes/CommonControls.xaml`, `Themes/DialogCommonStyles.xaml`, `Themes/IconGeometries.xaml`, `Theming/WindowThemeHelper.cs`, paquet NuGet `ThemeForge.Theme`. L'ancien `Services/ThemeService.cs` et les palettes historiques `*Theme.xaml` ont été supprimés.

---

## Multi-Exec - la diffusion atteint les mauvais terminaux {#multi-exec-broadcast-sends-to-wrong-terminals}

**Symptôme** : les frappes diffusées apparaissent dans des terminaux qui ne devraient pas les recevoir, ou n'apparaissent pas dans des terminaux pourtant abonnés.

**Cause racine** : la liste d'abonnement à la diffusion est indexée par identifiant de session. Si un onglet de session est fermé puis un nouveau ouvert, l'ancien identifiant peut subsister dans la liste de diffusion, ou la nouvelle session peut ne pas y être enregistrée.

**Solution** :
1. Vérifier l'état du bouton de diffusion de chaque terminal dans la barre d'outils de session (l'icône de diffusion doit être mise en évidence lorsqu'elle est active)
2. À la fermeture d'une session, `EmbeddedSessionManager` doit retirer la session de la liste des abonnés à la diffusion
3. À l'ouverture d'une session, l'adhésion à la diffusion est désactivée par défaut - l'utilisateur doit l'activer explicitement
4. Vérifier que `PostWebMessageAsString` cible la bonne instance de `WebView2` par identifiant de session, et non par index d'onglet

**Fichiers** : `EmbeddedSessionManager.cs`, `EmbeddedSshView.xaml.cs`

---

## Connexion rapide - échec d'une session SSH ad hoc {#quick-connect-ad-hoc-ssh-fails}

**Symptôme** : la connexion rapide (Ctrl+K) ouvre la superposition et analyse correctement la chaîne de connexion, mais la connexion SSH échoue avec des erreurs d'authentification ou "no suitable method found".

**Cause racine** : les connexions ad hoc par connexion rapide créent un `ServerProfileDto` transitoire sans identifiants enregistrés. Si le serveur cible exige une authentification par clé et qu'aucune clé par défaut n'est configurée, la connexion n'a aucune méthode d'authentification viable.

**Solution** :
1. Vérifier que Pageant tourne avec les clés appropriées chargées (la connexion rapide utilise Pageant comme méthode d'authentification SSH par défaut)
2. Sinon, préciser les identifiants dans la chaîne de connexion ou configurer une clé SSH par défaut dans Settings > Authentication
3. Pour l'authentification par mot de passe, l'analyseur de la connexion rapide accepte le format `user:password@host` (les identifiants ne sont pas persistés)
4. Vérifier qu'`AuthPreflightChecker` dispose de sources d'authentification valides avant la tentative de connexion

**Fichiers** : `ConnectionService.Ssh.cs`, `QuickConnectOverlay.xaml.cs`

---

## Redimensionnement RDP - reconnexions persistantes (réglage delta/anti-rebond) {#rdp-resize-still-reconnecting-deltadebounce-tuning}

**Symptôme** : redimensionner la fenêtre de l'application provoque une brève déconnexion/reconnexion de la session RDP embarquée, avec un scintillement visible ou un écran noir momentané.

**Cause racine** : l'appel à `UpdateResolution()` déclenche une reconnexion RDP lorsque le delta de résolution dépasse le seuil. Si le délai d'anti-rebond est trop court ou le seuil de delta trop bas, un redimensionnement rapide de la fenêtre entraîne des reconnexions répétées.

**Solution** :
1. Augmenter l'intervalle d'anti-rebond du redimensionnement (par défaut : 500 ms). Une valeur de 800 à 1000 ms réduit les reconnexions pendant un redimensionnement actif
2. Augmenter le seuil minimal de delta de résolution - les petites variations (moins de 50 px dans l'une ou l'autre dimension) doivent être totalement ignorées
3. Vérifier que `_allowResolutionUpdates` reste soumis à la garde de stabilisation de 5 secondes après connexion
4. Si le problème persiste, vérifier qu'`OnResizeTimerTick` compare bien avec la DERNIÈRE RÉSOLUTION APPLIQUÉE, et non avec la dernière demandée

**Fichiers** : `EmbeddedRdpView.xaml.cs` - `OnResizeTimerTick()`, `UpdateResolution()`

---

## 27. VNC - bibliothèque noVNC indisponible {#vnc-novnc-unavailable}

**Symptôme** : l'onglet VNC affiche l'erreur "noVNC Library Unavailable" au lieu du bureau distant.

**Cause racine** : la bibliothèque JavaScript noVNC est chargée depuis un CDN (`cdn.jsdelivr.net`). Dans un environnement hors ligne ou à réseau restreint, l'import échoue.

**Solution** :
1. Vérifier que la machine dispose d'une connectivité Internet
2. Pour les environnements isolés, télécharger noVNC depuis `https://github.com/novnc/noVNC/releases` et placer les fichiers dans `Assets/vnc/`. Modifier `vnc.html` pour importer depuis le chemin local au lieu du CDN
3. L'application affiche un message d'erreur explicite avec des instructions lorsque le CDN est injoignable

**Fichiers** : `Assets/vnc.html`, `Views/EmbeddedVncView.xaml.cs`

---

## 28. VNC - conflit de port du proxy WebSocket {#vnc-websocket-port-conflict}

**Symptôme** : la connexion VNC échoue avec une erreur de liaison de port.

**Cause racine** : `WebSocketVncProxy` se lie à un port local aléatoire. Dans de rares cas, ce port peut déjà être utilisé.

**Solution** : relancer la connexion - un nouveau port aléatoire sera choisi. Si le problème persiste, chercher des processus qui monopolisent les ports éphémères.

**Fichiers** : `Services/WebSocketVncProxy.cs`

---

## 29. Redirection X11 - aucun affichage {#x11-no-display}

**Symptôme** : les applications redirigées par X11 échouent avec "Cannot open display" ou une erreur similaire.

**Cause racine** : aucun serveur X11 (VcXsrv, Xming, X410) n'est installé ou en cours d'exécution sur l'hôte Windows.

**Solution** :
1. Installer VcXsrv depuis `https://sourceforge.net/projects/vcxsrv/` ou Xming
2. Heimdall détecte et démarre automatiquement le serveur X lorsque la redirection X11 est activée
3. Si le démarrage automatique échoue, définir manuellement le chemin du serveur X dans Settings > X11 Server Path
4. Vérifier que la variable d'environnement `DISPLAY` est définie (Heimdall positionne `localhost:0.0` automatiquement)
5. Quand aucun serveur X n'est disponible au moment de la connexion, le texte d'état de la session le dit et la session est lancée sans redirection ; corriger le serveur, puis se reconnecter

**Fichiers** : `Services/X11ServerManager.cs`, `Services/Handlers/SshHandler.cs`

---

## 30. Telnet - la connexion se fige {#telnet-connection-hangs}

**Symptôme** : la connexion Telnet semble aboutir mais aucune invite n'apparaît.

**Cause racine** : certains serveurs Telnet exigent des réponses de négociation IAC spécifiques. L'implémentation Telnet de Heimdall gère la négociation DO/WILL/DONT/WONT de base, mais ne satisfait pas nécessairement toutes les exigences serveur.

**Solution** :
1. Vérifier que le port cible est correct (par défaut : 23)
2. Tester avec un client Telnet standard pour confirmer que le serveur fonctionne
3. Certains équipements anciens peuvent exiger une négociation de type de terminal spécifique, non encore implémentée

**Fichiers** : `Terminal/TelnetSession.cs`

---

## 31. FTP - échecs en mode passif {#ftp-passive-mode}

**Symptôme** : le listage des répertoires FTP fonctionne mais les transferts de fichiers échouent ou expirent.

**Cause racine** : FTP utilise des connexions de données séparées. Le mode passif (par défaut) impose au serveur d'ouvrir un port auquel le client se connecte. Les pare-feu peuvent bloquer ces ports.

**Solution** :
1. Vérifier que la plage de ports passifs du serveur FTP est accessible
2. Heimdall utilise `AutoPassive` de FluentFTP lorsque le mode passif est activé
3. Désactiver le mode passif dans le profil FTP lorsque le serveur exige le mode actif ; Heimdall utilise alors `AutoActive` de FluentFTP

**Fichiers** : `Sftp/FtpBrowser.cs`

---

## 32. Détachement d'onglet - session WebView2 perdue {#tab-detach-webview2}

**Symptôme** : après avoir détaché un onglet SSH vers une fenêtre flottante puis l'avoir réancré, le terminal peut perdre son état WebView2.

**Cause racine** : les contrôles WebView2 peuvent se comporter de façon imprévisible lorsqu'ils changent de parent entre arbres visuels WPF. Le contrôle conserve son état interne mais le contexte de rendu peut nécessiter une réinitialisation.

**Solution** :
1. Si le terminal apparaît vide après réancrage, la session est toujours vivante - essayez de cliquer dans la zone du terminal
2. Pour les sessions RDP, le détachement est irréversible (les contrôles ActiveX ne peuvent pas changer de parent en toute sécurité)
3. Les sessions fractionnées ne peuvent pas être détachées (par conception)

**Fichiers** : `Views/FloatingSessionWindow.xaml.cs`, `MainWindow.xaml.cs`

---

## 33. Serveur éphémère - accès refusé sur le port 69 {#tftp-port-access-denied}

Avant de diagnostiquer la connectivité, vérifiez que TFTP est activé dans Settings > Advanced > File sharing. TFTP est optionnel depuis la phase 3.7 et le partage fonctionne en HTTP uniquement par défaut.

**Symptôme** : le serveur TFTP ne démarre pas, avec un "access denied" sur le port 69.

**Cause racine** : les ports inférieurs à 1024 exigent des privilèges élevés sous Windows.

**Solution** :
1. Exécuter Heimdall en tant qu'administrateur si TFTP est nécessaire
2. Le serveur HTTP (port 8080) fonctionne sans élévation
3. Sans élévation, TFTP n'est pas disponible (restriction de sécurité Windows)

**Fichiers** : `Services/EphemeralFileServer.cs`

---

## 36. SFTP - permission refusée sur le repli sudo (échec d'authentification) {#sftp-sudo-fallback-auth-failure}

**Symptôme** : les opérations SFTP sur des fichiers appartenant à root affichent "Permission denied" alors que le repli sudo devrait s'activer. Le journal indique `SshAuthenticationException: Permission denied (publickey,password)`.

**Cause racine** : les méthodes utilitaires sudo (`DownloadViaSudoAsync`, `UploadViaSudoAsync`) créaient un `new SshClient(connInfo)` brut, sans intégration Pageant/agent SSH ni vérification TOFU de la clé d'hôte. La connexion SSH elle-même échouait avant que la commande sudo puisse s'exécuter.

**Solution** :
1. Créer une fabrique partagée `CreateSudoSshClientAsync()` qui s'appuie sur `SshConnectionFactory.Create()` (même authentification que la session principale : Pageant, clés, mot de passe)
2. Attacher la vérification de clé d'hôte via `SshConnectionFactory.AttachHostKeyVerification()` en utilisant le `_hostKeyStore` stocké
3. Conserver le champ `_hostKeyStore` dans `EmbeddedSftpView` (il n'était transmis qu'à `RemoteFileEditor`)

**Leçon clé** : toute connexion SSH secondaire (pour sudo, la supervision de santé, etc.) DOIT utiliser la même fabrique et la même chaîne d'authentification que la connexion principale. Les instances brutes de `SshClient` court-circuitent Pageant, les invites keyboard-interactive et la vérification TOFU.

**Fichiers** : `Views/EmbeddedSftpView.xaml.cs`, `Ssh/SshConnectionFactory.cs`

---

## 37. SFTP - le parseur de sudo ls affiche un répertoire vide {#sftp-sudo-ls-parser-empty}

**Symptôme** : activer "Browse as root" (mode sudo) affiche un listage de répertoire vide ou quasi vide. Seuls les liens symboliques (comme `/bin -> usr/bin`) apparaissent.

**Cause racine** : `ls -la --time-style=long-iso` produit **8 colonnes** par ligne :
```
drwxr-xr-x 2 root root 4096 2026-03-18 14:30 dirname
```
Le parseur utilisait `Split(null, 9)` et testait `parts.Length < 9`, ce qui écartait TOUTES les entrées à nom de fichier simple (elles ne produisent que 8 jetons). Les liens symboliques comme `bin -> usr/bin` produisaient assez de jetons pour passer.

**Solution** : passage à `Split(null, 8)` pour que le nom de fichier (qui peut contenir des espaces) reste intact dans `parts[7]`. Tester `parts.Length < 8`.

**Leçon clé** : toujours vérifier le nombre réel de colonnes de la sortie d'une commande avant d'écrire un parseur. Tester avec une vraie sortie serveur, pas avec des suppositions.

**Fichiers** : `Views/EmbeddedSftpView.xaml.cs` (méthode `ParseLsOutput`)

---

## 38. SFTP - le repli sudo n'utilise que les erreurs typées de permission refusée {#sftp-typed-permission-denied-only}

**Symptôme** : l'envoi ou le téléchargement d'un fichier appartenant à root affiche une erreur de transfert générique au lieu de déclencher le repli sudo. Le journal peut indiquer `SshException: Failure`.

**Cause racine** : Heimdall ne considère volontairement plus les chaînes génériques `Failure` comme un refus de permission. Cette heuristique pouvait déclencher des opérations sudo privilégiées sur des échecs sans rapport avec les permissions, comme des erreurs de canal, des coupures réseau ou des refus de stratégie côté serveur.

**Solution** : `EmbeddedSftpViewModel.IsPermissionDenied()` n'accepte que des signaux typés de permission refusée DISTANTS : `SftpPermissionDeniedException`, et le refus de permission propre à la suppression récursive. L'`UnauthorizedAccessException` locale n'en fait plus partie : c'est le système de fichiers local qui refuse (un dossier en lecture seule, une ACL), et escalader dessus lançait un transfert distant privilégié qui échouait sur le même chemin local avec un message accusant le serveur. Si un serveur renvoie un échec générique ambigu pour un vrai problème de permission, réessayez manuellement avec "Browse as root" ou consultez les journaux serveur ; ne réintroduisez pas de correspondance par sous-chaîne.

**Leçon clé** : les faux négatifs sont plus sûrs que les faux positifs privilégiés. L'escalade sudo doit reposer sur des erreurs typées, pas sur le texte des messages.

**Fichiers** : `Views/EmbeddedSftpView.xaml.cs` (méthode `IsPermissionDenied`)

---

## 39. WebView2 - erreur de configuration side-by-side (0x800736B1) {#webview2-sxs-error}

**Symptôme** : le terminal SSH embarqué affiche "WebView2 initialization failed: The application has failed to start because its side-by-side configuration is incorrect (0x800736B1)".

**Cause racine** : le WebView2 Fixed Version Runtime embarqué n'était qu'un sous-ensemble incomplet de fichiers (DLL sélectionnées à la main). Le manifeste de `msedgewebview2.exe` référence des versions précises du runtime VC++ et des assemblies SxS qui doivent être présentes dans le même répertoire.

**Solution** : copier le répertoire de runtime COMPLET depuis `C:\Program Files (x86)\Microsoft\EdgeWebView\Application\{version}\` au lieu de sélectionner des fichiers à la main. N'élaguer que le superflu Edge non essentiel (Copilot, identité, extensions), mais conserver les manifestes, EBWebView et toutes les DLL.

**Leçon clé** : le WebView2 Fixed Version Runtime n'est pas une simple collection de DLL. Il exige des manifestes SxS et une arborescence de répertoires précise. Copiez toujours le runtime complet et élaguez avec prudence.

**Fichiers** : `Services/WebView2Helper.cs`, `Build.ps1`, `runtimes/webview2/`

---

## 40. Traversée HTTP - contournement par préfixe frère {#http-traversal-sibling-prefix}

**Symptôme** : un audit de sécurité a révélé que le test `StartsWith` d'EphemeralFileServer pouvait être contourné avec des noms de répertoires frères. Par exemple, servir `/data` autorisait aussi l'accès à `/data-other/secret.txt`.

**Cause racine** : `fullPath.StartsWith(_servingDirectory)` correspond à tout chemin commençant par le même préfixe, y compris des répertoires frères aux noms proches.

**Solution** : ajouter un `Path.DirectorySeparatorChar` final à la base de comparaison, et prévoir un test de correspondance exacte avec la racine :
```csharp
var safeBase = _servingDirectory.EndsWith(Path.DirectorySeparatorChar)
    ? _servingDirectory
    : _servingDirectory + Path.DirectorySeparatorChar;
if (!fullPath.StartsWith(safeBase) && !string.Equals(fullPath, _servingDirectory))
```

Appliqué aux gestionnaires HTTP et TFTP.

**Fichiers** : `Services/EphemeralFileServer.cs`

## 41. Scan par tunnel des outils - peu ou pas d'hôtes trouvés {#tool-tunnel-scan-few-hosts}

**Symptôme** : la cartographie réseau (ou le scanner de ports, le banner grabber, le testeur de pare-feu, le scanner d'identifiants par défaut) via une passerelle SSH "Route via" ne trouve que l'hôte passerelle ou très peu d'hôtes, alors qu'un scan direct depuis le même sous-réseau en renvoie des dizaines.

**Cause racine** (deux problèmes) :

1. **Aucun délai d'expiration par sonde sur `/dev/tcp`** : la primitive bash `echo >/dev/tcp/HOST/PORT` bloque pendant tout le délai de retransmission TCP du noyau (20 à 127 secondes) sur les ports filtrés (paquets silencieusement rejetés par un pare-feu). Avec un `CommandTimeout` de 10 à 35 secondes sur le canal SSH, un seul port filtré suffisait à faire tuer la commande de scan entière avant d'atteindre les ports suivants.

2. **Aucune phase de découverte d'hôtes** (cartographie réseau uniquement) : le scan par tunnel passait directement au sondage de ports, sans balayage ping ni lecture de la table ARP. Seuls les hôtes ayant des ports ouverts dans la liste exacte scannée étaient renvoyés - les hôtes répondant à ICMP mais sans port ouvert correspondant restaient invisibles.

Par ailleurs, `/dev/tcp` est une fonctionnalité propre à bash. Si le shell de connexion de la passerelle est `dash` ou `sh`, toutes les sondes échouent silencieusement.

**Solution** (appliquée) :

- **Délai d'expiration par sonde** : les 5 vues d'outils utilisent désormais `timeout 2 bash -c "echo >/dev/tcp/HOST/PORT"` au lieu du `(echo >/dev/tcp/HOST/PORT)` nu. La commande `timeout` envoie SIGTERM au bout de 2 secondes (nettoyage propre du processus), et le `bash -c` explicite garantit la prise en charge de `/dev/tcp`.

- **Scan par tunnel en 3 phases de la cartographie réseau** : (1) balayage ping par lots via des tâches d'arrière-plan parallèles + lecture de la table ARP, (2) résolution DNS inverse par lots en une seule commande SSH, (3) sondes `/dev/tcp` parallèles par hôte, bornées par `sleep 5; kill $(jobs -p); wait`.

**Fichiers** : `Views/Tools/NetworkCartographyView.xaml.cs`, `Views/Tools/PortScannerView.xaml.cs`, `Views/Tools/BannerGrabberView.xaml.cs`, `Views/Tools/FirewallTesterView.xaml.cs`, `Views/Tools/DefaultCredentialView.xaml.cs`

---

## 42. SSH - liste des passerelles TestEnv vide après import {#ssh-testenv-gateway-dropdown-empty}

**Symptôme** : après l'import des sessions `Heimdall-TestEnv`, l'édition d'une session SSH/SFTP/RDP tunnelée affiche une liste déroulante de passerelles vide, alors même que le profil importé possède un `SshGatewayId`.

**Cause racine** : `servers.testenv.json` contient des profils de serveur, pas des entrées `SshGatewayDto`. Heimdall stocke les passerelles SSH dans le `config\settings.json` de la build d'exécution, sous `AppSettings.SshGateways`, tandis que les profils de serveur dans `servers.json` ne contiennent que des références aux identifiants de ces passerelles. Si la configuration de build active n'a pas de passerelle correspondante dans `settings.json`, la boîte de dialogue d'édition n'a rien à lister et la résolution de la chaîne de passerelles ne peut pas lier l'identifiant référencé.

Ce point passe facilement inaperçu car la racine du dépôt possède elle aussi un `config\settings.json`, mais un lancement en Debug lit `src\Heimdall.App\bin\Debug\net10.0-windows\config\settings.json`.

**Solution** : injecter la passerelle TestEnv dans le fichier de paramètres d'exécution exact utilisé par l'exécutable, puis redémarrer Heimdall :

```powershell
& 'G:\_Projects\Tests\Heimdall-TestEnv\scripts\Inject-Gateway.ps1' `
  -SettingsPath 'G:\_dev\SnapConnect\Heimdall\src\Heimdall.App\bin\Debug\net10.0-windows\config\settings.json'
```

Autre possibilité : créer la passerelle manuellement dans Settings > SSH & SFTP > SSH gateways et enregistrer les paramètres. Les modifications externes de `settings.json` ne sont pas rechargées à chaud ; redémarrez l'application après avoir exécuté le script.

**Fichiers** : `G:\_Projects\Tests\Heimdall-TestEnv\scripts\Inject-Gateway.ps1`, le `config\settings.json` d'exécution

---

## 43. RDP - `IMsRdpExtendedSettings` inaccessible par un accès de propriété `dynamic` {#rdp-extendedsettings-dynamic-access}

**Symptôme** : le code accédant à `ax.ExtendedSettings`, où `ax` est l'OCX encapsulé par AxHost, lève une erreur de binder à l'exécution du type :

```text
RuntimeBinderException: 'System.__ComObject' does not contain a definition for 'ExtendedSettings'
```

Cela peut survenir même sur des machines Windows 10/11 où `MsTscAx.MsTscAx.10` est enregistré.

**Cause racine** : la surface IDispatch du `System.__ComObject` de l'encapsuleur AxHost n'expose pas la propriété `ExtendedSettings`, alors même que l'OCX RDP sous-jacent prend en charge `IMsRdpClient10` / `IMsRdpExtendedSettings` par QueryInterface sur la vtable.

**Solution** :

1. Déclarer une interface d'interopérabilité typée :

```csharp
[ComImport]
[Guid("302D8188-0052-4807-806A-362B628F9AC5")]
internal interface IMsRdpExtendedSettings
{
    void put_Property(string name, object value);
}
```

2. Récupérer l'objet OCX réel via `AxHost.GetOcx()`.
3. Faire un QI direct par transtypage COM du CLR :

```csharp
var extendedSettings = ocx as IMsRdpExtendedSettings;
```

4. Conserver un repli explicite par `Marshal.QueryInterface` avec le même IID, pour le diagnostic dans les environnements COM inhabituels.

N'utilisez **pas** `IServiceProvider.QueryService` dans ce cas. Sur `MsTscAx.MsTscAx.10`, il renvoie `E_NOINTERFACE` pour les interfaces COM soeurs et constitue le mauvais motif d'acquisition.

**Leçon clé** : pour les interfaces COM soeurs de MsTscAx, faites confiance au QueryInterface direct sur l'OCX, pas à la surface IDispatch dynamique exposée par l'encapsuleur AxHost.

**Fichiers** : `Heimdall.Rdp/ActiveX/ComInterfaces.cs`, `Heimdall.Rdp/ActiveX/RdpActiveXHost.cs`

---

## 44. SSH - clé d'hôte indisponible sur le repli Plink {#ssh-host-key-unavailable-plink}

**Symptôme** : une session SSH Pageant seule ou tunnelée échoue avant le lancement de plink, avec un message localisé de clé d'hôte indisponible.

**Cause racine** : Heimdall n'a pas pu résoudre une empreinte de clé d'hôte via son propre modèle de confiance. Cela arrive lorsqu'aucune empreinte n'est stockée et que l'`IPlinkHostKeyProbe` ne parvient pas à analyser la clé présentée, expire, ou n'est pas disponible. Heimdall refuse délibérément de se rabattre sur le cache de registre de PuTTY/Plink, car cela contournerait le magasin TOFU de l'application.

**Solution** :

1. Démarrer un chemin de connexion SSH/SFTP interactif normal, capable de mener à bien l'invite de clé d'hôte de Heimdall, ou importer la clé via `Settings > SSH & SFTP > Trusted host keys`.
2. Vérifier que le chemin plink configuré est valide et que `PlinkHostKeyProbe` peut l'exécuter.
3. Pour les tests, injecter `IPlinkHostKeyProbe` ; ne lancez pas `plink.exe` depuis des tests unitaires.

**Leçon clé** : `SshFailureCode.HostKeyUnavailable` signifie qu'aucune comparaison digne de confiance n'a pu être faite. Ce n'est pas une discordance ; ne le remappez pas sur `HostKeyMismatch`.

**Fichiers** : `Services/PlinkHostKeyDecider.cs`, `Services/IPlinkHostKeyProbe.cs`, `Services/TunnelService.cs`, `Services/Handlers/SshHandler.cs`

---

## 45. SSH/SFTP - clé d'hôte discordante en cours de session {#ssh-sftp-host-key-mismatch-mid-session}

**Symptôme** : une session SSH/SFTP existante se déconnecte et l'interface affiche un avertissement de sécurité au lieu d'une invite de reconnexion générique. La reconnexion automatique SSH ne démarre pas.

**Cause racine** : une connexion secondaire ou une boucle de lecture a observé une `HostKeyRejectedException` alors que la session était déjà établie. Heimdall achemine ce cas par des événements de sécurité typés afin qu'un MITM éventuel ne soit pas masqué derrière un simple "Disconnected".

**Solution** :

1. Traiter l'avertissement comme un événement de sécurité. Comparer l'empreinte présentée avec une source de confiance hors bande.
2. Si l'hôte a légitimement changé de clé, mettre à jour la clé d'hôte de confiance via l'interface de confiance explicite.
3. Ne pas reconnecter automatiquement ni accepter silencieusement depuis ce chemin.

**Leçon clé** : `SshSessionSecurityEvent` et `HostKeyRotatedDuringUpload` sont des signaux additifs. Les événements `Disconnected` existants sont toujours émis pour compatibilité, mais l'interface de sécurité doit préserver l'événement typé.

**Fichiers** : `SshSessionFailureDispatcher.cs`, `SshSessionSecurityEvent.cs`, `SftpBrowser.cs`, `SshShellSession.cs`, `RemoteFileEditor.cs`, `Views/EmbeddedSftpView.xaml.cs`, `Views/EmbeddedSshView.xaml.cs`

---

## 46. FTP - avertissement d'identifiants en clair {#ftp-cleartext-credential-warning}

**Symptôme** : la connexion FTP aboutit mais la zone d'état avertit que le canal est en clair.

**Cause racine** : le profil utilise FTP avec `FtpUseSsl=false` et un nom d'utilisateur non vide. Dans ce mode, FTP transmet les identifiants sans chiffrement.

**Solution** :

1. Préférer SFTP lorsque le serveur prend en charge SSH.
2. Si FTP est imposé, activer FTPS (`FtpUseSsl=true`) et vérifier le chemin du certificat serveur.
3. Réserver le FTP anonyme en clair aux points d'accès publics et non sensibles.

**Leçon clé** : il s'agit d'un `ConnectionResult.Warning` non bloquant, pas d'un échec de connexion. Il doit être acheminé vers le texte d'état plutôt que vers une fenêtre modale, afin que les flux FTP historiques restent possibles.

**Fichiers** : `Services/Handlers/FtpHandler.cs`, `Services/ProtocolSessionResults.cs`, `Services/ConnectionService.cs`, `Sftp/FtpBrowser.cs`

---

## 47. Passerelle WinRM - réponse serveur invalide HTTP 12152 {#winrm-gateway-12152}

**Symptôme** : un profil WinRM routé via une passerelle SSH échoue avec l'erreur WinHTTP `12152` ("the server returned an invalid or unrecognized response"). Le journal Heimdall montre un tunnel établi proprement ; l'erreur apparaît dans le terminal PowerShell.

**Cause racine** : environnementale, ce n'est pas un défaut de Heimdall. Un tunnel `plink` construit à la main reproduit l'échec en dehors de Heimdall - la redirection TCP s'ouvre (`TcpTestSucceeded: True`) mais l'échange HTTP/WinRM est fermé par le service cible ou par un équipement de couche applicative sur le chemin `bastion -> cible`. Un profil RDP passant par le même bastion fonctionne, ce qui confirme la solidité de la machinerie de tunnel.

**Solution** : aucun correctif de code dans Heimdall. Diagnostiquer le chemin environnemental (stratégie de redirection du bastion, écouteur WinRM cible, IPS/proxy intermédiaire). Procédure d'isolation complète et grille de lecture de la console `plink` : voir [winrm-gateway-12152-diagnostic.md](winrm-gateway-12152-diagnostic.md).

**Fichiers** : aucun (environnemental).

---

## 48. Fournisseur d'identifiants KeePassXC - pièges courants {#keepassxc-credential-provider}

**Symptôme** : le fournisseur d'identifiants externe ne renvoie rien, signale "not found", ou le bouton Test des paramètres échoue lors de l'utilisation de KeePassXC.

**Cause racine** : presque toujours une incohérence de configuration plutôt qu'un bug. Les suspects habituels :

- **`keepassxc-cli` n'est pas installé ou introuvable.** Il doit se trouver dans le `PATH`, ou être installé dans `C:\Program Files\KeePassXC\` (le paquet fournit `keepassxc-cli.exe`). Installez KeePassXC s'il est absent.
- **Le titre de l'entrée du coffre ne correspond pas.** La recherche utilise le champ "Vault entry name" du profil et se rabat sur le nom d'affichage du serveur. Le **titre** de l'entrée KeePassXC doit correspondre exactement à cette valeur. C'est la première cause de "not found".
- **Mauvais préréglage pour le type de base.** Choisissez le préréglage correspondant à la base : "KeePassXC (key file)" pour mot de passe + fichier de clé, "KeePassXC (key file only)" pour une base à fichier de clé seul (`--no-password`), ou "KeePassXC" pour une base à mot de passe maître uniquement.
- **Confusion sur le secret de déverrouillage.** Le champ Unlock secret correspond au **mot de passe maître** de la base, transmis à l'outil via stdin. Laissez-le vide pour les bases à fichier de clé seul (le préréglage utilise `--no-password` et ne lit jamais stdin).

**Solution** :

1. Vérifier que `keepassxc-cli --version` s'exécute (via le PATH ou le chemin d'installation complet).
2. Rendre le titre de l'entrée du coffre identique au "Vault entry name" du profil (ou à son nom d'affichage).
3. Sélectionner le préréglage correspondant à la base, et renseigner le chemin du fichier de clé lorsque le préréglage contient `{KeyFile}`.
4. Renseigner le secret de déverrouillage avec le mot de passe maître, ou le laisser vide pour les bases à fichier de clé seul.
5. Utiliser le bouton Test des paramètres pour confirmer la récupération avant de se connecter.

**Leçon clé** : KeePassXC lui-même n'a pas besoin d'être lancé ni déverrouillé - `keepassxc-cli` ouvre directement le fichier `.kdbx` avec le mot de passe maître et/ou le fichier de clé fournis par Heimdall.

**Fichiers** : `Security/CommandCredentialProvider.cs`, `Security/CredentialProviderFactory.cs`, `Services/CredentialProviderPresetService.cs`, `ViewModels/SettingsViewModel.cs`

---

## 49. RDP embarqué - session coupée peu après la connexion {#rdp-slow-server-cutoff}

**Symptôme** : un serveur dont la session Windows se charge plus lentement que d'habitude est coupé par Heimdall peu après qu'il a déjà annoncé "Connecté". La session est visiblement active d'abord, puis coupée d'elle-même.

**Cause racine** : non confirmée. Trois candidats subsistent : un redimensionnement forcé environ dix secondes après la connexion, une reconnexion automatique épuisée, ou un nettoyage prématuré des identifiants. Aucun journal d'une reproduction lente n'existe encore, et c'est pourquoi aucun d'eux n'a été écarté.

**Solution** : aucune pour l'instant. Ce qui trancherait est une capture propre couvrant toute la séquence, avec l'heure de coupure notée à la main : voir [repro/capture-rdp-slow-server-cutoff-log.md](repro/capture-rdp-slow-server-cutoff-log.md). La procédure est écrite pour quiconque peut reproduire la panne, et une seule capture qui la suit suffit à départager les trois candidats.

**Fichiers** : aucun tant que la cause n'est pas confirmée.

## 50. Tâche planifiée - exécutée mais rien de connecté {#scheduled-task-connected-nothing}

**Symptôme** : l'onglet Scheduled affiche une exécution récente pour une tâche, aucune session ne s'est ouverte, et le journal contient une ligne commençant par `Scheduled task '<nom>' (serverId=<id>) connected nothing:`.

**Cause racine** : la tâche n'a correspondu à aucun profil. La suite de la ligne dit lequel des trois cas s'est produit : aucun profil ne porte l'identifiant enregistré par la tâche (le profil a été supprimé ou recréé, et un profil du même nom n'est jamais présumé le remplacer) ; la tâche n'enregistre aucun identifiant et aucun profil ne porte son nom ; ou la tâche n'enregistre aucun identifiant et plusieurs profils portent son nom, si bien que celui qu'elle vise ne peut pas être établi. L'heure de dernière exécution est écrite avant que la tâche s'exécute, ce qui explique l'exécution affichée.

**Solution** : modifiez la tâche et pointez-la sur le profil voulu. Une tâche enregistrée depuis la version courante mémorise l'identifiant de ce profil et ne dépend plus de son nom.

**Fichiers** : `src/Heimdall.App/ViewModels/Scheduled/ScheduledTaskServerResolver.cs`, `src/Heimdall.App/ViewModels/Scheduled/ScheduledTasksViewModel.cs`

---

## 51. SSH - le serveur pose une question interactive à laquelle ce client ne peut pas répondre {#ssh-keyboard-interactive-unsupported-prompt}

**Symptôme** : une connexion par mot de passe échoue avec un message disant que le serveur a posé une question interactive à laquelle ce client ne peut pas répondre, en nommant la question (par exemple `Verification code:`).

**Cause racine** : le serveur authentifie par keyboard-interactive et demande un second facteur après le mot de passe. Heimdall ne répond qu'à une demande de mot de passe avec le mot de passe stocké ; toute autre demande est laissée vide et enregistrée, et le refus qui suit est signalé comme cette question sans réponse (`SshFailureCode.KeyboardInteractiveUnsupportedPrompt`) plutôt que comme un mot de passe rejeté. Avant cette classification, le même refus était imputé au mot de passe.

**Solution** :

1. Utiliser pour cet hôte un client qui prend en charge le second facteur du serveur, ou s'authentifier avec une clé que le serveur accepte sans défi.
2. Si le serveur vous appartient, exempter la source ou le compte du client du second facteur, ou activer l'authentification par clé publique.

**Leçon clé** : un refus de mot de passe signalé après un tour keyboard-interactive doit se lire avec ce que le tour a demandé ; le classifieur le fait à partir de `SshConnectionParams.KeyboardInteractive`.

**Fichiers** : `Heimdall.Ssh/SshConnectionFactory.cs` (`AnswerKeyboardInteractivePrompts`), `Heimdall.Ssh/FailureClassifier.cs`, `Heimdall.Ssh/KeyboardInteractiveObservation.cs`

---

## 52. Tunnel - repli Plink refusé pour un proxy SOCKS ou une redirection distante {#tunnel-plink-fallback-forwarding-unsupported}

**Symptôme** : une session tunnelisée dont l'authentification à la passerelle a été refusée par le client SSH intégré échoue avec un message disant que le repli Plink ne peut pas fournir le proxy SOCKS ou la redirection de port distante dont le profil a besoin.

**Cause racine** : le repli Plink n'ouvre qu'une simple redirection locale (`-L`). Un profil avec `SocksProxyPort` ou `RemoteBindPort` obtenait cette redirection simple et un succès annoncé alors que la redirection nécessaire n'existait pas ; le repli refuse désormais un tel profil avant de lancer plink.

**Solution** :

1. Corriger l'authentification à la passerelle pour le client intégré (en général l'agent ou la clé que la passerelle attend), qui sert tous les modes de redirection.
2. Ou retirer le proxy SOCKS / la redirection distante du profil si la session n'en a pas besoin.

**Fichiers** : `Services/TunnelService.cs` (`EstablishPlinkTunnelAsync`), `Heimdall.Ssh/Plink/PlinkTunnelRunner.cs`

---

## 53. Mise à jour - la bannière dit que la mise à jour ne s'est pas appliquée {#update-did-not-apply}

**Symptôme** : après avoir accepté une mise à jour, Heimdall revient sur la version précédente et la bannière signale que la mise à jour ne s'est pas appliquée, a été annulée, ou n'a pas pu démarrer parce que Heimdall tournait encore.

**Cause racine** : le relanceur enregistre l'étape atteinte avant de s'arrêter, et le démarrage suivant la rapporte. Les trois causes les plus fréquentes, chacune avec sa formulation : l'invite d'élévation a été refusée (rapportée comme une annulation, pas un échec) ; Heimdall n'était pas sorti au bout de deux minutes, et le relanceur a refusé d'exécuter l'installateur sur un processus vivant ; ou l'installateur lui-même a signalé une erreur. Une copie que l'installateur n'a pas mise en place (zip portable, MSI) ne se voit plus proposer d'installateur et est renvoyée vers la page de publication.

**Solution** :

1. Lire la transcription du relanceur : `Heimdall_relaunch_<date>.log` dans le dossier de journaux indiqué dans le panneau À propos. Elle nomme l'étape et, pour une erreur de l'installateur, le code de sortie.
2. Si Heimdall tournait encore, fermer toutes les fenêtres (fenêtres de session détachées comprises) et réessayer ; une invite de fermeture de session restée ouverte est la raison habituelle.
3. Si la copie est un zip portable ou un déploiement MSI, télécharger la nouvelle archive ou le nouveau paquet depuis la page de publication.

**Fichiers** : `Heimdall.Core/Updates/UpdateRelaunchScript.cs`, `Heimdall.Core/Updates/UpdateOutcomeClassifier.cs`, `Services/UpdateRelaunchOutcomeText.cs`
