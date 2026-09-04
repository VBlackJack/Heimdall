<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->
# Heimdall - Reference des fonctionnalites

*Egalement disponible en anglais : [../FEATURES.md](../FEATURES.md).*

Le catalogue complet de ce que fait Heimdall, protocole par protocole. Si vous cherchez plutot comment demarrer, lisez le [guide utilisateur](USER-GUIDE.md) ; pour la version courte, le [README](../../README.fr.md).

---

## Fonctionnalités

### Bureau à distance (RDP)
- Sessions embarquées via l'ActiveX MsTscAx dans une interface à onglets
- Sessions externes via mstsc.exe avec remplissage automatique des identifiants - le fichier `.rdp` généré respecte le profil de résolution défini par serveur, et le mode Auto s'aligne désormais sur le mode Auto embarqué avec Smart Sizing, lancement fenêtré, mode mono-écran et dimensions de la zone de travail principale (`ae0dd70`)
- **Forçage de mode ponctuel** : clic droit sur un profil RDP → *Se connecter avec...* pour lancer la session en mode embarqué ou externe le temps d'une connexion, sans toucher au profil enregistré. Les sessions forcées affichent un discret suffixe `(forced embedded/external)` dans le titre de l'onglet
- Redimensionnement dynamique de la résolution avec garde de stabilisation
- Profils de résolution par serveur : Fit Window, Fixed, Smart Sizing et Multimon, avec un sélecteur **Écrans sélectionnés** par profil en mode Multimon (sélection vide = tous les écrans, rétrocompatible avec les profils existants) et une validation de topologie à la connexion qui bascule en mode mono-écran quand la machine hôte ne peut pas honorer la sélection enregistrée (`2e9b938`)
- Le mode Fit Window met le bureau distant à l'échelle de la zone hôte avec Smart Sizing activé par défaut, ce qui élimine les barres de défilement natives Win32 sur les cibles RDP Windows réelles ; utilisez le mode Fixed pour un rendu natif au pixel près
- Suivi automatique du facteur d'échelle DPI via `IMsRdpExtendedSettings` avec mise à jour sur `Window.DpiChanged`
- **Menu Résolution et bouton de barre d'outils sensibles au mode** : le menu débute par un en-tête `Active mode: <mode>` (affichant `Fixed (1920x1080)` le cas échéant) et le glyphe du bouton change selon le mode (Auto / Fit / Smart / Fixed / Multimon)
- Sous-menu de résolution dans le menu contextuel de l'onglet, avec préréglages, Match Window, Custom et Save as default - même en-tête `Active mode` que le menu de la barre d'outils
- Rendu en letterbox pour les résolutions fixes lorsque Smart Sizing est désactivé - la zone RDP active est matérialisée par une bordure de 1 px, les bandes autour étant peintes avec le `SurfaceBrush` du thème (le `WindowsFormsHost` est épinglé sur la zone exacte, de sorte que le HWND Win32 ne laisse plus transparaître le gris système par défaut dans le letterbox), et un badge d'aide s'estompe après quelques secondes lors du premier letterbox
- Ergonomie plein écran avec une pastille de sortie à fort contraste, plus les sorties clavier F11, Échap et Ctrl+Shift+F11
- Gestion du rapport d'affichage (Stretch, 16:9, 4:3, 21:9) et prévention de la mise en veille
- Surface de redirection complète : presse-papiers, lecteurs, imprimantes, ports COM, cartes à puce, webcam, USB, audio
- **Indicateurs de redirection repliés automatiquement** : par défaut, la zone d'état de la barre d'outils masque les redirections inactives et les expose derrière une discrète pastille `+N` ; le réglage optionnel `RdpRedirectionIndicatorsAlwaysExpanded` conserve l'ancien comportement "tout afficher"
- **Raccourcis système SendKeys** : en plus de Ctrl+Alt+Del / Win / Alt+Tab / Ctrl+Esc / Impr. écran / Échap, le menu SendKeys propose désormais `Win+L` (verrouiller la station), `Win+D` (afficher le bureau) et `Win+E` (explorateur de fichiers) pour les tâches d'administration rapides
- **Édition du profil toujours accessible depuis la surcouche de reconnexion** : quel que soit le code de déconnexion (réseau, transitoire, sécurité), le bouton `Edit profile` reste visible pour ajuster la résolution, la passerelle ou le multi-écran sans fermer la surcouche au préalable
- Remplissage automatique des identifiants pour les boîtes CredUI (EnumThreadWindows + UI Automation), avec des diagnostics Debug de fenêtre broker limités aux métadonnées : titre, handle, PID et nom de processus ; les champs d'identifiants ne sont jamais journalisés (`1d7c78c`)
- **État de lancement externe honnête** : lorsqu'un client mstsc externe est lancé, la session apparaît en couleur d'avertissement avec un état dédié *External client launched*, signalant que Heimdall ne peut pas observer directement la session distante au-delà du lancement lui-même
- **Import RDP unifié** : les fichiers `.rdp` déposés sur la fenêtre principale ou importés depuis `Settings -> Import` empruntent le même flux d'aperçu et de résolution de conflits
- **Performance** : préchauffage COM au démarrage, pré-résolution DNS à la sélection d'un serveur, indicateurs d'expérience par serveur (fond d'écran/thèmes/animations), suppression de la sonde de transport UDP pour les environnements très filtrés (le client Bureau à distance n'expose aucun moyen, pour une application, de forcer TCP ; ce qui disparaît, c'est la sonde qui expire quand UDP est bloqué)

### Terminal SSH
- Terminal embarqué via WebView2 + xterm.js (rendu VT100/xterm complet)
- Transport en mode pipe pour des flèches, des couleurs et des séquences d'échappement correctes
- **Prise en charge multi-agents** : Pageant (PuTTY) et l'agent OpenSSH de Windows (canal nommé `\\.\pipe\openssh-ssh-agent`) derrière une abstraction commune `ISshAgent`. Priorité configurable par l'utilisateur dans `Settings > SSH & SFTP > Connexion > SSH agent preference` (par défaut : OpenSSH d'abord, Pageant ensuite). Les clés RSA négocient SHA-2 automatiquement, si bien que les serveurs modernes ayant désactivé `ssh-rsa` acceptent toujours les clés mises en cache par l'agent.
- **Champ de phrase secrète de clé distinct** : séparé du mot de passe de connexion, les deux étant persistés chiffrés. Cela permet les scénarios clé-avec-repli-mot-de-passe sans l'ambiguïté de champ des configurations historiques.
- **Import de configuration OpenSSH avec ProxyJump** : les chaînes à un ou plusieurs sauts sont mappées automatiquement sur le modèle de passerelle de Heimdall avec des liens `ParentGatewayId`. Les formes non prises en charge (ProxyCommand, jetons `%h`/`%p`, cycles) sont rejetées avec un diagnostic explicite plutôt qu'importées de travers en silence.
- Heartbeat keepalive SSH (évite les déconnexions dues à TMOUT)
- Vérification TOFU de la clé d'hôte confirmée par l'utilisateur avec épinglage persistant de l'empreinte ; les décisions de confiance sont tranchées *avant* `Connect()` via une sonde de pré-authentification dédiée - le callback `HostKeyReceived` de SSH.NET n'effectue jamais de travail asynchrone ni de dispatch UI
- Application fail-closed de la clé d'hôte pour SSH.NET comme pour le repli Plink, avec `HostKeyUnavailable` lorsqu'une clé de passerelle épinglée ne peut pas être résolue sans retomber sur le cache de PuTTY/Plink
- L'identité de réutilisation des tunnels tient compte de la passerelle (identifiants de passerelle stables + hachage de chaîne normalisé), ce qui évite tout partage accidentel entre réseaux privés qui se recouvrent
- Chaînage de tunnels multi-passerelles avec détection des dépendances circulaires
- Allocation dynamique du port de tunnel avec nouvelles tentatives bornées en cas de course sur le bind (`AddressAlreadyInUse`)
- Comptage de références des tunnels (les tunnels partagés survivent à la fermeture d'une session isolée)
- Redimensionnement du terminal via la requête SSH window-change (API publique `ShellStream.ChangeWindowSize`, sans réflexion)
- Redirection X11 avec détection automatique du serveur X et démarrage automatique
- 29 codes d'échec structurés avec des messages d'erreur localisés
- Des événements de sécurité typés en cours de session distinguent les attaques sur la clé d'hôte des déconnexions ordinaires et suppriment la reconnexion automatique SSH en cas de signal MITM
- Surcouche de reconnexion automatique en cas de déconnexion inattendue (SSH et RDP)

### VNC
- Visionneuse VNC embarquée via noVNC + WebView2
- Proxy WebSocket vers TCP pour une intégration transparente
- Synchronisation du presse-papiers, modes de mise à l'échelle, mode lecture seule
- Déploiement portable de WebView2 (Fixed Version Runtime embarqué pour les serveurs isolés)

### Telnet
- Telnet TCP brut avec négociation IAC
- Prise en charge de la sous-négociation NAWS (taille de fenêtre)
- Rendu dans le même terminal xterm.js que SSH
- Authentification par nom d'utilisateur/mot de passe, avertissement de sécurité sur le transport en clair

### Navigateur SFTP
- Panneau de navigation de fichiers embarqué avec arborescence de répertoires et liste de fichiers
- **Ouverture automatique en compagnon** : s'ouvre automatiquement à côté d'une session SSH sous forme de split vertical (conditionné par un réglage). La reconnexion du volet SFTP reste circonscrite au volet, si bien que le terminal SSH voisin et son historique sont préservés ; un keepalive SSH maintient en vie les sessions SFTP inactives
- **Suivre le répertoire SSH** (optionnel) : le compagnon peut suivre le répertoire courant du terminal SSH via la séquence d'échappement OSC 7, avec une bascule par volet (au mieux ; inerte sur les shells qui n'émettent pas OSC 7)
- Deux modes d'édition : éditeur AvalonEdit intégré OU éditeur externe avec téléversement automatique à l'enregistrement (les échecs de téléversement transitoires sont réessayés)
- **Mode sudo "Parcourir en root"** : une bascule dans la barre d'outils active le listage de répertoires par `sudo ls -la` via un canal exec SSH - parcourez n'importe quel répertoire indépendamment des permissions de l'utilisateur SFTP
- **Repli sudo complet** sur toutes les opérations : téléversement (`sudo tee`), téléchargement (`sudo cat`), édition, chmod, renommage, suppression, mkdir - déclenché uniquement sur des exceptions typées de permission refusée
- Les sessions d'édition sudo mettent en cache le vérificateur de clé d'hôte épinglée, détectent une rotation de clé en cours d'édition, suivent les tâches de téléversement et nettoient les fichiers temporaires même lorsque l'écriture privilégiée échoue
- Téléversement et téléchargement par glisser-déposer
- **Couper / Copier / Coller / Dupliquer** avec gestion non destructive des collisions en SFTP ; la copie côté serveur (clé d'hôte épinglée) réserve la destination de manière exclusive et est journalisée comme une opération unique. La copie côté serveur est refusée dès qu'une telle réservation est impossible : toujours en FTP, et en SFTP quand la commande côté serveur n'est pas utilisable
- **Téléversement récursif de dossiers** et dépôt directement sur une ligne de dossier
- **Collage inter-volets** entre deux navigateurs de fichiers, sur le même serveur ou entre serveurs
- **Coller depuis l'Explorateur** : téléverse les fichiers/dossiers présents dans le presse-papiers Windows (CF_HDROP) vers le répertoire courant
- Opérations de fichiers SFTP/FTP journalisées (download / upload / mkdir / delete / rename / copy)
- Boîte de dialogue chmod, marque-pages de chemins, filtre sur le nom de fichier

### Navigateur FTP
- Client FTP/FTPS appuyé sur les API asynchrones de FluentFTP
- Réutilise l'intégralité de l'interface du navigateur SFTP via l'interface `IRemoteBrowser`
- Mode passif configurable et prise en charge SSL/TLS (FTPS)
- Les connexions FTP en clair avec identifiants font remonter un avertissement non bloquant dans la zone d'état de la session
- La validation de l'hôte et du port est identique à celle des gestionnaires SSH/SFTP
- L'analyse des listages de répertoires est déléguée à FluentFTP pour couvrir les variantes de serveurs

### Citrix
- Intégration StoreBrowse pour les applications et bureaux publiés
- Prise en charge de l'authentification SSO (Kerberos)
- Onglets de session embarqués avec la même ergonomie que RDP

### WinRM (PowerShell Remoting)
- Sessions PowerShell distantes via WinRM / PS-Remoting natif - les connexions directes ne nécessitent aucun SSH
- Terminal interactif embarqué : un `pwsh.exe` local (PowerShell 7+, détecté automatiquement) ou `powershell.exe` (repli 5.1) est hébergé dans un ConPTY et exécute `Enter-PSSession`, en réutilisant la vue terminal du Shell local
- Transports HTTP (5985) et HTTPS (5986) avec une bascule `Use SSL` et un port par défaut dynamique ; validation complète du certificat TLS par défaut
- Deux modes d'identité : un identifiant stocké explicite (chiffré DPAPI) ou l'identité Windows courante (SSO Kerberos, aucun secret stocké)
- Authentification `Negotiate` (Kerberos avec repli NTLM)
- En mode identifiant, le mot de passe est injecté par un script d'amorçage auto-supprimé et restreint par ACL - aucun texte en clair sur disque ni dans l'historique PowerShell
- Une vérification préalable du transport (joignabilité TCP + handshake TLS) fait remonter des erreurs claires et localisées avant le lancement de la session
- Routage optionnel via une passerelle SSH : une session WinRM peut être tunnelisée à travers un bastion SSH, comme RDP et SSH. À travers le tunnel, le transport WinRM est uniquement HTTP (authentification NTLM) ; les connexions WinRM directes ne sont pas affectées.
- Limitation connue des passerelles : certains environnements acceptent le tunnel au niveau TCP mais la cible (ou un équipement intermédiaire) ferme l'échange HTTP WinRM - diagnostiqué comme environnemental, non imputable à Heimdall. Voir [docs/fr/winrm-gateway-12152-diagnostic.md](winrm-gateway-12152-diagnostic.md).

### Shell local
- PowerShell, cmd, bash ou shell personnalisé embarqué via ConPTY
- Mode d'élévation configurable : **Auto** (gsudo `--direct` avec repli), **gsudo**, **Runas** (fenêtre externe) ou **None**
- Compatible avec les gestionnaires de privilèges de poste (AdminByRequest, CyberArk, BeyondTrust) via l'option `--direct` et le repli runas
- Navigateur de fichiers local côte à côte avec synchronisation du cd et éditeur AvalonEdit embarqué
- Variables d'environnement HEIMDALL_* injectées pour le scripting contextuel

### Diffusion Multi-Exec
- Envoie les frappes clavier simultanément à plusieurs sessions SSH actives
- Indicateurs visuels : bordure colorée et badge BROADCAST sur les terminaux destinataires

### Connexion rapide (Ctrl+K)
- Palette de commandes pour des connexions ponctuelles sans enregistrer de profil de serveur
- Prend en charge le format `user@host:port` avec préfixe de protocole optionnel
- La saisie d'une simple IP ou d'un nom d'hôte propose automatiquement des connexions SSH et RDP, l'ordre étant orienté par l'historique propre à l'hôte (dernier protocole utilisé en tête)
- Sert également de sélecteur de serveur et d'outil pour les splits (la recherche approximative passe à l'échelle quelle que soit la taille de l'inventaire)
- Rendue sous forme de `Popup` WPF (HWND propre) afin de s'afficher au-dessus des surfaces ActiveX RDP/VNC
- Avec une requête vide, la vue fait remonter en tête des suggestions les serveurs dont l'hôte apparaît dans le journal des connexions récentes : se reconnecter à une machine récemment utilisée tient en un Ctrl+K + Entrée
- Exécute les snippets de la Command Library directement depuis la palette : ouvrez un snippet pour voir sa description, ses badges de risque et de plateforme, ses notes, ses exemples (copier ou envoyer) et ses liens de documentation ; renseignez les paramètres du modèle en ligne avec un aperçu de commande en direct ; les termes recherchés sont surlignés dans les résultats, et le détail est accessible au clavier et aux lecteurs d'écran (v2026.061602)

### Panneau des tunnels
- Panneau latéral rétractable listant tous les tunnels SSH actifs
- Affichage en temps réel de l'état, du port local, de la cible distante et de la chaîne de passerelles
- Visualisation de la chaîne de tunnels dans l'en-tête des onglets de session (via GatewayA → GatewayB)
- Allocation dynamique des ports avec comptage de références pour les tunnels partagés

### Supervision de la santé des serveurs
- Panneau repliable de la barre latérale affichant l'usage CPU, RAM et disque
- Canal SSH multiplexé (n'interfère pas avec la session terminal)
- Interroge `top`, `free`, `df` toutes les 15 secondes via des commandes SSH asynchrones, avec barres de progression

### Enregistreur de macros
- Enregistre les saisies du terminal avec le timing entre les frappes
- Sauvegarde des macros en fichiers JSON, rejeu avec les délais d'origine
- **Étapes expect** : une macro peut attendre un motif de sortie attendu avant d'envoyer l'étape suivante (attente de motif), rédigées dans un éditeur de macros dédié
- Accessible depuis le menu contextuel de la session

### Scanner réseau
- Balayage ping ICMP sur des sous-réseaux CIDR (Ctrl+Shift+N)
- Sonde des ports TCP sur les hôtes qui répondent (SSH, RDP, VNC, HTTP, HTTPS)
- "Ajouter aux sessions" en un clic pour les hôtes découverts, avec type de connexion détecté automatiquement

### Tâches planifiées
- Planificateur de connexions automatiques quotidiennes ou par intervalle
- Minuteur en arrière-plan avec dispatch asynchrone correct et ticks protégés par sémaphore
- Une tâche cible un profil par son identifiant. Une tâche enregistrée sans identifiant (anciens fichiers de tâches) ne correspond par nom affiché que si un seul profil le porte ; identifiant disparu, nom absent ou nom partagé par plusieurs profils, rien n'est connecté et le journal dit lequel des trois cas s'est produit

### Outils externes
- Outils configurables dans le menu contextuel des serveurs, avec panneau d'édition en ligne
- 8 variables de substitution : `{Host}`, `{Port}`, `{User}`, `{ServerName}`, `{Protocol}`, `{KeyFile}`, `{Project}`, `{Gateway}`
- Options Exécuter en tant qu'administrateur, Exécuter masqué et Répertoire de travail, avec boutons de parcours
- Aperçu de la commande en direct avec les substitutions résolues depuis le serveur sélectionné
- Bouton de test pour lancer directement depuis les Paramètres
- Validation de l'existence du binaire à l'enregistrement (recherche dans le PATH + chemin absolu)
- Délai d'exécution configurable (60 s par défaut)
- Intégré à la palette de commandes Ctrl+K

### Serveur de fichiers rapide
- Serveur de fichiers HTTP en un clic avec prise en charge TFTP optionnelle, activable depuis Settings > Advanced > File sharing, pour transférer des fichiers vers des serveurs dépourvus de SFTP (serveurs durcis, conteneurs, équipements réseau)
- Affiche des commandes `wget`/`curl` prêtes à l'emploi, n'ajoute le snippet de commande `tftp` que si TFTP est activé, et copie automatiquement l'URL de partage dans le presse-papiers
- HTTP : listage de répertoire, types MIME, protection contre la traversée de chemins
- TFTP : implémentation RFC 1350 en lecture seule

### Boîte à outils sysops intégrée (58 outils)

Tous les outils s'ouvrent comme des onglets de session (split avec n'importe quelle session ou outil, détachement, réorganisation). Accessibles via l'**onglet Outils dédié**, la palette **Ctrl+K**, l'**onglet Outils de la barre latérale** (bascule Sessions/Outils en haut du panneau de gauche, **Ctrl+Shift+T**) ou le menu **"+" → Ajouter un outil**. La barre latérale à onglets héberge côte à côte le `TreeView` des sessions et un explorateur d'outils pleine hauteur - l'onglet Outils affiche un `TreeView` repliable de catégories (Réseau, Sécurité, Encodage, Système, Externe) alimenté par `ToolRegistry`, avec un champ de filtre portant sur le nom + les alias et une section **Favoris** toujours présente en tête. Un clic droit sur une feuille d'outil de la barre latérale permet de l'épingler ou de le désépingler sans le lancer ; les mêmes `FavoriteToolIds` persistés alimentent la section Favoris de la barre latérale et l'onglet Outils dédié, les favoris restant triés alphabétiquement sur le nom affiché localisé et filtrés comme toute autre catégorie. Les outils peuvent être enregistrés dans le `TreeView` des sessions aux côtés des sessions réelles. `ToolRegistry` centralisé avec icônes vectorielles, catégories et alias de commande. Les **favoris** (épinglage/désépinglage persistés) et les outils **récemment utilisés** restent également disponibles sur l'onglet Outils dédié. Comportement singleton pour les outils sans contexte. Système d'aide intégré avec exemples d'utilisation (bouton ?). Panneau de détail dédié pour les outils accompagnés d'une description. Password Generator prend en charge des préréglages personnalisés enregistrables (persistance JSON), un effacement automatique optionnel du presse-papiers et 3 modes de génération (Random, Syllable, Passphrase). Navigation croisée entre outils via les menus contextuels du clic droit (IP → Port Scanner → Cert Inspector). Les outils réseau peuvent scanner à travers un tunnel SSH (sélecteur de passerelle "Route via"). Surcouche d'**accueil au premier lancement** avec introduction guidée.

| Catégorie | Outils |
|----------|-------|
| **Réseau** | **Network Cartography** (amorçage par ARP + découverte multi-sondes [DNS inverse, NetBIOS, TCP], balayage ping + empreinte OS, scan de ports, capture de bannières, analyse d'en-têtes HTTP/HTTPS, inspection de certificats TLS, sonde NetBIOS NBSTAT, requête SNMPv2c + classificateur d'OID constructeurs [Cisco/Juniper/Fortinet/Palo Alto/MikroTik/VMware], découverte mDNS/Bonjour, table de 300+ OUI MAC, classification de rôle multi-sources, détection de VLAN, colonnes adresse MAC + latence, export de topologie Draw.io, historique/diff de scans, détection automatique du sous-réseau distant via passerelle SSH, **base de connaissances persistante avec accélération par cache à TTL**, **scan tunnelisé avec balayage ping distant + découverte ARP + sondes de ports en parallèle**), **Ping Monitor** (graphe de latence continu + routage par passerelle SSH), DNS Lookup (serveur personnalisé + via tunnel), SSL Cert Inspector (chaîne + version TLS + via tunnel), Port Scanner (progression + capture de bannière + via tunnel), Subnet Calculator (IPv4 + IPv6), IP Converter, HTTP Status Codes, Whois Lookup, Network Calculator (supernet + planificateur de VLAN) |
| **Sécurité** | Password Generator (temps de cassage + historique + préréglages enregistrables), SSH Key Generator (RSA + Ed25519), Hash Generator (SHA3 + progression), HMAC Generator, JWT Parser (vérification de signature HMAC), Certificate Generator (auto-signé + CA/feuille), TOTP Generator (RFC 6238), **SecNumCloud Audit** (conformité ANSSI v3.2 sur les volets Réseau/Crypto/Accès/Exploitation avec détection automatique du CIDR + routage par passerelle + export HTML/CSV/Draw.io) |
| **Encodage** | Base64 Encoder (URL-safe RFC 4648), URL Encoder, JSON Formatter (position de l'erreur), Regex Tester (surlignage des correspondances), Text Diff (au niveau du mot), Text Case Converter (8 formats) |
| **Système** | Chmod Calculator, Crontab Builder, DateTime Converter (fuseau horaire + relatif), UUID Generator (v4 + v7), ULID Generator, Hosts File Editor, SSH Config Generator, Log Viewer / Tail (filtre regex), Cron Job Manager (crontab + tâches Windows), Service Status Dashboard, **Notes** (éditeur Markdown façon Obsidian avec WYSIWYG Milkdown + thème Dracula, barre latérale repliable à largeur persistée, menu de mise en forme au clic droit, modèles localisés EN/FR, explorateur de fichiers en TreeView, `[[wiki-links]]` insensibles aux accents, tags, glisser-déposer, export Confluence/HTML), **Diagram Editor** (draw.io embarqué hors ligne, Nouveau/Ouvrir/Enregistrer/Exporter en PNG), **Command Library**, **Privilege Launcher** |

### Gestion des sessions
- Sessions à onglets avec réorganisation par glisser-déposer
- Détachement d'un onglet vers une fenêtre flottante (glisser-sortir façon Chrome ou menu contextuel)
- **Split récursif à N volets** : jusqu'à 8 volets par onglet, dans n'importe quelle disposition (2x2, en L, 3 côte à côte, etc.)
- Split supplémentaire de n'importe quel volet : clic droit → "Split..." → Horizontal | Vertical, ou palette de commandes
- **Fusionner un onglet existant** : clic droit → "Merge with..." → session ou outil → Horizontal | Vertical (rattache la connexion vivante sans reconnexion)
- **Splits mixtes session + outil** : combinez librement connexions et outils intégrés dans le même onglet (par exemple terminal SSH à gauche + Network Cartography à droite)
- **Split par glisser-déposer** : faites glisser un onglet sur la zone de contenu d'un autre onglet pour fusionner (orientation détectée automatiquement d'après la position de dépôt)
- Échange de volets, bascule d'orientation (Ctrl+Shift+O), détachement de n'importe quel volet vers une fenêtre flottante
- L'annulation du split restaure les volets en onglets indépendants avec toutes leurs métadonnées préservées
- **`SplitService` dédié** : orchestration du split/merge dans un service dédié avec des jetons d'annulation par session (libération différée du CTS), CancellationToken propagé à tous les gestionnaires de protocole, démontage d'onglet centralisé via `CloseAllPanes`
- Surcouche de déconnexion par volet avec Reconnecter, **Modifier le profil**, **Copier l'erreur** et Fermer (libellés accessibles pour les lecteurs d'écran). Modifier le profil ouvre le profil enregistré qui a échoué, et se cache pour les sessions ad hoc, qui n'en ont pas ; Copier l'erreur place le message, l'étape, le code et le détail dans le presse-papiers
- Divulgation d'échec circonscrite au volet pour SSH et RDP, avec diagnostics structurés étape/code/détail
- Surcouche de chargement avec indicateur pendant la connexion d'un volet
- **Taille minimale de volet imposée** : 120x80 px empêche le séparateur de réduire les volets à une taille inutilisable
- **Double-clic sur le séparateur** : remise du ratio à 50/50 ; bordure au survol des volets pour mieux identifier le volet actif
- **Curseur de séparateur dynamique** : SizeNS pour les splits horizontaux, SizeWE pour les verticaux (mis à jour lors de la bascule d'orientation)
- Ratio du séparateur mémorisé par volet d'un changement d'onglet à l'autre ; restauré lors d'une fusion à partir de l'historique de disposition
- Persistance de la disposition des splits (schéma JSON versionné) : les serveurs précédemment appairés sont suggérés dans la palette de commandes (tous les serveurs sont visibles en mode split)
- **Annulation par session** : fermer un onglet annule proprement toute opération de split ou de reconnexion en cours (jeton propagé aux gestionnaires de connexion SSH/RDP/VNC)
- **Nettoyage différé de la machine à états** : la reconnexion ne libère l'ancien tunnel/état qu'une fois la nouvelle connexion réussie ou définitivement échouée
- **Retour sur la fusion** : message dans la barre d'état lorsqu'un outil occupé bloque une opération de fusion
- La palette de commandes est rendue en `Popup` WPF (HWND propre) au-dessus des surfaces ActiveX RDP/VNC
- **Opérations en masse** : sélection multiple (Ctrl+Clic, Maj+Clic) → clic droit → connexion groupée, duplication, suppression, déplacement vers un projet/dossier, modification du port, du nom d'utilisateur, du mot de passe (chiffré DPAPI, avec boîte de confirmation) et **modification groupée de la passerelle SSH** (sans identifiants, quatre résultats explicites - conserver / forcer en direct / hériter / spécifique - en ignorant les protocoles qui ne gèrent pas les passerelles)
- **Renommage en ligne** : F2 ou Ctrl+E renomme sessions et dossiers directement dans l'arborescence, sans ouvrir de boîte de dialogue, et reste correct sous virtualisation
- **Filtres d'arborescence structurés** : filtrez l'arborescence des sessions par protocole/type, favori et état de connexion, en combinaison avec la recherche textuelle temporisée, le tout en une passe versionnée sur des noeuds stables ; une pastille colorée sur le bouton de filtre signale un filtre actif
- **Arborescence de sessions virtualisée** : la virtualisation avec recyclage garde l'arborescence fluide à plusieurs milliers de sessions et "Tout développer" ne fige plus l'interface ; les noms longs s'affichent en entier avec une barre de défilement horizontale automatique lorsqu'ils dépassent le volet, et la recherche développe désormais les dossiers contenant une correspondance et surligne le texte trouvé
- Journalisation globale des sessions (optionnelle) : transcriptions texte par session pour SSH / Telnet / Shell local et journal d'événements connexion/déconnexion (motif + durée) pour RDP / VNC / Citrix, avec ACL restrictives et rotation par taille ; **forçage tri-état par profil** (activé / désactivé / hérité) dans la boîte de dialogue serveur
- Journal de l'historique des connexions (JSONL avec rotation automatique)
- Capture d'écran vers le presse-papiers (Ctrl+Shift+S)

### Interface utilisateur
- Changement de thème à chaud parmi **17 thèmes ThemeForge** (Drakul par défaut, plus Dracula, Striga, Cinder, Bracken, Tarn, Mortis, Slate, Magellan, Voivode, Carmilla, Whitby, Vesper, Wormwood, Sconce, Parchment, Folio - regroupés en Root / Dark / Light / Alt), ainsi qu'un **sélecteur d'accent à 9 teintes** (Défaut, Bleu, Cyan, Vert, Orange, Rose, Violet, Rouge, Jaune). Le tout s'appuie sur le paquet NuGet public `ThemeForge.Theme` (nuget.org) via le wrapper `HeimdallThemeService` ; un ResourceDictionary `HeimdallThemeBridge` réexprime les brosses de l'application Heimdall sur les emplacements de couleurs ThemeForge et est réintégré à chaque changement de thème, afin que les convertisseurs et les panneaux construits par code se recolorent en direct
- 1 870+ lignes de styles de contrôles WPF partagées par tous les thèmes, réactives aux changements de thème à chaud (`DynamicResource` partout ; déclencheur `MultiBinding` + `ThemeRevision` pour les convertisseurs qui résolvent des brosses ; `SetResourceReference` dans les panneaux en code-behind)
- Design System à 45 tokens : typographie (10 tailles, 11 px minimum), espacement (8 tokens dont des asymétriques), padding des boutons (4 rôles), padding des champs, rayon des angles, opacité, tailles d'icônes, famille de police à chasse fixe, micro-animations (150 ms/250 ms)
- Conforme WCAG AA : toutes les paires premier plan/arrière-plan sont vérifiées à un ratio de contraste de 4,5:1 ou plus, et le curseur des barres de défilement à 4,2:1 ou plus sur chaque thème ThemeForge
- FocusIndicatorBrush pour l'accessibilité de la navigation au clavier sur tous les styles de boutons
- Système d'icônes unifié à deux niveaux : géométries vectorielles (`Geo.*`) pour les icônes métier + Segoe MDL2 pour le chrome de l'interface
- Infobulles localisées sur tous les boutons purement iconographiques ; AutomationProperties.Name sur tous les contrôles interactifs via l'i18n
- 19 styles de contrôles thématisés avec états hover/pressed/focused/disabled complets
- 5 palettes de couleurs de terminal : Dracula, Solarized Dark, Monokai, Nord, Default - Dracula est également appliqué à l'éditeur Milkdown des Notes
- Famille et taille de police du terminal configurables
- Panneau de paramètres avec 6 sous-onglets de navigation à gauche (General, Terminal, SSH & SFTP, RDP, Security, Advanced) ; le sous-onglet RDP expose désormais le tableau `RdpResolutionPresets` auparavant caché sous forme de liste multiligne éditable, ainsi que l'indicateur `RdpDialogAdvancedDefault` sous forme de case à cocher. Les sous-onglets RDP et `SSH & SFTP` regroupent chacun leurs options derrière un contrôle segmenté - RDP en Affichage et audio / Périphériques / Performance / Comportement, `SSH & SFTP` en Connexion / Session / SFTP et X11 / Clés d'hôtes / Passerelles
- Boîte de dialogue serveur : un flux en deux étapes - sélection de la carte de protocole, puis un **éditeur à quatre onglets** (General / Options / Network / Info) avec un badge de protocole permanent dans l'en-tête, des badges d'erreur par onglet, une visibilité d'onglet propre à chaque protocole et un focus de validation qui saute au premier champ invalide, y compris d'un onglet à l'autre. La fenêtre est librement redimensionnable, avec défilement natif à la molette. L'onglet Options RDP conserve un mini-sommaire à quatre pastilles (Display / Audio / Devices / Performance) ; sa section Display propose une liste déroulante `Common resolutions` pour pré-remplir Width/Height en mode Fixed, ainsi qu'une bascule dédiée `Enable multi-monitor`
- Hiérarchie TreeView : Dossier > Serveur, avec dossiers imbriqués, icônes d'outils colorées par catégorie et pastilles d'état
- Palette de commandes (Ctrl+K) : icônes de protocole, pastilles d'état, indices de point de terminaison, Ctrl+Entrée pour un split
- Héritage de connexion : valeurs par défaut au niveau du dossier pour la passerelle, le nom d'utilisateur SSH et le chemin de clé
- États vides : les vues d'outils affichent des indications avant la première requête, et un panneau d'accueil propose un appel à l'action d'import
- Bouton d'aide intégré ("?") sur les 58 outils, avec instructions d'utilisation localisées
- Indicateur d'activité d'onglet : pastille d'accent pulsante sur les onglets pendant les opérations d'outil longues
- **Barre latérale à onglets** (Sessions / Outils) : explorateur d'outils pleine hauteur avec catégories repliables, section Favoris toujours présente, lancement en un clic et gestion des favoris au clic droit sans lancement accidentel. Ctrl+Shift+T bascule l'onglet actif de la barre latérale
- **Ergonomie des sessions dans la barre latérale** : barre d'outils sur deux lignes avec recherche pleine largeur au-dessus d'actions purement iconographiques, largeur par défaut de 320 px, et troncature intelligente des noms longs qui préserve l'identifiant de session tout en abrégeant les suffixes entre parenthèses en fin de nom
- Mode plein écran (F11), bascule de la barre latérale (Ctrl+B), filtre (Ctrl+F)
- **Accueil au premier lancement** : une visite guidée en 6 étapes qui met en surbrillance le contrôle réel dont elle parle - le voile est découpé autour de la cible et cerclé - et qui navigue vers le bon onglet avant chaque étape plutôt qu'après. Rejouable à tout moment depuis `Paramètres > Général`, si bien qu'une touche Échap réflexe ne la termine plus définitivement. Une étape dont la cible est introuvable retombe sur une carte centrée plutôt que de cercler du vide
- Interface bilingue : anglais et français (6 054 clés i18n par langue, parité EN/FR exacte)
- i18n déclarative : extension de balisage WPF `{loc:Translate Key}` avec changement de langue à chaud
- Accessibilité WCAG 2.1 AA : AutomationProperties.Name sur tous les contrôles interactifs via `{loc:Translate}`, LiveSetting="Polite" sur les sorties dynamiques, indicateurs de focus clavier, infobulles sur les états désactivés, décompte des résultats de filtre annoncé en région live à chaque changement, et lignes de dossier prenant le focus clavier comme cibles fiables du menu contextuel Maj+F10 / touche Applications, avec un nom d'automatisation localisé

### Sécurité
- Chiffrement DPAPI + intégrité HMAC-SHA256 via le `CredentialProtector` unifié
- **Coffre à mot de passe maître (optionnel, chiffrement au repos)** : une clé de chiffrement de clé Argon2id enveloppe une clé de chiffrement de données (DEK/KEK) ; `CredentialProtector` est conscient des versions et reste rétrocompatible avec les blobs DPAPI historiques. Activation / changement / désactivation depuis les Paramètres, avec un moteur de migration qui ré-enveloppe les identifiants existants et des écritures de configuration atomiques (temporaire puis renommage)
- **Verrou de déverrouillage au démarrage et verrouillage du plan de travail** : lorsqu'un mot de passe maître est défini, le plan de travail est verrouillé au lancement jusqu'au déverrouillage ; verrouillage manuel et verrouillage automatique sur inactivité (`AutoLockIdleMinutes`) avec surcouche, les sessions verrouillées étant masquées plutôt que déconnectées (déconnexion au verrouillage disponible en option)
- **Déverrouillage du coffre par Windows Hello** : une clé Hello liée au TPM (signature → HKDF → AES-GCM, composée avec DPAPI) permet en option de déverrouiller le coffre par biométrie/PIN au lieu du mot de passe maître ; fail-closed avec repli sur le mot de passe maître, conditionné à la présence d'un TPM
- Fournisseur d'identifiants externe : choisissez un fournisseur en ligne de commande (modèles préconfigurés pour KeePassXC, KeePass2/KPScript, Bitwarden CLI, 1Password CLI, pass - avec un secret de déverrouillage optionnel fourni via stdin, une commande de nom d'utilisateur séparée, un nom d'entrée de coffre par profil et un mode de sortie "première ligne uniquement") ou le Gestionnaire d'identifiants Windows natif ; le fournisseur est construit par `ICredentialProviderFactory` et couvre SSH (sessions embarquées et repli Plink), SFTP, RDP, WinRM en mode identifiant, FTP et VNC. Telnet, Citrix et le mode PuTTY externe ne consomment pas d'identifiants issus de ce fournisseur - navigateur de chemin de base de données, indications de saisie, bouton de test avec retour en ligne ; KeePassXC gère également l'authentification par fichier de clé (.keyx/.key), seule ou combinée à un mot de passe maître
- Barrière Windows Hello optionnelle : exige une vérification biométrique/PIN avant que des identifiants stockés ne soient utilisés à la connexion (unitaire et en masse), fail-closed avec une fenêtre de grâce configurable
- Hachage du code PIN en PBKDF2-SHA256 (100 000 itérations) avec mécanique de verrouillage
- Application des ACL Windows sur les répertoires de configuration, les fichiers de log et les fichiers temporaires
- Utilitaires de sécurité centralisés dans `InputValidator` : `EscapeShellArg()`, `EscapeForDoubleQuotedString()`, `ValidateDomain()`, `SanitizeCsvCell()`, `IsShellTarget()` - prévention de l'injection shell (CWE-78) sur tous les tunnels SSH et tous les appels `CreateCommand()` des outils, assainissement des substitutions sensible au contexte (strict pour les cibles shell, souple pour les exécutables ordinaires), prévention de l'injection de formules CSV sur tous les exporteurs, assainissement CRLF sur les en-têtes HTTP
- Prévention de la traversée de répertoires HTTP/TFTP avec contrôle de préfixe frère
- Validation de l'Origin WebSocket sur le proxy VNC (prévention CSWSH)
- Création de fichier atomique avec ACL restrictives pour les fichiers temporaires sensibles (à l'abri des TOCTOU)
- Les éditions et téléversements SFTP privilégiés (sudo) transmettent leur contenu par le canal privilégié vers un répertoire temporaire appartenant à root créé à côté de la cible, puis valident par un renommage atomique qui refuse les liens symboliques - aucun passage par un `/tmp` inscriptible par un attaquant, et le chemin de lecture privilégié conserve un descripteur no-follow
- Les jetons de lancement Citrix ne sont déchiffrés qu'à la frontière du lancement et ne sont jamais écrits dans un log ou une exception ; un coffre verrouillé échoue en fail-closed plutôt que de lancer la session
- Prévention de la traversée de chemins sur les opérations de renommage et de création de dossier du navigateur de fichiers local
- Écritures de ConfigManager sûres en concurrence via SemaphoreSlim
- Content Security Policy (CSP) et blocage de navigation pour WebView2
- Les documents WebView embarqués (éditeur de notes Milkdown, vue VNC) imposent une origine exacte schéma/hôte/port/chemin pour le trafic `postMessage` accepté et pour la navigation - aucune correspondance de confiance par sous-chaîne n'est utilisée, si bien qu'un document étranger ne peut ni poster vers l'hôte ni le faire naviguer
- IPC Pageant durcie : DACL réservée à soi-même sur le mappage de fichier partagé, suffixe aléatoire cryptographique dans le nom du mappage (64 bits d'entropie), liste blanche des processus Pageant de confiance avant tout trafic d'agent, et vérification préalable d'agent vide
- Comparaison des empreintes de clé d'hôte en temps constant via `CryptographicOperations.FixedTimeEquals`
- Import de `known_hosts` borné par ligne (64 Ko) et par fichier (50 Mo) avec un `StreamReader` en flux ; une entrée malformée dégrade vers un diagnostic plutôt qu'une exception dans l'interface
- La purge de stderr de Plink caviarde les affectations de password / passphrase / token / bearer ainsi que les options `-pw` / `-pwfile` ; la tâche de purge est jointe avant `Process.Kill()` afin qu'aucun lecteur d'arrière-plan ne survive à son tube
- Protection XXE : DtdProcessing.Prohibit sur tous les importeurs XML (mRemoteNG, RDCMan, cache Citrix)
- Fichier de mot de passe Plink : création atomique avec ACL sous Windows, mode 0600 sous Unix (sans repli)
- Wake-on-LAN par paquet magique UDP (menu contextuel du clic droit)
- Gestion de la première utilisation et des discordances de clé d'hôte SSH confirmée par l'utilisateur, sur les chemins SSH.NET comme sur le repli Plink, les décisions interactives étant tranchées dans une sonde de pré-authentification plutôt qu'à l'intérieur du callback `HostKeyReceived` de SSH.NET
- `HostKeyTrustService` centralisé avec métadonnées par entrée (première vue, dernière vue, algorithme, source) - les chemins de production exigent des dépendances de vérificateur de clé d'hôte à la compilation ; aucun repli d'acceptation automatique silencieux dans le code de release
- Dépendances de clé d'hôte non-nullables à la compilation sur les points d'entrée SSH/SFTP/tunnel/sudo ; `RejectingHostKeyVerifier` est le vérificateur fail-closed sûr et `AutoAcceptHostKeyVerifier` est réservé aux tests
- Les événements de discordance de clé d'hôte en cours de session se propagent par `SshSessionSecurityEvent` / `HostKeyRotatedDuringUpload` au lieu d'être réduits à un texte de déconnexion générique
- Sous-onglet `Settings > SSH & SFTP > Clés d'hôtes` : grille dense et auditable de chaque clé d'hôte de confiance avec provenance de la source, import depuis `~/.ssh/known_hosts`, export vers ce même fichier, résolution de conflit explicite ligne par ligne ("Keep existing" par défaut) et actions de copie/suppression de ligne
- Synchronisation optionnelle de `known_hosts` au démarrage, afin que Heimdall, la CLI OpenSSH et Plink partagent une vue unique de la confiance
- Les sessions de repli Plink imposent des empreintes `-hostkey` épinglées issues du magasin de confiance partagé et refusent de se lancer quand Heimdall ne peut pas résoudre une empreinte épinglée ou sondée en toute sécurité
- Le remplissage automatique via le broker d'identifiants exige une correspondance du titre de l'hôte RDP avant d'injecter un mot de passe
- Les limitations de sécurité connues et les notes de modèle de menace sont suivies dans [docs/fr/SECURITY.md](SECURITY.md)
- Entrées CredMan circonscrites à la session, avec nettoyage déterministe

### Import et migration
- Migration depuis Heimdall v1 (identifiants chiffrés DPAPI préservés)
- Import depuis JSON, MobaXterm (.mxtsessions / .ini), mRemoteNG (.xml), RDCMan (.rdg) et fichiers .rdp
- Les imports de sessions JSON ne contiennent que des profils de serveurs. Les définitions de passerelles SSH vivent dans `settings.json`
  (`AppSettings.SshGateways`), si bien que des jeux de test comme `Heimdall-TestEnv` doivent injecter la
  passerelle correspondante dans la configuration de build d'exécution exacte avant que les sessions tunnelisées puissent la résoudre.

### Mises à jour intégrées
- Vérification des mises à jour au démarrage face à la dernière release GitHub (espacée dans le temps, configurable dans les Paramètres), avec une bannière non bloquante proposant **Voir la release**, **Plus tard** et **Ignorer cette version**
- Section **Updates** dans les Paramètres, avec un bouton **Vérifier maintenant** manuel et la version installée
- Chaque release publie un fichier `SHA256SUMS.txt` afin de vérifier l'intégrité de l'installateur avant de l'exécuter
- L'installateur téléchargé est déposé dans un répertoire à ACL restrictives et maintenu sous un handle interdisant l'écriture, de la vérification jusqu'au lancement ; son SHA-256 (ainsi que l'Authenticode lorsque l'installateur est signé) est revérifié juste avant le lancement élevé, ce qui interdit toute substitution entre vérification et exécution

---


---

## Pile technologique

| Couche | Technologie |
|---|---|
| Runtime | .NET 10 (C# 14) |
| Framework UI | WPF (MVVM via CommunityToolkit.Mvvm) |
| Injection de dépendances | Microsoft.Extensions.DependencyInjection |
| SSH/SFTP | SSH.NET 2025.1.0 |
| Rendu du terminal | WebView2 + xterm.js |
| VNC | noVNC (client VNC HTML5 dans WebView2) |
| Éditeur de code | AvalonEdit |
| RDP | ActiveX MsTscAx (WindowsFormsHost) |
| Citrix | Intégration de la CLI StoreBrowse |
| Crypto | System.Security.Cryptography.ProtectedData (DPAPI) |
| Tests | xUnit (7 900+ tests passants répartis sur 8 projets) |
| Outils intégrés | 58 outils sysops (Ctrl+K → `tools` ou Ctrl+Shift+T) |
| Sérialisation | System.Text.Json |

---

## Architecture

La solution est découpée en 9 projets sources aux frontières de dépendances nettes :

```
Heimdall.App          WPF application (MVVM, views, themes, services)
  +-- Heimdall.Core     Models, security (DPAPI, HMAC, PIN), config, state machine, i18n
  +-- Heimdall.Ssh      SSH engine (SSH.NET), tunnels, Pageant IPC, TOFU, failure classifier
  +-- Heimdall.Rdp      RDP + Citrix engine (ActiveX MsTscAx), credential autofill, StoreBrowse
  +-- Heimdall.Sftp     SFTP/FTP browser (SSH.NET + FluentFTP), remote file editing
  +-- Heimdall.Terminal  Terminal sessions (pipe mode, ConPTY, Telnet), smart paste guard
  +-- TwinShell.*        Terminal emulator core, persistence, and infrastructure components
```

Projets de tests : `Heimdall.Core.Tests`, `Heimdall.Ssh.Tests`, `Heimdall.Rdp.Tests`, `Heimdall.Sftp.Tests`, `Heimdall.Terminal.Tests`, `Heimdall.App.Tests`, `Heimdall.App.UiTests`, `TwinShell.Infrastructure.Tests`.

Voir [docs/fr/ARCHITECTURE.md](ARCHITECTURE.md) pour les décisions de conception détaillées et les diagrammes de flux de données.

---

