<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->
# Heimdall - FAQ des réglages

*Also available in English: [../SETTINGS-FAQ.md](../SETTINGS-FAQ.md).*

Settings compte 65 options. Cette page traite celles qui posent réellement question : les
ambiguës, celles qui portent un vrai compromis, et celle dont le libellé induit en erreur. Les
options qui font ce que leur nom annonce, comme Thème ou Taille de police, ne sont pas répétées
ici.

Quand une réponse dit qu'un réglage ne fait pas quelque chose, c'est une affirmation mesurée ou
vérifiée dans le code, pas une supposition.

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

**Cache bitmap - à lire.** Le libellé induit en erreur et nous en sommes conscients. La case
pilote la propriété `BitmapPersistence` du contrôle RDP, qui décide si le cache bitmap est
écrit **sur disque** entre deux sessions. **Elle ne pilote pas le cache en mémoire**, et aucun
réglage de Heimdall ne le fait. La décocher ne libère aucune RAM et vous coûte le cache disque
qui aurait épargné des redessins à la reconnexion. Laissez-la active.

**Profondeur de couleur** - 32 bits par défaut. La baisser à 16 bits n'a économisé aucune
mémoire mesurable. Baissez-la si vous manquez de bande passante, pas si vous manquez de
mémoire.

**Mode de résolution, Largeur, Hauteur** - les seuls réglages dont l'effet sur la mémoire a été
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

## Voir aussi

- [Mémoire RDP et réglage des sessions](RDP-PERFORMANCE.md)
- [Guide utilisateur](USER-GUIDE.md)
- [Dépannage](TROUBLESHOOTING.md)
