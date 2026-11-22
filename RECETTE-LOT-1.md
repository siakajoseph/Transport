# Recette intégrée du lot 1

État de développement sur données fictives. Les essais Windows automatisés couvrent installation, lancement et désinstallation. La recette utilisateur aura lieu sur la version intégrée.

| Parcours | Vérifié en intégration | Travaux ouverts |
|---|---|---|
| Guichet et caisse | Concurrence siège/tronçon, expiration, reprise espèces, clôture, bagages, embarquement et manifeste | Duplicatas tracés, remises de fonds, remboursement supervisé, mode secours |
| Fret | Devis, pré-enregistrement, dépôt mesuré, port payé et dû, chargement et arrivée par scan, contrôle du code au retrait, port dû en caisse de destination, plafonds de poids et de volume, manifeste fret figé au départ, suivi client par référence | Qualification de l’étiquette QR sur imprimante, contrôle des marchandises, qualification terrain de la capacité attribuée au fret, transferts, envoi SMS réel, livraison à domicile et litiges |
| Site passagers | Sélection du départ, du tronçon et du siège sur le site de compagnie, option invité sur le stock partagé avec le guichet, billet marchand de test | Opérateur réel, notifications, espace client |
| Socle et éditeur | Départs programmés, routes, tarifs, séparation des bases compagnie, agents nominatifs, caisse et vente limitées au poste et aux arrêts autorisés, administration de compagnie avec TOTP | Administration éditeur, accès support autorisé, paramétrage, abonnements, exports, personnalisation des documents |
| Exploitation | Construction API et MSI, suite automatisée sur PostgreSQL | Sauvegarde/restauration ciblée, charge, continuité, fiscalité pays, audit indépendant et qualification matérielle |

Critère de passage en recette intégrée : tracer l’ouverture d’une compagnie jusqu’à la vente guichet et web, le dépôt et la remise d’un colis, puis le rapprochement de caisse. Vérifier le cloisonnement avec deux compagnies et l’absence de double encaissement lors des reprises.

Le profil Company couvre la compagnie, l’entité, l’agence, les arrêts de vente et le poste. La CI lance deux instances de compagnie et trois postes d’agence. Elle contrôle la vente indépendante du même siège dans deux bases, l’affectation des caisses et des billets, le suivi public du colis sur le site de sa compagnie et la révocation d’un poste. Le profil Demo conserve les parcours de développement historiques. Les essais nominatifs du fret et de l’embarquement sont décrits ci-dessous.

La v0.12 étend le profil Company aux agents nominatifs. La qualification utilise plusieurs agents au même guichet et deux agences desservant le départ et la destination. Elle vérifie les refus de caisse croisée, les droits de scan et de départ, la portée du manifeste de travail, le dépôt et la remise au bon arrêt, la signature de l’agent et la révocation individuelle.

**Administration de compagnie.** La qualification exerce l’inscription avec mot de passe et TOTP, le refus du rejeu du code, la séparation entre compagnies, l’isolation des identifiants guichet et administrateur, les modifications concurrentes et la révocation à la déconnexion. Le parcours navigateur crée un agent, utilise sa clé au guichet via l’API, désactive le compte et vérifie l’effet sur la prochaine requête.

### Site passagers de compagnie — option invité

| Exigence du lot 1 | Critère de recette | Preuve ou limite |
|---|---|---|
| Réservation web | Sur `/reservation/index.html`, sélectionner un départ, un tronçon et un siège. Le serveur renvoie la compagnie, le tarif central, la référence invité et l’échéance. | Test navigateur mobile sur l’API de compagnie et contrôle de l’option dans PostgreSQL (`test_browser_reservation`). |
| Inventaire commun | Deux soumissions identiques, même simultanées, rendent la même option. Le guichet refuse le siège sur le tronçon occupé et le récupère à expiration. | `tests/test_reservations.py`, profil Company sur PostgreSQL |
| Cloisonnement | Deux compagnies peuvent réserver le même siège indépendamment. La réservation invitée ne donne aucun accès à la caisse. | Tests guest et compagnies dans la qualification du guichet |
| Vente web payée | Confirmation du prestataire de test sur le compte marchand de la compagnie, billet unique, embarquement et rapprochement marchand, traitement des confirmations tardives. | Simulateur local et tests de confirmation décrits ci-dessous. Le remboursement effectif reste ouvert. |

Sur une base de développement existante, appliquer `db/015-option-web.sql` après la migration 014. Compose initialise automatiquement les bases neuves. La recette utilisateur intégrée reste ouverte.

### Périmètre de recette des trois lots

Les épreuves ci-dessous reprennent la proposition technique V1, rubriques « Lot et activité » et « Dimensionnement et recette ». La correspondance exhaustive avec les articles numérotés du cahier v4 reste à établir.

| Lot | Parcours à qualifier | État de la preuve |
|---|---|---|
| 1 — Éditeur | Ouvrir une compagnie, configurer ses agences et ses modules, appliquer sa politique d’abonnement, tracer un accès support autorisé. | Ouverture initiale par commande d’exploitation et premier poste en agence. L’assistant web éditeur, les modules, l’abonnement et le support restent ouverts. |
| 1 — Concurrence et reprise | 100 demandes guichet/web sur le même siège, un seul gagnant. Vérifier rappel marchand rejoué, montant incorrect, confirmation tardive et reprise après arrêt. | Concurrence guichet et reprise espèces couvertes. Les options web partagent l’inventaire. Rejeux et écarts marchands testés sur simulateur. Charge mixte à qualifier. |
| 1 — Coupure et matériel | Sur deux postes isolés, vendre le dernier droit alloué, franchir l’échéance H2, reconnecter et rapprocher. Vérifier imprimante, balance et scanner. | Ouvert. Le lancement du MSI en CI couvre le poste Windows. |
| 1 — Charge et continuité | 3 000 utilisateurs simultanés, pics ×3 puis ×5, p95 < 300 ms et p99 < 1 s. Restaurer une compagnie et rapprocher ses ventes avec RPO 5 min et RTO < 1 h. | Mesures et exercice chronométré ouverts. |
| 2 — Exploitation | Affecter véhicule et chauffeur, suivre la mission et les positions GPS, rapprocher carburant et maintenance. | Lot ultérieur. |
| 3 — Gestion | Générer une écriture équilibrée par événement, rapprocher les mouvements par entité, clôturer puis corriger par contre-écriture. | Lot ultérieur. L’export des mouvements opérationnels relève déjà du lot 1. |

### Paiement par compte marchand fictif

Activer `TRANSPORT_SANDBOX_PAYMENTS=true` exclusivement sur une installation de test avec données fictives. La valeur par défaut désactive les routes de simulation. Sur base existante, appliquer `db/016-marchand-test.sql` après la migration 015. Le simulateur local représente un compte marchand par compagnie et utilise le tarif central. Il ne communique avec aucun opérateur.

| Critère | Parcours de qualification dans `tests/test_merchant.py` |
|---|---|
| Option → paiement → billet | Le navigateur mobile paie, retrouve le code signé après rechargement et peut le présenter au contrôle. |
| Reprise et cloisonnement | Quatre confirmations simultanées renvoient le même billet. Une autre compagnie ou une autre option ne peut pas récupérer la transaction. |
| Rapprochement et embarquement | Le reçu de 6 000 XOF et le billet sont liés. La vente reste hors des sessions espèces. Le test décode le QR PNG du billet et transmet le résultat au contrôle. Un agent habilité valide le premier scan et le second est refusé. |
| Écarts et retard | Une confirmation après réattribution du siège, un montant différent ou une autre devise laissent le reçu en attente de traitement, sans émission de billet. |

Le journal du simulateur est consulté côté serveur. Restent ouverts le raccordement au prestataire externe, ses notifications authentifiées, le traitement opérateur des écarts et remboursements et le rapprochement avec les reversements réels.


### Ouverture de compagnie par l’exploitant

Sur une base isolée, initialisée en profil Company, appliquer au besoin la migration 017 après la 016. L’exploitant autorisé exécute le script psql [ouvrir-compagnie.sql](ops/ouvrir-compagnie.sql) avec l’UUID de la compagnie, ses noms commercial et juridique, le pays, la première agence, le modèle de voyage et l’arrêt. La commande renvoie l’UUID de l’agence pour l’inscription du poste et de l’agent. L’ouverture, ses paramètres, son horodatage et le compte SQL d’exécution sont conservés dans `company_opening`.

Les tests ouvrent deux compagnies, rejouent concurremment chaque demande et utilisent les agences créées pour vendre au guichet. Ils contrôlent aussi le refus d’un UUID de compagnie étrangère ou d’une seconde demande incompatible. L’opération utilise les autorisations SQL de déploiement existantes. Le parcours web éditeur et l’identification nominative de l’opérateur éditeur restent à réaliser.
