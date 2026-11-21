using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GoingMultiplayer
{
    // Experimental single-process integration. No transport or synchronized runs yet.
    internal static class LocalSeats
    {
        internal sealed class Seat
        {
            internal Player Player;
            internal PlayerData Data;
            internal Costume Costume;
            internal Material Material;
            internal bool Scripted;
            internal bool Persistent;
            internal Vector2 Move;
            internal string Palette;
            internal ISeatPad Pad;
            internal NetworkInputState Network;
        }

        internal static readonly Dictionary<Player, Seat> Seats = new Dictionary<Player, Seat>();
        private static readonly Dictionary<PlayerInputRewired, Seat> Inputs = new Dictionary<PlayerInputRewired, Seat>();
        private static Harmony patches;
        private static Player protectedLocal;
        private static bool originalGrabbable;
        internal static bool NegativeHealth;
        internal static int Count { get { return Seats.Count; } }

        internal static void Install()
        {
            if (patches != null) return;
            using (var file = File.OpenRead(typeof(Player).Assembly.Location))
            using (var hash = SHA256.Create())
                if (BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant()
                    != "ac1cac156da136779536db2ba85131ef9d7ef667bed984adf83ff313a14e1401")
                    throw new InvalidOperationException("Unsupported Going Under assembly; refusing gameplay patches");
            patches = new Harmony("goingmultiplayer.local-seats");
            try { ApplyPatches(); }
            catch
            {
                patches.UnpatchSelf();
                patches = null;
                throw;
            }
        }

        private static void ApplyPatches()
        {
            PatchGetter(typeof(Player), "data", "DataGet");
            PatchGetter(typeof(Player), "health", "HealthGet");
            PatchGetter(typeof(Player), "overHealth", "ArmorGet");
            PatchGetter(typeof(Player), "MaxHealth", "MaxHealthGet");
            PatchSetter("health", "HealthSet");
            PatchSetter("overHealth", "ArmorSet");
            PatchSetter("MaxHealth", "MaxHealthSet");
            foreach (MethodInfo method in typeof(PlayerInputRewired).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                string prefix = method.ReturnType == typeof(float) ? "FloatInput"
                    : method.ReturnType == typeof(bool) ? "BoolInput"
                    : method.ReturnType == typeof(int) ? "IntInput"
                    : method.ReturnType == typeof(Vector2) ? "VectorInput" : null;
                if (prefix != null) patches.Patch(method, prefix: Method(prefix));
            }
            foreach (string name in new[] { "TryAddToInventory", "UpdateEquippedItem", "StoreActiveItem",
                "BarfOutItem", "BarfOutDeepPockets", "RemoveFromInventory", "ScrollInventorySlot" })
                patches.Patch(AccessTools.Method(typeof(PlayerInventory), name),
                    prefix: Method("InventoryContext"), finalizer: Method("RestoreContext"));
            foreach (string name in new[] { "SetCostume", "RefreshCostume" })
                patches.Patch(AccessTools.Method(typeof(PlayerView), name),
                    prefix: Method("ViewContext"), finalizer: Method("RestoreContext"));
        }

        private static HarmonyMethod Method(string name)
        {
            return new HarmonyMethod(typeof(LocalSeats).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static void PatchGetter(Type type, string property, string prefix)
        {
            patches.Patch(AccessTools.PropertyGetter(type, property), prefix: Method(prefix));
        }

        private static void PatchSetter(string property, string prefix)
        {
            patches.Patch(AccessTools.PropertySetter(typeof(Player), property), prefix: Method(prefix));
        }

        internal static Seat Spawn(Player local, Vector3 position, bool scripted)
        {
            return Spawn(local, position, scripted, false);
        }

        internal static Seat Spawn(Player local, Vector3 position, bool scripted, bool persistent)
        {
            Install();
            GameObject holder = new GameObject("GMP independent player data");
            holder.SetActive(false); // Prevent vanilla singleton Awake/Start on this data holder.
            PlayerData data = holder.AddComponent<PlayerData>();
            data.enabled = false;
            data.playerPrefab = local.data.playerPrefab;
            data.input = new PlayerInputRewired();
            data.maxHealth = local.MaxHealth;
            data.currentHealth = data.maxHealth;
            data.inventory = holder.AddComponent<PlayerInventory>();
            data.inventory.inventory = new List<Entity> { null, null, null };
            data.inventory.inventoryContainer = new GameObject("GMP inventory").transform;
            data.inventory.inventoryContainer.SetParent(holder.transform, false);
            Player original = Player.singlePlayer;
            Player player;
            try
            {
                Player.singlePlayer = null;
                player = Object.Instantiate(data.playerPrefab, position, Quaternion.identity).GetComponent<Player>();
            }
            finally { Player.singlePlayer = original; }
            if (!player || !data) throw new InvalidOperationException("Independent player/data was rejected");
            var seat = new Seat { Player = player, Data = data, Scripted = scripted, Persistent = persistent };
            Seats.Add(player, seat);
            if (persistent)
            {
                // A session actor survives scene loads; ActorRegistry re-binds and
                // repositions it after the next scene is ready.
                Object.DontDestroyOnLoad(holder);
                Object.DontDestroyOnLoad(player.gameObject);
            }
            if (protectedLocal != local)
            {
                if (protectedLocal) protectedLocal.isGrabbable = originalGrabbable;
                protectedLocal = local;
                originalGrabbable = local.isGrabbable;
                local.isGrabbable = false;
            }
            player.isGrabbable = false; // Teammates are actors, not pickup items.
            Inputs.Add(data.input, seat);
            foreach (Camera camera in player.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
            foreach (AudioListener listener in player.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
            try { ApplyPalette(seat, local); }
            catch
            {
                Seats.Remove(player);
                Inputs.Remove(data.input);
                Object.Destroy(player.gameObject);
                Object.Destroy(holder);
                if (seat.Material) Object.Destroy(seat.Material);
                if (seat.Costume) Object.Destroy(seat.Costume);
                if (Seats.Count == 0 && protectedLocal)
                {
                    protectedLocal.isGrabbable = originalGrabbable;
                    protectedLocal = null;
                }
                throw;
            }
            if (!scripted) ControllerSeats.AssignPreferred(seat);
            return seat;
        }

        // Re-create only the scene object for an existing actor. Its data holder,
        // inventory, health and costume survive, so the network id still maps to
        // the same gameplay state after a scene transition.
        internal static void Respawn(Seat seat, Vector3 position)
        {
            if (seat == null || !seat.Data)
                throw new InvalidOperationException("Cannot respawn an actor without data");
            var stale = new List<Player>();
            foreach (KeyValuePair<Player, Seat> pair in Seats)
                if (pair.Value == seat) stale.Add(pair.Key);
            foreach (Player old in stale) Seats.Remove(old);

            Player original = Player.singlePlayer;
            Player player;
            try
            {
                Player.singlePlayer = null;
                player = Object.Instantiate(seat.Data.playerPrefab, position, Quaternion.identity)
                    .GetComponent<Player>();
            }
            finally { Player.singlePlayer = original; }
            if (!player) throw new InvalidOperationException("Actor respawn was rejected");

            seat.Player = player;
            Seats[player] = seat;
            if (seat.Data.input != null) Inputs[seat.Data.input] = seat;
            player.isGrabbable = false;
            foreach (Camera camera in player.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
            foreach (AudioListener listener in player.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
            if (seat.Costume) player.view.SetCostume(seat.Costume);
            if (seat.Persistent) Object.DontDestroyOnLoad(player.gameObject);
        }

        internal static void Forget(Seat seat)
        {
            if (seat == null) return;
            var stale = new List<Player>();
            foreach (KeyValuePair<Player, Seat> pair in Seats)
                if (pair.Value == seat) stale.Add(pair.Key);
            foreach (Player old in stale) Seats.Remove(old);
            if (seat.Data != null && seat.Data.input != null) Inputs.Remove(seat.Data.input);
        }

        internal static Seat ScriptLocalInput(Player player)
        {
            var seat = new Seat { Player = player, Data = player.data, Scripted = true };
            Inputs[player.input] = seat;
            return seat;
        }

        private static void ApplyPalette(Seat seat, Player local)
        {
            Costume source = local.view.currentCostume;
            var choices = new List<Costume>();
            foreach (Costume candidate in GlobalSettings.defaults.jackieCostumes)
                if (candidate && candidate.material && candidate.material != source.material
                    && candidate.material.mainTexture != source.material.mainTexture)
                    choices.Add(candidate);
            if (choices.Count == 0) throw new InvalidOperationException("No alternate authored palette found");
            Costume chosen = choices[(Seats.Count - 1) % choices.Count];
            // Own Costume/Material instances; never change an asset shared with player one.
            // Costumes also carry palette-bearing mesh/vertex data. Swapping only
            // their material can leave the original colors visible.
            seat.Costume = Object.Instantiate(chosen);
            seat.Material = new Material(chosen.material);
            seat.Material.name = "GMP P2 palette (" + chosen.name + ")";
            seat.Costume.material = seat.Material;
            seat.Palette = chosen.name;
            seat.Player.view.SetCostume(seat.Costume);
        }

        private static bool DataGet(Player __instance, ref PlayerData __result)
        {
            Seat seat;
            if (!Seats.TryGetValue(__instance, out seat)) return true;
            __result = seat.Data;
            return false;
        }

        private static bool HealthGet(Player __instance, ref float __result)
        {
            Seat seat;
            if (!Seats.TryGetValue(__instance, out seat) || NegativeHealth) return true;
            __result = seat.Data.currentHealth;
            return false;
        }

        private static bool HealthSet(Player __instance, float __0)
        {
            Seat seat;
            if (!Seats.TryGetValue(__instance, out seat)) return true;
            seat.Data.currentHealth = __0;
            return false;
        }

        private static bool ArmorGet(Player __instance, ref float __result)
        {
            Seat seat;
            if (!Seats.TryGetValue(__instance, out seat)) return true;
            __result = seat.Data.currentOverhealth;
            return false;
        }

        private static bool ArmorSet(Player __instance, float __0)
        {
            Seat seat;
            if (!Seats.TryGetValue(__instance, out seat)) return true;
            seat.Data.currentOverhealth = __0;
            return false;
        }

        private static bool MaxHealthGet(Player __instance, ref float __result)
        {
            Seat seat;
            if (!Seats.TryGetValue(__instance, out seat)) return true;
            __result = seat.Data.maxHealth;
            return false;
        }

        private static bool MaxHealthSet(Player __instance, float __0)
        {
            Seat seat;
            if (!Seats.TryGetValue(__instance, out seat)) return true;
            seat.Data.maxHealth = __0;
            return false;
        }

        private static bool FloatInput(PlayerInputRewired __instance, MethodBase __originalMethod, ref float __result)
        {
            Seat seat;
            if (!Inputs.TryGetValue(__instance, out seat)) return true;
            if (seat.Network != null)
            {
                string name = __originalMethod.Name;
                __result = name == "GetHorizontalAxis" ? seat.Network.Command.MoveX
                    : name == "GetVerticalAxis" ? seat.Network.Command.MoveY
                    : name == "GetCameraHorizontalAxisController" ? seat.Network.Command.CameraX
                    : 0f;
                return false;
            }
            if (seat.Pad != null)
            {
                __result = seat.Pad.Read().Axis(__originalMethod.Name);
                return false;
            }
            Vector2 move = seat.Scripted ? seat.Move : new Vector2(
                (Input.GetKey(KeyCode.L) ? 1 : 0) - (Input.GetKey(KeyCode.J) ? 1 : 0),
                (Input.GetKey(KeyCode.I) ? 1 : 0) - (Input.GetKey(KeyCode.K) ? 1 : 0));
            __result = __originalMethod.Name == "GetHorizontalAxis" ? move.x
                : __originalMethod.Name == "GetVerticalAxis" ? move.y : 0f;
            return false;
        }

        private static bool BoolInput(PlayerInputRewired __instance, MethodBase __originalMethod, ref bool __result)
        {
            Seat seat;
            if (!Inputs.TryGetValue(__instance, out seat)) return true;
            string name = __originalMethod.Name;
            if (seat.Network != null)
            {
                __result = NetworkBool(seat.Network, name);
                return false;
            }
            if (seat.Pad != null)
            {
                __result = seat.Pad.Read().Button(name);
                return false;
            }
            __result = name == "IsEnabled";
            if (!seat.Scripted)
            {
                KeyCode key = name.StartsWith("Attack") ? KeyCode.U
                    : name.StartsWith("Interact") ? KeyCode.O
                    : name.StartsWith("Dodge") ? KeyCode.RightShift
                    : name.StartsWith("Throw") ? KeyCode.P : KeyCode.None;
                if (key != KeyCode.None)
                    __result = name.EndsWith("Held") ? Input.GetKey(key) : Input.GetKeyDown(key);
            }
            return false;
        }

        private static bool IntInput(PlayerInputRewired __instance, ref int __result)
        {
            Seat seat;
            if (!Inputs.TryGetValue(__instance, out seat)) return true;
            if (seat.Network != null)
            {
                __result = seat.Network.Command.Inventory;
                return false;
            }
            if (seat.Pad != null)
            {
                __result = seat.Pad.Read().Inventory();
                return false;
            }
            __result = seat.Scripted ? 0 : Input.GetKeyDown(KeyCode.N) ? 1 : Input.GetKeyDown(KeyCode.H) ? -1 : 0;
            return false;
        }

        private static bool VectorInput(PlayerInputRewired __instance, MethodBase __originalMethod, ref Vector2 __result)
        {
            Seat seat;
            if (!Inputs.TryGetValue(__instance, out seat)) return true;
            if (seat.Network != null)
            {
                string name = __originalMethod.Name;
                __result = name == "GetMovementAxisController"
                    ? new Vector2(seat.Network.Command.MoveX, seat.Network.Command.MoveY)
                    : name == "GetCameraAxisController" || name == "GetCameraAxisMouse"
                    ? new Vector2(seat.Network.Command.CameraX, seat.Network.Command.CameraY)
                    : Vector2.zero;
                return false;
            }
            if (seat.Pad != null)
            {
                PadFrame frame = seat.Pad.Read();
                __result = __originalMethod.Name == "GetMovementAxisController"
                    ? new Vector2(frame.Axis("GetHorizontalAxis"), frame.Axis("GetVerticalAxis"))
                    : __originalMethod.Name == "GetCameraAxisController" && frame.Connected
                    ? new Vector2(PadFrame.Clean(frame.LookX), PadFrame.Clean(frame.LookY)) : Vector2.zero;
                return false;
            }
            __result = Vector2.zero;
            return false;
        }

        private static bool NetworkBool(NetworkInputState network, string name)
        {
            if (name == "IsEnabled") return true;
            if (name == "AnyActionPressed") return network.AnyActionPressed;
            if (name == "FocusSwapLeft") return network.Pressed(PlayerButtons.FocusSwapLeft);
            if (name == "FocusSwapRight") return network.Pressed(PlayerButtons.FocusSwapRight);
            PlayerButtons button = name.StartsWith("AltAttack") ? PlayerButtons.AltAttack
                : name.StartsWith("Attack") ? PlayerButtons.Attack
                : name.StartsWith("Interact") ? PlayerButtons.Interact
                : name.StartsWith("Dodge") ? PlayerButtons.Dodge
                : name.StartsWith("Throw") ? PlayerButtons.Throw
                : name.StartsWith("Focus") ? PlayerButtons.Focus : PlayerButtons.None;
            if (button == PlayerButtons.None) return false;
            if (name.EndsWith("Held")) return network.Held(button);
            if (name.EndsWith("Released")) return network.Released(button);
            return network.Pressed(button);
        }

        private static void InventoryContext(PlayerInventory __instance, out Player __state)
        {
            __state = Player.singlePlayer;
            foreach (Seat seat in Seats.Values)
                if (seat.Data.inventory == __instance) { Player.singlePlayer = seat.Player; break; }
        }

        private static void ViewContext(PlayerView __instance, out Player __state)
        {
            __state = Player.singlePlayer;
            Player player = __instance.GetComponentInParent<Player>();
            if (player && Seats.ContainsKey(player))
                Player.singlePlayer = player;
        }

        private static Exception RestoreContext(Exception __exception, Player __state)
        {
            Player.singlePlayer = __state;
            return __exception;
        }

        internal static void Clear()
        {
            ControllerSeats.Release();
            foreach (Seat seat in Seats.Values)
            {
                if (seat.Player) Object.Destroy(seat.Player.gameObject);
                if (seat.Data) Object.Destroy(seat.Data.gameObject);
                if (seat.Material) Object.Destroy(seat.Material);
                if (seat.Costume) Object.Destroy(seat.Costume);
            }
            Seats.Clear();
            Inputs.Clear();
            if (protectedLocal) protectedLocal.isGrabbable = originalGrabbable;
            protectedLocal = null;
            if (patches != null) patches.UnpatchSelf();
            patches = null;
        }
    }
}
