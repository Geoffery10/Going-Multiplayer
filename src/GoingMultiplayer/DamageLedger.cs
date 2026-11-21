using System.Collections.Generic;

namespace GoingMultiplayer
{
    internal enum DamageDecision { Apply, Duplicate, Reject }

    // One ledger per session. Only the host's current-epoch damage events
    // reach this boundary; session/sender validation belongs to the transport.
    internal sealed class DamageLedger
    {
        private readonly int epoch;
        private readonly int maxPlayers;
        private readonly HashSet<long> applied = new HashSet<long>();

        internal DamageLedger(int currentEpoch, int maxPlayers = 2)
        {
            epoch = currentEpoch;
            this.maxPlayers = maxPlayers < 2 ? 2 : maxPlayers;
        }

        internal DamageDecision Accept(int eventEpoch, long eventId, int target, float amount)
        {
            if (eventEpoch != epoch || eventId <= 0 || target < 1 || target > maxPlayers
                || float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f || amount > 100f)
                return DamageDecision.Reject;
            return applied.Add(eventId) ? DamageDecision.Apply : DamageDecision.Duplicate;
        }
    }
}
