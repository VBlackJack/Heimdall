*Ce document est la version française de [SECURITY.md](SECURITY.md). / This document is the French version.*

# Politique de sécurité

## Signaler une vulnérabilité

Signalez toute vulnérabilité suspectée par le signalement privé de GitHub :
[ouvrir un rapport privé](https://github.com/VBlackJack/Heimdall/security/advisories/new).
Le rapport reste visible de vous et du mainteneur seulement, et le correctif se
discute dans ce même fil. N'ouvrez pas de ticket public pour une vulnérabilité
présumée.

Indiquez dans le rapport :

- la version ou le commit concerné ;
- les étapes de reproduction ;
- l'impact attendu ;
- tout journal pertinent, avec les identifiants et les noms d'hôtes expurgés.

## Où adresser le reste

| Sujet | Canal |
| --- | --- |
| Vulnérabilité présumée | [Signalement privé](https://github.com/VBlackJack/Heimdall/security/advisories/new) |
| Bogue, plantage, régression | [Issues](https://github.com/VBlackJack/Heimdall/issues) |
| Question, idée, retour d'usage | [Discussions](https://github.com/VBlackJack/Heimdall/discussions) |

## Il n'y a pas de canal de messagerie

Ce projet ne publie aucune adresse de contact, et la messagerie n'est pas un
moyen pris en charge pour joindre le mainteneur. Les adresses qui apparaissent
dans les métadonnées de commit sont un artefact de la façon dont Git enregistre
la paternité, pas une invitation : elles ne sont pas relevées, et un message qui
y est envoyé ne reçoit pas de réponse. Utilisez l'un des canaux GitHub ci-dessus.

Le modèle de menace de référence, les limitations connues, les mesures de
sécurité et les références des tests de sécurité se trouvent dans
[docs/fr/SECURITY.md](docs/fr/SECURITY.md).
