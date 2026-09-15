# 📜 Notes de Version — La Roue de la Fortune (Valheim)

Ce document retrace l'historique complet des versions, améliorations, correctifs et évolutions apportés au système de Roue de la Fortune du serveur **Le Camp du Feu Sacré**.

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

### 🧪 Tests & Qualité
- Couverture étendue : **46 tests unitaires** passants ([run-tests.js](file:///d:/Work/roue-de-la-fortune/bot/test/run-tests.js)), couvrant l'arriéré du podium, les boucles `safeLoop`, le module de logging, l'historique de traçabilité, les embeds et les paliers collectifs.

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
