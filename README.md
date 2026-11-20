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
