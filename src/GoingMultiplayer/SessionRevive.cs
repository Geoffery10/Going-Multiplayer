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
        private static Harmony patches;

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
            if (view != null && view.anim != null) view.anim.SetBool("Prone", false);
        }

        internal static bool IsDown(Player player)
        {
            return player != null && player && player.health + player.overHealth <= 0f;
        }

        // Called every frame on the client that owns the local player.
        internal static void Tick(Player local, Player partner, int partnerId,
            System.Action<int, float> sendRevive)
        {
            if (!Active || local == null || !local || partner == null || !partner)
            {
                holdTime = 0f;
                return;
            }
            bool down = IsDown(partner);
            bool canGive = local.health >= MinGiverHealth;
            float distance = Vector3.Distance(local.transform.position,
                partner.transform.position);
            bool holding = down && canGive && distance <= ReviveRange
                && local.input != null && local.input.InteractHeld();
            if (!holding)
            {
                holdTime = 0f;
                return;
            }
            holdTime += Time.deltaTime;
            if (holdTime < HoldSeconds) return;
            holdTime = 0f;

            float give = Mathf.Floor(local.health * 0.5f);
            if (give < 1f) give = 1f;
            if (local.health - give < 1f) return;
            local.health -= give;
            Revive(partner, give);
            if (sendRevive != null) sendRevive(partnerId, give);
        }

        internal static void Reset()
        {
            holdTime = 0f;
            Active = false;
        }
    }
}
