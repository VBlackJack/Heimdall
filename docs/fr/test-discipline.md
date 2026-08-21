*Ce document est la version française de [../test-discipline.md](../test-discipline.md). / This document is the French version.*

# Discipline de test

Lorsqu'on étend la couverture de test, deux règles non négociables s'appliquent :

- **Producteur d'abord** - Un test de mapping doit citer le fichier, la ligne et le déclencheur du producteur réel. Si le producteur est introuvable dans `src/`, le test repart en investigation avant d'être écrit.
- **Architecture d'abord** - N'introduisez pas de refactoring dans le seul but de rendre un test atteignable. Si le test exige une nouvelle couture (seam), décidez ce refactoring explicitement, comme un changement d'architecture justifié par lui-même, et non comme un effet de bord de la couverture de test.
