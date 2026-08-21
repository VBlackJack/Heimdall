*Ce document est la version française de [../SECURITY.md](../SECURITY.md). / This document is the French version.*

# Notes de sécurité

Ce document consigne les considérations de sécurité connues, les limitations et
les décisions délibérées de défense en profondeur de Heimdall.

## Signaler une vulnérabilité

Signalez toute vulnérabilité suspectée en privé au mainteneur. Ce dépôt ne
publie pas pour l'instant d'adresse de messagerie dédiée à la sécurité ;
utilisez le canal privé par lequel vous avez obtenu les sources, ou consultez
`LICENSE` pour le contexte de maintenance et de licence. N'ouvrez pas de ticket
public pour un problème de sécurité.

## Périmètre du modèle de menace

Heimdall est une application de bureau mono-utilisateur qui stocke localement
les identifiants SSH et RDP au moyen de DPAPI et de HMAC-SHA256, et qui gère
des connexions sortantes. Elle suppose que :

- Le compte utilisateur Windows local est de confiance. Un code malveillant
  s'exécutant sous le même utilisateur peut observer la mémoire de
  l'application.
- Le disque local est de confiance. Les secrets chiffrés par DPAPI sont liés au
  profil utilisateur.
- Le réseau n'est de confiance à aucun bout. L'épinglage TOFU des clés d'hôte
  est la défense principale contre les attaques de type MITM.

Hors périmètre : les installations partagées multi-utilisateurs, la chaîne de
démarrage sécurisé et les attaques sur la chaîne d'approvisionnement visant
SSH.NET ou WebView2. Suivez l'exposition des dépendances avec
`dotnet list package --vulnerable`.

## Limitations connues

### Durée de vie des identifiants en mémoire managée

`System.String` est immuable et réside sur le tas du GC. Les identifiants en
clair transmis à :

- `IMsTscNonScriptable.put_ClearTextPassword` pour le RDP,
- `PasswordAuthenticationMethod` et `KeyboardInteractiveAuthenticationMethod`
  pour SSH.NET,
- `CredentialAutofill.InjectPassword` via `WM_SETTEXT`,

sont brièvement détenus sous forme d'instances `string` avant d'être passés au
code natif. Les tampons `char[]` propriétaires sont remis à zéro quand c'est
possible et les références de champ sont annulées après le transfert, mais le
GC peut conserver des copies jusqu'à la prochaine collecte Gen2. `SecureString`
n'apporte pas de garantie plus forte sur les Windows modernes.

Mesure d'atténuation : verrouillez le poste lorsqu'il n'est pas utilisé. Un
attaquant disposant de primitives de lecture mémoire locales peut extraire les
identifiants des clients SSH et RDP de bureau, celui-ci compris.

### DACL de la mémoire partagée Pageant

`PageantClient.SendMessage` crée un file mapping nommé avec **deux couches de
durcissement** contre l'espionnage userland au sein de la même session :

1. **DACL self-only** via `SecurityAttributesScope.CreateSelfOnly` - le handle
   de mapping est créé avec un `SECURITY_ATTRIBUTES` explicite dont le SDDL est
   `D:P(A;;FA;;;<currentUserSid>)`, refusant l'accès y compris aux autres
   processus exécutés sous le **même** utilisateur Windows.
2. **Nom de mapping cryptographiquement aléatoire** -
   `RandomNumberGenerator.GetHexString(16)` fournit 64 bits d'entropie dans le
   nom du mapping, ce qui met en échec l'énumération opportuniste par un
   processus malveillant connaissant le PID de Heimdall.

La poignée de main IPC vérifie en outre que la fenêtre Pageant appartient à un
processus dont le nom figure dans la liste blanche de confiance
(`pageant`, `putty`, `plink`, `pscp`, `psftp`, `kitty`, `winscp`,
`keepassxc-proxy`, `keepassxc`) avant d'envoyer le moindre trafic d'agent, ce
qui limite l'usurpation de classe de fenêtre.

### Frontières de journalisation des identifiants

Les chemins de code RDP, SSH, SFTP et de gestion d'identifiants ne journalisent
jamais les noms d'utilisateur, les domaines, la présence d'un mot de passe, la
longueur d'un mot de passe, les mots de passe, les phrases secrètes ni le
contenu des champs de saisie d'identifiants. Les lignes de journal de connexion
peuvent identifier uniquement l'hôte cible et le protocole.

`CredentialAutofill.cs` est l'exemple RDP canonique. Depuis `1d7c78c`, les
diagnostics d'énumération des brokers sont émis sous la forme d'une entrée
Debug par tentative d'autofill, avec un résultat final au niveau Info et une
journalisation Warning uniquement lorsque l'énumération elle-même lève une
exception. Ces diagnostics peuvent inclure des titres de fenêtres du système,
des handles, des PID, des noms de processus et des motifs de rejet, mais jamais
le contenu des champs de saisie. Les titres de fenêtres peuvent contenir des
données identifiant un hôte ; par exemple, `Enter credentials for
server01.corp.local` est fourni par le système ou le client distant et se situe
hors de la politique de cette couche relative aux champs d'identifiants.

### Course à l'allocation de port dans TunnelManager

`TunnelManager.GetEphemeralPort` et `TunnelManager.AllocatePort` réservent un
port éphémère du système, lisent son numéro, le libèrent, puis le retournent.
Entre la libération et le bind réel du tunnel, un autre processus peut
revendiquer le même port. Trois mesures d'atténuation sont en place :

1. Une double vérification dans `OpenTunnelAsync` et `OpenChainedTunnelAsync`
   revalide `IsPortTracked(localPort)` sous `_registryLock` et libère la
   session en cas de collision.
2. `StartForwardedPortWithRetry` encadre les appels réels à
   `ForwardedPortLocal.Start()` et `ForwardedPortDynamic.Start()` par une
   nouvelle tentative bornée (3 essais, 50 ms d'espacement) uniquement sur
   `SocketException(AddressAlreadyInUse)`. Les autres erreurs de socket et les
   exceptions non liées aux sockets se propagent immédiatement, sans nouvelle
   tentative. Cela couvre le cas courant où un autre processus local détenait
   le port de façon transitoire.
3. Les ports locaux intermédiaires des tunnels chaînés bénéficient du même
   traitement. `ForwardedPortRemote.Start()` non (bind côté serveur, surface de
   course différente).

Les appelants peuvent encore observer `SshFailureCode.PortInUse` lorsque le
port est réellement occupé ; réessayer est sans danger à n'importe quelle
couche.

### Modèle de confiance des clés d'hôte SSH

Les décisions de confiance sur les clés d'hôte sont résolues **avant** le
`Connect()` réel, via une sonde de pré-authentification
(`SshConnectionFactory.ProbeHostKeyAsync` avec `NoneAuthenticationMethod`). La
connexion réelle utilise ensuite un `PinnedFingerprintVerifier` strict et
synchrone qui n'accepte que l'empreinte pré-résolue. Le callback
`HostKeyReceived` de SSH.NET n'effectue aucun travail asynchrone, aucun
dispatch UI et aucun appel à `IHostKeyVerifier.VerifyAsync` depuis son
intérieur - cet invariant dispose d'un test de non-régression dédié
(`IHostKeyVerifierIntegrationTests.AttachHostKeyVerification_RejectsInteractiveVerifierSynchronously`).

Les chemins d'exécution de production exigent `HostKeyStore` et
`IHostKeyVerifier` au niveau du type pour les points d'entrée SSH, SFTP,
tunnel, sudo et édition distante. `RejectingHostKeyVerifier.Instance` est le
vérificateur fail-closed sûr pour les tests ou les contextes non interactifs ;
`AutoAcceptHostKeyVerifier.Instance` est réservé aux flux de test explicites
qui nécessitent une acceptation à la première utilisation.
`ToolGatewayConnector` refuse d'acheminer le trafic d'un outil à travers une
passerelle qui n'a pas encore d'empreinte épinglée ; l'utilisateur doit d'abord
mener une session SSH interactive normale afin que la clé d'hôte soit capturée
dans `HostKeyStore` par le chemin de confiance confirmée.

Les entrées de confiance portent des métadonnées (`FirstSeen`, `LastSeen`,
`Algorithm`, `Source`, `PublicKeyBase64`) via `HostKeyEntry`. La persistance
est additive : `trustedHostKeysV2` dans `settings.json` contient les entrées
enrichies ; l'ancien dictionnaire de chaînes `trustedHostKeys` reste lisible
pour la sûreté en cas de retour arrière et n'est jamais réécrit depuis le
chemin V2.

L'import et l'export de `~/.ssh/known_hosts` sont des actions utilisateur
explicites exposées dans `Settings > SSH & SFTP > Trusted host keys`. L'import
préserve les entrées existantes en conflit, sauf si l'utilisateur choisit
explicitement le remplacement dans une fenêtre modale dédiée. L'export préserve
mot pour mot chaque ligne dont Heimdall n'est pas à l'origine (y compris
`@cert-authority`, `@revoked` et les entrées hachées que Heimdall ne sait pas
consommer entièrement).

Les chemins de repli Plink sont eux aussi fail-closed. `PlinkHostKeyDecider`
accepte immédiatement une empreinte stockée ; sinon il demande la clé présentée
à une sonde `IPlinkHostKeyProbe` injectable et exécute le vérificateur normal
avant de lancer plink avec `-hostkey`. Si aucun de ces chemins ne parvient à
résoudre une empreinte de confiance pour Heimdall, l'opération retourne
`SshFailureCode.HostKeyUnavailable` et refuse de se rabattre sur le cache
propre à PuTTY/Plink.

L'identité d'un tunnel réutilisable inclut la cible distante, le mode de
transfert et une clé de chaîne de passerelles résistante aux collisions
(`GatewayChainKey`), dérivée d'identifiants de passerelle stables et d'un
hachage SHA-256 versionné sur des segments de chaîne préfixés par leur
longueur. Deux locataires qui exposent tous deux `10.0.0.5:3389` à travers des
bastions différents ne partagent pas de tunnel local.

Les échecs de clé d'hôte en cours de session sont remontés sous forme
d'événements de sécurité typés, et non de chaînes de déconnexion génériques.
`SshSessionFailureDispatcher` associe `HostKeyRejectedException` à
`SshSessionSecurityEvent` ; l'interface SSH bloque la reconnexion automatique
en cas de non-concordance de clé d'hôte, et le SFTP affiche une bannière de
sécurité. `RemoteFileEditor` lève séparément `HostKeyRotatedDuringUpload`
lorsqu'une session d'édition sudo observe une clé d'hôte différente pendant une
remontée automatique.

L'ancienne surcharge sur tableau d'octets `HostKeyStore.Verify(byte[])`
subsiste pour la compatibilité ascendante mais est marquée `[Obsolete]` ; le
nouveau code doit utiliser les API de vérification tenant compte de l'hôte et
du port, afin que les décisions de confiance restent cantonnées au bon point
d'accès.

### Escalade sudo en SFTP et édition distante

Le repli sudo du SFTP est délibérément étroit. `EmbeddedSftpViewModel` n'escalade
que pour des exceptions typées de permission refusée
(`SftpPermissionDeniedException` et `UnauthorizedAccessException` locale) ; les
messages génériques `SshException("Failure")` ne déclenchent aucune opération
privilégiée. Ce compromis accepte quelques invites de nouvelle tentative
manuelle en échange de l'absence d'actions sudo sur des échecs qui ne relèvent
pas des permissions.

Les remontées privilégiées séparent les commandes d'écriture et de nettoyage.
L'écriture par `sudo tee` est exécutée séparément, et la suppression du fichier
de transit `/tmp/.heimdall_*` s'effectue depuis un chemin `finally` avec une
commande de nettoyage non annulable. Les échecs de nettoyage sont journalisés
en avertissement tout en préservant l'erreur d'écriture d'origine.

`RemoteFileEditor` suit les tâches de remontée du surveillant de fichiers par
session d'édition, propage l'annulation via `CloseEdit` et `Dispose`, et
observe les fautes de manière synchrone afin que les exceptions de remontée en
arrière-plan non gérées n'atteignent pas le pipeline
`UnobservedTaskException` à l'échelle du processus. Les sessions d'édition sudo
mettent en cache le `PinnedFingerprintVerifier` construit à l'ouverture au lieu
de résoudre à nouveau la confiance de clé d'hôte à chaque enregistrement.

### Garanties de validation des remontées distantes

Chaque écriture distante commence par une remontée vers un chemin temporaire
unique situé à côté de la destination, de sorte qu'un transfert tronqué
n'atterrit jamais sur la destination. Ce qui diffère selon le protocole, c'est
l'étape de validation, et avec elle la garantie dont bénéficie une destination
existante.

La remontée de fichier SFTP ne remplace une destination qu'elle a observée que
par un renommage atomique. `SftpAtomicUpload.CommitRename` tente d'abord
l'extension OpenSSH `posix-rename@openssh.com`, qui remplace la destination en
une seule opération côté serveur. Un échec n'est éligible au repli par
renommage simple que lorsque
`SftpBrowser.IsAtomicRenameCapabilityFailure` reconnaît une erreur de capacité
(`NotSupportedException`, ou une `SftpException` portant
`StatusCode.OperationUnsupported`) ; tout autre échec, erreurs de permission
comprises, se propage tel quel. Une fois rétrogradé, le chemin sonde la
destination et le repli ne s'exécute que si cette sonde prouve son absence :
une destination que la sonde signale comme présente lève une
`InvalidOperationException` et reste intacte, et une sonde qui échoue
elle-même est propagée plutôt que supposée. Heimdall ne déplace donc jamais, ne
supprime jamais et ne sauvegarde jamais une destination qu'il a observée, et
n'ouvre jamais de fenêtre pendant laquelle une telle destination serait
manquante. Le fichier temporaire remonté est supprimé par le chemin de retour
arrière de l'appelant.

Cette garantie est bornée à ce que la sonde a observé, et le repli n'est pas
transactionnel. Entre une sonde qui signale la destination absente et le
renommage simple qui suit, un autre écrivain peut créer ce chemin. Le renommage
atterrit alors sur une cible que Heimdall n'a jamais vue, avec la sémantique
que le serveur applique à une destination existante : SFTP laisse ce cas à
l'implémentation, si bien qu'un serveur peut refuser le renommage ou écraser
silencieusement. Seul le chemin `posix-rename` est atomique vis-à-vis d'une
telle création concurrente. Un déploiement qui doit exclure cette course
nécessite un serveur offrant l'extension.

La copie distante SFTP réserve la destination de façon exclusive, ou bien est
refusée. La copie s'exécute comme une commande côté serveur sur un canal exec
SSH épinglé à la clé d'hôte résolue à la connexion, et c'est cette commande qui
rend réel le contrat de non-écrasement : un fichier est mis en attente puis
publié par un lien physique, une racine de répertoire est réservée par un
`mkdir` sans `-p`, et les deux échouent si la destination existe déjà. Si la
commande ne peut pas être utilisée, la copie est refusée et la raison est
rapportée ; il n'existe pas de seconde voie.

Il en existait une auparavant. Lorsque la commande côté serveur n'était pas
disponible, la copie se rabattait sur un téléchargement vers un fichier
temporaire local puis une republication par renommage simple, en ne resondant
la destination qu'après l'échec de ce renommage. Un serveur dont le renommage
écrase silencieusement la destination réussissait donc sans exception ni
avertissement, ce qui signifiait que le contrat documenté de non-écrasement
n'était pas honoré sur ce chemin. Le repli a été supprimé plutôt qu'annoté :
une copie qui ne peut pas promettre que la destination reste intacte n'est tout
simplement pas effectuée.

Annuler une copie lève une annulation, pas un refus. Ce sont deux issues
distinctes, rapportées et journalisées séparément, afin que "l'utilisateur a
arrêté ceci" ne soit jamais présenté comme "ce serveur ne peut pas copier en
toute sécurité".

La copie distante FTP est refusée, pas tentée. Le contrat de copie veut qu'une
destination existante ne soit jamais écrasée, et FTP ne peut pas l'honorer :
toute publication que ce client propose se réduit à une vérification
d'existence côté client suivie d'un renommage simple, et la RFC 959 ne dit rien
de ce que fait un renommage vers une destination existante, si bien qu'un
serveur qui écrase silencieusement reste conforme. Auparavant, la copie FTP
passait par la remontée ordinaire, dont la validation remplace une destination
existante et rapporte un succès : toute vérification préalable manquée devenait
donc une perte de données silencieuse. `CopyAsync` sur le navigateur FTP lève
désormais toujours une exception, et l'utilisateur est orienté vers le SFTP
avec une commande de copie côté serveur fonctionnelle, seule voie qui réserve
la destination de façon exclusive. Le couper et le déplacer FTP ne font aucune
promesse de ce type et émettent toujours un renommage simple : ils peuvent donc
écraser silencieusement. Ne comptez pas non plus sur un déplacement pour
préserver une destination existante.

La remontée FTP conserve le remplacement en deux étapes et n'est pas atomique.
FluentFTP n'expose pas de remplacement atomique, donc
`FtpAtomicUpload.CommitRenameAsync` déplace une destination existante vers un
fichier voisin `.bak`, déplace le fichier temporaire remonté à sa place, puis
supprime la sauvegarde. Une validation en échec restaure la sauvegarde, et une
restauration en échec lève une `InvalidOperationException` portant à la fois
l'erreur de validation et l'erreur de restauration. Entre les deux
déplacements, la destination n'existe pas : un lecteur concurrent peut donc
observer un fichier manquant, et un plantage peut laisser la charge utile sous
le voisin `.bak`.

Le remplacement FTP ne préserve par ailleurs aucune métadonnée du fichier
remplacé. Ce qui atterrit à la destination est un fichier fraîchement remonté,
portant le propriétaire, le mode et les horodatages que le serveur attribue à
une nouvelle remontée : la propriété, les permissions, les horodatages, les
ACL, les attributs étendus et les capacités du fichier précédent ont disparu.
FTP n'expose aucune commande qui permettrait de les restaurer, et Heimdall ne
simule pas une préservation qu'il ne peut pas assurer. Une destination dont
l'accès est régi par son propre mode ou sa propre ACL ne doit pas être
remplacée par FTP ; utilisez le SFTP, dont le chemin de remplacement préserve
le mode de permission complet et refuse la validation quand il ne le peut pas.

Les deux faits sont rapportés ensemble. Un remplacement réussi d'une
destination existante lève exactement un `RemoteOperationWarning` par opération
sur la surface de session, nommant en un seul message l'absence de garantie
d'atomicité et la perte de métadonnées. Il n'est levé qu'une fois le
déplacement de validation réussi : aucun avertissement n'est dû lorsque la
destination était absente, lorsque le déplacement de sauvegarde a échoué, ou
lorsque la validation a échoué et que la sauvegarde a été restaurée, car dans
ces cas rien n'a été remplacé.

### Avertissements de transport FTP et FTPS

Le FTP est implémenté au-dessus de l'`AsyncFtpClient` de FluentFTP. `FtpHandler`
valide l'hôte et le port cibles avant la connexion. Si un utilisateur se
connecte avec des identifiants alors que TLS est désactivé,
`ConnectionResult.Warning` porte vers la surface de statut un avertissement
localisé et non bloquant sur la circulation en clair ; cela ne bloque ni les
sessions anonymes ni les sessions FTPS explicites. Le FTPS explicite active TLS
pour le canal de contrôle ainsi que `DataConnectionEncryption` de FluentFTP, de
sorte que les transferts de fichiers utilisent un canal de données chiffré.

Le certificat du canal de contrôle FTPS est validé et épinglé par Heimdall. Le
canal de données souffre d'une limitation tierce dans FluentFTP 54.2.0 :
`FtpDataStream` installe un gestionnaire d'acceptation de certificat
inconditionnel, si bien que Heimdall ne peut pas vérifier l'identité de ce
canal. Ce comportement est également présent dans les sources amont actuelles.
Aucune option de `FtpConfig` n'expose la validation du certificat du canal de
données, et fournir un second callback via `ConfigureAuthentication` est rejeté
par .NET, car `SslStream` a déjà été construit avec le callback de FluentFTP.

La garantie exacte présentée à l'utilisateur est la suivante :

*FTPS ne peut etre considere comme liant l'identite du canal de donnees a
celle du canal de controle que si le serveur exige la reprise de session TLS
et qu'un transfert reel reussit sous cette politique. Sans cette exigence
serveur, la garantie est indisponible.*

.NET autorise la reprise de session mais n'expose aucune API permettant à
Heimdall de l'exiger ou d'observer si elle a eu lieu. Les sessions FTPS actives
affichent donc un avertissement persistant et non bloquant indiquant que
l'identité du canal de données n'est pas vérifiée.

### Énumération des identités par l'agent SSH

Les implémentations d'`ISshAgent` (`PageantAgent`, `OpenSshPipeAgent`) ne
conservent jamais de handle IPC d'une requête à l'autre. Chaque appel à
`GetIdentities` et `Sign` ouvre un nouveau mapping de mémoire partagée
(Pageant) ou une nouvelle connexion de tube nommé (agent OpenSSH), puis le
libère avant de retourner. Les sondes de disponibilité ont un délai de 250 ms ;
les requêtes réelles ont un délai de 5 s. Un tube introuvable comme un
dépassement de délai retournent "indisponible" sans lever d'exception. La
préférence de l'utilisateur entre les agents est un réglage d'exécution
(`AppSettings.SshAgentPreference`) ; les changements prennent effet à la
prochaine tentative de connexion, sans redémarrage de l'application.

`OpenSshPipeAgent.SendRequest` repose sur des entrées/sorties de tube
asynchrones (`NamedPipeClientStream` ouvert avec `PipeOptions.Asynchronous`) et
sur un jeton lié de délai/annulation, en remplacement du `ReadTimeout` au
mieux, que `NamedPipeClientStream` ignore silencieusement dans certains modes.

### Comparaison des empreintes de clé d'hôte

`HostKeyStore.Verify` et `HostKeyTrustService.Verify` / `Trust` / `Import`
comparent l'empreinte stockée et l'empreinte présentée avec l'assistant partagé
`HostKeyStore.ConstantTimeEquals`, qui délègue à
`CryptographicOperations.FixedTimeEquals` après une garde d'égalité de longueur
sans risque ici, puisque les empreintes de clé d'hôte OpenSSH sont de taille
fixe : `SHA256:` suivi de 43 caractères base64. Les empreintes de clé d'hôte ne
sont pas secrètes (les serveurs les publient, `ssh-keyscan` les récupère, les
enregistrements DNS SSHFP les exposent) : il s'agit donc de défense en
profondeur, pas d'une mesure d'atténuation porteuse. Le motif est local à
`HostKeyStore` et ne doit pas être recopié tel quel pour comparer des secrets
de longueur variable.

### Import de known_hosts - bornes anti-déni de service

`KnownHostsParser` applique deux plafonds stricts lorsqu'il consomme des
fichiers `known_hosts` fournis de l'extérieur :

- **`MaxLineLength = 65 536`** - les lignes de plus de 64 Ko sont ignorées avec
  un diagnostic `MalformedLine` ; cela protège contre une ligne géante unique
  forçant une allocation importante.
- **`MaxFileSizeBytes = 50 MB`** - les fichiers de plus de 50 Mo sont refusés
  d'emblée avec un diagnostic typé `FileTooLarge`. L'importeur du coeur comme
  l'importeur côté application lisent en flux via `StreamReader` plutôt que
  `File.ReadAllText`, et encadrent les entrées/sorties d'un `try/catch` afin que
  les fichiers verrouillés ou illisibles se dégradent en diagnostics
  `FileReadError` au lieu de remonter des exceptions vers l'interface.

### Durée de vie du fichier de mot de passe SSH

Lorsqu'une session SSH interactive s'authentifie par mot de passe, ce mot de passe est écrit dans un
fichier éphémère transmis au lanceur via `-pwfile`, parce que l'alternative consiste à le placer sur
une ligne de commande que tout processus de la machine peut lire. Ce fichier vivait auparavant
jusqu'à la fin de la session : un secret nécessaire à une seule poignée de main pouvait donc rester
sur le disque pendant des heures.

Il est désormais supprimé au premier octet que le lanceur écrit sur stdout ou stderr - **mais
uniquement pour un lanceur dont le comportement de `-pwfile` a été mesuré**. Dans PuTTY 0.83,
`-pwfile` est traité à l'intérieur de `cmdline_process_param` pendant l'analyse de la ligne de
commande - une ligne lue, handle refermé aussitôt - strictement avant toute activité réseau : la
moindre sortie intervient donc après la lecture du mot de passe. Mesuré sur ce binaire : un
`-pwfile` illisible vers un hôte injoignable signale immédiatement l'erreur de fichier, là où un
fichier lisible vers le même hôte consomme au contraire l'intégralité du délai réseau.

Cette conclusion vaut pour ce build et pour aucun autre. Heimdall laisse l'utilisateur pointer
`PlinkPath` vers n'importe quel exécutable : le lanceur est donc identifié par le SHA-256 de ses
octets, comparé au build mesuré livré dans `Assets/Tools/plink.exe`. Un autre build peut très bien
afficher quelque chose avant de lire le fichier ; pour tout autre exécutable - octets inconnus,
chemin illisible, échec quelconque - la suppression anticipée est retenue et le fichier est libéré à
la fin du processus, comme auparavant. Rien n'est accordé sur la foi d'un nom de fichier, d'un
répertoire, d'une ressource de version ou d'une chaîne affichée par `-V` : aucun de ces éléments ne
dit quoi que ce soit du moment où le fichier est lu.

Identifier les octets ne suffit pas à lui seul à décrire **l'image qui s'exécute**, pour deux
raisons distinctes.

La première est le temps. Le gestionnaire peut attendre sur une boîte de dialogue interactive de mot
de passe, et cette attente n'est pas bornée ; une mise à jour légitime arrivant dans cette fenêtre
attribuerait le verdict précédent à un build non mesuré. Le mot de passe est donc résolu
intégralement d'abord, dialogue compris, et c'est seulement ensuite que l'exécutable est ouvert une
fois, haché depuis ce même handle et - en cas de correspondance - maintenu ouvert avec un partage
qui refuse l'écriture et la suppression jusqu'à ce que le lancement soit émis. Mesuré sur une copie
temporaire : tant que cet épinglage est tenu, l'image démarre toujours, tandis que la remplacer et y
écrire sont l'une comme l'autre refusées. L'épinglage est relâché dès que le lancement retourne, si
bien qu'une mise à jour ultérieure n'est pas bloquée pour toute la session.

La seconde est le chemin. **Un chemin absolu n'identifie pas un fichier.** Le handle ouvert épingle
le fichier, pas les répertoires nommés sur le trajet qui y mène : une jonction située n'importe où
dans le chemin peut être supprimée et recréée en pointant ailleurs pendant que le handle est tenu,
et la chaîne absolue identique se résout alors vers une autre image. Cela a été reproduit, pas
théorisé - avec un plink attesté et épinglé sous `...\current\plink.exe`, repointer la jonction
`current` puis lancer la même chaîne a exécuté un autre exécutable. `Path.GetFullPath` n'y change
rien : la chaîne était déjà absolue et déjà normalisée.

Un bail attesté porte donc aussi le chemin obtenu **depuis le handle lui-même**, par
`GetFinalPathNameByHandle`, tous les points d'analyse déjà suivis, et c'est ce chemin qui est lancé.
Mesuré : la forme retournée préfixée par `\\?\` démarre l'image, et démarre bien l'image attestée
même après que la jonction a été repointée. Si ce chemin ne peut pas être résolu, rien n'est
attesté : la connexion se poursuit sur le chemin configuré et le fichier de mot de passe attend la
fin du processus.

Ce n'est **pas** une défense contre un binaire hostile. Heimdall confie le mot de passe à
l'exécutable vers lequel on l'a pointé : un exécutable choisi pour le voler a déjà gagné. Ce que la
vérification d'identité établit est plus étroit : déterminer si une conclusion temporelle tirée d'un
build mesuré peut être appliquée au binaire réellement lancé.

**L'exposition est réduite, et le cas résiduel est plus étroit que ce qui était écrit ici au
départ.** Mesuré contre une cible OpenSSH 9.6p1 réelle dont la commande forcée est `sleep 600` - une
session qui se connecte puis ne dit plus rien du tout - le lanceur écrit malgré tout `Using username
"..."` sur stderr dès qu'il dispose d'un nom de connexion. Cela atteint la barrière via le flux de
sortie fusionné, et le fichier de mot de passe est supprimé 93 ms après le retour de la connexion,
alors que le processus tourne encore et que la fin du processus n'est pas survenue. Une commande
distante silencieuse est donc couverte.

**Un profil sans nom d'utilisateur configuré est désormais refusé plutôt que laissé exposé.** Dans
ce cas, le lanceur attend un nom de connexion et n'écrit rien sur l'un ou l'autre flux - mesuré :
zéro octet après trois secondes, processus toujours vivant - de sorte qu'aucun premier octet
n'arrive et que le fichier de mot de passe vivrait jusqu'à la fin du processus. Heimdall refuse donc
la connexion avant la boîte de dialogue de mot de passe, avant toute sonde de clé d'hôte ou mutation
de confiance, avant que le lanceur ne soit identifié et avant que le fichier n'existe, en demandant
à l'utilisateur de renseigner un nom d'utilisateur ou de se connecter par clé.

Le refus est limité aux connexions qui déposeraient un mot de passe sur le disque : un mot de passe
stocké, avec ou sans clé, ou bien ni mot de passe ni clé, puisque ce chemin va ensuite en réclamer
un. **Un profil qui s'authentifie par clé et sans mot de passe n'est pas concerné** - il n'écrit
jamais le fichier, il n'a donc rien à protéger ici, et rien n'est affirmé ici au sujet des
connexions par clé seule.

La fin du processus reste le filet de sécurité, tous les chemins qui libèrent le fichier passent par
une barrière unique afin que la suppression s'exécute exactement une fois, et un lancement qui
échoue ou qui est annulé libère sur-le-champ.

### Durcissement des arguments de sous-processus

`PlinkTunnelRunner` construit la liste d'arguments de plink via
`ProcessStartInfo.ArgumentList` (pas de concaténation de chaînes), et la tâche
de vidage de stderr est **jointe** au moment du `Stop()`, avant
`Process.Kill()`, de sorte que le lecteur en arrière-plan ne peut pas survivre
au tube auquel il était rattaché. Le sanitiseur de vidage (`SanitizeForLog`)
masque les affectations mot de passe / phrase secrète sur un seul jeton, les
affectations token / bearer jusqu'à la fin de ligne, ainsi que les options
`-pw` / `-pwfile`, afin qu'un écho inattendu de plink sur stderr ne puisse pas
faire fuiter des identifiants dans le journal applicatif.

### Entrées distantes dont le type ne peut pas être déterminé

Un listage classe chaque entrée distante : fichier régulier, répertoire, lien symbolique, tube,
socket, périphérique. Lorsque chacun de ces tests échoue, l'entrée est rapportée comme
**inclassable** plutôt que comme un fichier régulier. Cette valeur est le zéro de l'énumération : une
valeur non initialisée ou non mappée est donc la valeur non transférable, et une branche oubliée
échoue en fermeture plutôt qu'en ouverture.

Une entrée inclassable est refusée par l'orchestration applicative comme par le chemin SFTP gardé :
elle est exclue de l'inventaire transférable, elle n'est donc ni écrasée par une remontée, ni
téléchargée, ni collée, ni dupliquée, et la garde de remontée SFTP la refuse explicitement. Elle est
affichée avec une icône distincte afin de ne pas être prise pour un fichier. Le renommage reste
disponible, comme pour un tube ou un socket, car un renommage déplace un nom et ne lit ni n'écrit le
contenu de l'objet.

La borne exacte : cela couvre la classification propre au listage SFTP et le mappeur de listage FTP.
En FTP, une chaîne de permissions de neuf caractères est un mode seul et ne porte pas de caractère de
type : elle n'est donc pas lue comme telle ; une chaîne de dix caractères ou plus en porte bien un en
tête (le caractère supplémentaire des formes ACL `-rw-r--r--+` et SELinux `.` vient en dernier), et
un caractère de type que ce build ne reconnaît pas rend l'entrée inclassable. Ce que cela n'ajoute
**pas**, c'est une garde de type sur le chemin de remontée FTP lui-même :
`EnsureUploadTargetSupported` n'est toujours appelé que depuis le navigateur SFTP, si bien qu'une
remontée FTP ne consulte pas du tout le type de la destination. Cette lacune est antérieure à ce
changement, s'applique de la même façon aux liens, tubes, sockets et périphériques ; elle est suivie
séparément et n'est pas refermée ici.

Le listage basé sur `ls` utilisé pour la navigation sudo n'est pas concerné : il ignore déjà toute
ligne dont le caractère de type n'est pas l'un de ceux qu'il reconnaît. Ces lignes sont entièrement
écartées du listage ; elles ne sont pas classées comme inclassables, et cet écart ne doit pas être
lu comme produisant ce type.

### Collage du presse-papiers entre endpoints distincts

Un collage entre deux endpoints distants différents télécharge chaque fichier source et le dépose sur
le serveur de destination. Chaque noeud qu'il y crée, fichier comme répertoire, passe par une
primitive **exclusive** : un fichier est mis en attente puis publié par un lien physique, un
répertoire est réservé par un `mkdir` sans `-p`. Les deux échouent lorsque quelque chose occupe déjà
le nom, et c'est le serveur qui tranche, pas le client.

Un transport incapable d'offrir une telle primitive ne colle pas. FTP n'a aucune opération au moment
de la validation qui échoue lorsque la destination existe (tout se réduit à une vérification
d'existence côté client suivie d'un renommage) : il décline donc la capacité, et un collage entre
endpoints vers lui est refusé **avant** la création du moindre répertoire et avant l'envoi du moindre
octet.

Ce refus anticipé se décide par transport, pas par session. Une session SFTP qui annonce la capacité
mais ne peut pas atteindre son canal exec épinglé refuse plus tard, au premier noeud qu'elle tente de
publier : un fichier source peut alors déjà avoir été récupéré dans un temporaire local. Rien n'est
créé ni remplacé sur la destination dans ce cas, et le temporaire est supprimé ; ce qui est perdu,
c'est l'effort de transfert, pas des données.

Atteindre ce refus suppose que les deux volets soient reconnus comme des endpoints différents :
l'identité d'endpoint du presse-papiers est donc résolue par un point d'extension dédié que les
décorateurs propagent, et non en testant le type concret du navigateur. Un test de type répond au
sujet de l'enveloppe dès que le décorateur de journal d'opérations est en place, ce qui donnerait à
chaque volet FTP la même clé vide et ferait passer deux serveurs FTP *différents* pour un seul
endpoint. Les endpoints FTP distincts sont donc identifiés à travers le décorateur, et un collage
entre eux emprunte le chemin inter-endpoints et rencontre la barrière.

Une identité d'endpoint totalement indéterminable n'est jamais traitée comme une correspondance non
plus. Deux identités inconnues sont deux serveurs que personne ne saurait nommer : le collage est
donc routé vers le chemin inter-endpoints et publie de façon exclusive ou bien est refusé. Perdre
les métadonnées d'endpoint peut dégrader l'expérience ; cela ne peut pas rouvrir silencieusement un
écrasement.

Cela referme le contournement inter-endpoints. Cela ne change rien au contrat des opérations qui
sont réellement intra-endpoint : un renommage ou une copie au sein d'un même serveur se comporte
comme il l'a toujours fait.

Les listages de répertoires sont lus en direct depuis la destination plutôt que depuis ce que le
volet a affiché en dernier, mais ils servent **uniquement** à choisir un nom qui n'est pas déjà pris.
Un listage ne fait autorité sur rien : il est déjà périmé à l'instant où il revient. La garantie
provient de la réservation exclusive de chaque noeud, jamais d'une sonde préalable.

Ni une collision, ni une annulation, ni un résultat non confirmé n'autorisent jamais la suppression
de la source d'un couper. Une annulation, en particulier, n'est pas la preuve que rien n'a atterri :
un lien ou une création de répertoire peut prendre effet juste avant que la réponse ne soit perdue.
Heimdall demande le rechargement de la destination et, lorsque la session et le listage sont encore
disponibles, l'état rafraîchi est ce que vous voyez ; un rafraîchissement en échec ne change rien au
verdict. La source est conservée et l'entrée de presse-papiers est conservée dans les deux cas, car
un rechargement en échec ne transforme jamais une issue non confirmée en succès et n'autorise jamais
la suppression de la source.

L'atomicité est par noeud, pas transactionnelle à l'échelle d'une arborescence. Un collage interrompu
en cours de route peut laisser une arborescence partielle sur la destination. C'est délibéré :
nettoyer récursivement un répertoire créé par le collage pourrait supprimer des entrées qu'un tiers y
a ajoutées entre-temps.

#### Ce contre quoi cela ne protège pas

La protection vise la **concurrence accidentelle** : deux volets, une vue périmée, un collègue qui
écrit dans le même répertoire au même moment.

Elle n'établit pas la provenance du contenu publié face à un acteur malveillant disposant du droit
d'écriture dans le même répertoire. Les chemins de mise en attente sont nommés, et un attaquant
capable de substituer l'entrée de mise en attente entre deux opérations par nom peut faire publier un
contenu que ce client n'a pas écrit. Le nettoyage par nom partage cette limite. Rien ici ne doit être
lu comme une défense contre une partie qui peut déjà écrire là où vous écrivez.

Les tests couvrent le contrat, le câblage et les commandes générées. **Aucun serveur SFTP réel n'est
sollicité nulle part dans la suite.** En particulier, qu'un `ln` ou un `mkdir` distant accepte `--`
comme marqueur de fin d'options est une propriété des utilitaires de ce serveur, pas quelque chose
qui soit démontré ici, ni une garantie universelle. Un utilitaire qui rejette ces formes fait échouer
la commande, et l'appelant refuse : il n'existe pas de repli vers une primitive susceptible de
remplacer la destination.

## Tests de sécurité

- Tests unitaires de la vérification TOFU :
  `tests/Heimdall.Ssh.Tests/HostKeyStoreTests.cs` et
  `tests/Heimdall.Ssh.Tests/IHostKeyVerifierIntegrationTests.cs`, dont un test
  de non-régression anti-interblocage qui exécute le callback de clé d'hôte sous
  un `SynchronizationContext` mono-thread avec un vérificateur lent et vérifie
  que le callback retourne en moins de 50 ms.
- Orchestration du service de confiance et aller-retour known_hosts :
  `tests/Heimdall.Ssh.Tests/KnownHostsImportExportTests.cs`.
- Protocole et IPC de l'agent SSH :
  `tests/Heimdall.Ssh.Tests/OpenSshAgentProtocolTests.cs` (encodage/décodage de
  protocole pur) et `tests/Heimdall.Ssh.Tests/OpenSshPipeAgentTests.cs`
  (transport par tube nommé utilisant un tube de test suffixé par un GUID,
  indépendant du véritable service Windows OpenSSH Agent).
- Assistant de nouvelle tentative de bind local :
  `tests/Heimdall.Ssh.Tests/TunnelManagerStartRetryTests.cs`, dont un test qui
  retient un vrai port TCP via `Socket.Bind` et confirme que l'assistant de
  nouvelle tentative échoue toujours en fermeture avec `AddressAlreadyInUse`.
- Tests de caractérisation de `TunnelManager` et identité de réutilisation
  tenant compte de la passerelle :
  `tests/Heimdall.Ssh.Tests/TunnelManagerTests.cs` et
  `tests/Heimdall.App.Tests/TunnelReuseIdentityTests.cs`.
- Couverture des décisions fail-closed de Plink :
  `tests/Heimdall.App.Tests/PlinkFailClosedTests.cs`.
- Fabrique de `SECURITY_ATTRIBUTES` Pageant et constructeur de SDDL self-only :
  `tests/Heimdall.Ssh.Tests/PageantClientTests.cs`
  (`BuildSelfOnlySddl_*`, `CreateSelfOnly_ManyAllocations_DoNotLeakOrThrow`).
- Comparaison d'empreinte à temps constant :
  `tests/Heimdall.Ssh.Tests/HostKeyStoreTests.cs`
  (`ConstantTimeEquals_*`).
- Distribution des événements de sécurité en cours de session et démontage du
  shell : `tests/Heimdall.Ssh.Tests/SshSessionFailureDispatcherTests.cs` et
  `tests/Heimdall.Ssh.Tests/SshShellSessionTeardownTests.cs`.
- Masquage des secrets sur stderr :
  `tests/Heimdall.Ssh.Tests/PlinkTunnelRunnerTests.cs`
  (`SanitizeForLog_RedactsBearerToEndOfLine`,
  `SanitizeForLog_RedactsTokenToEndOfLine`,
  `SanitizeForLog_RedactsSingleTokenPassword`,
  `SanitizeForLog_RedactsPlinkCredentialFlags`).
- Plafonds anti-déni de service de known_hosts et dégradation gracieuse des
  entrées/sorties : `tests/Heimdall.Core.Tests/Ssh/KnownHostsParserTests.cs` et
  `tests/Heimdall.Ssh.Tests/KnownHostsImportExportTests.cs`, plus
  `tests/Heimdall.App.Tests/KnownHostsImporterStreamingTests.cs`
  (`ImportFile_OversizedFile_RejectedWithoutThrowing`, cas de ligne trop
  longue).
- Escalade sudo SFTP, rotation de clé d'hôte en édition distante et cycle de vie
  des tâches de remontée :
  `tests/Heimdall.App.Tests/IsPermissionDeniedTests.cs`,
  `tests/Heimdall.App.Tests/RemoteFileEditorRotationTests.cs` et
  `tests/Heimdall.App.Tests/RemoteFileEditorTaskTrackingTests.cs`.
- Construction des commandes sudo et durcissement du lancement d'éditeur
  externe : `tests/Heimdall.App.Tests/SudoUploadCommandsTests.cs` et
  `tests/Heimdall.App.Tests/ResolveEditorPathTests.cs`.
- Analyseur FTP, validation hôte/port et couverture de l'avertissement en clair :
  `tests/Heimdall.App.Tests/FtpBrowserParsingTests.cs` et
  `tests/Heimdall.App.Tests/FtpHandlerValidationTests.cs`.
- Tests de non-régression sur l'injection shell : couverture d'`InputValidator`
  dans `tests/Heimdall.Core.Tests`.
- Assainissement de la génération de fichiers RDP :
  `tests/Heimdall.Ssh.Tests/RdpFileGeneratorTests.cs`.
- La CI impose : une compilation sans aucun avertissement sous
  `TreatWarningsAsErrors`, `dotnet format --verify-no-changes`, la suite de
  tests complète, la parité des locales JSON (les jeux de clés EN et FR doivent
  être identiques, actuellement 5 489 clés chacun) et une analyse informative
  `dotnet list package --vulnerable`.
- Analyse des dépendances pour revue manuelle : `dotnet list Heimdall.slnx
  package --vulnerable --include-transitive`. La CI émet des avertissements mais
  ne bloque pas sur les résultats de vulnérabilité, car les avis contiennent
  parfois des faux positifs ou des entrées sans chemin de mise à niveau.
