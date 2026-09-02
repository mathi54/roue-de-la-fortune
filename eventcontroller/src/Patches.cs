using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EventController
{
    // ================================================================
    // v3.4.0 : LE SYSTEME D'EVENT GLOBAL A ETE RETIRE.
    //
    // Le mod repose desormais uniquement sur les POTIONS personnelles
    // (voir EventPotions.cs). Ce fichier ne multiplie plus rien a la
    // source : il se contente de MARQUER les drops de source legitime
    // dans leur ZDO, et de doubler au RAMASSAGE chez les joueurs bufes.
    //
    // Le tracker de contexte (DropContext) reste le coeur du systeme :
    // un ItemDrop n'est marque "source legitime" QUE s'il nait dans le
    // call stack d'une source reconnue :
    //   - mort de mob        (CharacterDrop.DropItems, Ragdoll.SpawnLoot)
    //   - destruction        (TreeLog, TreeBase, MineRock, MineRock5,
    //                         DropOnDestroyed)
    //   - pickable           (Pickable.RPC_Pick)
    //
    // Tout objet instancie HORS de ces contextes (chargement reseau,
    // drop volontaire joueur, sortie de smelter, coffres...) n'est
    // JAMAIS marque, donc jamais double par la potion de butin.
    //
    // Tous les hooks utilisent Prepare() : si la methode n'existe pas
    // dans la version de Valheim, le hook est ignore avec un warning,
    // sans jamais casser le PatchAll.
    // ================================================================

    internal enum DropKind
    {
        None = 0,        // aucun contexte : ne pas toucher
        MobDrop,         // drops de mob (source legitime)
        ExcludedMob,     // drops d'un mob blackliste (jamais marques)
        Destruction,     // arbres, rochers, destructibles (source legitime)
        PickableSpawn    // drops d'un pickable (source legitime)
    }

    // Tracker thread-static du contexte courant, avec compteur de
    // profondeur pour supporter la reentrance. Le premier contexte
    // ouvert gagne.
    internal static class DropContext
    {
        [ThreadStatic] private static DropKind _kind;
        [ThreadStatic] private static int _depth;

        public static DropKind Current => _depth > 0 ? _kind : DropKind.None;

        public static void Push(DropKind kind)
        {
            if (_depth == 0) _kind = kind;
            _depth++;
        }

        public static void Pop()
        {
            if (_depth > 0) _depth--;
            if (_depth == 0) _kind = DropKind.None;
        }
    }

    // Helper : trouve la premiere methode existante parmi des candidats.
    internal static class PatchUtil
    {
        public static System.Reflection.MethodBase FindMethod(Type type, params string[] candidates)
        {
            foreach (var name in candidates)
            {
                try
                {
                    var m = AccessTools.Method(type, name);
                    if (m != null) return m;
                }
                catch
                {
                    // AmbiguousMatch ou autre : on passe au candidat suivant
                }
            }
            return null;
        }
    }

    // ================================================================
    // MARQUAGE DE SOURCE : ItemDrop.Awake
    //
    // Chaque ItemDrop qui nait dans un contexte de drop legitime recoit
    // la marque SourceKey dans son ZDO (ecrite par l'owner uniquement).
    // La potion de butin ne double au ramassage QUE les items marques.
    //
    // Les drops d'un mob blackliste (ExcludedMob) ne sont pas marques :
    // le butin des boss ne sera jamais double.
    // ================================================================
    [HarmonyPatch]
    static class Patch_ItemDropAwake
    {
        // Legacy (versions <= 3.3.x avec event global) : encore LU par
        // Patch_PickupDoubling en protection, pour ne pas re-doubler des
        // items historiques deja multiplies par un ancien event et
        // toujours au sol. N'est plus jamais ECRIT.
        internal const string ProcessedKey = "mathi_evt_done";

        // Marque "drop de source legitime" (mob / destruction / pickable).
        internal const string SourceKey = "mathi_evt_src";

        private static readonly ConditionalWeakTable<ItemDrop, object> _processedInstances =
            new ConditionalWeakTable<ItemDrop, object>();

        static System.Reflection.MethodBase TargetMethod()
        {
            var m = PatchUtil.FindMethod(typeof(ItemDrop), "Awake", "Start");
            if (m != null)
                EventControllerPlugin.Log.LogInfo($"[Patch_ItemDropAwake] hook sur ItemDrop.{m.Name}");
            else
                EventControllerPlugin.Log.LogError("[Patch_ItemDropAwake] ItemDrop.Awake/Start introuvable !");
            return m;
        }

        static void Postfix(ItemDrop __instance)
        {
            try
            {
                if (__instance == null) return;

                var nview = __instance.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return;

                // Anti double-traitement local (GC-friendly)
                if (_processedInstances.TryGetValue(__instance, out _)) return;
                _processedInstances.Add(__instance, null);

                var zdo = nview.GetZDO();
                if (zdo == null) return;

                var ctx = DropContext.Current;
                if (ctx == DropKind.None || ctx == DropKind.ExcludedMob) return;

                // Source legitime : marquer (owner uniquement).
                if (nview.IsOwner() && !zdo.GetBool(SourceKey))
                {
                    zdo.Set(SourceKey, true);

                    if (EventControllerPlugin.CfgVerboseLog.Value)
                    {
                        var prefabName = __instance.m_itemData != null && __instance.m_itemData.m_dropPrefab != null
                            ? __instance.m_itemData.m_dropPrefab.name : "?";
                        EventControllerPlugin.Log.LogInfo($"[SourceMark] {prefabName} (ctx={ctx})");
                    }
                }
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogWarning($"Patch_ItemDropAwake failed : {e.Message}");
            }
        }
    }

    // ================================================================
    // CONTEXTE : drops de mobs via CharacterDrop
    // Ouvre MobDrop, ou ExcludedMob si le mob est blackliste (ses drops
    // ne seront alors pas marques -> jamais doubles par la potion).
    // ================================================================
    [HarmonyPatch]
    static class Ctx_CharacterDrop
    {
        static System.Reflection.MethodBase _m;
        static bool _searched;

        static bool Prepare()
        {
            if (!_searched)
            {
                _searched = true;
                _m = PatchUtil.FindMethod(typeof(CharacterDrop),
                    "DropItems", "GenerateDrops", "OnDeath", "DoDrops");
                if (_m != null)
                    EventControllerPlugin.Log.LogInfo($"[Ctx_CharacterDrop] hook sur CharacterDrop.{_m.Name}");
                else
                    EventControllerPlugin.Log.LogWarning(
                        "[Ctx_CharacterDrop] methode introuvable : drops de mobs non marques.");
            }
            return _m != null;
        }

        static System.Reflection.MethodBase TargetMethod() => _m;

        static void Prefix(CharacterDrop __instance)
        {
            var kind = DropKind.MobDrop;
            try
            {
                var character = __instance != null ? __instance.GetComponent<Character>() : null;
                if (character != null)
                {
                    var charName = EventControllerPlugin.CleanPrefabName(character.name);
                    if (EventControllerPlugin.IsCharacterExcluded(charName))
                    {
                        kind = DropKind.ExcludedMob;
                        if (EventControllerPlugin.CfgVerboseLog.Value)
                            EventControllerPlugin.Log.LogInfo(
                                $"[CharacterDrop] {charName} est exclu, drops non marques.");
                    }
                }
            }
            catch { /* en cas de doute : MobDrop standard */ }
            DropContext.Push(kind);
        }

        static void Finalizer() => DropContext.Pop();
    }

    // ================================================================
    // CONTEXTE : loot differe via Ragdoll (beaucoup d'humanoides
    // droppent a la disparition du cadavre, pas a la mort).
    // ================================================================
    [HarmonyPatch]
    static class Ctx_RagdollLoot
    {
        static System.Reflection.MethodBase _m;
        static bool _searched;

        static bool Prepare()
        {
            if (!_searched)
            {
                _searched = true;
                _m = PatchUtil.FindMethod(typeof(Ragdoll), "SpawnLoot", "DropLoot");
                if (_m != null)
                    EventControllerPlugin.Log.LogInfo($"[Ctx_RagdollLoot] hook sur Ragdoll.{_m.Name}");
                else
                    EventControllerPlugin.Log.LogWarning(
                        "[Ctx_RagdollLoot] Ragdoll.SpawnLoot introuvable : loot ragdoll non marque.");
            }
            return _m != null;
        }

        static System.Reflection.MethodBase TargetMethod() => _m;
        static void Prefix() => DropContext.Push(DropKind.MobDrop);
        static void Finalizer() => DropContext.Pop();
    }

    // ================================================================
    // CONTEXTES : destructions (arbres, rochers, pots, etc.)
    // ================================================================
    [HarmonyPatch]
    static class Ctx_DropOnDestroyed
    {
        static System.Reflection.MethodBase _m;
        static bool _searched;
        static bool Prepare()
        {
            if (!_searched)
            {
                _searched = true;
                _m = PatchUtil.FindMethod(typeof(DropOnDestroyed), "OnDestroyed");
                if (_m != null)
                    EventControllerPlugin.Log.LogInfo($"[Ctx_DropOnDestroyed] hook sur DropOnDestroyed.{_m.Name}");
                else
                    EventControllerPlugin.Log.LogWarning("[Ctx_DropOnDestroyed] OnDestroyed introuvable.");
            }
            return _m != null;
        }
        static System.Reflection.MethodBase TargetMethod() => _m;
        static void Prefix() => DropContext.Push(DropKind.Destruction);
        static void Finalizer() => DropContext.Pop();
    }

    [HarmonyPatch]
    static class Ctx_TreeLog
    {
        static System.Reflection.MethodBase _m;
        static bool _searched;
        static bool Prepare()
        {
            if (!_searched)
            {
                _searched = true;
                _m = PatchUtil.FindMethod(typeof(TreeLog), "Destroy", "RPC_Damage");
                if (_m != null)
                    EventControllerPlugin.Log.LogInfo($"[Ctx_TreeLog] hook sur TreeLog.{_m.Name}");
                else
                    EventControllerPlugin.Log.LogWarning("[Ctx_TreeLog] methode introuvable : bois non marque.");
            }
            return _m != null;
        }
        static System.Reflection.MethodBase TargetMethod() => _m;
        static void Prefix() => DropContext.Push(DropKind.Destruction);
        static void Finalizer() => DropContext.Pop();
    }

    [HarmonyPatch]
    static class Ctx_TreeBase
    {
        static System.Reflection.MethodBase _m;
        static bool _searched;
        static bool Prepare()
        {
            if (!_searched)
            {
                _searched = true;
                _m = PatchUtil.FindMethod(typeof(TreeBase), "RPC_Damage", "Destroy");
                if (_m != null)
                    EventControllerPlugin.Log.LogInfo($"[Ctx_TreeBase] hook sur TreeBase.{_m.Name}");
                // pas de warning si absent : TreeLog couvre le bois
            }
            return _m != null;
        }
        static System.Reflection.MethodBase TargetMethod() => _m;
        static void Prefix() => DropContext.Push(DropKind.Destruction);
        static void Finalizer() => DropContext.Pop();
    }

    [HarmonyPatch]
    static class Ctx_MineRock5
    {
        static System.Reflection.MethodBase _m;
        static bool _searched;
        static bool Prepare()
        {
            if (!_searched)
            {
                _searched = true;
                _m = PatchUtil.FindMethod(typeof(MineRock5), "DamageArea", "RPC_Damage");
                if (_m != null)
                    EventControllerPlugin.Log.LogInfo($"[Ctx_MineRock5] hook sur MineRock5.{_m.Name}");
                else
                    EventControllerPlugin.Log.LogWarning("[Ctx_MineRock5] methode introuvable : minerai (nouveaux rochers) non marque.");
            }
            return _m != null;
        }
        static System.Reflection.MethodBase TargetMethod() => _m;
        static void Prefix() => DropContext.Push(DropKind.Destruction);
        static void Finalizer() => DropContext.Pop();
    }

    [HarmonyPatch]
    static class Ctx_MineRock
    {
        static System.Reflection.MethodBase _m;
        static bool _searched;
        static bool Prepare()
        {
            if (!_searched)
            {
                _searched = true;
                _m = PatchUtil.FindMethod(typeof(MineRock), "RPC_Hit", "RPC_Damage", "DamageArea");
                if (_m != null)
                    EventControllerPlugin.Log.LogInfo($"[Ctx_MineRock] hook sur MineRock.{_m.Name}");
                else
                    EventControllerPlugin.Log.LogWarning("[Ctx_MineRock] methode introuvable : minerai (anciens rochers) non marque.");
            }
            return _m != null;
        }
        static System.Reflection.MethodBase TargetMethod() => _m;
        static void Prefix() => DropContext.Push(DropKind.Destruction);
        static void Finalizer() => DropContext.Pop();
    }

    // ================================================================
    // CONTEXTE : pickables (baies, champignons, fleurs...).
    // RPC_Pick s'execute chez l'OWNER du pickable : c'est la que les
    // drops naissent -> le marquage se fait au bon endroit, fiable en
    // multijoueur. Fallback Interact si RPC_Pick absent.
    // (v3.4.0 : plus aucune multiplication ici, seulement le contexte.)
    // ================================================================
    [HarmonyPatch]
    static class Ctx_PickableRpcPick
    {
        internal static bool Active;
        static System.Reflection.MethodBase _m;
        static bool _searched;

        static bool Prepare()
        {
            if (!_searched)
            {
                _searched = true;
                _m = PatchUtil.FindMethod(typeof(Pickable), "RPC_Pick");
                Active = _m != null;
                if (Active)
                    EventControllerPlugin.Log.LogInfo("[Ctx_PickableRpcPick] hook sur Pickable.RPC_Pick");
                else
                    EventControllerPlugin.Log.LogWarning(
                        "[Ctx_PickableRpcPick] RPC_Pick introuvable, fallback sur Interact.");
            }
            return Active;
        }

        static System.Reflection.MethodBase TargetMethod() => _m;
        static void Prefix() => DropContext.Push(DropKind.PickableSpawn);
        static void Finalizer() => DropContext.Pop();
    }

    // Fallback : hook Interact, actif seulement si RPC_Pick absent.
    // (Degrade : ne marque que si le cliqueur est owner du pickable.)
    [HarmonyPatch]
    static class Ctx_PickableInteractFallback
    {
        static System.Reflection.MethodBase _m;
        static bool _searched;

        static bool Prepare()
        {
            if (!_searched)
            {
                _searched = true;
                if (!Ctx_PickableRpcPick.Active)
                    _m = PatchUtil.FindMethod(typeof(Pickable), "Interact");
            }
            return _m != null;
        }

        static System.Reflection.MethodBase TargetMethod() => _m;
        static void Prefix() => DropContext.Push(DropKind.PickableSpawn);
        static void Finalizer() => DropContext.Pop();
    }

    // ================================================================
    // XP : Elixir d'experience (local au joueur qui l'a bu)
    // ================================================================
    [HarmonyPatch(typeof(Skills), nameof(Skills.RaiseSkill))]
    static class Patch_RaiseSkill
    {
        static void Prefix(Skills.SkillType skillType, ref float factor)
        {
            if (!EventPotions.LocalXpBuff) return;

            float mult = EventControllerPlugin.CfgPotionXpMult.Value;
            if (mult <= 1f) return;

            float orig = factor;
            factor *= mult;

            if (EventControllerPlugin.CfgVerboseLog.Value)
                EventControllerPlugin.Log.LogInfo(
                    $"[Potion XP x{mult}] {skillType} : factor {orig} -> {factor}");
        }
    }

    // ================================================================
    // Reset de l'etat local a la deconnexion (retour menu principal)
    // ================================================================
    [HarmonyPatch(typeof(ZNet), "OnDestroy")]
    static class Patch_ZNet_OnDestroy
    {
        static void Postfix()
        {
            EventControllerPlugin.ResetLocalState();
        }
    }

    // ================================================================
    // Le client DEMANDE les blacklists au serveur quand son joueur
    // spawn (pull, en complement du push connexion + heartbeat).
    // ================================================================
    [HarmonyPatch]
    static class Patch_PlayerSpawned_RequestState
    {
        static System.Reflection.MethodBase _m;
        static bool _searched;

        static bool Prepare()
        {
            if (!_searched)
            {
                _searched = true;
                _m = PatchUtil.FindMethod(typeof(Player), "OnSpawned");
                if (_m != null)
                    EventControllerPlugin.Log.LogInfo("[RequestState] hook sur Player.OnSpawned");
                else
                    EventControllerPlugin.Log.LogWarning(
                        "[RequestState] Player.OnSpawned introuvable, le heartbeat servira de rattrapage.");
            }
            return _m != null;
        }

        static System.Reflection.MethodBase TargetMethod() => _m;

        static void Postfix(Player __instance)
        {
            try
            {
                if (__instance == null || __instance != Player.m_localPlayer) return;
                StateSync.RequestStateFromServer("Player.OnSpawned");
                EventPotions.FlushPendingGives(); // v3.5.1 : gives arrives pendant le chargement
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogWarning($"RequestState failed : {e.Message}");
            }
        }
    }

    // ================================================================
    // POTION DE BUTIN — "ramassage double"
    //
    // S'execute chez le RAMASSEUR (Humanoid.Pickup ne tourne que pour
    // le joueur local) : aucun probleme d'ownership.
    //
    // Conditions pour doubler, TOUTES requises :
    //  - le joueur local a le buff de butin actif
    //  - l'item porte la marque SourceKey (ne dans un contexte mob/
    //    destruction/pickable) -> un item jete par un joueur, sorti
    //    d'un coffre ou d'un smelter n'est JAMAIS double
    //  - l'item n'a pas ProcessedKey (protection : items deja x2 par
    //    un event global d'une version <= 3.3.x encore au sol)
    //  - l'item n'a pas deja ete double par un buff (DoubledKey)
    //  - l'item n'est pas blackliste (trophees, items uniques...)
    //
    // Binding par index (__0) : insensible au nom du parametre.
    // ================================================================
    [HarmonyPatch]
    static class Patch_PickupDoubling
    {
        internal const string DoubledKey = "mathi_evt_pot";

        static System.Reflection.MethodBase _m;
        static bool _searched;

        static bool Prepare()
        {
            if (!_searched)
            {
                _searched = true;
                _m = PatchUtil.FindMethod(typeof(Humanoid), "Pickup");
                if (_m != null)
                    EventControllerPlugin.Log.LogInfo("[PickupDoubling] hook sur Humanoid.Pickup");
                else
                    EventControllerPlugin.Log.LogWarning(
                        "[PickupDoubling] Humanoid.Pickup introuvable : potion de butin inactive.");
            }
            return _m != null;
        }

        static System.Reflection.MethodBase TargetMethod() => _m;

        static void Prefix(Humanoid __instance, UnityEngine.GameObject __0)
        {
            try
            {
                if (!EventPotions.LocalDropBuff) return;
                if (__instance == null || __instance != Player.m_localPlayer) return;
                if (__0 == null) return;

                var drop = __0.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData == null) return;

                var nview = __0.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return;
                var zdo = nview.GetZDO();
                if (zdo == null) return;

                // v3.4.2 : le pickup vanilla RECHARGE m_itemData depuis le
                // ZDO (ItemDrop.Load) juste avant l'ajout a l'inventaire.
                // Modifier la memoire ne suffit donc pas : il faut ecrire
                // le stack double dans le ZDO, ce que seul l'owner peut
                // faire de facon autoritaire. Si on n'est pas encore owner,
                // le jeu fait RequestOwn et RETENTE le pickup une fois
                // l'ownership obtenu -> ce Prefix agira a ce moment-la.
                if (!nview.IsOwner()) return;

                if (!zdo.GetBool(Patch_ItemDropAwake.SourceKey)) return; // source non legitime
                if (zdo.GetBool(Patch_ItemDropAwake.ProcessedKey)) return; // legacy event x2
                if (zdo.GetBool(DoubledKey)) return; // deja double par un buff

                var prefabName = drop.m_itemData.m_dropPrefab != null
                    ? drop.m_itemData.m_dropPrefab.name : null;
                if (!EventControllerPlugin.ShouldMultiplyItem(prefabName)) return;

                float mult = EventControllerPlugin.CfgPotionDropMult.Value;
                if (mult <= 1f) return;

                int orig = drop.m_itemData.m_stack;
                int newStack = Mathf.Max(1, Mathf.RoundToInt(orig * mult));
                if (drop.m_itemData.m_shared != null
                    && drop.m_itemData.m_shared.m_maxStackSize > 0)
                {
                    newStack = Mathf.Min(newStack, drop.m_itemData.m_shared.m_maxStackSize);
                }

                drop.m_itemData.m_stack = newStack;
                zdo.Set(ZDOVars.s_stack, newStack); // le Load() du pickup relira cette valeur
                zdo.Set(DoubledKey, true);

                if (EventControllerPlugin.CfgVerboseLog.Value)
                    EventControllerPlugin.Log.LogInfo(
                        $"[Potion butin x{mult}] {prefabName} : {orig} -> {newStack} au ramassage");
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogWarning($"Patch_PickupDoubling failed : {e.Message}");
            }
        }
    }
}
