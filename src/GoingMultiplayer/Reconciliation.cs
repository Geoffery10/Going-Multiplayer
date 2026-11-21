using UnityEngine;

namespace GoingMultiplayer
{
    // Server reconciliation for the guest's own player. The host simulates the
    // guest from its commands; the guest also runs its own player locally for
    // responsiveness. When the authoritative pose drifts past a deadband the
    // guest is pulled back, gently for small errors and by a hard snap for large
    // ones.
    //
    // Deliberately not rollback: there is no input history yet. 30 Hz commands
    // and a 0.75 unit deadband keep ordinary walking from rubber-banding.
    internal static class Reconciliation
    {
        internal const float Deadband = 0.75f;
        internal const float HardSnapDistance = 3f;
        internal const float SoftSpeed = 8f;
        internal const float MaxSpeed = 60f;

        internal static int Corrections;
        internal static int HardSnaps;
        internal static float LastError;
        internal static Vector3 LastAuthoritative;

        private static Player glidePlayer;
        private static Vector3 glideTarget;
        private static float glideSpeed;
        private static bool gliding;

        internal static void Reset()
        {
            Corrections = 0;
            HardSnaps = 0;
            LastError = 0f;
            gliding = false;
            glidePlayer = null;
        }

        internal static void Apply(Player local, PlayerSnapshot snapshot)
        {
            if (local == null || !local) return;
            Vector3 authoritative = new Vector3(snapshot.X, snapshot.Y, snapshot.Z);
            Vector3 current = local.transform.position;
            float error = Vector3.Distance(current, authoritative);
            LastError = error;
            LastAuthoritative = authoritative;
            if (error <= Deadband) return;

            LastAuthoritative = authoritative;
            Corrections++;
            if (error >= HardSnapDistance) HardSnaps++;
            // Glide instead of teleporting: a hard snap is visually a jump and a
            // small step per snapshot looks like a stutter. Speed scales with the
            // error so large corrections still finish fast.
            glidePlayer = local;
            glideTarget = authoritative;
            glideSpeed = Mathf.Clamp(error * 6f, SoftSpeed, MaxSpeed);
            gliding = true;
        }

        // Called once per frame while a correction is pending.
        internal static void Tick(float deltaTime)
        {
            if (!gliding) return;
            if (glidePlayer == null || !glidePlayer)
            {
                gliding = false;
                glidePlayer = null;
                return;
            }
            float step = glideSpeed * Mathf.Max(deltaTime, 0f);
            Vector3 current = glidePlayer.transform.position;
            Vector3 next = Vector3.MoveTowards(current, glideTarget, step);
            Move(glidePlayer, next, false);
            if (Vector3.Distance(next, glideTarget) < 0.05f)
            {
                gliding = false;
                glidePlayer = null;
            }
        }

        private static void Move(Player player, Vector3 position, bool zeroVelocity)
        {
            player.transform.position = position;
            if (player.rb)
            {
                player.rb.position = position;
                if (zeroVelocity) player.rb.velocity = Vector3.zero;
            }
        }
    }
}
