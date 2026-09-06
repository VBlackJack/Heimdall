<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->
# Heimdall - Guide utilisateur

*Also available in English: [../USER-GUIDE.md](../USER-GUIDE.md).*

Ce guide s'adresse aux personnes qui utilisent Heimdall, pas à celles qui le développent. Il
répond aux questions de la première heure, et à celles qui surgissent quand quelque chose ne va
pas. Pour la liste complète des fonctionnalités, voir la [référence](FEATURES.md) ; pour les
sujets de développement, [DEVELOPMENT.md](DEVELOPMENT.md).

Heimdall vous connecte à des machines distantes : bureaux Windows en RDP, Linux et équipements
réseau en SSH, transferts de fichiers en SFTP et FTP, plus VNC, Telnet, Citrix, WinRM et un shell
local. Tout s'ouvre dans un onglet, au sein d'une seule fenêtre.

---

## Sommaire

1. [Installation](#installation)
2. [Votre première connexion](#votre-première-connexion)
3. [Où vivent vos mots de passe](#où-vivent-vos-mots-de-passe)
4. [Transférer des fichiers](#transférer-des-fichiers)
5. [Quand une connexion échoue](#quand-une-connexion-échoue)
6. [Envoyer un journal quand vous avez besoin d'aide](#envoyer-un-journal-quand-vous-avez-besoin-daide)
7. [Mettre à jour](#mettre-à-jour)
8. [Les raccourcis qui valent la peine](#les-raccourcis-qui-valent-la-peine)

---

## Installation

Téléchargez la dernière version depuis la page [Releases](https://github.com/VBlackJack/Heimdall/releases). Il existe deux
éditions, et toutes deux contiennent déjà tout ce dont elles ont besoin côté .NET. Vous n'installez
rien au préalable.

| Édition | À prendre quand |
|---|---|
| **Standard** | Machine Windows 10 ou 11 ordinaire. Téléchargement plus léger. |
| **Self-Contained** | La machine n'a pas Microsoft Edge, ou pas d'accès à Internet du tout. Plus lourde, n'a besoin de rien. |

**Dans le doute, prenez Standard.** Elle s'appuie sur Microsoft Edge, présent sur la quasi-totalité
des machines Windows 10 et 11.

Chaque édition existe en **installeur** (crée des raccourcis, gère les mises à jour, se
désinstalle) ou en **zip** (décompressez-le n'importe où et lancez `Heimdall.exe`, rien n'est
installé).

> Si les terminaux, l'écran VNC ou l'éditeur de notes s'affichent vides avec un message parlant de
> WebView2, c'est que la machine n'a pas Edge. Installez Microsoft Edge et redémarrez Heimdall, ou
> réinstallez avec l'édition Self-Contained.

---

## Votre première connexion

Le panneau de gauche est votre liste de sessions. Il commence vide.

1. Appuyez sur **Ctrl+N**, ou utilisez le bouton au-dessus de la liste, pour ajouter une session.
2. **Choisissez d'abord le protocole.** RDP pour un bureau Windows, SSH pour un shell Linux ou
   réseau, SFTP pour parcourir des fichiers, et ainsi de suite. Les champs de l'étape suivante
   s'adaptent.
3. Renseignez le nom que vous voulez voir dans la liste, l'adresse de la machine, et vos
   identifiants.
4. Enregistrez. La session apparaît dans le panneau de gauche.
5. Double-cliquez dessus pour vous connecter.

Quelques points utiles à ce stade :

- **Le nom d'utilisateur SSH n'est pas facultatif.** Heimdall ne peut pas ouvrir de session sans
  lui, et vous le dira plutôt que de tenter la connexion.
- **Le port est déjà correct la plupart du temps.** N'y touchez pas sauf indication contraire.
- **Les réglages avancés sont masqués par défaut**, derrière un interrupteur dans la boîte de
  dialogue. Vous n'en avez pas besoin pour une connexion ordinaire.

### La première connexion SSH

Il vous sera demandé de confirmer la *clé d'hôte* du serveur, une empreinte qui identifie la
machine. C'est normal lors d'une première connexion. Acceptez-la si vous vous connectez à une
machine que vous vous attendez à joindre ; Heimdall s'en souvient ensuite.

Si cette même demande réapparaît plus tard pour une machine déjà acceptée, **arrêtez-vous et
demandez conseil.** Cela peut signifier que la machine a été réinstallée, ou que quelque chose se
fait passer pour elle. Sur cette demande, le bouton mis en avant est **Refuser** : appuyer sur
Entrée refuse la connexion. Accepter la nouvelle clé, ou ne lui faire confiance que pour cette
session, demande un clic délibéré.

### Connexion rapide

**Ctrl+K** ouvre un champ de recherche où vous pouvez taper le nom d'une session existante, ou
directement une adresse comme `admin@192.168.1.10`. C'est le moyen le plus rapide d'atteindre ce
que vous utilisez souvent.

---

## Où vivent vos mots de passe

Les mots de passe enregistrés dans une session sont chiffrés sur votre propre machine, liés à votre
compte Windows. Un autre utilisateur Windows du même ordinateur ne peut pas les lire. Ils se
trouvent sous :

```
%LOCALAPPDATA%\Heimdall
```

Vous pouvez coller ce chemin dans la barre d'adresse de l'Explorateur de fichiers.

### Le mot de passe maître, et un avertissement

Les réglages proposent un **mot de passe maître** qui chiffre vos identifiants stockés derrière un
mot de passe saisi au démarrage. Il apporte une vraie protection, et une conséquence qu'il faut
lire avant de l'activer :

> **Le mot de passe maître ne peut être ni récupéré ni réinitialisé.** Si vous l'oubliez, Heimdall
> ne s'ouvrira plus et les identifiants stockés sont perdus. Il n'existe ni lien de
> réinitialisation ni porte dérobée, et c'est voulu.

Si vous l'activez, traitez-le comme la clé d'un coffre : notez-le à un endroit sûr, ou rangez-le
dans un gestionnaire de mots de passe.

Heimdall peut aussi utiliser **Windows Hello** (empreinte, visage ou code PIN) comme barrière avant
l'usage des identifiants stockés, et lire les mots de passe depuis un gestionnaire externe plutôt
que de les stocker lui-même. Les deux se trouvent dans les réglages, rubrique Sécurité.

---

## Transférer des fichiers

Ouvrez une session **SFTP** (ou FTP) pour obtenir un navigateur de fichiers à deux panneaux : votre
machine d'un côté, la machine distante de l'autre.

- **Glissez-déposez** entre les panneaux pour copier, dans les deux sens, dossiers entiers compris.
- **Double-cliquez sur un fichier texte distant** pour l'éditer. Heimdall le télécharge, l'ouvre,
  et le renvoie à chaque enregistrement. Fermez l'éditeur quand vous avez fini.
- **F2** renomme, **F5** rafraîchit la liste.

> La suppression dans l'explorateur de fichiers local est **définitive**. Elle n'utilise pas la
> Corbeille, et un dossier part avec tout ce qu'il contient. La confirmation le dit ; lisez-la
> avant de cliquer sur oui.

---

## Quand une connexion échoue

Heimdall affiche la raison en clair partout où il le peut. Les cas courants :

| Ce que vous voyez | Ce que cela veut dire en général |
|---|---|
| Le mot de passe est refusé | Mauvais mot de passe, ou compte verrouillé sur la machine distante. |
| La connexion expire | La machine est éteinte, ou un pare-feu bloque. Vérifiez l'adresse. |
| La clé d'hôte a changé | Voir l'avertissement plus haut. N'acceptez pas sans demander. |
| Le serveur pose une question à laquelle ce client ne peut pas répondre | Le serveur veut un code de vérification ou un autre second facteur. Heimdall ne répond qu'aux demandes de mot de passe ; utilisez un autre client pour ce serveur. |
| Un message parlant de WebView2 | La machine n'a pas Microsoft Edge. Voir [Installation](#installation). |
| "Passerelle SSH introuvable" | La session pointe vers une passerelle qui n'existe plus. Modifiez la session pour en choisir une, ou recréez-la dans les réglages. |

Une session RDP qui se déconnecte toute seule tentera de se reconnecter d'elle-même, en vous
montrant ce qu'elle fait. Vous pouvez annuler depuis la barre d'outils.

Si la raison affichée ne suffit pas, le journal en dira davantage.

---

## Envoyer un journal quand vous avez besoin d'aide

Heimdall tient un journal de diagnostic. Quand vous signalez un problème, ce journal est la chose
la plus utile que vous puissiez joindre.

**Pour le trouver :**

1. Allez dans les **Réglages** (l'engrenage), puis l'onglet **Avancé**, puis **Outils et
   intégrations**.
2. Descendez jusqu'à la rubrique **À propos**. Elle affiche à l'écran le chemin complet du dossier
   des journaux, en face de **Logs**, avec un bouton **Ouvrir le dossier des journaux** à côté.
3. Le fichier porte la date du jour, par exemple `heimdall_20260821.log`.

Si le bouton ne fait rien, copiez le chemin affiché à côté et collez-le dans la barre d'adresse de
l'Explorateur de fichiers.

**Avant de l'envoyer**, ouvrez-le et parcourez-le. Il consigne les machines auxquelles vous vous
êtes connecté et les erreurs rencontrées. Il ne contient pas vos mots de passe, Heimdall ne les
écrit jamais dans le journal, mais les noms d'hôtes et d'utilisateurs sont le genre de chose que
vous ne souhaitez peut-être pas publier.

---

## Mettre à jour

Heimdall vérifie les mises à jour de lui-même et vous prévient quand il y en a une.

Pour vérifier vous-même : **Réglages** -> **Général** -> **Vérifier maintenant**.

Si une mise à jour existe, Heimdall peut généralement la télécharger et l'installer pour vous.
Certaines versions ne savent pas s'installer elles-mêmes ; dans ce cas, il vous le dit et vous
renvoie vers la page de publication pour le faire à la main.

Si vous avez installé depuis le zip plutôt qu'avec l'installeur, remplacez le contenu du dossier
par la nouvelle version. Vos sessions et vos réglages vivent en dehors du dossier du programme et
ne sont pas touchés. Heimdall reconnaît une telle copie et ne propose pas de l'écraser ; il affiche
la page de publication à la place.

Si vous avez choisi **Ignorer cette version** sur la bannière et changez d'avis, la version ignorée
est listée dans **Réglages** -> **Mises à jour** avec un bouton qui la propose à nouveau.

Installer une mise à jour enregistre vos réglages non sauvegardés, l'état de dépliage de l'arbre et
la position de la fenêtre avant la sortie de Heimdall, exactement comme la fermeture de la fenêtre.
Si une mise à jour ne s'est pas appliquée, la bannière le dit au démarrage suivant et le journal du
relanceur se trouve dans le dossier de journaux indiqué dans le panneau À propos.

---

## Les raccourcis qui valent la peine

Appuyez sur **F1** à tout moment pour la liste complète. Ceux qui se rentabilisent tout de suite :

| Raccourci | Ce qu'il fait |
|---|---|
| `Ctrl+K` | Connexion rapide : chercher une session, ou taper une adresse |
| `Ctrl+N` | Ajouter une session |
| `Ctrl+E` | Modifier la session sélectionnée |
| `Ctrl+B` | Afficher ou masquer le panneau de gauche |
| `Ctrl+F` | Aller au champ de recherche |
| `F11` | Plein écran, `Echap` pour en sortir |
| `Ctrl+Shift+T` | Basculer le panneau gauche entre Sessions et Outils |
| `Ctrl+A` | Dans l'arborescence, sélectionner toutes les sessions des dossiers ouverts |
| `Alt+Haut` / `Alt+Bas` | Déplacer la session en surbrillance dans son dossier |
| `F2` | Renommer la session ou le dossier sélectionné |

---

## Toujours bloqué ?

- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) détaille des pannes précises. Il est rédigé pour un
  lecteur technique, alors cherchez-y le message exact que vous avez vu.
- La page du projet est accessible depuis la même rubrique À propos que le dossier des journaux.
