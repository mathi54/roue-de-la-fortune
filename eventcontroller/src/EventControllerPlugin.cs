using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EventController
{
    [BepInDependency("com.jotunn.jotunn", BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin(GUID, NAME, VERSION)]
    public class EventControllerPlugin : BaseUnityPlugin
    {
        public const string GUID = "mathi.eventcontroller";
        public const string NAME = "Event Controller";
        public const string VERSION = "3.6.1";

        internal static ManualLogSource Log;
        internal static EventControllerPlugin Instance;

        // Blacklists configurables (CSV, separe par virgules)
        internal static ConfigEntry<string> CfgExcludedItems;
        internal static ConfigEntry<string> CfgExcludedCharacters;

        // Debug
        internal static ConfigEntry<bool> CfgVerboseLog;

        // Sync periodique des blacklists vers les clients
        internal static ConfigEntry<float> CfgHeartbeatInterval;

        // Potions d'event personnelles
        internal static ConfigEntry<float> CfgPotionXpMult;
        internal static ConfigEntry<float> CfgPotionDropMult;
        internal static ConfigEntry<float> CfgPotionDurationMinutes;
        internal static ConfigEntry<float> CfgPotionMaxStackMinutes;

        // Sets compiles a partir de la config (recompiles a chaque changement)
        internal static HashSet<string> ExcludedItemsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal static HashSet<string> ExcludedCharactersSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ============================================================
        // BLACKLIST DEFAULT : items qui ne doivent JAMAIS etre doubles
        // par la potion de butin.
        //
        // Inclus par defaut :
        //  - TOUS les trophees (prefabs commencant par "Trophy",
        //    detectes via StartsWith dans ShouldMultiplyItem)
        //  - Items boss uniques, quetes, cles, artefacts rares
        // ============================================================
        private static readonly string DefaultExcludedItems = string.Join(",", new[]
        {
            // Items uniques / quetes / cles
            "Wishbone", "DragonEgg", "DragonTear", "YagluthDrop",
            "QueenDrop", "FaderDrop",
            "CryptKey", "TrophyForestcrypt", "MeadowsCryptKey",
            "ChestRunestone", "Tarball",

            // Cristaux de teleportation et items de progression
            "Eitr", "BlackCore",

            // Items legendaires/rares
            "DvergrKeyFragment", "DvergrKey",
            "RoyalJelly", // rare, vient des reines abeilles

            // Trophees specifiques boss
            "TrophyEikthyr", "TrophyTheElder", "TrophyBonemass",
            "TrophyDragonQueen", "TrophyGoblinKing",
            "TrophySeekerQueen", "TrophyFader"
        });

        // ============================================================
        // BLACKLIST DEFAULT : characters dont les drops ne sont jamais
        // marques comme source legitime (boss + characters speciaux).
        // Leur butin ne sera donc jamais double par la potion.
        // ============================================================
        private static readonly string DefaultExcludedCharacters = string.Join(",", new[]
        {
            // Bosses
            "Eikthyr", "gd_king", "Bonemass", "Dragon", "GoblinKing",
            "SeekerQueen", "Fader",

            // Mini-boss / forsaken alternatives
            "Hildir_cave_hermitcrab", "Hildir_cave_gjall", "Hildir_cave_fenring",
            "TheHive",

            // Special creatures
            "BonemassDragon"
        });

        void Awake()
        {
            Instance = this;
            Log = Logger;

            CfgExcludedItems = Config.Bind("Blacklist", "ExcludedItems",
                DefaultExcludedItems,
                "Liste d'items (prefabs) a NE PAS doubler avec la potion de butin, separes par virgule. " +
                "Voir https://valheim-modding.github.io/Jotunn/data/items/item-list.html pour la liste des prefabs. " +
                "Note: tous les prefabs commencant par 'Trophy' sont AUTOMATIQUEMENT exclus.");

            CfgExcludedCharacters = Config.Bind("Blacklist", "ExcludedCharacters",
                DefaultExcludedCharacters,
                "Liste de characters (prefabs de mob/boss) dont les drops NE doivent JAMAIS etre doubles, " +
                "separes par virgule. Si un mob de cette liste est tue, aucun de ses drops n'est marque. " +
                "Voir https://valheim-modding.github.io/Jotunn/data/prefabs/character-list.html");

            CfgVerboseLog = Config.Bind("Debug", "VerboseLog", false,
                "Logs detailles : marquage des drops, doublement au ramassage, gains XP, heartbeat.");

            CfgHeartbeatInterval = Config.Bind("Sync", "HeartbeatIntervalSeconds", 5.0f,
                "Intervalle (secondes) entre les envois periodiques des blacklists aux clients. " +
                "La potion de butin evalue la blacklist chez le RAMASSEUR : ce heartbeat garantit " +
                "que chaque client a la liste a jour du serveur. Mettre 0 pour desactiver. " +
                "Recommande : 5.0.");

            CfgPotionXpMult = Config.Bind("Potions", "PotionXpMultiplier", 2.0f,
                "Multiplicateur XP de l'Elixir d'experience.");
            CfgPotionDropMult = Config.Bind("Potions", "PotionDropMultiplier", 2.0f,
                "Multiplicateur de butin de l'Elixir de butin (applique au RAMASSAGE des drops " +
                "de source legitime : mobs, arbres, rochers, cueillette).");
            CfgPotionDurationMinutes = Config.Bind("Potions", "PotionDurationMinutes", 15.0f,
                "Duree (minutes) des deux elixirs. Necessite un redemarrage pour etre prise en " +
                "compte (la duree est fixee a la creation des potions au chargement du jeu).");

            CfgPotionMaxStackMinutes = Config.Bind("Potions", "PotionMaxStackMinutes", 60.0f,
                "v3.6.1 : boire un elixir alors qu'un elixir du MEME type est actif AJOUTE sa duree " +
                "au buff existant (5 + 10 = 15 min restantes) au lieu de creer un second buff. " +
                "Ce plafond limite le temps restant total cumule (minutes). 0 = pas de plafond.");

            // Compile les blacklists au demarrage + hot-reload si l'utilisateur edite
            RebuildBlacklists();
            CfgExcludedItems.SettingChanged += (_, __) => { RebuildBlacklists(); StateSync.BroadcastToAll(); };
            CfgExcludedCharacters.SettingChanged += (_, __) => { RebuildBlacklists(); StateSync.BroadcastToAll(); };

            new Harmony(GUID).PatchAll();
            Commands.Register();
            EventPotions.Init(); // enregistrement des elixirs (via Jotunn)
            Log.LogInfo($"{NAME} v{VERSION} charge.");
            Log.LogInfo($"  Items exclus : {ExcludedItemsSet.Count} entrees");
            Log.LogInfo($"  Characters exclus : {ExcludedCharactersSet.Count} entrees");

            // Heartbeat : envoi periodique des blacklists (serveur uniquement).
            StartCoroutine(HeartbeatLoop());
        }

        // ============================================================
        // HEARTBEAT
        //
        // Envoi periodique des blacklists aux clients pour garantir que
        // tous les peers sont a jour, meme si un RPC ponctuel a ete
        // perdu (probleme reseau, mod tiers qui filtre, etc.).
        //
        // S'execute UNIQUEMENT cote serveur.
        // ============================================================
        private System.Collections.IEnumerator HeartbeatLoop()
        {
            // Petit delai initial pour laisser ZNet s'initialiser
            yield return new WaitForSeconds(10f);

            while (true)
            {
                float interval = CfgHeartbeatInterval?.Value ?? 5f;
                if (interval <= 0f)
                {
                    yield return new WaitForSeconds(5f); // re-check dans 5s si remis a > 0
                    continue;
                }

                if (ZNet.instance != null
                    && ZNet.instance.IsServer()
                    && ZNet.instance.GetPeers().Count > 0)
                {
                    if (CfgVerboseLog.Value)
                        Log.LogInfo($"[Heartbeat] Envoi periodique des blacklists (interval={interval}s)");
                    StateSync.BroadcastToAllSilent();
                }

                yield return new WaitForSeconds(interval);
            }
        }

        // ============================================================
        // Reconstruit les Sets a partir des valeurs CSV de la config.
        // Appelee au demarrage ET quand l'utilisateur modifie la config.
        // ============================================================
        internal static void RebuildBlacklists()
        {
            ExcludedItemsSet = ParseCsv(CfgExcludedItems.Value);
            ExcludedCharactersSet = ParseCsv(CfgExcludedCharacters.Value);
            Log?.LogInfo($"[Blacklists] Rebuild : {ExcludedItemsSet.Count} items, {ExcludedCharactersSet.Count} characters");
        }

        private static HashSet<string> ParseCsv(string csv)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(csv)) return set;
            foreach (var raw in csv.Split(','))
            {
                var trimmed = raw.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    set.Add(trimmed);
            }
            return set;
        }

        // ============================================================
        // Met a jour les blacklists en memoire a partir du reseau
        // (sans toucher au .cfg local du client). Retourne true si
        // les listes ont effectivement change.
        // ============================================================
        internal static bool UpdateBlacklistsFromNetwork(string csvItems, string csvChars)
        {
            var newItems = ParseCsv(csvItems);
            var newChars = ParseCsv(csvChars);

            bool changed = !newItems.SetEquals(ExcludedItemsSet)
                        || !newChars.SetEquals(ExcludedCharactersSet);

            ExcludedItemsSet = newItems;
            ExcludedCharactersSet = newChars;

            if (changed)
                Log?.LogInfo($"[Blacklists] Sync reseau : {ExcludedItemsSet.Count} items, " +
                             $"{ExcludedCharactersSet.Count} characters (fichier .cfg local preserve)");
            return changed;
        }

        // ============================================================
        // Reinitialise l'etat local du mod au retour au menu principal
        // ============================================================
        internal static void ResetLocalState()
        {
            EventPotions.ResetLocalBuffs();
            RebuildBlacklists();
            Log?.LogInfo("[LocalState] Etat reinitialise au retour au menu (buffs off, config locale restauree).");
        }

        // ============================================================
        // Nettoyage robuste des noms de prefab
        // Supprime recursivement "(Clone)" et nettoie les espaces.
        // ============================================================
        public static string CleanPrefabName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string cleaned = name;
            while (cleaned.Contains("(Clone)"))
            {
                cleaned = cleaned.Replace("(Clone)", "");
            }
            return cleaned.Trim();
        }

        // ============================================================
        // Test centralise : la potion de butin peut-elle doubler ce
        // prefab d'item ?
        //
        // Regles :
        // 1. Si le nom commence par "Trophy" -> NON
        // 2. Si le nom est dans ExcludedItemsSet -> NON
        // 3. Sinon -> OUI
        // ============================================================
        public static bool ShouldMultiplyItem(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;

            string cleanedName = CleanPrefabName(prefabName);

            // Tous les trophees auto-exclus (TrophyDeer, TrophyGreydwarf, etc.)
            if (cleanedName.StartsWith("Trophy", StringComparison.OrdinalIgnoreCase))
                return false;

            if (ExcludedItemsSet.Contains(cleanedName))
                return false;

            return true;
        }

        // ============================================================
        // Test centralise : faut-il exclure les drops de ce character ?
        // ============================================================
        public static bool IsCharacterExcluded(string characterName)
        {
            if (string.IsNullOrEmpty(characterName)) return false;
            string cleanedName = CleanPrefabName(characterName);
            return ExcludedCharactersSet.Contains(cleanedName);
        }
    }
}
