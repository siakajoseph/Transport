# Recette intégrée du lot 1

État de développement sur données fictives. Les essais Windows automatisés couvrent installation, lancement et désinstallation. La recette utilisateur aura lieu sur la version intégrée.

| Parcours | Vérifié en intégration | Travaux ouverts |
|---|---|---|
| Guichet et caisse | Concurrence siège/tronçon, expiration, reprise espèces, clôture, bagages, embarquement et manifeste | Comptes agents, duplicatas tracés, remises de fonds, remboursement supervisé, mode secours |
| Fret | Devis, pré-enregistrement, dépôt mesuré, port payé et dû, rapprochement des recettes de dépôt | Qualification de l’étiquette QR sur imprimante, contrôle des marchandises et capacité fret, chargement, transferts, retrait sécurisé, encaissement du port dû, livraison et litiges |
| Site passagers | Inventaire des sièges et tarifs disponibles via API | Site par compagnie, achat invité, paiement marchand, notifications, espace client |
| Socle et éditeur | Départs programmés, routes et tarifs de démonstration | Cloisonnement compagnie/agence, rôles, accès support autorisé, paramétrage, abonnements, exports, personnalisation des documents |
| Exploitation | Construction API et MSI, suite automatisée sur PostgreSQL | Sauvegarde/restauration ciblée, charge, continuité, fiscalité pays, audit indépendant et qualification matérielle |

Critère de passage en recette intégrée : tracer l’ouverture d’une compagnie jusqu’à la vente guichet et web, le dépôt et la remise d’un colis, puis le rapprochement de caisse. Vérifier le cloisonnement avec deux compagnies et l’absence de double encaissement lors des reprises.
