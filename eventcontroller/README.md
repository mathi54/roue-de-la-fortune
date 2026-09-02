# EventController v3.1

Mod Valheim BepInEx **client + serveur** qui ajoute la commande
`xpevent` pour declencher des events boostant XP et drops de ressources.

## Nouveautes v3.1

- **Approche blacklist** : par defaut TOUS les drops sont multiplies,
  sauf ceux explicitement exclus dans le fichier de config
- **Trophees auto-exclus** : tous les prefabs commencant par `Trophy`
  ne sont jamais multiplies (drops de mob/boss)
- **Items exclus configurables** : liste editable dans `mathi.eventcontroller.cfg`
- **Characters exclus configurables** : si un mob/boss de cette liste
  est tue, AUCUN de ses drops n'est multiplie
- **Hot-reload des blacklists** : modifies le .cfg et c'est applique
  sans restart
- **Fix broadcast** : le message d'annonce s'affiche maintenant au
  centre de l'ecran (en plus du chat) pour tous les joueurs

## Installation

DLL requise cote SERVEUR ET CLIENT (chaque joueur).

## Configuration des blacklists

`BepInEx/config/mathi.eventcontroller.cfg` :

```ini
[Blacklist]
## Items prefabs a NE PAS multiplier (separes par virgule)
ExcludedItems = Wishbone,DragonEgg,DragonTear,YagluthDrop,QueenDrop,FaderDrop,...

## Characters dont les drops ne sont pas multiplies
ExcludedCharacters = Eikthyr,gd_king,Bonemass,Dragon,GoblinKing,SeekerQueen,Fader,...
```

**Items exclus par defaut** : items uniques de boss (Wishbone, DragonEgg,
DragonTear, drops Yagluth/Queen/Fader), cles de crypte, items legendaires
(Eitr, DvergrKey), royal jelly, trophees specifiques des boss.

**Trophees auto-exclus** : TOUS les prefabs commencant par "Trophy"
sont automatiquement non-multiplies, sans avoir besoin de les lister.

**Characters exclus par defaut** : tous les boss vanilla (Eikthyr,
gd_king, Bonemass, Dragon, GoblinKing, SeekerQueen, Fader) + Hildir
mini-boss + creatures speciales.

### Listes de reference pour ajouter des entrees

- Items : https://valheim-modding.github.io/Jotunn/data/items/item-list.html
- Characters : https://valheim-modding.github.io/Jotunn/data/prefabs/character-list.html

## Commandes

```
xpevent start <nom> [xpMult] [dropMult]   # demarre un event
xpevent stop                              # arrete l'event en cours
xpevent status                            # affiche l'etat
```

Le nom est obligatoire et doit etre du texte.

## Trois facons d'appeler la commande

1. **Cron Job** (recommande pour automatisation)
2. **F5 client (admin)** : `xpevent start MonTest 2.0 2.0`
3. **Console serveur AMP** : tape directement la commande

## Compilation

```powershell
dotnet build -c Release
```

## Architecture technique

### Drops items (Patch_ItemDropAwake)
Cote client, hook `ItemDrop.Awake`. Multiplie le stack si :
- L'item n'est pas un trophee (StartsWith "Trophy")
- L'item n'est pas dans `ExcludedItems`
- L'item ne vient pas d'un character blacklist (flag ZDO)

### Drops de characters (Patch_CharacterDropGenerate + Patch_ItemDropOnEnable)
Cote serveur, hook `CharacterDrop.GenerateDrops`. Si le mob est dans
`ExcludedCharacters`, on active un flag global pendant l'execution. Le
hook `ItemDrop.OnEnable` set un flag ZDO `mathi_evt_excl` sur chaque
item spawne. `ItemDrop.Awake` cote client lit ce flag et skip la
multiplication.

### Pickables (Patch_PickableInteract)
Cote client, hook `Pickable.Interact`. Modifie m_amount avant l'appel,
restore en Postfix.

### XP (Patch_RaiseSkill)
Cote client, hook `Skills.RaiseSkill`. Multiplie le factor avant l'appel.

### Sync state (RPC EventController_State)
Le serveur broadcast l'etat (running, xp, drop, name) a chaque
client connecte au demarrage/arret d'event, et aux nouveaux joueurs
qui se connectent en plein event.

### Commande F5 (RPC EventController_Cmd)
Les clients peuvent envoyer la commande au serveur via RPC custom.
Le serveur valide admin via SteamID et execute.
