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
