# Guide utilisateur — La Roue de la Fortune

*Pour l'administrateur du Camp du Feu Sacré : tout ce qu'il faut savoir pour faire tourner le système au quotidien, sans toucher au code.*

## 1. Comment ça marche, vu de Discord

Dans le salon des votes, deux types de messages, tous envoyés par le bot **Le Camp du Feu Sacré** :

- **Un joueur du serveur vote** → embed "🎁 *Pseudo* a fait tourner la Roue de la Fortune !" avec la récompense gagnée. L'objet arrive dans son inventaire s'il est en jeu (dans la minute), sinon à sa prochaine connexion.
- **Quelqu'un d'extérieur vote** → simple ligne "*Pseudo* vient de voter pour le serveur !". Rien d'autre.

Le **tableau des gains** est épinglé en haut du salon et se met à jour tout seul à chaque redémarrage du bot.

Le **salon admin** (privé) ne reçoit que les vrais incidents : livraison impossible ou récompense expirée après 30 jours.

Le **1er du mois à 10 h**, le podium des meilleurs votants du mois écoulé est annoncé dans `#meilleur-votant` et les lots sont distribués (top 3 : Trésor du Jarl ; 4e-5e : Bourse du Viking).

## 2. La règle d'or à rappeler aux joueurs

> Votez avec votre **pseudo exact en jeu** (ou un pseudo déclaré dans la table d'alias auprès de Mathi).
> Il faut s'être connecté **au moins une fois** au serveur pour être reconnu.
> Une récompense non récupérée expire au bout de **30 jours**.

## 3. Où sont les fichiers

Instance AMP **"Valheim Roue de la Fortune"** → File Manager → `node-server/app/` :

| Fichier | À quoi il sert | Tu y touches ? |
|---|---|---|
| `config.json` | tokens, salons Discord, port Valheim, **alias**, réglages | parfois (alias) |
| `rewards.json` | **la table des récompenses** | oui, quand tu veux changer les gains |
| `state.json` | mémoire du bot (file d'attente, joueurs connus…) | seulement bot arrêté, cas particuliers |
| `src/` | le code | non |

**Règle absolue** : avant de modifier `state.json`, **Stop** de l'instance ; après, **Start**. Pour `rewards.json` et `config.json`, un simple **Restart** après modification suffit.

## 4. Changer les récompenses

Ouvre `rewards.json`. Chaque tier a un `weight` (poids du tirage) et une liste de `rewards` :

```json
{ "item": "Ruby", "amount": 1, "label": "Rubis", "emoji": "🔴" }
```

- `item` : le **nom de prefab exact** de l'objet (vérifiable dans WackysDatabase ou avec `spawn <nom>` en console).
- `amount` : un nombre, ou une fourchette `[min, max]`.
- `label` : ce qui s'affiche sur Discord (sans le nombre : "Piastres", pas "20 piastres").
- Poids actuels : Commun 60, Peu Commun 25, Rare 12, Ultra Rare 3 (= pourcentages).

Pour ajouter une récompense : copie une ligne, change les valeurs, **virgule entre chaque ligne, pas après la dernière**. Redémarre le bot : le tableau épinglé se met à jour.

Les lots mensuels sont dans la section `"monthly"` (`top3` et `rank4to5`), chacun avec sa liste d'items livrés ensemble.

Les élixirs disponibles : `MeadEventXP` / `MeadEventDrop` (15 min), `MeadEventXP5` / `MeadEventDrop5`, `MeadEventXP10` / `MeadEventDrop10`. Ils se cumulent quand on en boit plusieurs du même type (plafond 60 min, réglable dans le `.cfg` d'EventController).

## 5. Alias et ajout manuel de joueurs

**Table d'alias** (`config.json`, section `aliases`) — pour un joueur qui vote avec un pseudo différent de son personnage :

```json
"aliases": {
  "ketil": "Andromaque",
  "kris": "Paikan24"
}
```

Le vote « Ketil » livre alors la récompense au personnage « Andromaque ». Restart du bot après modification.

**Ajout manuel dans `knownPlayers`** (`state.json`, bot arrêté) : uniquement pour un joueur qui veut voter avant sa toute première connexion — voir `memo-ajouter-joueurs.md`. Normalement inutile : tout joueur qui se connecte est appris automatiquement.

## 6. Donner une récompense à la main (rattrapage, event, cadeau)

Sur le serveur en SSH, joueur connecté et **apparu en jeu** (pas sur l'écran de chargement) :

```bash
curl -X POST http://127.0.0.1:52858/give -H "X-Auth-Token: <ton-token>" \
     -d '{"playername":"Grudu","item":"Coins","amount":50,"message":"Cadeau du Jarl !"}'
```

Le port et le token sont ceux du `.cfg` de ValheimRestApi de l'instance visée. Le `message` s'affiche en doré à l'écran du joueur.

## 7. Simuler une récompense en attente (test)

**Stop** du bot, puis dans `state.json`, ajouter dans `"queue"` :

```json
{ "playername": "Grudu", "item": "Amber", "amount": 3, "prizeText": "🟠 3 × Ambre",
  "tierId": "commun", "voteDate": null, "kind": "vote", "queuedAt": 1786659000000 }
```

**Start** → à la prochaine connexion du joueur (après ~1-2 min en jeu), l'objet est livré.

## 8. Que faire si…

| Symptôme | Cause probable | Solution |
|---|---|---|
| Aucun message du bot, même pour un joueur connu | bot arrêté ou planté | AMP → instance → Start, lire la console |
| Joueur connu, embed OK, mais rien en jeu | le joueur n'a pas la dernière EventController | vérifier la ligne AzuAntiCheat "list of mods" dans le log serveur (version) ; modpack à jour |
| "Envoi Discord impossible … Missing Access" | le bot n'a pas accès au salon | permissions du salon : Voir, Envoyer, Intégrer des liens |
| "ValheimRestApi injoignable" en console | port/token différents entre `.cfg` du mod et `config.json` | aligner `Port` ↔ `baseUrl`, `AuthToken` ↔ `apiToken` |
| `embeds.rewardWon is not a function` (ou similaire) | fichiers de `src/` dépareillés après une mise à jour partielle | redéployer TOUT le dossier `src/` du dépôt, Restart |
| Récompense "expirée" dans le salon admin | joueur jamais reconnecté en 30 jours | rien à faire (ou rattrapage curl si mérité) |
| Vote d'un joueur du serveur affiché comme "extérieur" | pseudo ≠ personnage et pas d'alias, ou personnage jamais vu depuis la mise en place | ajouter l'alias (§5), ou une connexion du perso |
| Le tableau épinglé n'est pas à jour | pas de redémarrage après `rewards.json` | Restart du bot |
| `spawn MeadEventXP5` inconnu | EventController < 3.6.0 | déployer la 3.6.1 |

La console de l'instance AMP est ta meilleure amie : le bot y explique tout ce qu'il fait et ce qu'il ignore (votes extérieurs, claims, alias, livraisons, erreurs).

## 9. Passer d'une instance Valheim à une autre

Dans `config.json`, section `valheim` : `baseUrl` (port du `.cfg` ValheimRestApi de l'instance cible, `BindAddress = 127.0.0.1`), `apiToken` (son AuthToken), `instance` (nom, purement informatif). Restart du bot. La liste `knownPlayers` se remplira avec les joueurs de la nouvelle instance au fil des connexions.

## 10. Mises à jour de mods

À chaque nouvelle DLL EventController : serveur **+** modpack Thunderstore (tcli) **+** whitelist AzuAntiCheat (le hash change, `Instant Ban` est actif). ValheimRestApi ne va **que** sur le serveur.
