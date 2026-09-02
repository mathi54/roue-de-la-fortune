# EventController — Changelog

## v3.6.1 — Cumul des elixirs de meme type

- Boire un elixir alors qu'un elixir du MEME type (XP ou butin) est deja actif AJOUTE sa duree au buff existant (5 + 10 = 15 min restantes) au lieu d'afficher deux buffs separes. Plafond configurable : [Potions] PotionMaxStackMinutes (60 par defaut, 0 = illimite).
- Correction d'un bug induit par la v3.6.0 : l'expiration d'une fiole courte coupait le bonus alors qu'une fiole longue du meme type tournait encore. Stop() ne coupe desormais le bonus que s'il ne reste aucun autre elixir actif du meme type.
- Nom de buff generique a l'ecran ("Elixir d'experience" / "Elixir de butin", sans duree) ; les noms d'items gardent la duree.

## v3.6.0 — Variantes d'elixirs 5 et 10 min (Roue du Valhalla)

- 4 nouveaux elixirs a duree FIXE : MeadEventXP5, MeadEventXP10, MeadEventDrop5, MeadEventDrop10 (chacun avec son StatusEffect dedie, le TTL etant porte par le SE).
- Les 2 potions historiques (MeadEventXP / MeadEventDrop) gardent leurs noms et leur duree configuree (CfgPotionDurationMinutes, 15 min par defaut) : aucun impact sur xpevent give, les blacklists ou les fioles deja en circulation.
- Enregistrement refactorise en une methode generique RegisterPotion (prefab clone, SE, traductions FR/EN avec duree affichee dans le nom et la description).
- Usage prevu : recompenses de vote (tier Ultra Rare : 5/10 min) et lots mensuels du podium (15 min pour le top 3, 5 min pour les rangs 4-5).

## v3.5.1 — Tampon anti "livre trop vite"

- Un give recu pendant l'ecran de chargement (Player.m_localPlayer null) n'echoue plus : il est mis en tampon et livre automatiquement au spawn du joueur (hook Player.OnSpawned existant). C'etait la cause des recompenses perdues quand le bot livrait dans les secondes suivant la connexion.

## v3.5.0 — Give generique (Roue du Valhalla)

- Nouveau RPC `EventController_GiveItem` (6e RPC enregistre) : le serveur peut donner N'IMPORTE quel item a UN joueur cible, avec message HUD personnalise. Payload : (prefabName: string, count: int, message: string).
- Nouvelle methode `EventPotions.HandleGiveItem` : meme cascade que les elixirs (Inventory.AddItem par reflexion, repli au sol auto-pickup si inventaire plein, message HUD dore).
- Utilise par ValheimRestApi (`POST /give`, mode rpc) pour les recompenses de vote Top-Serveurs du bot "Roue du Valhalla".
- Aucun changement sur les elixirs, blacklists ou le doublement au ramassage.

## v3.4.1

- Les elixirs sont ajoutes DIRECTEMENT dans l'inventaire du joueur (Inventory.AddItem resolu par reflexion, cascade de signatures multi-versions). Repli automatique : fiole deposee aux pieds si l'inventaire est plein, avec message HUD adapte.
- Fix compilation : EventPotions passe en public (coherence d'accessibilite avec SE_EventBuff.Kind).

## v3.4.0 — Potions uniquement (retrait de l'event global)

Le mod repose desormais entierement sur les elixirs opt-in ; le systeme d'event global (start/stop, etat persiste, annonces HUD, multiplicateurs serveur) a ete retire.

**Ce qui change :**
- `xpevent start` / `xpevent stop` n'existent plus (un message de migration s'affiche si tapes). Commandes restantes : `xpevent give <xp|drop|both> [nombre]` et `xpevent status` (config potions + tailles de blacklists).
- Plus AUCUNE multiplication a la source : `ItemDrop.Awake` ne fait plus que MARQUER les drops de source legitime (`mathi_evt_src`). Le doublement se fait exclusivement au ramassage chez les joueurs bufes.
- Le RPC State ne transporte plus que les blacklists (le client en a besoin : la potion de butin evalue `ShouldMultiplyItem` chez le ramasseur). Push connexion + pull au spawn + heartbeat conserves.
- Les hooks Pickable ne multiplient plus `m_amount` : ils ne servent plus qu'a poser le contexte pour le marquage.
- Sections `.cfg` supprimees : `[State]` et `[Defaults]` (les anciennes cles restantes dans vos fichiers sont inertes, supprimables a la main). `[Blacklist]`, `[Debug]`, `[Sync]`, `[Potions]` conservees.
- Protection heritage : les items encore au sol multiplies par un ancien event (marques `mathi_evt_done`) ne seront pas re-doubles par la potion.

**Inchange :** architecture par contexte de source (anti-dupe par construction), blacklists (les drops d'un boss exclu ne sont jamais marques, donc jamais doubles), les deux elixirs et leur distribution.

## v3.3.0 — Potions d'event personnelles

Deux consommables opt-in distribues par l'admin (non craftables), duree 15 min configurable ([Potions] du .cfg) :

- **Elixir d'experience** (`MeadEventXP`, fiole jaune) : XP x2 pour celui qui la boit. Calcul 100% local au joueur.
- **Elixir de butin** (`MeadEventDrop`, fiole bleue) : butin x2 **au ramassage** pour celui qui la boit. Les drops de source legitime (mob, arbre, rocher, cueillette) sont marques dans leur ZDO (`mathi_evt_src`) meme hors event ; au ramassage par un joueur bufe, le stack double juste avant l'entree en inventaire. Un item jete par un joueur, sorti d'un coffre ou d'un smelter n'a pas la marque : dupe impossible par construction.

Commande : `xpevent give <xp|drop|both> [nombre]` — fait apparaitre les elixirs aux pieds de chaque joueur connecte (auto-ramasses), message HUD a la reception. Nouveau RPC `EventController_Give` (5 RPCs au total).

Regle anti-cumul : event global et potion ne se multiplient jamais entre eux — le **maximum** des deux s'applique (XP : max des multiplicateurs ; butin : un drop deja x2 par l'event n'est pas re-double au ramassage).

Limites connues : le buff ne persiste pas a la deconnexion (comportement vanilla des potions) ; pendant un event global de meme valeur, l'elixir de butin n'apporte rien (regle du max) ; nouvelle dependance de compilation : Jotunn.dll (deja present sur serveurs et clients).

## v3.2.1 — Ultra-review pré-multi

- **FIX annonce HUD à la connexion** : un joueur qui se connectait pendant un event actif ne voyait jamais l'annonce (le state initial arrivait pendant le chargement, `MessageHud` inexistant, et le changement d'état était consommé sans affichage). Nouveau flag client `HudEventAnnounced` : l'annonce s'affiche au premier state reçu après le spawn (pull ou heartbeat), et l'annonce de fin ne s'affiche que si le début avait été annoncé.
- **Logs de hooks harmonisés** : chaque hook de contexte logge désormais la méthode exacte sur laquelle il s'est posé (`[Ctx_TreeLog] hook sur TreeLog.Destroy`, etc.) — diagnostic immédiat après une MAJ de Valheim.
- **Dédoublonnage des logs de chargement** : Harmony appelle `Prepare()` deux fois, les hooks loggaient donc en double ; corrigé avec un flag de recherche unique.
- Aucune modification de la logique de multiplication : identique à la 3.2.0 validée en solo.

# v3.2.0 — Refonte multijoueur

## Symptômes corrigés

- « Le mod ne marche que sur certaines choses » (pickables morts en multi, certains mobs non doublés)
- « Dès que quelqu'un se connecte, plus rien ne marche » (+ duplication du stock au sol)
- Erreurs `Failed to check permission CommunicateWithUsingText: UserID was invalid` dans les logs client

## Les 9 bugs identifiés dans l'audit de la v3.1.4

| # | Gravité | Bug | Conséquence |
|---|---------|-----|-------------|
| 1 | CRITIQUE | `ItemDrop.Awake` multipliait aussi les objets **chargés** (zones, réplication, connexion) | Un joueur qui se connecte pendant un event devient owner de ses zones et **double réellement tout le stock au sol** |
| 2 | CRITIQUE | Aucune distinction de source du drop | Items jetés par un joueur ×2 (**dupe volontaire**), sorties de smelter/four ×2 (production doublée) |
| 3 | CRITIQUE | `Pickable.Interact` s'exécute chez le **cliqueur**, mais le spawn a lieu chez l'**owner** via `RPC_Pick` | Pickables cassés à plusieurs ; en solo, double multiplication en cascade (Interact ×2 PUIS Awake ×2 = jusqu'à ×4, visible dans les logs) |
| 4 | GRAVE | État persisté du `.cfg` restauré au boot **sans savoir si client ou serveur** | Event fantôme côté client possible, amplifie le bug 1 pendant le chargement du monde |
| 5 | PERF | `AutoStackItems` : 2× `Physics.OverlapSphere(4m)` par fusion d'items | Coût CPU serveur inutile |
| 6 | BUG | `SendChatReply` via le RPC natif `ChatMessage` mal formé | Les erreurs `UserID was invalid` des logs client |
| 7 | RÉSEAU | `InvokeRoutedRPC(0L, ...)` = Everybody | La commande admin partait chez **tous** les clients |
| 8 | FIABILITÉ | Sync d'état en push-only | Un client qui rate le RPC de connexion reste désynchronisé jusqu'au heartbeat |
| 9 | LOG | `stateActuallyChanged` comparé **après** écrasement des valeurs | Détection de changement xp/drop toujours fausse |

## Nouvelle architecture : multiplication par contexte de source

Un drop n'est multiplié **que s'il naît dans le call stack d'une source reconnue** (`DropContext` thread-static avec compteur de profondeur) :

- **MobDrop** : `CharacterDrop.DropItems` + `Ragdoll.SpawnLoot` (nouveau — beaucoup d'humanoïdes droppent via le cadavre, pas à la mort)
- **ExcludedMob** : mob blacklisté → drops marqués traités, jamais multipliés
- **Destruction** : `DropOnDestroyed.OnDestroyed`, `TreeLog`, `TreeBase`, `MineRock`, `MineRock5`
- **PickableSpawn** : la multiplication se fait **une seule fois** via `m_amount` dans `Pickable.RPC_Pick` (chez l'owner → fiable en multi) ; les ItemDrop spawnés sont marqués sans re-multiplication

Tout objet instancié **hors contexte** (chargement réseau, item jeté par un joueur, sortie de station) n'est **jamais touché, par construction**. Les structures détruites (`WearNTear`) ne sont volontairement PAS un contexte (pas de dupe de matériaux de construction).

Tous les hooks de contexte utilisent `Prepare()` : si une méthode n'existe pas dans une future version de Valheim, seul ce type de source se désactive avec un warning — **plus jamais de crash PatchAll**.

## Autres changements

- **État persisté serveur-only** : lu en pending au `Awake`, appliqué dans `ApplyPendingStateIfServer()` au démarrage de ZNet, uniquement si `IsServer()`
- **Nouveau RPC `EventController_Request`** : le client demande l'état au serveur à son spawn (`Player.OnSpawned`) — triple filet : push connexion + pull spawn + heartbeat 5s
- **Nouveau RPC `EventController_Msg`** : réponses admin via `MessageHud` (remplace `ChatMessage` natif → fin des erreurs UserID)
- Commande admin routée vers `GetServerPeerID()` au lieu de Everybody
- `AutoStackItems` supprimé (inutile avec la nouvelle architecture)
- Log `[CHANGEMENT]`/`[heartbeat]` corrigé (capture de l'état avant écrasement)

## Déploiement

1. Remplacer les 3 fichiers `src/` + `EventController.csproj` dans `D:\DLL\eventcontrollerv3\`
2. `dotnet build -c Release`
3. Déployer la DLL **côté serveur ET côté client** (les 4 instances)
4. Redémarrer les serveurs
5. Push GitHub pour synchro avec le dev

## Plan de test multijoueur (2 joueurs minimum : A = admin, B = joueur)

| Test | Attendu |
|------|---------|
| A lance `xpevent start Test`, A et B coupent chacun un arbre | Bois ×2 **des deux côtés** |
| B mine du cuivre pendant l'event | Minerai ×2 |
| B cueille des baies/champignons (même si A les a plantés) | ×2 (RPC_Pick owner-side) |
| Un joueur C se connecte pendant l'event, regarde les items déjà au sol | Stock au sol **INCHANGÉ** (fini la duplication) |
| B jette 1 bois de son inventaire au sol | Reste **1** (fini la dupe) |
| Un smelter sort un lingot pendant l'event | Production **normale** ×1 |
| B tue un mob normal / A tue un boss blacklisté | Mob ×2, boss ×1 |
| A fait `xpevent stop`, B coupe un arbre 10s après | ×1 chez B (heartbeat) |
| B se déconnecte/reconnecte pendant l'event | HUD event affiché au spawn, drops ×2 immédiatement (pull) |

## Limites connues

- Le loot spawné via `Ragdoll` ne connaît plus le mob d'origine : la blacklist `ExcludedCharacters` ne s'y applique pas (les boss vanilla n'utilisent pas ce chemin, impact nul en pratique)
- Le loot des coffres de donjon n'est pas multiplié (pas un contexte) — comportement voulu
