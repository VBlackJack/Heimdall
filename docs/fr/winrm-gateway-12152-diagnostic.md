*Ce document est la version française de [../winrm-gateway-12152-diagnostic.md](../winrm-gateway-12152-diagnostic.md). / This document is the French version.*

# WinRM via passerelle SSH - diagnostic `12152`

Statut : **clos - aucun correctif de code n'est requis dans Heimdall.**

## Contexte

Un profil WinRM peut être routé au travers d'une passerelle SSH (HTTP uniquement
sur le tunnel, authentification NTLM). Dans un environnement de domaine donné, un
tel profil échoue : Heimdall établit proprement le tunnel SSH, mais
`Enter-PSSession` renvoie l'erreur WinHTTP **`12152`** ("the server returned an
invalid or unrecognized response").

Le journal Heimdall affiche `Tunnel established ... port 59850` sans aucune
erreur de port redirigé - le `12152` apparaît plus loin dans la chaîne, dans le
terminal PowerShell, et non dans le logger Heimdall. Un profil RDP passant par le
*même* bastion et le *même* hôte fonctionne : la machinerie de tunnel de Heimdall
est donc saine.

Montage de référence : cible WinRM `winrm-target.example.internal:5985` (HTTP)
via le bastion SSH `bastion.example.internal:22`.

## Test d'isolation manuel

Pour séparer Heimdall de son environnement, le même tunnel a été remonté à la
main, hors de Heimdall, et WinRM a été sollicité directement contre ce tunnel.

```
# Pageant loaded with the bastion key, in a real console (not ISE):
plink -ssh -N -L 59850:winrm-target.example.internal:5985 <user>@bastion.example.internal
```

```powershell
# In a separate real console:
Test-NetConnection 127.0.0.1 -Port 59850
Invoke-WebRequest http://127.0.0.1:59850/wsman
$cred = Get-Credential
Enter-PSSession -ComputerName 127.0.0.1 -Port 59850 -Authentication Negotiate -Credential $cred
```

`plink` est indispensable (et non le `ssh` d'OpenSSH) : le bastion n'accepte que
l'authentification par clé publique et la clé réside dans Pageant. Le mode par
identifiants (NTLM) est utilisé parce que le passage par le tunnel transforme le
SPN Kerberos en `HTTP/127.0.0.1`, pour lequel aucun ticket n'existe.

## Résultat observé

| Couche | Résultat |
|---|---|
| Tunnel TCP | **OK** - `Test-NetConnection` rapporte `TcpTestSucceeded: True` |
| HTTP / WinRM | **Échec** - `Invoke-WebRequest .../wsman` : "The underlying connection was closed: An unexpected error occurred on a receive." |
| `Enter-PSSession` | **Échec** - `PSRemotingTransportException` |

La redirection TCP atteint bien la cible, mais l'échange HTTP/WinRM est fermé de
manière inattendue.

## Conclusion

La panne se reproduit **hors de Heimdall**, avec un tunnel monté à la main.
Heimdall n'est pas sur le chemin du défaut. La cause est environnementale : le
service WinRM sur la cible, ou un équipement de couche applicative situé sur le
trajet `bastion -> winrm-target.example.internal:5985`, met fin à la session
HTTP.

## Lire la console `plink`

La ligne que `plink` affiche juste après la tentative échouée permet d'attribuer
le défaut à l'un ou l'autre maillon de la chaîne :

| Message `plink` | Signification |
|---|---|
| `administratively prohibited` | Le serveur SSH du bastion refuse la redirection vers la cible (`AllowTcpForwarding` / ACL). La panne se situe au niveau SSH. |
| `connection refused` | Le bastion atteint la cible, mais le port 5985 renvoie un RST - service non lié à cette interface, ou pare-feu. |
| `remote host closed` / `remote side closed connection` | La redirection s'ouvre, puis la cible ferme la connexion en cours d'échange - cela désigne le service WinRM ou un IPS/proxy de couche applicative. |
| *(aucune ligne)* | La redirection SSH est saine ; le défaut vient uniquement du service `winrm-target.example.internal:5985`. |

Dans tous les cas, la conclusion "hors de Heimdall" reste valable - cette ligne
ne fait qu'attribuer le défaut, elle ne change pas le verdict.

## Statut

Aucun correctif de code n'est requis dans Heimdall. WinRM via passerelle SSH est
livré et se comporte correctement ; il s'agit d'une limitation propre à
l'environnement d'une cible donnée.
