# 📝 Mémo — Ajouter des joueurs à la Roue de la Fortune

*(à faire uniquement pour un joueur qui veut voter AVANT sa première connexion au serveur —
tout joueur qui se connecte une fois est ajouté automatiquement et pour toujours)*

## Procédure

1. **AMP → instance du bot (Valheim Roue de la Fortune) → STOP**
   ⚠️ Toujours arrêter le bot avant de modifier le fichier, sinon il écrase les changements.

2. **File Manager → ouvrir `node-server/app/state.json`**

3. Dans la section `"knownPlayers"`, ajouter une ligne par joueur :

   ```json
   "knownPlayers": {
     "grudu": { "name": "Grudu", "lastSeen": 1786659000000 },
     "pseudo-en-minuscules": { "name": "PseudoExactEnJeu", "lastSeen": 1786659000000 }
   }
   ```

   Règles :
   - clé de gauche = pseudo **tout en minuscules**
   - `"name"` = pseudo avec la **casse exacte** du personnage en jeu
   - une **virgule entre chaque entrée, pas après la dernière**
   - `lastSeen` : recopier n'importe quel nombre déjà présent

4. **Sauvegarder → START du bot**

5. Vérifier la console du bot : `Connecté en tant que ...` = tout va bien.
   Si le bot ne démarre pas : erreur de virgule dans le JSON — corriger et relancer.

## Nouveauté : la table d'alias (config.json)

Pour un joueur qui vote avec un pseudo différent de son personnage (ex. pseudo Discord),
préférer la section `aliases` de `config.json` :

```json
"aliases": {
  "ketil": "Andromaque",
  "kris": "Paikan24"
}
```

Le vote « Ketil » livre alors la récompense au personnage « Andromaque ».
Simple Restart du bot après modification (pas besoin de Stop avant édition pour config.json).

## Rappels utiles (même fichier state.json)

- `"queue"` : récompenses en attente de livraison (expirent après 30 jours).
  On peut y supprimer une entrée indésirable (bot arrêté).
- `"seen"` : votes des 3 dernières heures, se nettoie tout seul — ne pas toucher.
- Un vote d'un pseudo inconnu du serveur (et sans alias) → simple ligne « X vient de voter »,
  aucune récompense.
