# ⚔️ La Roue de la Fortune — Récompenses de vote Top-Serveurs

Système de récompenses de vote pour le serveur Valheim **Le Camp du Feu Sacré**.
Chaque vote sur Top-Serveurs par un joueur du serveur fait "tourner la roue" : une récompense
aléatoire (table pondérée par rareté) est livrée **directement dans son inventaire** en jeu,
et annoncée sur Discord. Un podium mensuel récompense les meilleurs votants.

## Description

| Composant | Rôle | Technologie |
|---|---|---|
| **bot/** (voterewards-bot) | Détecte les votes (API Top-Serveurs), tire la récompense, gère la file d'attente, annonce sur Discord, déclenche la livraison | Node.js 22 + discord.js, instance AMP "NodeJS App Runner" |
| **valheim-restapi/** (`GiveCommand`) | Endpoint HTTP `POST /give` côté serveur : trouve le joueur connecté, valide le prefab, envoie le RPC de livraison | Mod BepInEx serveur (C#), existant, étendu |
| **EventController** v3.6.1 — sources dans le repo [ModEventXp](https://github.com/mathi54/ModEventXp) | Côté client : reçoit le RPC `EventController_GiveItem`, insère l'objet dans l'inventaire (repli au sol si plein), message HUD. Fournit aussi les 6 élixirs d'event (XP/Butin en 5, 10 et 15 min) avec cumul | Mod BepInEx client+serveur (C#), existant, étendu |

### Ce que fait le système, concrètement

1. Toutes les 60 s, le bot lit `GET /v1/votes/last` (votes de l'heure) et **réclame** chaque nouveau vote via `claim-username` (anti-doublon garanti par Top-Serveurs).
2. Alias éventuel (`config.aliases`) : "pseudo de vote" → "personnage en jeu" (ex. Ketil → Andromaque).
3. Si le personnage n'a jamais été vu en jeu → simple ligne Discord "X vient de voter pour le serveur !" (comme le webhook Top-Serveurs), rien d'autre.
4. Si c'est un joueur du serveur → tirage dans `rewards.json` → embed "🎁 X a fait tourner la Roue de la Fortune !".
5. Joueur en ligne depuis > 1 cycle → `POST /give` → RPC → objet dans l'inventaire + message doré en jeu. Sinon la récompense attend en file (30 jours max) et part à sa prochaine connexion.
6. Le 1er du mois : classement `players-ranking?type=lastMonth` → lots aux 3 premiers et aux 4e-5e, podium annoncé.

## Choix techniques

- **API Top-Serveurs plutôt que le webhook Discord** : source de vérité, avec le mécanisme `claim` qui rend impossible la double récompense même après un redémarrage du bot.
- **Livraison par RPC vers le client** (EventController) plutôt que spawn serveur : insertion réelle dans l'inventaire, aucun objet au sol, message HUD. Un mode `"drop"` (spawn aux pieds, auto-pickup) reste disponible en secours dans `GiveCommand`.
- **Fenêtre de stabilité** (joueur vu en ligne sur 2 cycles) + **tampon client jusqu'au spawn** : deux protections indépendantes contre la livraison pendant l'écran de chargement.
- **Liste des joueurs apprise automatiquement** (`knownPlayers`) via `/players` : aucune maintenance, une ligne par personnage jamais connecté. Table d'**alias** pour les pseudos de vote différents du personnage.
- **État sur fichier JSON** (`state.json`, écriture atomique) : suffisant pour ~15 joueurs, inclus dans les sauvegardes AMP, lisible/éditable à la main.
- **Zéro dépendance native** côté bot (discord.js uniquement) ; parsing tolérant des réponses de ValheimRestApi (objets, chaînes, JSON ré-échappé par `JsonBuilder`).
- **Tout est configurable sans toucher au code** : `config.json` (tokens, salons, ports, alias, comportement) et `rewards.json` (table de loot, lots mensuels — le tableau des gains épinglé se régénère au démarrage).

## Installation & utilisation

### Prérequis
- Top-Serveurs : la **Token de Votes** du serveur (page d'administration).
- Discord : une application/bot (portail développeur) invitée avec *Send Messages*, *Embed Links*, *Manage Messages* ; IDs des salons (mode développeur).
- Serveur Valheim : ValheimRestApi (avec `GiveCommand.cs`) + EventController ≥ 3.6.1, ce dernier aussi dans le **modpack** des joueurs et whitelisté dans AzuAntiCheat.

### ValheimRestApi — `BepInEx/config/fr.enfantsodin.valheim.restapi.cfg`
```ini
[Network]
Port = 52858            ; un port par instance
BindAddress = 127.0.0.1 ; le bot tourne sur la même machine : jamais exposé
[Security]
AuthToken = <chaine-secrete>
```

### Bot — instance AMP "NodeJS App Runner"
- Download Type : None. App Name : `src/index.js`. Node 22 LTS, `npm i` (défaut).
- Déposer à la racine (`node-server/app/`) : `package.json`, `src/`, `config.json`, `rewards.json`.
- `config.json` (depuis `config.example.json`) :

```json
{
  "discord": { "token": "...", "announceDelivery": false,
               "channels": { "public": "ID", "admin": "ID", "classement": "ID" } },
  "topServeurs": { "serverToken": "...", "pollIntervalSec": 60 },
  "valheim": { "baseUrl": "http://127.0.0.1:52858", "apiToken": "<meme AuthToken>", "instance": "Valheim Mod" },
  "rejectUnknownPlayers": true,
  "aliases": { "pseudo-de-vote": "PersonnageEnJeu" },
  "queue": { "retryIntervalSec": 60, "maxAgeDays": 30 },
  "monthly": { "enabled": true, "dayOfMonth": 1, "hour": 10 }
}
```
- **Update** (installe Node + dépendances) puis **Start**. Console attendue : `Connecté en tant que … — La Roue de la Fortune est en place ⚔️`.
- Tests : `npm test` (36 tests, clients simulés).

### Vérifications rapides (SSH sur le serveur, personnage connecté)
```bash
curl -X POST http://127.0.0.1:52858/give -H "X-Auth-Token: <token>" \
     -d '{"playername":"Grudu","item":"Coins","amount":20,"message":"Test !"}'
```
→ `"success":true,"mode":"rpc"`, les piastres dans l'inventaire, message doré à l'écran.

## Structure du dépôt

```
bot/                    Le bot Discord Node.js
├── src/index.js        Point d'entrée : Discord, boucles (votes / file / mensuel), tableau épinglé
├── src/core.js         Logique métier : processVotes, deliverQueue, runMonthlyIfDue, alias
├── src/topserveurs.js  Client API Top-Serveurs (votes/last, claim-username, players-ranking)
├── src/valheim.js      Client ValheimRestApi (/players, /give) + extractPlayerName tolérant
├── src/rewards.js      Tirage pondéré, libellés, lots mensuels par rang
├── src/store.js        state.json : votes vus (3 h), file (30 j), joueurs connus, podium, épinglé
├── src/embeds.js       Tous les messages Discord
├── src/config.js       Chargement + validation de config.json / rewards.json
├── test/run-tests.js   Suite de tests (node:assert, fakes injectés)
└── rewards.json        Table de loot du Camp du Feu Sacré
valheim-restapi/        Ajout au mod ValheimRestApi (Commands/GiveCommand.cs + notes)
docs/                   Présentation technique, guide utilisateur, mémo
```

> ℹ️ Les sources du mod **EventController** vivent dans leur propre dépôt : [mathi54/ModEventXp](https://github.com/mathi54/ModEventXp).

Voir `docs/PRESENTATION_TECHNIQUE.md` pour l'architecture détaillée et `docs/GUIDE_UTILISATEUR.md` pour l'exploitation au quotidien.
