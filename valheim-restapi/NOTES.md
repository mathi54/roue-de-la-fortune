# ValheimRestApi — ajout GiveCommand (Roue du Valhalla)

Ce dossier ne contient QUE l'ajout à intégrer dans le dépôt ValheimRestApi existant :

- `Commands/GiveCommand.cs` → à copier dans le dossier `Commands/` du projet (à côté de `InfoCommand.cs`, `PlayersCommand.cs`…).
  Route `POST /give`, body `{ playername, item, amount, message?, mode? }`, modes `rpc` (défaut, insertion inventaire via EventController ≥ 3.5.0) ou `drop` (spawn aux pieds).

Aucune autre modification n'est requise : l'auto-découverte `ActionManager` enregistre la route, `HttpServer` exécute déjà les commandes sur le thread principal (`MainThreadDispatcher`).

## Correctif optionnel — JsonBuilder.cs

`/players` renvoie un tableau de chaînes JSON ré-échappées (le `case string` de `AppendValue` précède la détection "JSON brut"). Le bot le décode, mais pour renvoyer de vrais objets :

```csharp
case string s:
    if (s.Length > 0 && (s[0] == '{' || s[0] == '['))
        sb.Append(s);        // JSON déjà sérialisé (JsonBuilder.Object imbriqué)
    else
        AppendString(sb, s);
    break;
```

## Config recommandée (`BepInEx/config/fr.enfantsodin.valheim.restapi.cfg`)

```ini
[Network]
Port = 52858
BindAddress = 127.0.0.1
[Security]
AuthToken = <chaine-secrete>
```
