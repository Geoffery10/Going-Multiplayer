using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GoingMultiplayer
{
    // Co-op down/revive. A player whose health reaches zero goes down instead of
    // dying: they keep their body in the world, cannot act, and a teammate can
    // hold Interact next to them to trade half of their own health for the
    // revive.
    internal static class SessionRevive
    {
        internal static bool Active;
        internal const float ReviveRange = 2.5f;
        internal const float HoldSeconds = 1.2f;
        internal const float MinGiverHealth = 2f;

        private static float holdTime;
        private static float wipeTime;
        private static float lastHealth = -1f;
        private static Harmony patches;

        internal static bool NearDowned { get; private set; }
        internal static float HoldProgress
        {
            get { return Mathf.Clamp01(holdTime / HoldSeconds); }
        }
        internal static bool SelfDown
        {
            get { return IsDown(Player.singlePlayer); }
        }

        internal static void Install()
        {
            if (patches != null) return;
            patches = new Harmony("goingmultiplayer.revive");
            MethodInfo death = AccessTools.Method(typeof(Entity), "TriggerDeath");
            if (death != null)
                patches.Patch(death, prefix: new HarmonyMethod(typeof(SessionRevive)
                    .GetMethod("TriggerDeathPrefix", BindingFlags.Static
                        | BindingFlags.NonPublic)));
        }

        // Replaces the death routine for players while a co-op session is live.
        private static bool TriggerDeathPrefix(Entity __instance)
        {
            if (!Active) return true;
            Player player = __instance as Player;
            if (player == null || !player) return true;
            Down(player);
            return false;
        }

        internal static void Down(Player player)
        {
            if (player == null || !player) return;
            PlayerView view = player.view;
            if (view != null && view.anim != null) view.anim.SetBool("Prone", true);
            if (player.rb) player.rb.velocity = Vector3.zero;
        }

        internal static void Revive(Player player, float amount)
        {
            if (player == null || !player) return;
            float restored = Mathf.Max(1f, amount);
            player.health = restored;
            player.overHealth = 0f;
            PlayerView view = player.view;
            if (view != null && view.anim != null)
            {
                view.anim.SetBool("Prone", false);
                view.anim.SetTrigger("Yay");
            }
            Feedback(player);
        }

        internal static bool IsDown(Player player)
        {
            return player != null && player && player.health + player.overHealth <= 0f;
        }

        // Called every frame on the client that owns the local player.
        internal static void Tick(Player local, Player partner, int partnerId,
            System.Action<int> requestRevive)
        {
            if (!Active || local == null || !local || partner == null || !partner)
            {
                holdTime = 0f;
                return;
            }
            // Taking damage cancels a revive in progress.
            if (lastHealth >= 0f && local.health < lastHealth) holdTime = 0f;
            lastHealth = local.health;

            bool down = IsDown(partner);
            bool canGive = local.health >= MinGiverHealth;
            float distance = Vector3.Distance(local.transform.position,
                partner.transform.position);
            NearDowned = down && canGive && distance <= ReviveRange;
            bool holding = NearDowned
                && local.input != null && local.input.InteractHeld();
            if (!holding)
            {
                holdTime = 0f;
                return;
            }
            holdTime += Time.deltaTime;
            if (holdTime < HoldSeconds) return;
            holdTime = 0f;

            // The host validates and applies; a guest asks the host to do it.
            if (requestRevive != null) requestRevive(partnerId);
        }

        // The host ends the run when nobody is left standing.
        internal static void TickWipe(Player local, Player partner, bool isHost,
            System.Action wipe)
        {
            if (!Active || !isHost || local == null || !local)
            {
                wipeTime = 0f;
                return;
            }
            bool allDown = IsDown(local) && (partner == null || !partner || IsDown(partner));
            if (!allDown)
            {
                wipeTime = 0f;
                return;
            }
            wipeTime += Time.deltaTime;
            if (wipeTime < 4f) return;
            wipeTime = 0f;
            if (wipe != null) wipe();
        }

        // Party wipe: hand the run back to the game's own death flow.
        internal static void ForceDeath(Player player)
        {
            if (player == null || !player) return;
            Active = false;
            holdTime = 0f;
            NearDowned = false;
            try { player.TriggerDeath(new KillInfo(player, null)); }
            catch (System.Exception) { }
        }

        private static void Feedback(Player player)
        {
            try
            {
                if (ParticleManager.instance != null)
                    ParticleManager.instance.PlayPoofHeart(player.GetCenter());
            }
            catch (System.Exception) { }
        }

        internal static void DrawGui()
        {
            if (!Active) return;
            float scale = Screen.height / 720f;
            if (scale <= 0f) scale = 1f;
            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity,
                new Vector3(scale, scale, 1f));
            float width = Screen.width / scale;
            if (SelfDown)
            {
                GUI.Box(new Rect(width * 0.5f - 220f, 560f, 440f, 48f),
                    "You are down - a teammate can hold Interact to revive you");
            }
            else if (NearDowned)
            {
                Rect panel = new Rect(width * 0.5f - 200f, 560f, 400f, 62f);
                GUI.Box(panel, "Hold Interact to revive");
                float bar = Mathf.Clamp01(HoldProgress);
                GUI.Box(new Rect(panel.x + 20f, panel.y + 32f, panel.width - 40f, 18f), "");
                if (bar > 0f)
                    GUI.Box(new Rect(panel.x + 20f, panel.y + 32f,
                        (panel.width - 40f) * bar, 18f), "");
            }
            GUI.matrix = saved;
        }

        internal static void Reset()
        {
            holdTime = 0f;
            wipeTime = 0f;
            lastHealth = -1f;
            NearDowned = false;
            Active = false;
        }
    }
}
