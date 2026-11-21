# Transport · Guichet Windows

Premier incrément de billetterie. Le jeu de démonstration contient une compagnie fictive, 32 sièges et un départ Abidjan → Yamoussoukro → Bouaké, sept jours après la première initialisation. Chaque tronçon a un tarif en FCFA. Le guichet WPF demande une option de huit minutes puis confirme la réception d’espèces. Le serveur émet une référence de billet et journalise le montant.

## Démarrer sur Windows 11 x64 avec Docker Desktop et WSL2

Dans WSL, depuis le répertoire du dépôt, créer une fois le mot de passe de la base locale puis démarrer le serveur. Conserver le fichier .env avec le volume de données.

```bash
test -f .env || (umask 077 && echo "TRANSPORT_DB_PASSWORD=$(openssl rand -hex 24)" > .env)
docker compose up -d --build
curl http://127.0.0.1:5080/api/trajets
```

Télécharger l’archive **Transport-Guichet-Windows-x64** du workflow vert dans l’onglet Actions. Extraire le MSI et l’installer sur le même PC que Docker Desktop. Lancer **Transport Guichet Démo** depuis le menu Démarrer. Le guichet contacte l’API locale sur le port 5080. Choisir le tronçon, le siège et le nom du passager. Réserver, puis cliquer sur la confirmation après réception des espèces. Une reprise sur la même option renvoie la même référence.

Pour compiler soi-même le MSI, installer le SDK .NET 10 x64 sur Windows et lancer `./installer/build.ps1` dans PowerShell depuis le dépôt. Le paquet de développement est autonome et non signé.

## Données et validation

PostgreSQL arbitre les conflits avec une contrainte d’exclusion par intervalle. Le serveur calcule le prix, vérifie l’échéance et sérialise réservation et paiement sur le siège concerné. La vente et l’écriture de caisse partagent une transaction. Après interruption du guichet, la référence de l’option est conservée dans le profil Windows. Les options échues sont immédiatement libres en lecture et nettoyées à la réservation suivante.

La CI lance 100 demandes pour le même siège, rejoue 24 confirmations, vérifie le journal en base, les tronçons adjacents et l’expiration. Un second travail compile le MSI, l’installe, lance l’application et le désinstalle. Pour exécuter les tests localement, utiliser une copie du dépôt dans un dossier distinct et un projet Compose neuf, avec le port 5080 libre. Les tests écrivent dans la base de démonstration. Pour arrêter le serveur en conservant les ventes, utiliser `docker compose stop`.

## Portée de cette étape

Démonstration locale sur données fictives, sans authentification. Le port de l’API est publié uniquement sur la boucle locale. Le volume conserve les ventes entre redémarrages. Le premier départ de démonstration se clôt à l’heure enregistrée. Le prochain incrément doit ajouter sessions de caisse par agent, gestion des départs, authentification des postes et des agents, billet QR et impression thermique. Les quotas hors ligne, les paiements électroniques, le mode multi-compagnies et les autres modules restent à développer avant exploitation commerciale.

## Version 0.2 · Caisse et impression de démonstration

Pour une base créée en version 0.1, démarrer le service db puis appliquer **une fois** la migration depuis WSL avant de relancer l’API. Sur une base neuve les deux scripts sont exécutés automatiquement.

```bash
docker compose up -d db
docker compose exec -T db psql -v ON_ERROR_STOP=1 -U postgres -d transport_demo < db/002-caisse.sql
docker compose up -d --build api
```

Ouvrir une caisse avec le fond de départ, effectuer la vente puis choisir l’imprimante et le papier 80 mm dans le pilote Windows. La référence permet de relancer l’impression après une panne. Clôturer en indiquant le montant compté. Le nom du compte Windows identifie l’agent dans cette démo locale. Le QR possède un code d’authenticité vérifiable en ligne par POST /api/billets/verifier. La vérification ne consomme pas le billet. Les ventes historiques restent consultables sans affectation rétroactive à une caisse.

## Version 0.3 · Embarquement en ligne

Après la migration 002 pour les bases antérieures, appliquer une fois la migration 003 sur les volumes existants, puis reconstruire le serveur et installer le MSI 0.3. Les volumes neufs exécutent les migrations automatiquement.

```bash
docker compose exec -T db psql -v ON_ERROR_STOP=1 -U postgres -d transport_demo < db/003-embarquement.sql
docker compose up -d --build api
```

Dans **Embarquement et manifeste**, sélectionner le voyage et l’arrêt de montée. Utiliser un lecteur de QR en mode clavier avec terminaison Entrée. Le manifeste de travail affiche tous les billets vendus pour ce voyage et l’heure de chaque embarquement. La démo propose le contrôle jusqu’à 24 heures après le départ prévu. Le manifeste officiel figé au départ, les bagages et le contrôle sans réseau restent à réaliser.

## Version 0.4 · Départ et manifeste officiel

Sur une base v0.3, exécuter la migration suivante depuis WSL et installer le MSI 0.4. Pour les versions plus anciennes, appliquer d’abord les migrations précédentes dans l’ordre. Les bases neuves s’initialisent automatiquement.

```bash
docker compose exec -T db psql -v ON_ERROR_STOP=1 -U postgres -d transport_demo < db/004-depart.sql
docker compose up -d --build api
```

Dans **Embarquement et manifeste**, sélectionner le voyage et confirmer le départ du véhicule. Cette action clôt les ventes pour tout le voyage et propose l’impression du manifeste passagers. Le document numéroté conserve les passagers vendus, sièges, tronçons et présences tels qu’enregistrés à cet instant. Une relance renvoie le même manifeste. Aux arrêts intermédiaires, les scans des billets vendus restent possibles et actualisent le manifeste de travail. La réimpression du manifeste officiel reprend son contenu figé. L’inventaire des bagages et les habilitations des responsables restent à intégrer.

## Version 0.5 · Bagages et suppléments

Depuis la v0.4, appliquer une fois la migration 005 sur la base existante puis installer le nouveau MSI. Pour une base plus ancienne, appliquer les migrations intermédiaires dans l’ordre.

```bash
docker compose exec -T db psql -v ON_ERROR_STOP=1 -U postgres -d transport_demo < db/005-bagages.sql
docker compose up -d --build api
```

Ouvrir la caisse, renseigner la référence du billet puis ouvrir **Bagages du billet**. Saisir le poids en kilogrammes, calculer le supplément, vérifier les espèces et confirmer. La démonstration utilise 20 kg de franchise et 500 FCFA par kilogramme supplémentaire entamé sur le cumul du billet. Les deux paramètres sont définis par voyage en base. Un devis reste récupérable sur le même poste après redémarrage. En cas de réponse incertaine, reprendre la même confirmation. Si le cumul a changé sur un autre poste, recalculer avant toute nouvelle perception. Les suppléments confirmés alimentent la clôture de caisse. Les étiquettes QR peuvent être rechargées par billet et imprimées via le pilote Windows. Le manifeste conserve les poids enregistrés avant le départ. Les manifestes émis avant migration conservent leur contenu. La saisie du poids est manuelle.

## Version 0.6 · Programmer un voyage

Dans **Programmer un départ**, choisir l’itinéraire modèle, la date, l’heure d’Abidjan et le numéro du car sur ce créneau. Le modèle reste disponible après le départ de son voyage initial. La nouvelle circulation reprend le plan des sièges, les arrêts, les tarifs et les paramètres de bagages. Chaque voyage possède son propre inventaire de sièges et son propre manifeste. Soumettre le même créneau et le même numéro de car renvoie la même référence. Pour ajouter un car de renfort à la même heure, utiliser un autre numéro. Ce numéro distingue les cars du créneau, l’affectation des véhicules immatriculés relève du module Flotte.

Pour une base v0.5 existante, depuis WSL, appliquer une fois la migration puis installer le MSI 0.6. Les bases neuves sont initialisées automatiquement.

```bash
docker compose exec -T db psql -v ON_ERROR_STOP=1 -U postgres -d transport_demo < db/006-programmation.sql
docker compose up -d --build api
```

## Version 0.7 · Rapport de caisse journalier

Depuis la v0.6, reconstruire le service API avec `docker compose up -d --build api` et installer le MSI v0.7. Le schéma de la base reste compatible avec la migration 006.

Dans **Rapport de caisse journalier**, choisir une date puis **Consulter** ou **Imprimer**. La journée suit l’heure d’Abidjan. Les ventes et suppléments sont affectés à leur date effective. Le rapprochement affiche le solde de chaque session entière, même quand elle traverse minuit. Une session encore ouverte présente un solde attendu provisoire. Le rapport porte sur les encaissements en espèces de l’agent Windows de démonstration.

## Intégration du lot 1 · Fret

La page locale /fret/index.html propose un devis et un pré-enregistrement. Le tarif de démonstration est de 500 FCFA/kg, avec un diviseur volumétrique de 5 000 et un arrondi supérieur au kg. Le pré-enregistrement ne constate ni réception physique ni paiement. Depuis une base v0.7, appliquer db/007-fret.sql puis reconstruire le service api. Le portail reste local pendant le développement du cloisonnement et des accès.

### Dépôt physique et caisse fret

Dans le guichet de démonstration, **Réception de colis** charge la référence du pré-enregistrement. L’agent vérifie poids et dimensions, renseigne les contacts et la nature du colis, puis confirme le prix recalculé par le serveur. Le port payé entre dans les espèces de la session. Le port dû est encaissé au retrait. En cas de réponse incertaine, rechercher la même référence avant tout nouvel encaissement. Pour les environnements de développement existants, appliquer db/008-depot-fret.sql après la migration 007 puis reconstruire le serveur. Les habilitations d’agence restent à intégrer.

### Acheminement et retrait du fret

Appliquer la migration 009 après la 008 sur les bases de développement. Depuis le panneau **Acheminement et retrait des colis**, sélectionner le voyage et l’arrêt puis scanner la référence de l’étiquette. Charger au point d’origine avant le départ. La confirmation du départ fait passer les colis chargés en transit. Scanner à destination pour ouvrir le retrait. L’agent compare la pièce présentée au nom du destinataire, renseigne ses quatre derniers caractères et vérifie le code de retrait. La confirmation centrale inscrit le port dû dans la caisse de destination. Les reprises de la même remise reproduisent son résultat.

La démonstration stocke la demande de SMS dans une file locale PostgreSQL. Le connecteur SMS réel reste à brancher. Le code de test est accessible à la suite d’intégration via cette file et ne figure ni sur l’étiquette ni dans les réponses du guichet. L’arrêt sélectionné au poste est contrôlé contre le voyage ; l’habilitation du poste à représenter l’agence relève du socle multi-compagnies. Les transferts entre véhicules restent à intégrer.

### Capacité et manifeste de chargement

La migration 010 introduit deux plafonds affectés exclusivement au fret. Les valeurs de démonstration de 1 000 kg et 4 000 litres doivent être adaptées au véhicule et à la place réservée aux bagages voyageurs. Le guichet affiche les capacités, le cumul des poids mesurés, le volume extérieur des colis et la place disponible. Un voyage programmé hérite des plafonds du modèle au moment de sa création. Les paramètres d’un voyage restent ajustables avant départ dans les limites du chargement déjà scanné.

La confirmation du départ fige aussi le manifeste fret avec son numéro et son horodatage. Les réimpressions utilisent la liste enregistrée au départ. Les voyages partis avant la migration ne reçoivent pas de manifeste rétroactif. Le chargement modélisé couvre une soute et un trajet direct ; les transbordements et l’organisation physique de l’empilage restent à traiter.

### Suivi d’un colis

Appliquer la migration 011 après la 010. Sur la page fret, la référence du pré-enregistrement permet de consulter l’état du colis et, après dépôt, la ville ou le trajet. Cette route accepte 30 consultations par minute et par adresse réseau, sur une instance. La page de démonstration et les routes internes du guichet restent sur le réseau local en attendant le cloisonnement des compagnies et des accès.

### Version 0.11 · Compagnies, agences et postes de vente

Le profil `Company` utilise une API, un réseau Compose et un volume PostgreSQL par compagnie. Un projet Compose distinct, un port et un mot de passe de base distincts sont nécessaires pour chaque compagnie. Le profil est fixé en base au premier démarrage de l’API après la migration 012. Sur une démonstration existante, appliquer la migration 012 après la 011 avant de reconstruire. Les données historiques restent dans leur base et les caisses antérieures ne sont pas attribuées à de nouveaux postes.

Exemple pour une nouvelle compagnie fictive (environnement de développement, identifiants et trajets fictifs) :

```bash
export TRANSPORT_ACCESS=Company TRANSPORT_PORT=5181 COMPOSE_PROJECT_NAME=transport-alpha
export TRANSPORT_DB_PASSWORD="$(openssl rand -hex 32)"
# Conserver le mot de passe dans la configuration privée de cette compagnie.
docker compose up -d --build
```

Dans PostgreSQL, créer la hiérarchie une fois et rattacher les arrêts d’origine au modèle de voyage. Cet exemple se lance dans `docker compose exec db psql -U postgres -d transport_demo` après démarrage de l’API.

```sql
BEGIN;
UPDATE company SET name='Transport Alpha';
INSERT INTO legal_entity(name,country) VALUES ('Alpha Côte d’Ivoire','CI') RETURNING id AS entite \gset
INSERT INTO agency(entity_id,name) VALUES (:'entite','Abidjan') RETURNING id AS agence \gset
INSERT INTO agency_origin(template_id,position,agency_id) VALUES (1,0,:'agence');
COMMIT;
\echo Agence : :agence
```

Depuis le même projet Compose, inscrire le poste en passant l’identifiant d’agence obtenu. La commande affiche une fois la clé confidentielle. Le serveur en conserve l’empreinte. Pour révoquer le poste, mettre `counter_desk.enabled` à `false` sur son identifiant.

```bash
docker compose exec -T db psql -Xq -U postgres -d transport_demo -v agency="$AGENCE_ID" -v label="Guichet 1" < ops/inscrire-poste.sql
```

Côté Windows, définir `TRANSPORT_API_URL` pour l’utilisateur (exemple local `http://127.0.0.1:5181/`) avant de lancer le guichet. Pour un serveur distant, utiliser une adresse HTTPS. Saisir la clé au lancement. Le guichet affiche la compagnie, l’agence et le poste. Les fichiers de reprise d’une vente sont rangés par compagnie et par poste. La clé identifie le poste, l’authentification nominative des agents reste à intégrer.

Le poste habilité peut vendre aux arrêts de son agence, ouvrir et clôturer sa caisse, consulter son rapport, reprendre une vente et imprimer ses propres billets. Le site fret propose le devis, le pré-enregistrement et le suivi dans la base de la compagnie. Les écrans internes de fret, d’embarquement et de supervision restent disponibles dans le profil historique `Demo` sur données fictives. Leur ouverture aux postes du profil `Company` nécessite leurs habilitations métier. Une connexion PostgreSQL d’administration reste privilégiée. L’accès support délégué et la console d’administration éditeur restent à développer.

### Version 0.12 · Agents nominatifs en agence

Le profil Company demande la clé du poste et la clé personnelle d’un agent de la même agence. Chaque personne possède son compte, son nom, son identifiant stable et ses habilitations cumulables. Le serveur vérifie ces éléments à chaque appel. La caisse, les options, les billets et les fichiers de reprise sont rattachés au poste et à l’agent. Le bouton **Changer d’agent** ferme la session de l’agent dans le guichet et redemande les identifiants dans l’application.

| Droit | Opérations |
|---|---|
| vente | Réserver, encaisser et imprimer ses billets, gérer sa caisse |
| fret | Consulter et déposer au point d’origine, charger, enregistrer l’arrivée et remettre à destination, gérer sa caisse |
| embarquement | Consulter les passagers au départ des arrêts autorisés et contrôler leur billet |
| depart | Confirmer le départ et réimprimer le manifeste officiel à la gare initiale |

Sur une installation Company v0.11, terminer les ventes en cours, rapprocher les reprises et clôturer les caisses avant la migration 013. Arrêter l’ancienne API pendant la migration. Les documents historiques conservent leur attribution au poste. Les caisses historiques restent consultables en base par l’administrateur. Les volumes neufs s’initialisent automatiquement.

Exemple d’inscription dans l’agence préparée en v0.11 :

```bash
docker compose exec -T db psql -Xq -U postgres -d transport_demo -v agency="$AGENCE_ID" -v name="Awa K." -v rights="vente,fret" < ops/inscrire-agent.sql
```

Remettre la clé affichée à la personne concernée. Seule l’empreinte est conservée en base. Pour révoquer cet accès, passer le champ `enabled` de son enregistrement `staff_account` à `false`. Les autres utilisateurs du même poste gardent leur accès. Le nom enregistré pour un scan, un chargement ou un départ provient du compte authentifié. Les dépôts et remises sont reliés à ce compte par leur session de caisse. Les clés sont des identifiants de développement. Les écrans d’administration des comptes, la double authentification des rôles sensibles et l’audit inviolable restent à intégrer.

### Administration des agents dans le profil Company

Le portail de chaque compagnie se trouve à `/administration/index.html` sur son serveur. Il permet de créer un agent dans une agence existante, de remettre sa clé personnelle, de modifier ses habilitations, de désactiver ou réactiver son compte et de consulter les 100 dernières modifications effectuées dans ce portail. Le journal conserve l’administrateur, la date et les valeurs avant et après. La caisse et le MSI v0.12 restent compatibles.

Appliquer `db/014-administration.sql` aux bases existantes après la migration 013. Dans la configuration privée de cette compagnie, ajouter `TRANSPORT_ADMIN_SEAL` avec 32 octets aléatoires encodés en 64 caractères hexadécimaux (générateur possible : `openssl rand -hex 32`). Conserver cette valeur avec les secrets de restauration de la compagnie. Elle chiffre les secrets TOTP. Reconstruire le service API. Si cette valeur est absente, les routes d’administration restent désactivées.

Depuis le projet Compose de la compagnie, une personne autorisée inscrit le premier administrateur dans un terminal privé :

```bash
docker compose exec api dotnet Transport.Api.dll admin-init direction.ci
```

Choisir un mot de passe d’au moins 15 caractères, importer la clé affichée dans une application d’authentification compatible TOTP (SHA-1, six chiffres, 30 secondes), puis confirmer l’inscription avec le code courant. Attendre le code suivant pour la première connexion. L’inscription vérifie le second facteur et préserve les identifiants déjà attribués. Pour une révocation d’urgence, désactiver le compte dans `company_admin`. En cas de perte du second facteur, l’exploitant peut inscrire un nouvel identifiant puis désactiver l’ancien après vérification d’identité.

Ouvrir le portail par HTTPS, ou via la boucle locale pour le développement. La session dure 20 minutes, son jeton reste en mémoire dans l’onglet et la déconnexion le révoque côté serveur. Une création délivre la clé de l’agent une fois. Les modifications concurrentes d’un même compte sont contrôlées par numéro de révision. Les administrateurs de compagnie et les agents du guichet ont des identifiants distincts.
