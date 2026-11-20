using System.Collections.Generic;

namespace GoingMultiplayer
{
    internal enum DamageDecision { Apply, Duplicate, Reject }

    // One ledger per session. Only the host's current-epoch damage events
    // reach this boundary; session/sender validation belongs to the transport.
    internal sealed class DamageLedger
    {
        private readonly int epoch;
        private readonly HashSet<long> applied = new HashSet<long>();

        internal DamageLedger(int currentEpoch) { epoch = currentEpoch; }

        internal DamageDecision Accept(int eventEpoch, long eventId, int target, float amount)
        {
            if (eventEpoch != epoch || eventId <= 0 || (target != 1 && target != 2)
                || float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f || amount > 100f)
                return DamageDecision.Reject;
            return applied.Add(eventId) ? DamageDecision.Apply : DamageDecision.Duplicate;
        }
    }
}
