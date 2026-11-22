# Mémoire du projet Transport

**Point de reprise du 22 novembre 2026.** Le développement du lot 1 se poursuit sur la branche `lot-1/guichet-especes` dans la [PR 1](https://github.com/siakajoseph/Transport/pull/1). L’utilisateur souhaite effectuer une seule recette sur la version intégrée. La fusion dans `main` requiert son autorisation explicite.

## Références et décisions

Le besoin métier est défini dans le **Cahier des charges fonctionnel v4** du projet Transport. La **Proposition technique V1 du 20 novembre 2026**, fichier `Transport_Proposition_technique_V1.pdf` du projet, précise l’architecture et les épreuves. Ces deux références guident le développement. Le texte intégral du v4 reste à retrouver pour vérifier la couverture article par article. Les preuves et les écarts identifiés figurent dans la [matrice de recette](RECETTE-LOT-1.md).

Le lot 1 réunit l’ouverture d’une compagnie, la gestion des accès, le guichet et la caisse, le fret, le site de réservation, les paiements et l’installation. Le lot 2 porte sur l’exploitation de la flotte, le GPS et la maintenance. Le lot 3 porte sur la finance et la gestion avancée. Le premier terrain de qualification utilise des compagnies ivoiriennes fictives, des montants en XOF et un compte marchand simulé par compagnie.

Le guichet Windows WPF et les portails web appellent la même API ASP.NET Core. Chaque compagnie a sa base PostgreSQL. L’éditeur ouvre une installation, la compagnie administre ses agents, les postes et agents autorisés réalisent les opérations d’agence. Conserver ces frontières lors de l’ajout de fonctionnalités.

## Étapes d’avancement

| Étape | Parcours et preuve | Reste à qualifier |
|---|---|---|
| Guichet et caisse | Option par siège et tronçon, encaissement espèces, clôture, bagages, embarquement et manifeste. Voir [tests guichet](tests/test_counter.py). | Remises de fonds, remboursements supervisés, secours hors ligne et matériel terrain. |
| Fret | Pré-enregistrement, dépôt, mesure, port payé ou dû, chargement, arrivée, remise et suivi. Voir [tests multi-agences](tests/test_companies.py). | Transferts, litiges, étiquette physique et services de notification. |
| Accès et administration de compagnie | Deux bases distinctes, rattachement des postes aux agences, agents nominatifs, habilitations, portail administrateur avec TOTP. [Qualification du 21 novembre](https://github.com/siakajoseph/Transport/actions/runs/44896177046). | Accès support autorisé et audité, administration éditeur complète. |
| Réservation invitée | Choix du trajet et du siège sur mobile, tarif central et reprise d’option. [Qualification du 22 novembre](https://github.com/siakajoseph/Transport/actions/runs/44969408863). | Notifications et espace client. |
| Paiement marchand de test | Paiement simulé, confirmation rejouable, billet QR, contrôle d’embarquement unique. Les montants incohérents ou confirmations tardives sont placés en attente. [Qualification du commit 5ee3af2](https://github.com/siakajoseph/Transport/actions/runs/45004455999). | Traitement des écarts et des remboursements, raccordement au prestataire et rapprochement des reversements. |
| Ouverture éditeur | Migration 017 et commande d’exploitation pour ouvrir une base de compagnie, créer sa première entité et rattacher la première agence à un arrêt. La [suite multi-compagnies](tests/test_companies.py) utilise cette commande avant la première vente. | Assistant web éditeur, modules, abonnements et support. |
| Installation | La CI construit le MSI Windows, installe le guichet, le lance et le désinstalle. | Qualification d’une installation intégrée sur poste neuf avec API, base, portail et guichet configurés ensemble. |

## Reprendre le développement

1. Lire cette mémoire et la matrice, puis vérifier la tête de la PR et le résultat des trois travaux GitHub Actions. Repartir du commit réellement présent sur la branche.
2. Poursuivre le parcours éditeur depuis l’ouverture en exploitation vers un assistant authentifié. Préserver l’indépendance des administrateurs de compagnie et des agents. Raccorder ensuite la gestion des écarts marchands et qualifier l’installation intégrée.
3. Pour chaque parcours, relever le scénario nominal, le refus attendu en cas d’accès hors périmètre et le comportement après reprise. Ajouter les tests, actualiser la matrice et inscrire dans cette mémoire le commit ou l’exécution de référence.
4. Avant d’annoncer la recette, vérifier sur données fictives le parcours complet depuis l’ouverture de compagnie jusqu’aux ventes guichet et web, au traitement d’un colis et à la clôture. Publier les limites explicites et mettre la tâche en pause à ce stade, ou si une décision de l’utilisateur devient indispensable.
