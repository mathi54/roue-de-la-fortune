using System;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ValheimRestApi.Commands
{
    /// <summary>
    /// POST /give — Donne un objet a un joueur connecte (recompenses de vote,
    /// bot "Roue de la Fortune"). Body JSON attendu :
    ///     { "playername": "Mathi", "item": "Coins", "amount": 20,
    ///       "message": "Roue de la Fortune : 20 Piastres !",   // optionnel (HUD client)
    ///       "mode": "rpc" }                                   // optionnel : "rpc" (defaut) ou "drop"
    ///
    /// Mode "rpc" (defaut) : envoie le RPC "EventController_GiveItem" au client
    /// cible — EventController (present chez tous les joueurs via le modpack)
    /// ajoute l'objet DIRECTEMENT dans l'inventaire (cascade Inventory.AddItem),
    /// avec repli au sol si l'inventaire est plein, et message HUD.
    ///
    /// Mode "drop" : spawn serveur aux pieds du joueur (auto-pickup). Utile en
    /// secours si un client a une version d'EventController sans le RPC.
    ///
    /// Thread : HttpServer execute deja Execute() via MainThreadDispatcher.
    /// </summary>
    public class GiveCommand : IRestAction
    {
        public string Route => "give";
        public string Description => "Donne un item a un joueur connecte : { playername, item, amount, message?, mode? }.";

        private const int MaxAmount = 1000;
        private const string RpcGiveItem = "EventController_GiveItem"; // doit matcher Commands.RpcGiveItem d'EventController

        public string Execute(string body)
        {
            // === Parsing du body (mini-extraction regex, pas de dependance JSON) ===
            string playername = JsonString(body, "playername");
            string itemName   = JsonString(body, "item");
            string message    = JsonString(body, "message") ?? "";
            string mode       = (JsonString(body, "mode") ?? "rpc").ToLowerInvariant();
            int amount        = JsonInt(body, "amount", 1);

            if (string.IsNullOrWhiteSpace(playername)) return Fail("playername is required");
            if (string.IsNullOrWhiteSpace(itemName))   return Fail("item is required");
            if (amount < 1 || amount > MaxAmount)      return Fail($"amount must be within 1-{MaxAmount}");
            if (mode != "rpc" && mode != "drop")       return Fail("mode must be 'rpc' or 'drop'");

            var znet = ZNet.instance;
            if (znet == null) return Fail("ZNet not ready");

            // === 1) Trouver le joueur connecte (insensible a la casse) ===
            ZNetPeer target = null;
            var peers = znet.GetPeers();
            if (peers != null)
            {
                foreach (var peer in peers)
                {
                    if (peer != null &&
                        string.Equals(peer.m_playerName, playername.Trim(),
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        target = peer;
                        break;
                    }
                }
            }
            if (target == null) return Fail("Player not online: " + playername);

            // === 2) Valider le prefab cote serveur (evite d'envoyer un RPC inutile) ===
            var scene = ZNetScene.instance;
            if (scene == null) return Fail("ZNetScene not ready");
            GameObject prefab = scene.GetPrefab(itemName.Trim());
            if (prefab == null) return Fail("Unknown item prefab: " + itemName);
            var refDrop = prefab.GetComponent<ItemDrop>();
            if (refDrop == null) return Fail("Prefab has no ItemDrop: " + itemName);

            // === 3) Livraison ===
            if (mode == "rpc")
                return GiveViaRpc(target, itemName.Trim(), amount, message);
            return GiveViaDrop(target, prefab, refDrop, itemName.Trim(), amount);
        }

        // ------------------------------------------------------------
        // Mode "rpc" : insertion directe dans l'inventaire via
        // EventController cote client (repli au sol gere par le client).
        // ------------------------------------------------------------
        private static string GiveViaRpc(ZNetPeer target, string itemName, int amount, string message)
        {
            if (ZRoutedRpc.instance == null) return Fail("ZRoutedRpc not ready");

            try
            {
                var pkg = new ZPackage();
                pkg.Write(itemName);
                pkg.Write(amount);
                pkg.Write(message ?? "");
                ZRoutedRpc.instance.InvokeRoutedRPC(target.m_uid, RpcGiveItem, pkg);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[RestAPI] /give RPC failed: {e}");
                return Fail("RPC send failed: " + e.Message);
            }

            Plugin.Logger.LogInfo(
                $"[RestAPI] /give (rpc) -> {amount} x {itemName} pour {target.m_playerName}");

            return JsonBuilder.Object(
                ("success",    true),
                ("mode",       "rpc"),
                ("playername", target.m_playerName),
                ("item",       itemName),
                ("amount",     amount));
        }

        // ------------------------------------------------------------
        // Mode "drop" : spawn serveur aux pieds du joueur (auto-pickup),
        // piles decoupees selon le m_maxStackSize de l'item.
        // ------------------------------------------------------------
        private static string GiveViaDrop(ZNetPeer target, GameObject prefab, ItemDrop refDrop,
                                          string itemName, int amount)
        {
            int maxStack = Mathf.Max(1, refDrop.m_itemData.m_shared.m_maxStackSize);
            int remaining = amount;
            int drops = 0;
            var basePos = target.GetRefPos() + Vector3.up * 1.5f;

            try
            {
                while (remaining > 0)
                {
                    int stack = Mathf.Min(remaining, maxStack);
                    remaining -= stack;

                    var pos = basePos + new Vector3(
                        UnityEngine.Random.Range(-0.4f, 0.4f), 0f,
                        UnityEngine.Random.Range(-0.4f, 0.4f));

                    var go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
                    var drop = go.GetComponent<ItemDrop>();
                    drop.m_itemData.m_stack = stack;

                    // Pousse le stack dans le ZDO pour la synchro reseau.
                    // (ItemDrop.Save() n'existe plus dans les versions recentes :
                    //  on ecrit directement la variable ZDO, comme le fait le jeu.)
                    var znv = go.GetComponent<ZNetView>();
                    var zdo = znv != null ? znv.GetZDO() : null;
                    if (zdo != null) zdo.Set(ZDOVars.s_stack, stack);

                    drops++;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[RestAPI] /give spawn failed: {e}");
                return Fail("Spawn failed after " + drops + " drop(s): " + e.Message);
            }

            Plugin.Logger.LogInfo(
                $"[RestAPI] /give (drop) -> {amount} x {itemName} pour {target.m_playerName} ({drops} drop(s))");

            return JsonBuilder.Object(
                ("success",    true),
                ("mode",       "drop"),
                ("playername", target.m_playerName),
                ("item",       itemName),
                ("amount",     amount),
                ("drops",      drops));
        }

        // === Helpers de parsing (body JSON plat) ===

        private static string JsonString(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var m = Regex.Match(body,
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            return m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        private static int JsonInt(string body, string key, int fallback)
        {
            if (string.IsNullOrEmpty(body)) return fallback;
            var m = Regex.Match(body, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : fallback;
        }

        private static string Fail(string error)
        {
            return JsonBuilder.Object(("success", false), ("error", error));
        }
    }
}
