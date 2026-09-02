using System;
using System.Collections.Generic;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace EventController
{
    // ================================================================
    // v3.3.0 : POTIONS D'EVENT PERSONNELLES
    //
    // Deux consommables distribues par l'admin (non craftables) :
    //  - Elixir d'experience (MeadEventXP)   : XP x2 pendant 15 min
    //  - Elixir de butin     (MeadEventDrop) : drops x2 pendant 15 min
    //
    // Contrairement a l'event global (xpevent start), la potion est un
    // choix INDIVIDUEL : seul celui qui la boit est affecte.
    //
    // Regle anti-cumul : si un event global tourne en meme temps, on
    // applique le MAXIMUM des deux multiplicateurs, jamais le produit.
    //
    // Mecanique du drop perso ("ramassage double") : les drops de
    // source legitime (mob, destruction, pickable) sont marques dans
    // leur ZDO par Patch_ItemDropAwake (SourceKey), event actif ou non.
    // Quand un joueur bufe RAMASSE un item marque, son stack est double
    // juste avant l'ajout a l'inventaire (Patch_PickupDoubling).
    // -> Tout s'execute chez le ramasseur : aucun probleme d'ownership.
    // -> Un item jete par un joueur n'a pas la marque : dupe impossible.
    // ================================================================

    // StatusEffect custom : maintient lui-meme les drapeaux locaux du
    // joueur, via Setup (application) et Stop (expiration/retrait/mort).
    // Aucune dependance aux signatures de SEMan qui varient entre
    // versions du jeu.
    public class SE_EventBuff : StatusEffect
    {
        public EventPotions.BuffKind Kind;
        private Character _target;
        private bool _mergedIntoExisting; // v3.6.1 : cet exemplaire a fusionne dans un buff deja actif

        public override void Setup(Character character)
        {
            base.Setup(character);
            _target = character;
            if (character == null || character != Player.m_localPlayer) return;

            // v3.6.1 : CUMUL. Si un elixir du meme type est deja actif, on ajoute
            // notre duree au buff existant et on s'efface aussitot -> un seul
            // buff a l'ecran, temps restant additionne (plafonne par la config).
            var existing = FindOtherActive(character);
            if (existing != null)
            {
                float remaining = Mathf.Max(0f, existing.m_ttl - existing.m_time);
                float total = remaining + m_ttl;
                float cap = EventControllerPlugin.CfgPotionMaxStackMinutes.Value * 60f;
                if (cap > 0f) total = Mathf.Min(total, cap);
                existing.m_ttl = existing.m_time + total;

                _mergedIntoExisting = true;
                m_ttl = 0.01f; // expire au prochain update, sans toucher aux drapeaux
                EventControllerPlugin.Log.LogInfo(
                    $"[Potion] Elixir {Kind} cumule : {total / 60f:0.#} min restantes.");
                return;
            }

            EventPotions.SetLocalBuff(Kind, true);
            EventControllerPlugin.Log.LogInfo(
                $"[Potion] Buff {Kind} actif sur le joueur local ({m_ttl / 60f:0} min).");
        }

        public override void Stop()
        {
            base.Stop();
            if (_target == null || _target != Player.m_localPlayer) return;
            if (_mergedIntoExisting) return; // n'a jamais porte le buff, rien a couper

            // v3.6.1 : on ne coupe le bonus que s'il ne reste AUCUN autre elixir
            // du meme type actif (corrige la perte du bonus quand une fiole
            // courte expirait alors qu'une longue tournait encore).
            bool stillActive = FindOtherActive(_target) != null;
            EventPotions.SetLocalBuff(Kind, stillActive);
            EventControllerPlugin.Log.LogInfo(stillActive
                ? $"[Potion] Un elixir {Kind} s'est termine, un autre est encore actif."
                : $"[Potion] Buff {Kind} termine pour le joueur local.");
        }

        /// <summary>Un autre SE_EventBuff du meme type, actif et non fusionne, sur ce personnage.</summary>
        private SE_EventBuff FindOtherActive(Character character)
        {
            try
            {
                var seman = character.GetSEMan();
                if (seman == null) return null;
                foreach (var se in seman.GetStatusEffects())
                {
                    if (se is SE_EventBuff other && other != this && other.Kind == Kind
                        && !other._mergedIntoExisting && !other.IsDone())
                        return other;
                }
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogWarning($"[Potion] FindOtherActive failed : {e.Message}");
            }
            return null;
        }
    }

    public static class EventPotions
    {
        public enum BuffKind { Xp, Drop }

        public const string XpPotionPrefab = "MeadEventXP";
        public const string DropPotionPrefab = "MeadEventDrop";
        public const string XpEffectName = "SE_MathiEventXP";
        public const string DropEffectName = "SE_MathiEventDrop";

        // Drapeaux locaux : la verite pour les patches de CE client.
        public static bool LocalXpBuff;
        public static bool LocalDropBuff;

        private static bool _registered;

        public static void SetLocalBuff(BuffKind kind, bool active)
        {
            if (kind == BuffKind.Xp) LocalXpBuff = active;
            else LocalDropBuff = active;
        }

        public static void ResetLocalBuffs()
        {
            LocalXpBuff = false;
            LocalDropBuff = false;
        }

        // Appele depuis Awake du plugin : on attend que les prefabs
        // vanilla soient disponibles pour cloner les meads.
        public static void Init()
        {
            PrefabManager.OnVanillaPrefabsAvailable += CreatePotions;
        }

        private static void CreatePotions()
        {
            // one-shot : Jotunn peut relancer l'evenement a chaque monde
            PrefabManager.OnVanillaPrefabsAvailable -= CreatePotions;
            if (_registered) return;
            _registered = true;

            try
            {
                int cfgMinutes = Mathf.Max(1, (int)EventControllerPlugin.CfgPotionDurationMinutes.Value);

                // v3.6.0 : les 2 potions historiques (duree = config, 15 min par defaut)
                // + 4 variantes a duree FIXE pour la Roue du Valhalla (5 et 10 min).
                // Les prefabs historiques gardent leurs noms -> aucun impact sur les
                // commandes xpevent, blacklists ou items deja en circulation.
                RegisterPotion(XpPotionPrefab,          "MeadTasty",       XpEffectName,          BuffKind.Xp,   cfgMinutes);
                RegisterPotion(DropPotionPrefab,        "MeadFrostResist", DropEffectName,        BuffKind.Drop, cfgMinutes);
                RegisterPotion(XpPotionPrefab + "5",    "MeadTasty",       XpEffectName + "5",    BuffKind.Xp,   5);
                RegisterPotion(XpPotionPrefab + "10",   "MeadTasty",       XpEffectName + "10",   BuffKind.Xp,   10);
                RegisterPotion(DropPotionPrefab + "5",  "MeadFrostResist", DropEffectName + "5",  BuffKind.Drop, 5);
                RegisterPotion(DropPotionPrefab + "10", "MeadFrostResist", DropEffectName + "10", BuffKind.Drop, 10);

                EventControllerPlugin.Log.LogInfo(
                    $"[Potions] 6 elixirs enregistres : {XpPotionPrefab}/{DropPotionPrefab} ({cfgMinutes} min) " +
                    $"+ variantes 5 et 10 min (Roue du Valhalla).");
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogError($"[Potions] Creation echouee : {e}");
            }
        }

        // ============================================================
        // v3.6.0 : enregistrement generique d'un elixir d'event.
        // prefabName unique + SE dedie (le TTL est porte par le SE,
        // d'ou un SE distinct par duree). SE_EventBuff gere les flags
        // locaux via Kind, quelle que soit la duree.
        // ============================================================
        private static void RegisterPotion(string prefabName, string clonePrefab,
                                           string effectName, BuffKind kind, int minutes)
        {
            string token = prefabName.ToLowerInvariant();
            bool isXp = kind == BuffKind.Xp;
            float mult = isXp
                ? EventControllerPlugin.CfgPotionXpMult.Value
                : EventControllerPlugin.CfgPotionDropMult.Value;

            var item = new CustomItem(prefabName, clonePrefab);
            var shared = item.ItemDrop.m_itemData.m_shared;
            shared.m_name = $"$item_{token}";
            shared.m_description = $"$item_{token}_desc";

            var se = ScriptableObject.CreateInstance<SE_EventBuff>();
            se.Kind = kind;
            se.name = effectName;
            // v3.6.1 : nom de buff GENERIQUE (sans duree) — avec le cumul, un seul
            // buff reste affiche quelle que soit la fiole bue en premier.
            se.m_name = isXp ? "$se_eventbuff_xp" : "$se_eventbuff_drop";
            se.m_tooltip = $"$item_{token}_desc";
            se.m_ttl = Mathf.Max(10f, minutes * 60f);
            if (shared.m_icons != null && shared.m_icons.Length > 0)
                se.m_icon = shared.m_icons[0];

            ItemManager.Instance.AddStatusEffect(new CustomStatusEffect(se, fixReference: false));
            shared.m_consumeStatusEffect = se;
            ItemManager.Instance.AddItem(item);

            string frName = isXp ? $"Elixir d'experience ({minutes} min)" : $"Elixir de butin ({minutes} min)";
            string frDesc = isXp
                ? $"XP x{mult} pendant {minutes} minutes."
                : $"Butin x{mult} au ramassage pendant {minutes} minutes (ressources, drops de monstres, cueillette).";
            string enName = isXp ? $"Experience Elixir ({minutes} min)" : $"Loot Elixir ({minutes} min)";
            string enDesc = isXp
                ? $"XP x{mult} for {minutes} minutes."
                : $"Loot x{mult} on pickup for {minutes} minutes (resources, monster drops, foraging).";

            var loc = LocalizationManager.Instance.GetLocalization();
            loc.AddTranslation("French", new Dictionary<string, string>
            {
                { $"item_{token}", frName },
                { $"item_{token}_desc", frDesc },
                { "se_eventbuff_xp", "Elixir d'experience" },
                { "se_eventbuff_drop", "Elixir de butin" }
            });
            loc.AddTranslation("English", new Dictionary<string, string>
            {
                { $"item_{token}", enName },
                { $"item_{token}_desc", enDesc },
                { "se_eventbuff_xp", "Experience Elixir" },
                { "se_eventbuff_drop", "Loot Elixir" }
            });
        }

        // ============================================================
        // Reception cote client d'un ordre de distribution.
        //
        // v3.4.1 : la potion est ajoutee DIRECTEMENT dans l'inventaire
        // du joueur local (via Inventory.AddItem, resolu par reflexion
        // car ses signatures varient entre versions de Valheim).
        // Si l'inventaire est plein ou que l'ajout echoue, repli sur
        // l'ancien comportement : la fiole apparait aux pieds du
        // joueur (auto-ramassee des qu'une place se libere).
        //
        // Note : le spawn de secours se fait HORS de tout DropContext
        // -> pas de marque SourceKey -> la potion de butin ne peut pas
        // doubler les fioles elles-memes. Par construction.
        // ============================================================
        public static void HandleGive(string kind, int count)
        {
            try
            {
                count = Mathf.Clamp(count, 1, 10);
                bool gaveAny = false;
                bool anyOnGround = false;

                if (kind == "xp" || kind == "both")
                {
                    var r = GiveToLocalPlayer(XpPotionPrefab, count);
                    gaveAny |= r != GiveResult.Failed;
                    anyOnGround |= r == GiveResult.DroppedOnGround;
                }
                if (kind == "drop" || kind == "both")
                {
                    var r = GiveToLocalPlayer(DropPotionPrefab, count);
                    gaveAny |= r != GiveResult.Failed;
                    anyOnGround |= r == GiveResult.DroppedOnGround;
                }

                if (gaveAny && MessageHud.instance != null)
                {
                    MessageHud.instance.ShowMessage(MessageHud.MessageType.Center,
                        anyOnGround
                            ? "<color=#7FFFD4>Elixir d'event recu !\nInventaire plein : il t'attend a tes pieds.</color>"
                            : "<color=#7FFFD4>Elixir d'event ajoute a ton inventaire !\nBois-le quand tu veux (15 min).</color>");
                }
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogWarning($"[Potions] HandleGive failed : {e.Message}");
            }
        }

        // ============================================================
        // v3.5.0 : GIVE GENERIQUE (recompenses de vote)
        //
        // Meme cascade que les elixirs, mais pour un prefab quelconque
        // envoye par le serveur (RPC EventController_GiveItem, declenche
        // par le bot "Roue du Valhalla" via ValheimRestApi /give).
        // message : texte HUD personnalise (vide = message par defaut).
        // ============================================================
        // v3.5.1 : gives recus AVANT le spawn du joueur local (ecran de
        // chargement, RPC arrive trop tot) — mis en tampon, puis livres
        // par FlushPendingGives() au Player.OnSpawned (hook existant).
        private static readonly List<(string prefab, int count, string message)> PendingGives
            = new List<(string, int, string)>();

        public static void FlushPendingGives()
        {
            (string prefab, int count, string message)[] items;
            lock (PendingGives)
            {
                if (PendingGives.Count == 0) return;
                items = PendingGives.ToArray();
                PendingGives.Clear();
            }
            EventControllerPlugin.Log.LogInfo(
                $"[GiveItem] Livraison de {items.Length} give(s) mis en tampon avant le spawn.");
            foreach (var it in items)
                HandleGiveItem(it.prefab, it.count, it.message);
        }

        public static void HandleGiveItem(string prefabName, int count, string message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(prefabName)) return;
                count = Mathf.Clamp(count, 1, 1000);

                // Pas encore en jeu (ecran de chargement) : on met en tampon,
                // le give sera rejoue au spawn. C'etait la cause des pertes
                // "delivre trop vite" : m_localPlayer est null a ce moment-la.
                if (Player.m_localPlayer == null)
                {
                    lock (PendingGives)
                    {
                        if (PendingGives.Count < 50)
                            PendingGives.Add((prefabName.Trim(), count, message ?? ""));
                    }
                    EventControllerPlugin.Log.LogInfo(
                        $"[GiveItem] Joueur pas encore spawn : give '{prefabName}' x{count} mis en tampon.");
                    return;
                }

                var r = GiveToLocalPlayer(prefabName.Trim(), count);
                if (r == GiveResult.Failed)
                {
                    EventControllerPlugin.Log.LogWarning(
                        $"[GiveItem] Echec du give '{prefabName}' x{count} (prefab inconnu cote client ?).");
                    return;
                }

                if (MessageHud.instance != null)
                {
                    string baseMsg = string.IsNullOrEmpty(message)
                        ? "<color=#FFD700>Recompense recue !</color>"
                        : $"<color=#FFD700>{message}</color>";
                    MessageHud.instance.ShowMessage(MessageHud.MessageType.Center,
                        r == GiveResult.DroppedOnGround
                            ? baseMsg + "\n<color=#7FFFD4>Inventaire plein : elle t'attend a tes pieds.</color>"
                            : baseMsg);
                }
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogWarning($"[GiveItem] HandleGiveItem failed : {e.Message}");
            }
        }

        private enum GiveResult { Failed, AddedToInventory, DroppedOnGround }

        private static GiveResult GiveToLocalPlayer(string prefabName, int count)
        {
            if (Player.m_localPlayer == null) return GiveResult.Failed;

            if (TryAddToInventory(prefabName, count))
            {
                EventControllerPlugin.Log.LogInfo($"[Potions] {count}x {prefabName} ajoute a l'inventaire.");
                return GiveResult.AddedToInventory;
            }

            if (SpawnAtLocalPlayer(prefabName, count))
            {
                EventControllerPlugin.Log.LogInfo($"[Potions] {count}x {prefabName} depose au sol (inventaire plein ou AddItem indisponible).");
                return GiveResult.DroppedOnGround;
            }

            return GiveResult.Failed;
        }

        // ============================================================
        // Ajout direct a l'inventaire par reflexion. Cascade sur les
        // signatures connues de Inventory.AddItem selon les versions :
        //  1) AddItem(string, int, int, int, long, string, bool)
        //  2) AddItem(string, int, int, int, long, string)
        //  3) AddItem(GameObject, int)
        // Retourne false si aucune n'existe ou si l'ajout echoue
        // (inventaire plein) -> le fallback au sol prend le relais.
        // ============================================================
        private static bool TryAddToInventory(string prefabName, int count)
        {
            try
            {
                var player = Player.m_localPlayer;
                if (player == null) return false;
                var inv = player.GetInventory();
                if (inv == null) return false;

                var t = typeof(Inventory);

                var m = HarmonyLib.AccessTools.Method(t, "AddItem",
                    new[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(long), typeof(string), typeof(bool) });
                if (m != null)
                {
                    try
                    {
                        var r = m.Invoke(inv, new object[] { prefabName, count, 1, 0, 0L, "", true });
                        if (ResultIsSuccess(r)) return true;
                    }
                    catch { /* on tente la signature suivante */ }
                }

                m = HarmonyLib.AccessTools.Method(t, "AddItem",
                    new[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(long), typeof(string) });
                if (m != null)
                {
                    try
                    {
                        var r = m.Invoke(inv, new object[] { prefabName, count, 1, 0, 0L, "" });
                        if (ResultIsSuccess(r)) return true;
                    }
                    catch { }
                }

                m = HarmonyLib.AccessTools.Method(t, "AddItem",
                    new[] { typeof(GameObject), typeof(int) });
                if (m != null)
                {
                    var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
                    if (prefab != null)
                    {
                        try
                        {
                            var r = m.Invoke(inv, new object[] { prefab, count });
                            if (ResultIsSuccess(r)) return true;
                        }
                        catch { }
                    }
                }
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogWarning($"[Potions] TryAddToInventory : {e.Message}");
            }
            return false;
        }

        // AddItem retourne selon la signature un bool ou un ItemData
        // (null = echec). On unifie l'interpretation ici.
        private static bool ResultIsSuccess(object r)
        {
            if (r == null) return false;
            if (r is bool b) return b;
            return true; // ItemData non-null = ajout reussi
        }

        private static bool SpawnAtLocalPlayer(string prefabName, int count)
        {
            var player = Player.m_localPlayer;
            if (player == null) return false;

            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            if (prefab == null)
            {
                EventControllerPlugin.Log.LogWarning($"[Potions] Prefab '{prefabName}' introuvable dans ObjectDB.");
                return false;
            }

            var pos = player.transform.position
                      + player.transform.forward * 0.4f
                      + Vector3.up * 1.2f;
            var go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);

            var drop = go.GetComponent<ItemDrop>();
            if (drop != null && drop.m_itemData != null && count > 1)
            {
                int max = drop.m_itemData.m_shared != null && drop.m_itemData.m_shared.m_maxStackSize > 0
                    ? drop.m_itemData.m_shared.m_maxStackSize : count;
                int stack = Mathf.Min(count, max);
                drop.m_itemData.m_stack = stack;
                var nv = go.GetComponent<ZNetView>();
                if (nv != null && nv.IsValid() && nv.GetZDO() != null)
                    nv.GetZDO().Set(ZDOVars.s_stack, stack);
            }

            return true;
        }
    }
}
