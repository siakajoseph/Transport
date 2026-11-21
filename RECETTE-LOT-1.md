# Recette intégrée du lot 1

État de développement sur données fictives. Les essais Windows automatisés couvrent installation, lancement et désinstallation. La recette utilisateur aura lieu sur la version intégrée.

| Parcours | Vérifié en intégration | Travaux ouverts |
|---|---|---|
| Guichet et caisse | Concurrence siège/tronçon, expiration, reprise espèces, clôture, bagages, embarquement et manifeste | Comptes agents, duplicatas tracés, remises de fonds, remboursement supervisé, mode secours |
| Fret | Devis, pré-enregistrement, dépôt mesuré, port payé et dû, chargement et arrivée par scan, contrôle du code au retrait, port dû en caisse de destination, plafonds de poids et de volume, manifeste fret figé au départ, suivi client par référence | Qualification de l’étiquette QR sur imprimante, contrôle des marchandises, qualification terrain de la capacité attribuée au fret, transferts, envoi SMS réel, habilitations agence, livraison à domicile et litiges |
| Site passagers | Inventaire des sièges et tarifs disponibles via API | Site par compagnie, achat invité, paiement marchand, notifications, espace client |
| Socle et éditeur | Départs programmés, routes, tarifs, séparation des bases compagnie, caisse et vente limitées au poste et aux arrêts autorisés | Habilitations des autres métiers, rôles, accès support autorisé, paramétrage, abonnements, exports, personnalisation des documents |
| Exploitation | Construction API et MSI, suite automatisée sur PostgreSQL | Sauvegarde/restauration ciblée, charge, continuité, fiscalité pays, audit indépendant et qualification matérielle |

Critère de passage en recette intégrée : tracer l’ouverture d’une compagnie jusqu’à la vente guichet et web, le dépôt et la remise d’un colis, puis le rapprochement de caisse. Vérifier le cloisonnement avec deux compagnies et l’absence de double encaissement lors des reprises.

Le profil Company couvre la compagnie, l’entité, l’agence, les arrêts de vente et le poste. La CI lance deux instances de compagnie et trois postes d’agence. Elle contrôle la vente indépendante du même siège dans deux bases, l’affectation des caisses et des billets, le suivi public du colis sur le site de sa compagnie et la révocation d’un poste. Le profil Demo conserve les parcours de développement historiques. Les droits nominatifs, les habilitations fret et embarquement restent ouverts avant la recette utilisateur.

La v0.12 étend le profil Company aux agents nominatifs. La qualification utilise plusieurs agents au même guichet et deux agences desservant le départ et la destination. Elle vérifie les refus de caisse croisée, les droits de scan et de départ, la portée du manifeste de travail, le dépôt et la remise au bon arrêt, la signature de l’agent et la révocation individuelle.
