# Recette intégrée du lot 1

État de développement sur données fictives. Les essais Windows automatisés couvrent installation, lancement et désinstallation. La recette utilisateur aura lieu sur la version intégrée.

| Parcours | Vérifié en intégration | Travaux ouverts |
|---|---|---|
| Guichet et caisse | Concurrence siège/tronçon, expiration, reprise espèces, clôture, bagages, embarquement et manifeste | Duplicatas tracés, remises de fonds, remboursement supervisé, mode secours |
| Fret | Devis, pré-enregistrement, dépôt mesuré, port payé et dû, chargement et arrivée par scan, contrôle du code au retrait, port dû en caisse de destination, plafonds de poids et de volume, manifeste fret figé au départ, suivi client par référence | Qualification de l’étiquette QR sur imprimante, contrôle des marchandises, qualification terrain de la capacité attribuée au fret, transferts, envoi SMS réel, livraison à domicile et litiges |
| Site passagers | Sélection du départ, du tronçon et du siège sur le site de compagnie, option invité sur le stock partagé avec le guichet, billet marchand de test | Opérateur réel, notifications, espace client |
| Socle et éditeur | Départs programmés, routes, tarifs, séparation des bases compagnie, agents nominatifs, caisse et vente limitées au poste et aux arrêts autorisés, administration de compagnie avec TOTP | Administration centrale, extension du support, paramétrage, abonnements, exports, personnalisation des documents |
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
| 1 — Éditeur | Ouvrir une compagnie, configurer ses agences et ses modules, appliquer sa politique d’abonnement, tracer un accès support autorisé. | Ouverture initiale par commande d’exploitation et premier poste en agence. L’assistant web sur mandat réutilise cette ouverture. La souscription de la vente web est appliquée à l’ouverture. Le support peut consulter un diagnostic sur accord de la compagnie. Facturation et autres modules restent ouverts. |
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

Le journal du simulateur est consulté côté serveur. Restent ouverts le raccordement au prestataire externe, ses notifications authentifiées, les remboursements effectifs et le rapprochement avec les reversements réels.


### Ouverture de compagnie par l’exploitant

Sur une base isolée, initialisée en profil Company, appliquer au besoin la migration 017 après la 016. L’exploitant autorisé exécute le script psql [ouvrir-compagnie.sql](ops/ouvrir-compagnie.sql) avec l’UUID de la compagnie, ses noms commercial et juridique, le pays, la première agence, le modèle de voyage et l’arrêt. La commande renvoie l’UUID de l’agence pour l’inscription du poste et de l’agent. L’ouverture, ses paramètres, son horodatage et le compte SQL d’exécution sont conservés dans `company_opening`.

Les tests ouvrent deux compagnies, rejouent concurremment chaque demande et utilisent les agences créées pour vendre au guichet. Ils contrôlent aussi le refus d’un UUID de compagnie étrangère ou d’une seconde demande incompatible. L’opération utilise les autorisations SQL de déploiement existantes. Le parcours web associe à cette ouverture le mandat de son destinataire.


### Assistant éditeur sur mandat

| Critère de recette | Vérification |
|---|---|
| Ouverture dans le navigateur | Un exploitant autorisé émet un mandat pour une base Company préparée. Son destinataire sélectionne l’arrêt et crée la première agence depuis `/editeur/index.html`. Le reçu donne l’UUID de l’agence. |
| Autorisation | Le mandat expire après vingt minutes. Une révocation prend effet à la requête suivante. Le jeton d’une autre compagnie, la session administrateur de compagnie et les identifiants guichet sont refusés. |
| Traçabilité et reprise | Le destinataire déclaré et le compte SQL émetteur sont enregistrés. Une reprise identique avec le même mandat rend le même reçu. Un mandat concurrent ne peut pas s’attribuer une ouverture existante. |

**Exploitation.** Sur une base existante, appliquer la migration 018 après la 017. Pour une nouvelle ouverture, exécuter [mandater-editeur.sql](ops/mandater-editeur.sql) avec les variables psql `company_id` et `operator_name`. Communiquer la clé affichée uniquement au destinataire. Ouvrir l’assistant via HTTPS ou sur la boucle locale. Pour révoquer le mandat, renseigner `publisher_mandate.revoked_at` pour son UUID. Fermer la session du navigateur efface la clé de la page. L’authentification repose sur la possession du mandat, dont le nom du destinataire est déclaré par l’émetteur. Le compte éditeur central avec authentification multifacteur, le portail central de gestion des compagnies et la facturation restent ouverts.

**Preuves.** [Parcours navigateur et refus d’accès](tests/test_publisher.py), [contrôle des identifiants compagnie](tests/test_admin.py), [vente au premier guichet](tests/test_companies.py).


### Catalogue éditeur et souscription de la vente web

Sur une base existante, appliquer `db/019-abonnement-web.sql` après la 018. La migration conserve la vente web active pour les installations antérieures. La première souscription sur mandat s’applique au moment de l’ouverture. L’exploitant précise `-v web_included=false` ou `true` lors de l’émission. Le défaut historique est `true`. L’assistant affiche la décision inscrite au mandat. Une modification ultérieure passe par [modifier-abonnement-web.sql](ops/modifier-abonnement-web.sql) avec l’UUID de compagnie, la révision attendue, la décision et son motif. Chaque changement effectif est journalisé.

| Critère | Contrôle intégré |
|---|---|
| Vente web non souscrite | L’interface affiche la fermeture. L’API répond 403 à une nouvelle option et la transaction libère le siège. Le guichet de la compagnie peut vendre. |
| Retrait après réservation | Une option valide créée avant le retrait conserve son échéance. Sa reprise et son paiement simulé aboutissent au billet. Une nouvelle option est refusée. |
| Modification concurrente | Une écriture sur une révision périmée échoue. Une demande sans changement sur la version courante conserve la révision et le nombre d’événements. |
| Catalogue des compagnies ouvertes | Le lecteur autorisé consolide les fiches de deux bases. Le test vérifie le refus de lecture des réservations par son compte SQL et le rejet d’un doublon sans émission d’un catalogue partiel. |

**Lecture du catalogue.** Configurer un service PostgreSQL local par compagnie avec un compte dédié ayant `CONNECT` sur la base, `USAGE` sur le schéma public et `SELECT` sur la seule vue `public.publisher_catalog_entry`. Lancer `python3 ops/catalogue-compagnies.py compagnie_a compagnie_b` avec le client `psql` disponible. Le JSON contient l’identifiant, le nom, la date d’ouverture, le statut de la vente web et la révision. Les services sont déclarés par l’exploitant. Chaque base est lue à l’instant de la consultation. L’interface web centrale, les autres modules et la facturation restent à développer.

**Preuves.** [Souscriptions, vente et catalogue en lecture seule](tests/test_subscriptions.py), [souscription présentée au destinataire du mandat](tests/test_publisher.py).


### Rapprochement et remboursement simulé

Sur base existante, appliquer la migration 020 après la 019. Dans une compagnie de test ayant activé le simulateur, l’exploitant habilite ou révoque individuellement un administrateur avec [habiliter-rapprochement-test.sql](ops/habiliter-rapprochement-test.sql), en indiquant `company_id`, `login` et `enabled`. L’accès exige aussi une session administrateur valide avec second facteur. L’habilitation est désactivée par défaut.

| Critère | Épreuve |
|---|---|
| Contrôler un écart | Le portail présente les 50 plus anciens paiements en attente, le montant reçu, la devise et le tarif attendu. L’opérateur saisit un motif. |
| Rembourser en simulation | La décision inscrit un mouvement au montant et dans la devise du reçu, avec référence, date, opérateur et motif. Seule l’option d’origine encore active est libérée. Les circonstances de l’écart sont conservées dans le reçu initial. |
| Reprendre après interruption | Deux validations identiques par le même opérateur renvoient une référence unique. Une décision différente est refusée. Le voyageur retrouve le statut remboursé et sa référence depuis son paiement. |
| Contrôler l’accès | Une session de compagnie étrangère est refusée. Une révocation individuelle empêche la consultation et la validation au prochain appel. |

[Qualification de bout en bout](tests/test_refunds.py). Ces mouvements restent dans le journal du simulateur de la compagnie. La connexion au prestataire de paiement et les reversements réels restent ouverts.

### Lot 1 — suivi du colis par son destinataire

| Critère | Preuve de recette |
|---|---|
| Transmettre le suivi après préinscription | Le site fret présente un lien de la compagnie que l’expéditeur peut copier. Son ouverture affiche les étapes enregistrées avec leur date et heure GMT. |
| Relier réception, départ et remise | Le [parcours deux agences](tests/test_companies.py) vérifie les six étapes après remise contre paiement, le rapprochement en caisse de destination et l’ouverture du lien sur navigateur mobile. |
| Limiter les informations publiques | La réponse historique contient le statut, la zone et les événements (statut et horodatage). La référence est introuvable sur le site de l’autre compagnie. Une recherche infructueuse efface l’historique affiché. |

Le lien donne accès au suivi à toute personne qui le détient. La notification par SMS et la qualification du matériel en agence restent ouvertes.

### Lot 1 — dernier siège partagé entre web et guichet

[Scénario concurrent](tests/test_contention.py) sur une compagnie fictive et un départ à un siège. Une barrière libère 92 demandes guichet et 8 demandes web distinctes. Le nombre de demandes web respecte le quota public par adresse. Le test exige un succès et 99 conflits métier, encaisse le gagnant par son canal, redémarre l’API de compagnie, rejoue la confirmation et vérifie un billet de 6 000 XOF en base. La mesure des percentiles sous charge à 3 000 utilisateurs reste ouverte.

### Continuité — restauration isolée d’une compagnie

L’exploitant sauvegarde la base avec `pg_dump -Fc` et exécute `bash ops/restaurer-compagnie.sh ARCHIVE UUID_COMPAGNIE transport_reprise_nom` sur un cluster de reprise préparé avec les rôles nécessaires. Utiliser un cluster isolé et une archive de confiance. La commande crée une nouvelle base, restaure les données puis vérifie l’identité avant toute bascule. En cas d’échec, la base candidate reste disponible pour diagnostic.

La [qualification PostgreSQL](tests/restauration.sh) compare chaque billet, réservation, canal et montant du journal des ventes après restauration. Elle vérifie le refus d’une identité étrangère et la protection d’une base candidate existante. La campagne porte sur des données fictives sans écritures concurrentes durant la comparaison. L’archivage continu, le RPO de cinq minutes, la bascule après panne imprévue et la mesure du RTO en volumétrie cible restent ouverts.

**Bascule planifiée sur données fictives.** Le test arrête l’API de la compagnie avant la sauvegarde et la relance à la même adresse avec `TRANSPORT_API_DATABASE` pointant vers la base restaurée. Il retrouve le billet signé après rejeu d’une confirmation marchande, vérifie les deux journaux des ventes, préenregistre un nouveau colis et contrôle son absence dans la base source et sur le site de l’autre compagnie. Le profil Company continue d’exiger une authentification au poste. [Parcours API après reprise](tests/reprise_api.py).

Ce scénario utilise un seul serveur PostgreSQL avec deux bases. La perte du serveur, la restauration sur un autre hôte et la reprise des opérations survenues après la sauvegarde restent à qualifier.

### Fret — récépissé du dépôt

Depuis le dossier déposé, le guichet d’origine peut imprimer le récépissé du client avec la compagnie, le trajet, la référence QR, les poids mesuré et facturable, l’heure et le port enregistré au dépôt. Le libellé distingue l’encaissement à l’origine du montant dû à destination lors du dépôt. La réimpression relit le dossier sans déclencher de mouvement de caisse. Le [parcours inter-agences](tests/test_companies.py) vérifie les valeurs après un dépôt en port dû, la relecture et les restrictions de rôle et d’agence. La mise en page sur imprimante physique reste à qualifier.

### Correspondance avec le cahier fonctionnel v4

| Articles | Parcours couvert | Critères encore ouverts |
|---|---|---|
| §2.1–2.6, §6–7, §9.1 | Bases par compagnie, droits par agence, mandat éditeur, souscription web, diagnostic support autorisé et tracé | Export intégral, marque blanche, versions des documents |
| §8.1–8.3, §9.2–9.3 | Stock partagé, vente espèces, paiement marchand fictif, billet et embarquement | Espace client et prestataire externe |
| §8.4–8.9 | Remboursement simulé des écarts, caisse et impression Windows | Perturbations, secours hors ligne, duplicata tracé, notifications et remises de fonds |
| §9.4 | Recherche par téléphone, poids facturable, dépôt, récépissé, chargement, remise et suivi | Règles par pays, contre-remboursement, transferts et livraison à domicile |
| §4, §11 | Concurrence sur le dernier siège et reprise planifiée | Charge cible et reprise après perte d’un serveur |
| §9.5–9.6, §9.8 | Lot 2 — exploitation, GPS et chauffeur | À qualifier au lot 2 |
| §9.7, §9.9–9.10 | Lot 3 — finance, service client et clientèle professionnelle | À qualifier au lot 3 |

**§9.4 — retrouver un contact.** En profil Company, un agent fret habilité recherche un numéro complet parmi les dépôts de son agence, pour le rôle choisi (expéditeur ou destinataire). Le guichet propose le nom à vérifier. Si plusieurs noms figurent pour ce numéro et ce rôle, l’agent doit saisir le nom confirmé. Le [test inter-agences](tests/test_companies.py) vérifie les deux rôles, l’absence de résultat dans l’autre agence et les refus d’accès.

### §9.4 — contrôle de la marchandise au dépôt

Appliquer `db/021-marchandises.sql` après la migration 020. L’exploitant configure dans chaque base compagnie les libellés de `freight_goods` et leur autorisation `permitted`. Dès la première entrée, la déclaration devient obligatoire. L’agent sélectionne le libellé au guichet. Le serveur contrôle la règle au moment du dépôt. Une marchandise inconnue ou interdite bloque l’enregistrement, y compris si le catalogue a changé depuis la consultation du dossier. Un catalogue vide conserve le fonctionnement antérieur sans filtrage.

La [recette inter-agences](tests/test_companies.py) utilise des règles fictives. Elle vérifie les refus, le dépôt autorisé, la conservation de la déclaration et la reprise identique après une interdiction ultérieure. Les listes réglementaires réelles par pays, la déclinaison des règles par entité et la vérification physique du contenu restent ouvertes.

**§9.4 — paramétrage par la compagnie.** Après la migration 022, un administrateur connecté avec second facteur peut ajouter un libellé, autoriser ou interdire son dépôt depuis « Marchandises au dépôt fret ». Le portail et le guichet utilisent le même catalogue. Chaque modification du portail conserve l’auteur, la date, la révision et les décisions précédente et nouvelle. Une révision périmée est refusée. Le [test administrateur](tests/test_admin.py) exerce la saisie au navigateur, les écritures concurrentes, la séparation des compagnies et la persistance du journal. Les règles s’appliquent à toutes les agences de la compagnie.

**§9.1 et §9.4 — consultation de l’audit fret.** Depuis la fiche d’une marchandise dans le catalogue, l’administrateur consulte les changements du portail, du plus récent au plus ancien, par pages de 50. Chaque ligne présente la révision, la date, le compte et l’évolution de la décision. La [recette administrateur](tests/test_admin.py) contrôle la création et la lecture dans le navigateur, la succession des décisions, le curseur par révision et le refus des sessions étrangères. Les modifications SQL directes antérieures restent hors de cet historique.

**§8.3 et §9.3 — conserver le billet web.** Après confirmation du paiement simulé, le site présente le nom du voyageur, la compagnie, le tronçon, l’heure de départ GMT, le siège, le montant payé et le QR signé. Le bouton d’impression permet d’enregistrer un PDF via le navigateur, y compris après reprise du paiement dans le même onglet. La [recette navigateur](tests/test_merchant.py) produit une page PDF et décode son QR pour le comparer au jeton émis. L’envoi par SMS, WhatsApp ou courriel et le prestataire marchand réel restent ouverts.

**§8.1 et §9.3 — échéance de l’option web.** Le site affiche les minutes et secondes restantes à partir de l’échéance et de l’horloge de la base. À expiration, la page propose de reconsulter le stock et retire le bouton de paiement. Une tentative de paiement déjà engagée conserve son parcours de rapprochement. Le [test navigateur](tests/test_reservations.py) utilise une option fictive de quatre secondes et vérifie que le siège redevient disponible sans encaissement.

**§8.1–8.2 — reprise avant paiement.** Le navigateur conserve dans l’onglet la référence de l’option confirmée. Au rechargement, la compagnie relit l’option active et restitue son siège, son montant et son échéance initiale. La consultation ne prolonge ni ne recrée l’option. Le [parcours marchand](tests/test_merchant.py) recharge l’onglet avant paiement, contrôle la référence et l’isolation inter-compagnies puis récupère le billet après règlement simulé. Le [test des options](tests/test_reservations.py) vérifie le refus après expiration. La récupération sur un autre appareil via un compte client reste ouverte.

**§9.4 — reçu de retrait en agence.** Après la remise, l’agent fret de destination peut imprimer le reçu depuis la référence du colis, même si la caisse est clôturée. Le montant correspond aux espèces reçues à destination. Le [test inter-agences](tests/test_companies.py) vérifie l’heure et le montant de la remise, les refus d’accès et la relecture sans nouvel encaissement. La sortie papier sur le matériel cible reste à qualifier.

### §2 et §9.1 — diagnostic du support éditeur sur accord de la compagnie

Appliquer `db/023-support.sql` puis `db/024-audit-support.sql` et `db/025-comptes-support.sql` après la migration 022. En profil Company avec administration TOTP, l’exploitant inscrit au préalable le compte nominatif du technicien avec `dotnet Transport.Api.dll support-init identifiant` sur le serveur, avec mot de passe et application TOTP. Il exécute ensuite [demander-diagnostic.sql](ops/demander-diagnostic.sql) dans la base concernée avec les variables psql `company_id` (UUID attendu), `technician` (identifiant du compte support), `station` (poste déclaré), `reason` et `minutes` (1 à 60, 60 par défaut). Il remet en privé la clé à ce technicien. Seule son empreinte est conservée en base. L’attribution du nom et la remise de la clé relèvent de l’exploitant.

Dans « Accès du support éditeur », l’administrateur voit le nom, le motif et l’échéance. Il peut autoriser une consultation ou retirer l’accès. Le technicien ouvre `/editeur/support.html` sur le site de cette compagnie et présente son mot de passe, son code TOTP et la clé de la demande. Le diagnostic indique le nom, le profil du serveur, le nombre d’agences et le nombre de postes actifs. La consultation consomme la clé et inscrit son horodatage. Si la réponse est perdue après validation, la compagnie voit la consultation dans son historique et peut examiner une nouvelle demande. Le portail présente les 100 demandes les plus récentes.

| Critère de recette | Preuve |
|---|---|
| Accord préalable, limité à une compagnie, jusqu’à l’échéance choisie, au plus une heure après la demande | [Test des autorisations](tests/test_support.py) avec deux compagnies, refus avant accord, après révocation et après expiration |
| Accès unique, enregistré avec la décision de l’administrateur | Deux lectures simultanées, une réponse avec diagnostic, même date côté support et côté compagnie |
| Parcours navigateur | Autorisation depuis le portail de compagnie puis consultation sur la page du technicien avec une clé fictive |

Le périmètre de ce mandat est un diagnostic de configuration en lecture seule. Les interventions correctives, l’authentification fédérée des techniciens et la qualification opérationnelle du support restent ouvertes.

**Traçabilité du support (proposition technique V1, p. 6).** L’accord, le retrait et la lecture conservent l’adresse réseau observée par l’API, avec leur date. Derrière un proxy, cette adresse peut être celle du relais. Le poste déclaré à l’émission est associé à la clé. Son identité matérielle reste à qualifier. Les anciennes demandes sans compte support rattaché nécessitent une nouvelle émission. Les jetons des comptes support et administrateur sont limités à leurs espaces respectifs. Le chaînage des empreintes et l’archivage hors du domaine d’administration restent ouverts.
