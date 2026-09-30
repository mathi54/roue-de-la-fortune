# 📜 Notes de Version — La Roue de la Fortune (Valheim)

Ce document retrace l'historique complet des versions, améliorations, correctifs et évolutions apportés au système de Roue de la Fortune du serveur **Le Camp du Feu Sacré**.

---

## [v1.1.3] — 2026-09-30

### 🚫 Blacklist de votants (`config.blacklist`)

Nouvelle liste de pseudos ignorés dans `config.json` (ex. `"blacklist": ["Ketil", "Ketill"]`),
insensible à la casse. Un pseudo blacklisté :
- ne déclenche **ni claim ni annonce Discord** quand il vote (trace dans roue.log uniquement) ;
- est **retiré du classement avant le top 5** du podium mensuel — le joueur suivant récupère
  son rang ;
- n'apparaît plus dans l'embed `/roue-classement`.

Ses votes comptent toujours pour le serveur sur Top-Serveurs (total mensuel inclus).

### 🗳️ Podium mensuel : total des votes annoncé

L'embed du podium annonce d'abord le nombre total de votes du mois (tous votants confondus),
puis le palmarès.

### 🧪 Tests

- 53 tests (2 nouveaux) : vote blacklisté ignoré ; podium filtré + total des votes correct.

---

## [v1.1.2] — 2026-09-29

### 🐛 Correctif — pseudos accentués jamais livrés (« Vimbé »)

**Symptôme** : les récompenses en file d'un joueur au pseudo accentué (Vimbé) échouaient en boucle
(`Livraison différée ÉCHEC … refusé par ValheimRestApi`) alors que les autres joueurs étaient livrés normalement.

**Cause** : le bot envoyait le body `/give` en UTF-8 avec `Content-Type: application/json` **sans charset**.
Côté mod, le `HttpListener` (Mono) décode alors le body avec l'encodage par défaut du système :
`Vimbé` (UTF-8) devient `VimbÃ©`, et le lookup du joueur connecté échoue (`Player not online`).
Le `/players` du mod, lui, déclare `charset=utf-8` — d'où l'asymétrie : le bot lisait bien les accents,
mais le mod relisait mal ce que le bot lui renvoyait.

**Correctifs** (`src/valheim.js`, `src/core.js`) :
- `Content-Type: application/json; charset=utf-8` sur toutes les requêtes vers ValheimRestApi.
- Le motif d'échec renvoyé par le mod (ex. `Player not online: VimbÃ©`) est maintenant remonté
  dans le log du bot (`ÉCHEC … refusé par ValheimRestApi (…)`), pour ne plus diagnostiquer à l'aveugle.

Côté mod, `Mathi-RestApi` force désormais aussi la lecture du body en UTF-8 (ceinture et bretelles,
actif au prochain redéploiement du DLL). Aucune récompense n'a été perdue : les entrées de Vimbé sont
restées en file et partiront à sa prochaine connexion.

### 🧪 Tests

- 51 tests (2 nouveaux) : header `charset=utf-8` vérifié, et remontée du motif d'échec du mod dans le log.

---

## [v1.1.1] — 2026-09-29

Fiabilisation de la livraison différée, après diagnostic d'une file de ~65 récompenses accumulées du 15 au 29 septembre sans jamais être livrées — y compris pour des joueurs revenus en jeu (Benito, VIMBÉ, Blota Hounkas). Cause : la fenêtre de stabilité exigeait 2 cycles consécutifs en ligne avant toute livraison différée, condition presque jamais réunie en pratique (sessions courtes, `/players` parfois instable), et ce chemin de code ne loggait strictement rien.

### 🚚 Livraison différée (core.js, store.js)
- **Livraison dès le 1er cycle en ligne** : le tampon `PendingGives` d'EventController (>= 3.5.1, modpack en 3.6.1) couvre déjà l'écran de chargement côté client. La fenêtre de stabilité est conservée uniquement pour la livraison directe au moment du vote.
- **Retrait de la file uniquement après give confirmé** (`peekDeliverable` non destructif + `removeEntry` par id) : un redémarrage AMP en pleine livraison ne peut plus perdre de récompense.
- **Compteur d'échecs par entrée** (`attempts`) + alerte dans le salon admin au 5e échec (une seule fois), configurable via `queue.alertAfterAttempts`.
- `state.json` : id unique rétro-rempli sur les entrées existantes au chargement — la file accumulée se livre d'elle-même après déploiement, à la connexion de chacun.

### 🔍 Traçabilité (core.js)
- Chaque tentative différée est loggée : `Livraison différée OK/ÉCHEC : N × Item → Joueur (raison)`.
- Indisponibilité de ValheimRestApi loggée avec le nombre de récompenses en attente (throttlée à 1 fois / 10 min).

### 🧹 Arrêt propre (index.js)
- L'arrêt gracieux attend la fin du cycle de livraison en cours (8 s max) avant de quitter.

### 🧪 Tests
- Suite portée à **50 tests** : livraison au 1er cycle, non-perte sur échec répété, alerte au 5e échec uniquement, throttle du log d'indisponibilité.

---

## [v1.1.0] — 2026-09-15

Cette version majeure apporte une fiabilisation complète du cycle de vie du bot (podium, boucles, arrêt propre), un module de diagnostic embarqué avec logs persistants, le blindage du mod serveur C#, ainsi que de nouvelles fonctionnalités communautaires majeures (commandes Slash Discord et paliers collectifs).

### 🛡️ Fiabilité & Robustesse (Production-Ready)
- **Rattrapage automatique du podium mensuel ([core.js](file:///d:/Work/roue-de-la-fortune/bot/src/core.js)) :**
  - Remplacement de la condition stricte `getDate() === 1` par une détection d'arriéré dans `runMonthlyIfDue`.
  - Le podium du mois écoulé est désormais distribué même si le bot ou le serveur a été coupé ou en maintenance le 1er du mois, sans aucun risque de double distribution grâce au verrouillage préalable de la clé de mois.
- **Boucles asynchrones sûres anti-chevauchement ([core.js](file:///d:/Work/roue-de-la-fortune/bot/src/core.js)) :**
  - Introduction du pattern séquentiel `safeLoop` en remplacement de `setInterval`.
  - Empêche toute ré-entrance ou concurrence sur les fichiers d'état et le réseau si l'API Top-Serveurs ou ValheimRestApi subit une latence.
- **Arrêt gracieux — Graceful Shutdown ([index.js](file:///d:/Work/roue-de-la-fortune/bot/src/index.js)) :**
  - Écoute des signaux système `SIGINT` et `SIGTERM` (conforme à la Règle Studio n°4).
  - Arrêt ordonné des boucles d'arrière-plan, fermeture du serveur HTTP local, déconnexion propre du client Discord (`client.destroy()`) et libération complète des descripteurs.
- **Journalisation persistante & Diagnostic embarqué ([logger.js](file:///d:/Work/roue-de-la-fortune/bot/src/logger.js)) :**
  - Création du module `Logger` (conforme à la Règle Studio n°1).
  - Écriture simultanée dans la console et dans le fichier persistant `logs/roue.log`.
  - Maintien d'un buffer glissant en mémoire des 100 derniers logs, consultable à distance via la nouvelle route HTTP admin `GET /logs?limit=N`.

### ⚙️ Mod ValheimRestApi & Sécurité (C#)
- **Sécurisation du parsing JSON dans GiveCommand ([GiveCommand.cs](file:///d:/Work/roue-de-la-fortune/valheim-restapi/Commands/GiveCommand.cs)) :**
  - Refonte complète de `JsonString` et `JsonInt` avec prise en charge du mode `Singleline`.
  - Implémentation d'un décodeur d'échappement robuste `UnescapeJsonString` gérant les sauts de ligne `\n`, retours chariot `\r`, tabulations `\t`, guillemets `\"`, barres obliques `\\` et séquences Unicode `\uXXXX`.
- **Traçabilité & Audit Trail des tirages ([store.js](file:///d:/Work/roue-de-la-fortune/bot/src/store.js)) :**
  - Conservation dans `state.json` d'un historique glissant des 50 derniers tirages/votes avec identifiant unique, horodatage ISO, votant, cible, palier et statut de livraison (conforme à la Règle Studio n°3).

### 🎮 Ergonomie Joueur & Fonctionnalités Communautaires
- **Commandes Slash Discord :**
  - `/mes-recompenses [pseudo]` : permet à chaque viking d'interroger directement ses récompenses en attente dans la file via une réponse éphémère (visible uniquement par lui).
  - `/roue-classement` : affiche en direct le classement Top 10 des votants du mois en cours sur Top-Serveurs.
- **Paliers Collectifs Communautaires (*Milestones*) :**
  - Ajout d'une section `milestones` dans `rewards.json` (ex: 50 et 100 votes).
  - Suivi des votants du mois dans `state.json`. Dès qu'un palier collectif de votes est franchi, une annonce festive est publiée sur Discord et tous les joueurs ayant voté ce mois-ci reçoivent automatiquement le cadeau collectif dans leur file d'attente.
- **Administration de la file d'attente :**
  - Ajout des routes HTTP admin `GET /queue` (inspection des items en attente) et `POST /queue/deliver` (déclenchement forcé d'un cycle de livraison pour les joueurs en ligne).
  - Documentation complète des commandes `curl` associées dans `GUIDE_UTILISATEUR.md`.

### 🎁 Fiabilisation de la Livraison à la Reconnexion & Résolution d'Alias
- **Résolution rétroactive dynamique des alias ([store.js](file:///d:/Work/roue-de-la-fortune/bot/src/store.js), [core.js](file:///d:/Work/roue-de-la-fortune/bot/src/core.js)) :**
  - Si un alias a été configuré dans `config.json` postérieurement à la mise en file d'un lot, `takeDeliverable` compare désormais à la fois le pseudo brut et le pseudo résolu pour livrer directement l'item au personnage en jeu.
- **Temporisation anti-rafale (Pacing) des paquets RPC ([core.js](file:///d:/Work/roue-de-la-fortune/bot/src/core.js)) :**
  - Insertion d'une pause progressive de 500 ms entre deux distributions lors du vidage de la file, évitant l'engorgement du client Valheim Unity et les désynchronisations lors de distributions multiples.
- **Normalisation Unicode FormC et robustesse des pseudos ([GiveCommand.cs](file:///d:/Work/roue-de-la-fortune/valheim-restapi/Commands/GiveCommand.cs)) :**
  - Nettoyage et normalisation Unicode FormC des noms de joueurs lors de la recherche de `ZNetPeer` pour éliminer tout risque d'échec sur les caractères accentués ou les espaces superflus.
- **Accompagnement et ergonomie joueur ([embeds.js](file:///d:/Work/roue-de-la-fortune/bot/src/embeds.js)) :**
  - Ajout d'une mention explicative dans l'embed de `/mes-recompenses` rappelant la fenêtre de vérification de 1 à 2 minutes après connexion et la nécessité de disposer de place et de poids libre dans l'inventaire.

### 🧪 Tests & Qualité
- Couverture étendue : **49 tests unitaires** passants ([run-tests.js](file:///d:/Work/roue-de-la-fortune/bot/test/run-tests.js)), couvrant l'arriéré du podium, les boucles `safeLoop`, le module de logging, l'historique de traçabilité, les alias rétroactifs, le pacing et les paliers collectifs.

---

## [v1.0.0] — 2026-09-08

Version initiale du système de Roue de la Fortune pour Le Camp du Feu Sacré.

### 🚀 Fonctionnalités initiales
- **Détection des votes Top-Serveurs :** Polling toutes les 60 s avec garantie anti-doublon via `claim-username`.
- **Livraison directe en jeu :** Commande `POST /give` dans ValheimRestApi et RPC direct `EventController_GiveItem` pour injection directe dans l'inventaire avec message HUD doré.
- **Fenêtre de stabilité (anti-drop néant) :** Vérification que le joueur est en ligne sur 2 cycles consécutifs (~60 s) avant livraison pour éviter les pertes pendant l'écran de chargement.
- **Gestion de la file d'attente :** Mise en file des récompenses pour les joueurs hors ligne avec rétention de 30 jours et livraison automatique à la reconnexion.
- **Apprentissage automatique & Alias :** Mémorisation des pseudos connectés via `/players` et table d'alias (`config.aliases`) pour lier le pseudo de vote au nom du personnage en jeu.
- **Tableau des gains dynamique :** Message épinglé sur Discord régénéré automatiquement d'après `rewards.json`.
- **Podium mensuel :** Récompenses automatiques le 1er du mois à 10h pour le Top 5 des votants (Trésor du Jarl pour le Top 3, Bourse du Viking pour les 4e-5e).
- **API locale d'administration :** Endpoint `POST /fakevote` sur port local protégé par token pour simuler un vote de bout en bout sans passer par Top-Serveurs.
