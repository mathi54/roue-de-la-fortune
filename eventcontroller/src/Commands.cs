using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EventController
{
    // ============================================================
    // ARCHITECTURE v3.4.0 - CLIENT + SERVEUR, 5 RPCs custom :
    //
    //  - EventController_Cmd     : client -> serveur (commande admin)
    //  - EventController_State   : serveur -> clients (blacklists)
    //  - EventController_Request : client -> serveur (demande des
    //                              blacklists, envoyee au spawn)
    //  - EventController_Msg     : serveur -> client (reponse admin,
    //                              affichee via MessageHud)
    //  - EventController_Give    : serveur -> clients (distribution
    //                              des elixirs)
    //
    // v3.4.0 : le systeme d'event global (start/stop) a ete retire.
    // Le RPC State ne transporte plus que les blacklists, necessaires
    // au client pour la potion de butin (ShouldMultiplyItem est evalue
    // chez le ramasseur).
    // ============================================================
    internal static class Commands
    {
        private static bool _registered;
        public const string CommandName = "xpevent";
        public const string RpcCmd = "EventController_Cmd";
        public const string RpcState = "EventController_State";
        public const string RpcRequest = "EventController_Request";
        public const string RpcMsg = "EventController_Msg";
        public const string RpcGive = "EventController_Give";
        public const string RpcGiveItem = "EventController_GiveItem";

        private const string MagicHeader = "MathiXpEvent_v3";

        public static void Register()
        {
            if (_registered) return;
            _registered = true;

            new Terminal.ConsoleCommand(
                CommandName,
                "[give <xp|drop|both> [nombre]] | [status]",
                args => HandleLocalCommand(args),
                isCheat: false,
                isNetwork: false,
                onlyServer: false,
                isSecret: false);

            EventControllerPlugin.Log.LogInfo(
                $"Commande Terminal '{CommandName}' enregistree.");
        }

        // ============================================================
        // "xpevent ..." en F5 (Terminal local) :
        //  - serveur (ou solo) : execute directement
        //  - client connecte   : RPC au serveur
        // ============================================================
        private static void HandleLocalCommand(Terminal.ConsoleEventArgs args)
        {
            var tokens = SplitArgs(args);

            if (ZNet.instance == null || ZNet.instance.IsServer())
            {
                ExecuteServerSide(tokens, args);
                return;
            }

            SendRpcToServer(tokens, args);
        }

        private static void SendRpcToServer(string[] tokens, Terminal.ConsoleEventArgs args)
        {
            try
            {
                if (ZRoutedRpc.instance == null)
                {
                    args.Context?.AddString("Erreur : pas connecte au serveur.");
                    return;
                }

                var pkg = new ZPackage();
                pkg.Write(MagicHeader);
                pkg.Write(tokens.Length);
                foreach (var t in tokens) pkg.Write(t);

                long serverId = GetServerRouteId();
                ZRoutedRpc.instance.InvokeRoutedRPC(serverId, RpcCmd, pkg);

                args.Context?.AddString("Commande envoyee au serveur...");
                EventControllerPlugin.Log.LogInfo(
                    $"[Client] RPC '{string.Join(" ", tokens)}' envoye au serveur (peer {serverId}).");
            }
            catch (Exception e)
            {
                args.Context?.AddString($"Erreur RPC : {e.Message}");
                EventControllerPlugin.Log.LogError($"SendRpcToServer failed : {e}");
            }
        }

        // ============================================================
        // SERVEUR : reception d'une commande admin depuis un client
        // ============================================================
        public static void OnRpcCmdReceived(long senderID, ZPackage pkg)
        {
            try
            {
                if (pkg == null) return;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

                var header = pkg.ReadString();
                if (header != MagicHeader)
                {
                    EventControllerPlugin.Log.LogWarning(
                        $"[RPC] Header invalide depuis {senderID}, ignore.");
                    return;
                }

                int count = pkg.ReadInt();
                if (count <= 0 || count > 16)
                {
                    EventControllerPlugin.Log.LogWarning(
                        $"[RPC] Count invalide ({count}) depuis {senderID}, ignore.");
                    return;
                }

                var tokens = new string[count];
                for (int i = 0; i < count; i++) tokens[i] = pkg.ReadString();

                // Resolution peerID -> SteamID via le socket
                string steamId = null;
                var peer = ZNet.instance.GetPeer(senderID);
                if (peer != null && peer.m_socket != null)
                {
                    steamId = peer.m_socket.GetHostName();
                    EventControllerPlugin.Log.LogInfo(
                        $"[RPC] PeerID {senderID} -> SteamID {steamId}");
                }

                if (string.IsNullOrEmpty(steamId) || !ZNet.instance.IsAdmin(steamId))
                {
                    EventControllerPlugin.Log.LogWarning(
                        $"[RPC] Non-admin (peer={senderID}, steam={steamId ?? \"?\"}) a tente : {string.Join(" ", tokens)}");
                    SendMsgToPeer(senderID, "<color=red>Refuse : tu n'es pas admin.</color>");
                    return;
                }

                EventControllerPlugin.Log.LogInfo(
                    $"[RPC] Admin {steamId} : {string.Join(" ", tokens)}");

                ExecuteServerSide(tokens, null, replyToPeer: senderID);
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogError($"OnRpcCmdReceived failed : {e}");
            }
        }

        public static void ExecuteServerSide(string[] tokens, Terminal.ConsoleEventArgs contextArgs,
            long replyToPeer = 0L)
        {
            void Reply(string msg)
            {
                EventControllerPlugin.Log.LogInfo($"[xpevent] {msg}");
                contextArgs?.Context?.AddString(msg);
                if (replyToPeer != 0L)
                    SendMsgToPeer(replyToPeer, msg);
            }

            if (tokens == null || tokens.Length < 2)
            {
                Reply($"Usage: {CommandName} give <xp|drop|both> [nombre] | status");
                return;
            }

            var sub = tokens[1].ToLowerInvariant();
            switch (sub)
            {
                // Distribution des potions d'event personnelles.
                // xpevent give <xp|drop|both> [nombre par joueur]
                case "give":
                {
                    string kind = tokens.Length >= 3 ? tokens[2].ToLowerInvariant() : "both";
                    if (kind != "xp" && kind != "drop" && kind != "both")
                    {
                        Reply("Usage: xpevent give <xp|drop|both> [nombre]");
                        return;
                    }
                    int count = 1;
                    if (tokens.Length >= 4) int.TryParse(tokens[3], out count);
                    count = Mathf.Clamp(count <= 0 ? 1 : count, 1, 10);

                    int sent = BroadcastGive(kind, count);
                    bool selfToo = Player.m_localPlayer != null; // solo / client-hote
                    if (selfToo) EventPotions.HandleGive(kind, count);

                    Reply($"Elixir(s) '{kind}' x{count} distribue(s) a {sent} joueur(s)" +
                          (selfToo ? " + toi." : "."));
                    break;
                }

                case "status":
                    Reply($"Potions : XP x{EventControllerPlugin.CfgPotionXpMult.Value} / " +
                          $"Butin x{EventControllerPlugin.CfgPotionDropMult.Value}, " +
                          $"duree {EventControllerPlugin.CfgPotionDurationMinutes.Value} min. " +
                          $"Blacklist : {EventControllerPlugin.ExcludedItemsSet.Count} items, " +
                          $"{EventControllerPlugin.ExcludedCharactersSet.Count} characters.");
                    break;

                // v3.4.0 : le systeme d'event global a ete retire.
                case "start":
                case "stop":
                    Reply("Le systeme d'event global a ete retire (v3.4.0). " +
                          "Utilise 'xpevent give <xp|drop|both> [nombre]' pour distribuer les elixirs.");
                    break;

                default:
                    Reply($"Sous-commande inconnue : {sub}. Usage: {CommandName} give <xp|drop|both> [nombre] | status");
                    break;
            }
        }

        // ============================================================
        // Distribution des potions a tous les peers connectes.
        // ============================================================
        private static int BroadcastGive(string kind, int count)
        {
            int sent = 0;
            try
            {
                if (ZNet.instance == null || ZRoutedRpc.instance == null) return 0;
                foreach (var p in ZNet.instance.GetPeers())
                {
                    if (p == null) continue;
                    var pkg = new ZPackage();
                    pkg.Write(kind);
                    pkg.Write(count);
                    ZRoutedRpc.instance.InvokeRoutedRPC(p.m_uid, RpcGive, pkg);
                    sent++;
                }
                EventControllerPlugin.Log.LogInfo($"[Potions] Give '{kind}' x{count} envoye a {sent} peer(s).");
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogError($"BroadcastGive failed : {e}");
            }
            return sent;
        }

        // CLIENT : reception d'un ordre de distribution
        public static void OnGiveReceived(long senderID, ZPackage pkg)
        {
            try
            {
                if (pkg == null) return;
                if (ZNet.instance != null && ZNet.instance.IsServer()) return;

                var kind = pkg.ReadString();
                var count = pkg.ReadInt();
                EventPotions.HandleGive(kind, count);
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogWarning($"OnGiveReceived failed : {e.Message}");
            }
        }

        // ============================================================
        // v3.5.0 : GIVE GENERIQUE (recompenses de vote, ValheimRestApi)
        //
        // Le serveur (via le mod ValheimRestApi et le bot "Roue du
        // Valhalla") envoie un item quelconque a UN joueur cible :
        //   pkg = (prefabName: string, count: int, message: string)
        // Meme cascade que les elixirs : Inventory.AddItem d'abord,
        // repli au sol (auto-pickup) si inventaire plein.
        // ============================================================
        // CLIENT : reception d'un give generique
        public static void OnGiveItemReceived(long senderID, ZPackage pkg)
        {
            try
            {
                if (pkg == null) return;
                if (ZNet.instance != null && ZNet.instance.IsServer()) return;

                var prefabName = pkg.ReadString();
                var count = pkg.ReadInt();
                var message = pkg.ReadString();
                EventPotions.HandleGiveItem(prefabName, count, message);
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogWarning($"OnGiveItemReceived failed : {e.Message}");
            }
        }

        // ============================================================
        // Reponse au client via notre RPC Msg (MessageHud).
        // ============================================================
        private static void SendMsgToPeer(long targetPeerId, string msg)
        {
            try
            {
                if (ZRoutedRpc.instance == null) return;
                var pkg = new ZPackage();
                pkg.Write(msg ?? "");
                ZRoutedRpc.instance.InvokeRoutedRPC(targetPeerId, RpcMsg, pkg);
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogWarning($"SendMsgToPeer failed : {e.Message}");
            }
        }

        // CLIENT : reception d'un message serveur -> affichage HUD
        public static void OnMsgReceived(long senderID, ZPackage pkg)
        {
            try
            {
                if (pkg == null) return;
                if (ZNet.instance != null && ZNet.instance.IsServer()) return;

                var msg = pkg.ReadString();
                if (string.IsNullOrEmpty(msg)) return;

                if (MessageHud.instance != null)
                    MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, msg);

                EventControllerPlugin.Log.LogInfo($"[Msg serveur] {msg}");
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogWarning($"OnMsgReceived failed : {e.Message}");
            }
        }

        private static string[] SplitArgs(Terminal.ConsoleEventArgs args)
        {
            var list = new List<string>();
            for (int i = 0; i < args.Length; i++)
                list.Add(args[i]);
            return list.ToArray();
        }

        // ============================================================
        // Resolution du peer ID du serveur, compatible toutes versions.
        // ZRoutedRpc.GetServerPeerID() n'existe pas dans l'assembly
        // 0.221.x -> on passe par ZNet.GetServerPeer() via reflexion.
        //
        // Fallback : 0L (Everybody). Le paquet atteint quand meme le
        // serveur, et les clients l'ignorent (check IsServer() dans
        // les handlers).
        // ============================================================
        internal static long GetServerRouteId()
        {
            try
            {
                if (ZNet.instance != null)
                {
                    var m = AccessTools.Method(typeof(ZNet), "GetServerPeer");
                    if (m != null)
                    {
                        var peer = m.Invoke(ZNet.instance, null) as ZNetPeer;
                        if (peer != null) return peer.m_uid;
                    }
                }
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogWarning($"GetServerRouteId fallback Everybody : {e.Message}");
            }
            return 0L; // ZRoutedRpc.Everybody
        }
    }

    // ============================================================
    // SYNC DES BLACKLISTS : Serveur <-> Clients
    //
    // Le client a besoin des blacklists du serveur : la potion de
    // butin evalue ShouldMultiplyItem au RAMASSAGE, donc cote client.
    // Triple filet : push a la connexion + pull au spawn + heartbeat.
    // ============================================================
    internal static class StateSync
    {
        public static void BroadcastToAll()
        {
            BroadcastInternal(silent: false);
        }

        // Variante silencieuse pour le heartbeat periodique
        public static void BroadcastToAllSilent()
        {
            BroadcastInternal(silent: true);
        }

        private static void BroadcastInternal(bool silent)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (ZRoutedRpc.instance == null) return;

                var peers = ZNet.instance.GetPeers();
                int sent = 0;
                foreach (var p in peers)
                {
                    if (p == null) continue;
                    var pkg = BuildStatePackage();
                    ZRoutedRpc.instance.InvokeRoutedRPC(p.m_uid, Commands.RpcState, pkg);
                    sent++;
                }

                if (!silent)
                {
                    EventControllerPlugin.Log.LogInfo(
                        $"[StateSync] Blacklists envoyees a {sent} peer(s).");
                }
                else if (EventControllerPlugin.CfgVerboseLog.Value)
                {
                    EventControllerPlugin.Log.LogInfo(
                        $"[Heartbeat] Blacklists (silencieux) a {sent} peer(s).");
                }
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogError($"StateSync.Broadcast failed : {e}");
            }
        }

        public static void SendToPeer(long peerId)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (ZRoutedRpc.instance == null) return;

                var pkg = BuildStatePackage();
                ZRoutedRpc.instance.InvokeRoutedRPC(peerId, Commands.RpcState, pkg);
                EventControllerPlugin.Log.LogInfo(
                    $"[StateSync] Blacklists envoyees au peer {peerId}.");
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogError($"StateSync.SendToPeer failed : {e}");
            }
        }

        // CLIENT -> SERVEUR : demande explicite des blacklists.
        public static void RequestStateFromServer(string source)
        {
            try
            {
                if (ZNet.instance == null || ZNet.instance.IsServer()) return;
                if (ZRoutedRpc.instance == null) return;

                long serverId = Commands.GetServerRouteId();
                ZRoutedRpc.instance.InvokeRoutedRPC(serverId, Commands.RpcRequest, new ZPackage());
                EventControllerPlugin.Log.LogInfo($"[StateSync] Demande des blacklists envoyee au serveur ({source}).");
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogWarning($"RequestStateFromServer failed : {e.Message}");
            }
        }

        // SERVEUR : un client demande les blacklists -> on lui renvoie
        public static void OnRequestReceived(long senderID, ZPackage pkg)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                SendToPeer(senderID);
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogWarning($"OnRequestReceived failed : {e.Message}");
            }
        }

        // v3.4.0 : le paquet ne transporte plus que les blacklists.
        private static ZPackage BuildStatePackage()
        {
            var pkg = new ZPackage();
            pkg.Write(EventControllerPlugin.CfgExcludedItems.Value ?? "");
            pkg.Write(EventControllerPlugin.CfgExcludedCharacters.Value ?? "");
            return pkg;
        }

        public static void OnStateReceived(long senderID, ZPackage pkg)
        {
            try
            {
                if (pkg == null) return;

                if (ZNet.instance != null && ZNet.instance.IsServer())
                    return; // le serveur est l'autorite, il ignore

                string excludedItems = pkg.ReadString();
                string excludedChars = pkg.ReadString();

                bool changed = EventControllerPlugin.UpdateBlacklistsFromNetwork(
                    excludedItems, excludedChars);

                if (changed || EventControllerPlugin.CfgVerboseLog.Value)
                {
                    EventControllerPlugin.Log.LogInfo(
                        $"[StateSync] Blacklists recues" + (changed ? " [CHANGEMENT]" : " [heartbeat]"));
                }
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogError($"OnStateReceived failed : {e}");
            }
        }
    }

    // ============================================================
    // Enregistrement des 5 RPCs
    // ============================================================
    internal static class RpcRegistration
    {
        public static bool Registered;

        public static void TryRegister(string source)
        {
            if (Registered) return;
            try
            {
                if (ZRoutedRpc.instance == null) return;

                ZRoutedRpc.instance.Register<ZPackage>(Commands.RpcCmd, Commands.OnRpcCmdReceived);
                ZRoutedRpc.instance.Register<ZPackage>(Commands.RpcState, StateSync.OnStateReceived);
                ZRoutedRpc.instance.Register<ZPackage>(Commands.RpcRequest, StateSync.OnRequestReceived);
                ZRoutedRpc.instance.Register<ZPackage>(Commands.RpcMsg, Commands.OnMsgReceived);
                ZRoutedRpc.instance.Register<ZPackage>(Commands.RpcGive, Commands.OnGiveReceived);
                ZRoutedRpc.instance.Register<ZPackage>(Commands.RpcGiveItem, Commands.OnGiveItemReceived);
                Registered = true;
                EventControllerPlugin.Log.LogInfo(
                    $"[Patch_RegisterRpc] 6 RPCs EventController enregistres via {source}.");
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogError($"RPC register failed via {source} : {e}");
            }
        }
    }

    [HarmonyPatch]
    static class Patch_GameStart
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Game), "Start")
                ?? AccessTools.Method(typeof(Game), "Awake");
        }
        static void Postfix() => RpcRegistration.TryRegister("Game.Start/Awake");
    }

    [HarmonyPatch]
    static class Patch_ZNetStart
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ZNet), "Start")
                ?? AccessTools.Method(typeof(ZNet), "Awake");
        }
        static void Postfix() => RpcRegistration.TryRegister("ZNet.Start/Awake");
    }

    // ============================================================
    // Nouveau joueur -> push des blacklists (en plus du pull client)
    // ============================================================
    [HarmonyPatch]
    static class Patch_PeerJoined
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ZNet), "RPC_PeerInfo");
        }

        static void Postfix(ZRpc rpc)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

                var peers = ZNet.instance.GetPeers();
                foreach (var p in peers)
                {
                    if (p != null && p.m_rpc == rpc)
                    {
                        StateSync.SendToPeer(p.m_uid);
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                EventControllerPlugin.Log.LogWarning($"Patch_PeerJoined failed : {e.Message}");
            }
        }
    }
}
