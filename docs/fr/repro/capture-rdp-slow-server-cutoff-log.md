## 🟦 Capturer un journal de diagnostic pour la coupure RDP sur serveur lent

*Ce document est la version française de [../../repro/capture-rdp-slow-server-cutoff-log.md](../../repro/capture-rdp-slow-server-cutoff-log.md). / This document is the French version.*

> Certains serveurs RDP dont la session Windows met plus de temps que d'habitude
> à se charger voient leur connexion coupée par Heimdall **après** qu'il a déjà
> annoncé "Connecté". Aucun journal d'une reproduction lente n'existe encore,
> donc la cause reste non confirmée : les trois candidats sont un
> redimensionnement forcé environ 10 s après la connexion, une reconnexion
> automatique épuisée, ou un nettoyage prématuré des identifiants. Une seule
> capture propre de `FileLogger` couvrant **toute** la séquence, avec **l'heure
> exacte de la coupure** notée à la main, suffit à les départager. Cette
> procédure fait en sorte qu'une seule capture suffise.
>
> ⚠️ La formulation exacte de l'option de journalisation dans les paramètres
> peut varier légèrement selon la version : les étapes ci-dessous décrivent ce
> qu'il faut chercher.

### 📋 Ce qu'il faut avoir avant de commencer

| Élément | Détail |
|---|---|
| 🖥️ La machine concernée | Le PC où la coupure RDP sur serveur lent se reproduit réellement |
| 🧩 Heimdall installé | Une version fonctionnelle qui présente le défaut |
| 📛 Le profil du serveur lent | Le profil RDP exact qui déclenche la coupure prématurée |
| ⏱️ Une horloge visible | Téléphone ou horloge de la barre des tâches, pour noter les heures à la seconde près |
| 📝 De quoi prendre des notes | Un fichier texte ou du papier pour les notes de contexte (étape 4) |

### ✅ ÉTAPE 1 - Activer la journalisation dans un fichier

> Heimdall écrit un fichier de journal par jour, mais seulement si la
> journalisation est activée dans les paramètres. Tout le diagnostic repose sur
> ce fichier : on le verrouille donc sur ACTIVÉ avant toute chose.

| # | Action |
|---|---|
| ☐ 1 | Ouvrir **Heimdall** |
| ☐ 2 | Aller dans **Paramètres** |
| ☐ 3 | Trouver l'option de journalisation (libellée à peu près **Activer la journalisation** / **Journalisation de diagnostic**) → s'assurer qu'elle est **ACTIVÉE** |
| ☐ 4 | Enregistrer ou fermer les paramètres pour que le changement s'applique |

### ✅ ÉTAPE 2 - Partir d'un journal propre

> Tous les événements du jour atterrissent dans un seul fichier. Démarrer
> Heimdall de zéro, la journalisation déjà active, garantit que le journal
> commence **avant** la connexion, et rend la reproduction facile à retrouver
> dans le fichier.

| # | Action |
|---|---|
| ☐ 1 | Fermer **Heimdall** complètement |
| ☐ 2 | Ouvrir le dossier des journaux : c'est le sous-dossier **`logs`** à côté de l'exécutable Heimdall (clic droit sur le raccourci de l'application → **Ouvrir l'emplacement du fichier** en cas de doute) |
| ☐ 3 | *(Facultatif)* Mettre de côté le fichier **`heimdall_<date>.log`** du jour s'il existe (un nouveau est créé au prochain lancement) |
| ☐ 4 | Relancer **Heimdall** |
| ☐ 5 | Vérifier qu'un fichier nommé **`heimdall_<date du jour>.log`** existe et se termine par une ligne **`Heimdall starting`** récente |

### ✅ ÉTAPE 3 - Reproduire la coupure et noter les heures exactes

> Il nous faut la séquence complète dans le journal : la connexion en cours, la
> connexion établie, puis la coupure prématurée. C'est l'heure de coupure notée
> à la main qui permet d'aligner le journal sur la panne, donc soyez précis.

| # | Action |
|---|---|
| ☐ 1 | ⚠️ Ne se connecter à aucun autre serveur d'abord : garder cette exécution centrée sur la seule tentative |
| ☐ 2 | Noter l'heure courante (**HH:MM:SS**) : c'est l'heure de "début de connexion" |
| ☐ 3 | Ouvrir le profil du serveur RDP lent et lancer la connexion |
| ☐ 4 | Noter le mode de connexion utilisé : **Embarqué** (session dans un onglet Heimdall) ou **Externe** (fenêtre mstsc séparée) |
| ☐ 5 | ⚠️ Ne pas toucher, redimensionner, cliquer ni déplacer la session : se contenter de regarder |
| ☐ 6 | Attendre que la coupure prématurée se produise d'elle-même |
| ☐ 7 | À l'instant où la session est coupée, noter **l'heure exacte de la coupure (HH:MM:SS)** |
| ☐ 8 | Recopier, mot pour mot, le message affiché par Heimdall au moment de la déconnexion |

### ✅ ÉTAPE 4 - Récupérer le journal et la note de contexte

> Le journal brut ne suffit pas : quelques faits qu'il ne peut pas enregistrer
> transforment un long diagnostic en un diagnostic court. Recueillez-les tant que
> la reproduction est fraîche.

| # | Action |
|---|---|
| ☐ 1 | Attendre environ **30 secondes** après la coupure (laisser toute reprise ou fermeture finir d'écrire dans le fichier) |
| ☐ 2 | Fermer **Heimdall** normalement |
| ☐ 3 | Dans le dossier **`logs`**, prendre **`heimdall_<date>.log`** et le copier sous un nom clair, par exemple **`heimdall_rdp-slow-cutoff_<date>.log`** |
| ☐ 4 | Écrire une courte note à côté, avec : l'hôte du serveur, le mode **Embarqué/Externe**, l'heure de début de connexion, **l'heure de coupure**, le message de déconnexion affiché, et combien de temps la session est restée visiblement active avant la coupure |
| ☐ 5 | Rapporter à la fois le fichier de journal renommé et la note (ce PC n'est pas joignable depuis la session d'assistance) |

### ✅ ÉTAPE 5 - *(Facultatif)* Capturer une seconde exécution

> Si la coupure est intermittente, un seul journal peut manquer le déclencheur.
> Une seconde capture propre lève le doute.

| # | Action |
|---|---|
| ☐ 1 | Répéter les **étapes 2 → 4** une fois de plus si vous en avez le temps |
| ☐ 2 | Garder les deux journaux sous des noms distincts pour ne pas les confondre |

### 🆘 Problèmes courants

| Symptôme | Correctif rapide |
|---|---|
| Aucun fichier `heimdall_<date>.log` n'apparaît | La journalisation est encore désactivée : revérifier l'option des paramètres à l'étape 1, puis relancer |
| Le fichier de journal existe mais s'arrête avant la coupure | L'application a été fermée trop vite : attendre environ 30 s après la coupure avant de fermer |
| Impossible de trouver le dossier `logs` | Il se trouve à côté de l'exécutable Heimdall : clic droit sur le raccourci → **Ouvrir l'emplacement du fichier** |
| Le serveur s'est connecté sans problème, aucune coupure | Le défaut est intermittent : réessayer, et ne garder que les journaux où la coupure a réellement eu lieu |
| Incertain sur Embarqué ou Externe | Externe ouvre une fenêtre **mstsc** séparée ; Embarqué affiche la session dans un onglet Heimdall |
