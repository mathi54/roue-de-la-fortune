# Présentation technique — La Roue du Valhalla

*Public : développeur / administrateur technique. Pour l'exploitation quotidienne, voir GUIDE_UTILISATEUR.md.*

## 1. Architecture

```
 Top-Serveurs API ──(poll 60 s)──► voterewards-bot (Node.js, AMP) ──► Discord (embeds)
                                          │
                                          │ POST /give  (HTTP 127.0.0.1, X-Auth-Token)
                                          ▼
                              ValheimRestApi (BepInEx serveur)
                                 GiveCommand : peer lookup + prefab check
                                          │
                                          │ ZRoutedRpc "EventController_GiveItem" (prefab, count, message)
                                          ▼
                              EventController (BepInEx client) ── Inventory.AddItem ── HUD
```

| Bloc | Emplacement | Thread / contexte | Persistance |
|---|---|---|---|
| Bot | instance AMP NodeJS, même machine que Valheim | boucles setInterval (votes 60 s, file 60 s, mensuel 300 s) | state.json |
| GiveCommand | processus serveur Valheim | thread principal Unity via MainThreadDispatcher du mod | aucune |
| EventController | processus client de chaque joueur (et serveur, inerte pour ce RPC) | main thread Unity (handler RPC) | tampon mémoire jusqu'au spawn |

## 2. Flux détaillés

### 2.1 Cycle processVotes (60 s)
1. `GET /v1/votes/last?server_token=` → votes de l'heure courante (tolérant aux noms de champs : playername | pseudo | username | name).
2. Dédup locale : clé `pseudo|datetime` dans `state.seen` (purge > 3 h). Top-Serveurs renvoie parfois le même vote avec un horodatage décalé — inoffensif, le claim tranche.
3. `GET /v1/votes/claim-username` → `claimed` : 2 déjà réclamé → silence ; 0 introuvable → silence (console) ; 1 → suite. Une erreur réseau au claim NE marque PAS le vote comme vu (retenté au cycle suivant).
4. **Alias** (`config.aliases`, insensible à la casse) : pseudo de vote → personnage en jeu. Le claim utilise le pseudo tel que voté, tout le reste utilise le personnage.
5. Filtre joueur du serveur : `rejectUnknownPlayers` (défaut true) et `knownPlayers` non vide et personnage inconnu → message public `voteOnly` ("X vient de voter pour le serveur !"), pas de tirage. Démarrage à froid (personne d'appris) → filtre inactif.
6. Tirage `drawReward` (tier pondéré → récompense uniforme → quantité fixe ou [min,max]).
7. Livraison immédiate si `isOnline && isStable` ; sinon `enqueue` + embed public.

### 2.2 Cycle deliverQueue (60 s)
1. Expiration : entrées `queuedAt` > maxAgeDays → retirées, log admin `expired`.
2. `GET /players` → apprentissage `knownPlayers` + fenêtre de stabilité : `stable = online ∩ prevOnline` (`ctx._prevOnline` mis à jour ici uniquement).
3. `takeDeliverable(stable)` → `POST /give` par entrée (message HUD dérivé de prizeText). Échec → `requeue` sans rafraîchir queuedAt + log admin.

### 2.3 Podium mensuel runMonthlyIfDue (300 s)
Le jour `monthly.dayOfMonth` à partir de `monthly.hour`, une seule fois par mois (`state.monthlyRun`, marqué AVANT distribution) : `players-ranking?type=lastMonth` → rangs 1-3 `rewards.monthly.top3`, rangs 4-5 `rank4to5` → chaque item du lot devient une entrée de file `kind: "monthly"` (livrée au personnage résolu par alias, podium affiché aux pseudos de vote) → embed podium dans `channels.classement` → tentative de livraison immédiate.

### 2.4 Livraison en jeu
- **GiveCommand** (`POST /give`, body `{playername, item, amount, message?, mode?}`) : parsing regex (pas de dépendance JSON), recherche du ZNetPeer par m_playerName (insensible à la casse), validation ZNetScene.GetPrefab + composant ItemDrop, puis :
  - `mode: "rpc"` (défaut) : `ZRoutedRpc.InvokeRoutedRPC(peer.m_uid, "EventController_GiveItem", pkg)` — `success:true` = RPC envoyé (pas de retour client).
  - `mode: "drop"` : Instantiate du prefab à GetRefPos() + 1,5 m, piles découpées par m_maxStackSize, stack poussé dans le ZDO (ZDOVars.s_stack).
- **EventController** `OnGiveItemReceived` → `HandleGiveItem` : si `Player.m_localPlayer == null` → tampon PendingGives (max 50), vidé par `FlushPendingGives()` dans le postfix Player.OnSpawned existant ; sinon cascade `TryAddToInventory` (réflexion multi-signatures Inventory.AddItem) → repli `SpawnAtLocalPlayer` → HUD doré.

## 3. Contrats d'interface

### ValheimRestApi
- Auth : header `X-Auth-Token` (ou `?token=`), config `[Security] AuthToken`.
- `GET /players` → `{ success, count, players: [...] }`. **Attention** : JsonBuilder ré-échappe les objets imbriqués (son `case string` précède la détection JSON brut) → `players` est en réalité un tableau de CHAÎNES JSON. Le bot le décode (`extractPlayerName`). Correctif possible côté mod : voir `valheim-restapi/NOTES.md`.
- `POST /give` → toujours HTTP 200 (SendJson), le statut réel est dans `success` / `error`.

### RPC EventController_GiveItem
ZPackage : `string prefabName`, `int count` (1-1000), `string message`. Enregistré des deux côtés dans RpcRegistration.TryRegister (6 RPC). Le serveur ignore le handler (IsServer()).

### Top-Serveurs
`votes/last` (heure courante), `votes/claim-username` (fenêtre 2 h, claimed 0/1/2), `servers/:token/players-ranking?type=current|lastMonth` (cache 15 min). Doc : https://top-serveurs.net/api.

## 4. Modèle de données (state.json)

```json
{
  "seen":  { "grudu|2026-08-13 22:54:46": 1786654501565 },
  "queue": [ { "playername": "Ketil", "item": "Coins", "amount": 130, "prizeText": "…",
               "tierId": "rare", "voteDate": "…", "kind": "vote|monthly", "queuedAt": 1786643038492 } ],
  "monthlyRun": "2026-09",
  "pinnedMessageId": "1537578667918491701",
  "knownPlayers": { "grudu": { "name": "Grudu", "lastSeen": 1786658943842 } }
}
```
Écriture atomique (.tmp + rename). Toute édition manuelle se fait BOT ARRÊTÉ.

## 5. Élixirs d'event (EventController ≥ 3.6.1)

| Prefab | Type | Durée |
|---|---|---|
| MeadEventXP / MeadEventDrop | historiques | [Potions] PotionDurationMinutes (15) |
| MeadEventXP5 / MeadEventDrop5 | fixe | 5 min |
| MeadEventXP10 / MeadEventDrop10 | fixe | 10 min |

Un StatusEffect (SE_EventBuff) par prefab (le TTL est porté par le SE). **Cumul** : dans Setup, si un SE_EventBuff de même Kind est déjà actif, sa durée restante est augmentée (m_ttl = m_time + total, plafond PotionMaxStackMinutes) et l'exemplaire courant s'efface (m_ttl = 0.01, flag _mergedIntoExisting). Stop() ne coupe le drapeau local que si aucun autre élixir du même type n'est actif. Nom de buff générique ($se_eventbuff_xp / _drop).

## 6. Sécurité

- ValheimRestApi lié à 127.0.0.1 + token : jamais exposé à Internet.
- Le bot n'écoute sur aucun port (connexions sortantes uniquement).
- GiveCommand refuse amount hors 1-1000 et les prefabs inconnus / sans ItemDrop.
- Anti-doublon vote : claim Top-Serveurs (serveur) + seen (local).
- Anti-abus podium : monthlyRun marqué avant distribution.
- AzuAntiCheat : chaque nouvelle DLL EventController doit être whitelistée (hash) — Instant Ban est actif sur le serveur.

## 7. Tests

`npm test` — 36 tests sur core.js, store.js, rewards.js, topserveurs.js, valheim.js, embeds.js avec fakes injectés (ctx.ts, ctx.valheim, ctx.notify, ctx.rng, ctx._prevOnline). Couvre : livraison immédiate/différée, claim 0/1/2, erreurs réseau, fenêtre de stabilité, votant extérieur, alias, expiration, podium (jour, doublon), format réel de /players, distribution des poids sur 20 000 tirages.

## 8. Points d'extension

- **Paliers collectifs** (prévu) : stats mensuelles → seuils de votes → lots à tous les votants connus ; nouvelle boucle dans core.js, section milestones dans rewards.json.
- Commande /mesvotes (slash command discord.js) : lecture de state.queue par pseudo.
- Multiplicateur d'événement : facteur appliqué dans drawReward selon un flag EventController.
- Migration state.json → SQLite si la charge augmente (interface Store inchangée).
- Correctif JsonBuilder côté mod pour renvoyer de vrais objets dans /players (le bot reste compatible).
