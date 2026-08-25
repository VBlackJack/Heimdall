<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->
# Heimdall - FAQ des réglages

*Also available in English: [../SETTINGS-FAQ.md](../SETTINGS-FAQ.md).*

Settings est un grand écran, et certaines de ses options sont opaques tant qu'on ne sait pas déjà
à quoi elles renvoient. Cette page traite celles-là : les ambiguës, celles qui portent un vrai
compromis, celle dont le libellé induit en erreur, et celles dont la formulation suppose un savoir
que l'interface ne donne jamais. Les options qui font ce que leur nom annonce, comme Thème ou
Taille de police, ne sont pas répétées ici.

Quand une réponse dit qu'un réglage ne fait pas quelque chose, c'est une affirmation mesurée ou
vérifiée dans le code, pas une supposition.

## Verrouiller Heimdall lui-même

Trois contrôles, dans le même écran, qui ne protègent pas la même chose.

**PIN d'application** est un verrou d'écran. Heimdall enregistre une empreinte de votre PIN et
compare ce que vous tapez au démarrage. Cela empêche quelqu'un qui s'assoit devant votre machine
déverrouillée de parcourir votre liste de serveurs. **Cela ne chiffre rien.** Vos mots de passe
enregistrés sont protégés par DPAPI que vous posiez un PIN ou non, et qui détient votre fichier
de configuration peut en retirer le PIN.

**Mot de passe maître** est du chiffrement. Ce que vous tapez passe par Argon2id pour dériver
une clé, et cette clé chiffre le trousseau. Sans lui, les secrets enregistrés sont illisibles, y
compris pour un programme tournant sous votre propre compte Windows. C'est celui-là qu'il faut
poser pour protéger vos identifiants au repos.

**Déverrouillage par Windows Hello** ne remplace ni l'un ni l'autre. Il se pose par-dessus le
mot de passe maître, pour déverrouiller d'une empreinte au lieu de le saisir.

**Lequel vous faut-il ?** Si la crainte est un collègue devant votre poste laissé sans
surveillance, le PIN suffit. Si elle porte sur le fichier d'identifiants lui-même, seul le mot
de passe maître y répond. Les deux se cumulent, et poser un PIN en pensant obtenir le second
protège beaucoup moins qu'il n'y paraît.

**Générer un fichier de récupération** écrit un fichier `.heimdall-recovery` capable de
réinitialiser un PIN oublié. Une réserve que la boîte de dialogue n'énonce pas : ce fichier est
chiffré pour votre compte Windows sur cette machine, donc inutilisable depuis une autre machine
ou un autre compte. Il sauve un PIN oublié, pas un ordinateur perdu.

## Sécurité

**Network Level Authentication (NLA)** - la machine distante vous authentifie *avant* d'ouvrir
une session de bureau. Laissez-la activée. Ne la désactivez que pour des cibles qui ne savent
pas faire, comme la plupart des serveurs `xrdp` Linux, qui n'implémentent pas CredSSP du tout.
Sans NLA vous arrivez sur l'écran de connexion distant au lieu d'être connecté directement.

**Authentification stricte du serveur** - refuse de se connecter si l'identité du serveur ne
peut pas être vérifiée. Désactivée par défaut, parce que beaucoup de serveurs RDP internes
utilisent des certificats auto-signés, invérifiables par construction. L'activer est plus sûr
et cassera les connexions vers ces serveurs.

**Exiger Credential Guard** - refuse d'ouvrir une session RDP *embarquée* si Windows Credential
Guard ne tourne pas sur **votre** machine. Cela protège les identifiants que votre poste délègue
au serveur distant. Le contrôle porte sur votre machine locale, pas sur la cible. Les sessions
RDP externes en sont exemptées.

**Exiger Windows Hello avant de se connecter** - demande votre facteur Windows Hello avant
qu'une connexion démarre. **Revérifier après** fixe la durée de validité d'un contrôle réussi,
pour ne pas être sollicité à chaque onglet.

## Identifiants depuis un coffre externe

**Utiliser un fournisseur d'identifiants externe** - permet à Heimdall d'obtenir un mot de passe
en exécutant une commande, typiquement le CLI d'un gestionnaire comme KeePassXC, Bitwarden ou
1Password.

**Commande de nom d'utilisateur (facultatif)** - celle-ci passe facilement inaperçue et elle
répond à une plainte fréquente. Sans elle, seul le *mot de passe* vient du coffre et le nom
d'utilisateur reste celui du profil. Renseignez-la et le nom est récupéré aussi, par une
seconde commande.

**Secret de déverrouillage** - transmis sur l'entrée standard de la commande, pour les coffres
qui doivent d'abord être déverrouillés. Bitwarden et 1Password exigent en plus une session
établie hors de Heimdall (`BW_SESSION`, `op signin`) ; Heimdall ne l'établit pas pour vous.

**N'utiliser que la première ligne de la sortie** - certains CLI impriment le secret suivi
d'autres champs. Laissez actif, sauf si votre mot de passe contient légitimement un saut de
ligne.

L'entrée du coffre est cherchée par le **Nom d'entrée du coffre** du profil si vous en
renseignez un, et par le nom affiché du profil sinon. Renseignez-le quand l'entrée de votre
coffre ne porte pas exactement le même nom que celle de Heimdall.

## RDP et mémoire

**Rendu accéléré par le matériel** - **désactivé par défaut depuis la v2026.082401, et c'est le
réglage qui compte le plus.** Quand il est actif, le contrôle RDP construit un périphérique
Direct3D et son contexte de décodage pour chaque session ouverte. Trois sessions simultanées en
1920x1080 ont mesuré 1146 Mo avec, 763 Mo sans : un tiers de l'empreinte et 840 handles Windows
de moins. La contrepartie est que le décodage passe sur le processeur : aucune différence n'a été
mesurable sur des bureaux immobiles ni sur du texte qui défile, et une session affichant de la
vidéo n'a pas été mesurée. Réactivez-le, globalement ou pour un seul serveur, si une session
paraît moins fluide.

**Conserver le cache bitmap sur disque - à lire.** Cette case s'appelait "Cache bitmap", un nom
qui induisait en erreur, elle a donc été renommée. Elle décide si le cache bitmap est écrit
**sur disque** entre deux sessions, pour qu'une reconnexion le réutilise au lieu de tout
redessiner. **Elle ne pilote pas le cache en mémoire.** La décocher ne libère aucune mémoire et
vous coûte le cache disque. Laissez-la active.

**Profondeur de couleur** - 32 bits par défaut. La baisser à 16 bits n'a économisé aucune
mémoire mesurable. Baissez-la si vous manquez de bande passante, pas si vous manquez de
mémoire.

**Mode de résolution, Largeur, Hauteur** - les autres réglages dont l'effet sur la mémoire a été
mesuré. Une session plus petite coûte environ 86 Mo de moins qu'en 1920x1080. `Auto` suit la
fenêtre de Heimdall, `Fixed` fige la taille choisie.

Les mesures complètes sont dans [Mémoire RDP et réglage des sessions](RDP-PERFORMANCE.md).

**Sessions embarquées maximum** - plafond de sessions embarquées simultanées. Augmentez-le si
vous en gardez beaucoup et que vous avez la mémoire ; chacune coûte environ 194 Mo.

**Résolution dynamique** - laisse la session se redimensionner avec la fenêtre au lieu de se
reconnecter. Laissez actif, sauf si un serveur se comporte mal quand la résolution change en
cours de session.

**Multi-écran** - étale la session sur vos écrans. Cela multiplie la géométrie de session, donc
le coût mémoire.

## Les délais, et pourquoi il y en a tant

Les délais avancés existent parce que des pannes différentes demandent des patiences
différentes. Vous n'avez presque jamais besoin d'y toucher.

**Délai de surveillance de connexion RDP** - combien de temps attendre avant de déclarer la
connexion en échec. Augmentez-le pour des serveurs lents ou lointains.

**Délai de stabilisation de la résolution après connexion** - une pause avant d'autoriser le
redimensionnement, pour qu'une session encore en train de négocier sa géométrie ne soit pas
redimensionnée aussitôt.

**Délai de surveillance de l'autofill d'identifiants** - combien de temps Heimdall guette
l'invite d'identifiants d'une session mstsc *externe* pour la remplir. Sans effet sur les
sessions embarquées.

**Délai de nettoyage du fichier .rdp et des identifiants** - le mode externe écrit un fichier
`.rdp` temporaire et un identifiant ; c'est le délai avant suppression, pour laisser à
`mstsc.exe` le temps de les lire.

**Intervalle de maintien de session** et **Intervalle anti-inactivité** sont deux choses
différentes. Le maintien est du trafic protocolaire qui empêche le *serveur* de couper une
session inactive. L'anti-inactivité simule une activité pour que le *bureau* distant ne se
verrouille pas.

## Sondes en arrière-plan

**Activer les sondes de joignabilité en arrière-plan** - Heimdall ouvre périodiquement une
connexion TCP vers chaque serveur configuré pour colorer la pastille dans l'arbre des sessions.
C'est pourquoi les journaux d'un serveur montrent une connexion courte par intervalle depuis
votre poste, même sans session ouverte. Ce n'est pas une tentative de connexion et cela ne
s'authentifie pas.

**Intervalle de contrôle**, **Délai de sonde** et **Sondes simultanées maximum** règlent la
fréquence, la patience et le parallélisme. Désactivez l'ensemble si vous gérez beaucoup de
serveurs et que le bruit dans leurs journaux compte plus que les pastilles d'état.

## Modes

**Mode RDP par défaut** - `Embedded` rend la session dans un onglet Heimdall. `External` lance
`mstsc.exe` dans sa propre fenêtre, identifiants remplis pour vous. Le mode externe consomme
plus de mémoire par session mais isole chaque session dans son processus.

**Mode SSH par défaut** - `Embedded` utilise le terminal intégré. `External` utilise PuTTY, ce
qui exige de renseigner **Chemin de PuTTY**.

## Journalisation

**Activer la journalisation** écrit le journal de l'application. **Activer la journalisation de
session** enregistre en plus le contenu des sessions terminal dans **Répertoire des journaux de
session**. La seconde enregistre ce que vous tapez et ce qui revient : réfléchissez à
l'emplacement de ce répertoire.

## Migration depuis l'ancienne version

**Ce que "legacy" désigne ici** - Heimdall a remplacé un outil PowerShell antérieur nommé
**RDPManager**. Cette section ne concerne que ceux qui l'ont utilisé. Si ce nom ne vous dit
rien, rien ici ne vous concerne.

Au premier démarrage, Heimdall cherche dans les dossiers voisins un répertoire `RDPManager`
contenant un `config/servers.json`, et propose de l'importer. Si vous refusez, il prend une
empreinte de ces données pour cesser de vous le demander.

**Proposer la migration au prochain démarrage** annule ce refus. Deux conditions doivent encore
être réunies au démarrage suivant, et c'est la partie qui surprend : l'ancien dossier doit
toujours être là, **et votre liste de serveurs actuelle doit être vide**. Heimdall ne propose
jamais de fondre un import dans un inventaire que vous avez déjà constitué. Cliquez avec des
serveurs déjà configurés et il ne se passera rien au démarrage suivant, sans message pour
l'expliquer.

## Partage de fichiers

**Activer le partage TFTP** démarre un petit serveur TFTP, pour pousser des firmwares et des
configurations vers du matériel réseau qui ne parle rien d'autre. TFTP n'a aucune
authentification ni aucun chiffrement : qui atteint le port peut lire et écrire dans le dossier
partagé. Activez-le sur un réseau de confiance, le temps du transfert, puis coupez-le.

## Passerelles SSH, PuTTY et Plink

**Passerelles SSH** sont des rebonds. Vous déclarez une machine joignable, et Heimdall fait
transiter par elle les sessions vers des machines qui ne le sont pas directement. C'est le
réglage à chercher quand un serveur n'est accessible que depuis l'intérieur d'un réseau que vous
atteignez en SSH.

**Chemin de plink.exe** n'est nécessaire que pour les chemins passant par PuTTY : clés Pageant,
serveurs en keyboard-interactive, et le repli sur Plink. Des fichiers de clés seuls n'en ont pas
besoin. **Chemin de PuTTY** n'est nécessaire que si le mode SSH est réglé sur External ; laissé
vide, il est cherché à côté de plink.exe.

## Détection des outils tiers

**Répertoires Sysinternals, NirSoft et NanaRun** - Heimdall n'embarque pas ces suites. Indiquez
un dossier où vous en avez déjà installé une, et les outils qui s'y trouvent apparaissent dans
la boîte à outils. Laissez vide et Heimdall propose simplement ses outils intégrés.

## Projets

Une étiquette pour regrouper les sessions par client, site ou environnement, et filtrer l'arbre
dessus. Purement organisationnel : cela ne change rien à la façon dont une connexion est faite.

## Éditeur externe

L'éditeur ouvert quand vous modifiez un fichier distant en SFTP. Laissé vide, Windows ouvre le
fichier avec ce qu'il associe à cette extension.

## Voir aussi

- [Mémoire RDP et réglage des sessions](RDP-PERFORMANCE.md)
- [Guide utilisateur](USER-GUIDE.md)
- [Dépannage](TROUBLESHOOTING.md)
