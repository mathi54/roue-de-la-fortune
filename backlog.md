# 📋 Backlog de Développement — La Roue de la Fortune (Valheim)

Ce document répertorie et structure de manière déterministe les tâches d'évolution, d'optimisation et de fiabilisation du système de Roue de la Fortune du serveur Valheim **Le Camp du Feu Sacré**.

---

## 📌 Synthèse de l'état d'avancement

- **Phase 1 : Fiabilité & Robustesse Critique** `[3/4]`
- **Phase 2 : Optimisations C# ValheimRestApi & Sécurité** `[0/2]`
- **Phase 3 : Ergonomie Joueur & Fonctionnalités Communautaires** `[0/3]`

---

## Phase 1 : Fiabilité & Robustesse Critique (Production-Ready)

* [x] **Tâche 1.1 : Détection d'arriéré et rattrapage automatique du podium mensuel**
  * **Fichiers concernés :** [core.js](file:///d:/Work/roue-de-la-fortune/bot/src/core.js), [run-tests.js](file:///d:/Work/roue-de-la-fortune/bot/test/run-tests.js)
  * **Objectif :** Remplacer le test strict `nowDate.getDate() !== day` par une logique d'arriéré afin que le podium du mois précédent soit automatiquement distribué même si le bot a été éteint ou en maintenance le 1er du mois, tout en garantissant l'absence de double distribution.
  * **Prompt Antigravity :**
    > "Dans `bot/src/core.js`, refactorise la fonction `runMonthlyIfDue(ctx, nowDate)` :
    > 1. Calcule la clé du mois précédent `previousMonthKey` et détermine si l'échéance minimale pour ce mois est atteinte (ex: nous sommes au moins le `dayOfMonth` à `hour`h du mois en cours, OU à n'importe quelle date ultérieure du même mois).
    > 2. Vérifie si `ctx.store.monthlyAlreadyRun(previousMonthKey)` est faux. Si c'est le cas et que l'échéance est passée, procède immédiatement à la récupération du classement Top-Serveurs `lastMonth` et à la distribution des récompenses.
    > 3. Mets à jour la suite de tests unitaires dans `bot/test/run-tests.js` pour valider les cas : distribution le 1er à l'heure H, rattrapage le 2 ou le 15 du mois, non-redistribution si déjà marqué, et non-distribution si avant le jour J à l'heure H."
  > [!IMPORTANT]
  > Le marquage `ctx.store.markMonthlyRun(monthKey)` doit impérativement rester positionné AVANT l'envoi dans la file d'attente pour éviter tout doublon de récompenses en cas de crash réseau ou d'exception.

* [x] **Tâche 1.2 : Boucles asynchrones sûres anti-chevauchement (Pattern safeLoop)**
  * **Fichiers concernés :** [index.js](file:///d:/Work/roue-de-la-fortune/bot/src/index.js)
  * **Objectif :** Éliminer `setInterval` au profit d'un mécanisme de boucle récursive séquentielle avec verrouillage d'exécution pour empêcher que des appels réseau longs (ex: timeout Top-Serveurs ou ValheimRestApi) ne s'exécutent en concurrence.
  * **Prompt Antigravity :**
    > "Dans `bot/src/index.js`, remplace la fonction `loop(fn, intervalSec, label)` par une implémentation `safeLoop(fn, intervalSec, label)` :
    > 1. Utilise un drapeau d'état `isRunning` pour interdire toute ré-entrance concurrente.
    > 2. Enchaîne les exécutions via `setTimeout` déclenché uniquement UNE FOIS que la promesse `fn()` est entièrement résolue ou rejetée.
    > 3. Gère les erreurs sans interrompre la programmation du tick suivant.
    > 4. Applique ce pattern pour les 3 boucles : `processVotes`, `deliverQueue` et `runMonthlyIfDue`."

* [x] **Tâche 1.3 : Arrêt gracieux (Graceful Shutdown) et libération des ressources**
  * **Fichiers concernés :** [index.js](file:///d:/Work/roue-de-la-fortune/bot/src/index.js)
  * **Objectif :** Conformer l'application à la directive de gestion de la mémoire et des ressources en écoutant les signaux système `SIGINT` et `SIGTERM` pour couper proprement les instances actives.
  * **Prompt Antigravity :**
    > "Dans `bot/src/index.js`, implémente une fonction `gracefulShutdown(signal)` enregistrée sur `process.on('SIGINT')` et `process.on('SIGTERM')` :
    > 1. Journalise l'arrêt propre avec le signal reçu.
    > 2. Ferme le serveur HTTP d'administration (`server.close()`).
    > 3. Déconnecte proprement le client Discord via `client.destroy()`.
    > 4. Assure un `process.exit(0)` après libération complète des descripteurs."

* [ ] **Tâche 1.4 : Journalisation persistante avec rotation et diagnostic embarqué**
  * **Fichiers concernés :** [logger.js](file:///d:/Work/roue-de-la-fortune/bot/src/logger.js), [index.js](file:///d:/Work/roue-de-la-fortune/bot/src/index.js), [run-tests.js](file:///d:/Work/roue-de-la-fortune/bot/test/run-tests.js)
  * **Objectif :** Respecter la directive de diagnostic embarqué (Règle Studio n°1) en écrivant tous les logs du bot dans un fichier persistant local et en fournissant un accès de consultation via l'API admin locale.
  * **Prompt Antigravity :**
    > "Crée le module `bot/src/logger.js` et intègre-le dans `bot/src/index.js` :
    > 1. Implémente une fonction de log écrivant à la fois dans `console.log` et dans un fichier local persistant `logs/roue.log` (avec création automatique du dossier `logs/`).
    > 2. Chaque ligne doit être formatée : `[YYYY-MM-DDTHH:mm:ss.sssZ] [LEVEL] Message`.
    > 3. Conserve un buffer en mémoire des 100 dernières lignes de log.
    > 4. Étends le serveur HTTP local `startAdminApi` dans `bot/src/index.js` pour exposer la route `GET /logs` protégée par `X-Auth-Token`, renvoyant le contenu de ce buffer au format JSON `{ success: true, count: N, logs: [...] }`."
  > [!NOTE]
  > Cette fonctionnalité permettra de diagnostiquer immédiatement les anomalies depuis le terminal SSH ou la console AMP sans avoir à fouiller manuellement dans les fichiers de logs système.

---

## Phase 2 : Optimisations C# ValheimRestApi & Sécurité

* [ ] **Tâche 2.1 : Sécurisation du parsing JSON dans GiveCommand.cs**
  * **Fichiers concernés :** [GiveCommand.cs](file:///d:/Work/roue-de-la-fortune/valheim-restapi/Commands/GiveCommand.cs)
  * **Objectif :** Remplacer le découpage artisanal par regex des chaînes JSON par une extraction blindée contre les caractères d'échappement, les guillemets et les retours à la ligne dans le paramètre `message`.
  * **Prompt Antigravity :**
    > "Dans `valheim-restapi/Commands/GiveCommand.cs`, refactorise les méthodes utilitaires `JsonString` et `JsonInt` :
    > 1. Corrige l'expression régulière ou implémente un décodeur de token JSON robuste capable de gérer sans échec les chaînes contenant des guillemets échappés `\"`, des antislashes `\\` et des retours à la ligne `\n`.
    > 2. Assure-toi que les chaînes vides ou malformées renvoient `null` proprement sans lever d'exception non interceptée.
    > 3. Préserve la compatibilité avec l'environnement BepInEx Mono / .NET Standard sans ajouter de dépendance binaire externe."

* [ ] **Tâche 2.2 : Traçabilité déterministe des tirages (Audit Trail)**
  * **Fichiers concernés :** [store.js](file:///d:/Work/roue-de-la-fortune/bot/src/store.js), [core.js](file:///d:/Work/roue-de-la-fortune/bot/src/core.js), [run-tests.js](file:///d:/Work/roue-de-la-fortune/bot/test/run-tests.js)
  * **Objectif :** Respecter la directive de déterminisme et d'auditabilité (Règle Studio n°3) en conservant dans `state.json` l'historique glissant des N derniers tirages pour permettre de résoudre les litiges de joueurs.
  * **Prompt Antigravity :**
    > "Dans `bot/src/store.js` et `bot/src/core.js` :
    > 1. Ajoute une section `history` (tableau limité aux 50 dernières entrées) dans `state.json`.
    > 2. À chaque exécution de `handleClaimedVote`, insère un enregistrement contenant : `{ id: uuid/timestamp, date: ISO, voter: name, target: target, tierId: tier.id, prizeText: label, outcome: 'delivered'|'queued'|'voteOnly' }`.
    > 3. Ajoute une méthode `store.getHistory(limit)` et une méthode de purge automatique pour maintenir la taille du tableau.
    > 4. Complète les tests unitaires dans `bot/test/run-tests.js`."

---

## Phase 3 : Ergonomie Joueur & Fonctionnalités Communautaires

* [ ] **Tâche 3.1 : Endpoints d'administration de la file d'attente (Inspection & Force-Deliver)**
  * **Fichiers concernés :** [index.js](file:///d:/Work/roue-de-la-fortune/bot/src/index.js), [core.js](file:///d:/Work/roue-de-la-fortune/bot/src/core.js)
  * **Objectif :** Permettre aux administrateurs de visualiser les récompenses en attente et de forcer une tentative de livraison immédiate via l'API HTTP locale.
  * **Prompt Antigravity :**
    > "Dans `bot/src/index.js` :
    > 1. Ajoute la route `GET /queue` sur l'API HTTP d'administration, renvoyant l'état actuel de `store.queue` au format JSON.
    > 2. Ajoute la route `POST /queue/deliver` déclenchant immédiatement un appel à `deliverQueue(ctx)` et renvoyant le statut d'exécution.
    > 3. Documente les commandes `curl` correspondantes dans `docs/GUIDE_UTILISATEUR.md`."

* [ ] **Tâche 3.2 : Commandes Slash Discord d'auto-assistance (`/mes-recompenses`, `/roue-classement`)**
  * **Fichiers concernés :** [index.js](file:///d:/Work/roue-de-la-fortune/bot/src/index.js), [core.js](file:///d:/Work/roue-de-la-fortune/bot/src/core.js), [embeds.js](file:///d:/Work/roue-de-la-fortune/bot/src/embeds.js)
  * **Objectif :** Améliorer l'expérience joueur sur Discord en offrant des commandes slash permettant d'interroger directement ses récompenses en attente ou d'afficher le classement Top-Serveurs du mois en cours.
  * **Prompt Antigravity :**
    > "Dans le bot Discord :
    > 1. Enregistre les commandes Slash `/mes-recompenses` et `/roue-classement`.
    > 2. `/mes-recompenses` : recherche dans `store.queue` toutes les entrées correspondant au pseudo (ou alias) du joueur et répond par un message éphémère (visible uniquement par lui) listant ses lots en attente.
    > 3. `/roue-classement` : interroge `ts.playersRanking('current')` et affiche un embed élégant du top 10 des votants du mois en cours."

* [ ] **Tâche 3.3 : Paliers collectifs communautaires (Milestones / Jauge de votes)**
  * **Fichiers concernés :** [rewards.json](file:///d:/Work/roue-de-la-fortune/bot/rewards.json), [core.js](file:///d:/Work/roue-de-la-fortune/bot/src/core.js), [embeds.js](file:///d:/Work/roue-de-la-fortune/bot/src/embeds.js), [store.js](file:///d:/Work/roue-de-la-fortune/bot/src/store.js)
  * **Objectif :** Gamifier l'effort collectif du serveur : si le serveur franchit des seuils de votes mensuels (ex: 50, 100, 200 votes), tous les joueurs ayant voté au moins une fois ce mois-ci reçoivent un lot bonus.
  * **Prompt Antigravity :**
    > "Dans `bot/rewards.json`, `bot/src/core.js` et `bot/src/store.js` :
    > 1. Définis une section `milestones` dans `rewards.json` contenant les seuils de votes et les récompenses associées.
    > 2. Dans `store.js`, mémorise les votants distincts du mois en cours et les paliers déjà validés.
    > 3. Dans `core.js`, vérifie après chaque vote validé si un nouveau palier collectif est franchi. Si oui, distribue le lot bonus à tous les votants du mois et publie une annonce festive sur Discord."
