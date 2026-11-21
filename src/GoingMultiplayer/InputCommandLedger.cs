namespace GoingMultiplayer
{
    internal enum InputDecision { Apply, Stale, Reject }

    // Host-side boundary for one remote player's movement commands. The
    // transport owns session/sender identity; this validates the current
    // epoch, expected player, monotonic sequence and finite axis range.
    internal sealed class InputCommandLedger
    {
        private readonly int epoch;
        private readonly int playerId;
        private long lastSequence;
        private bool hasSequence;

        internal InputCommandLedger(int currentEpoch, int expectedPlayerId)
        {
            epoch = currentEpoch;
            playerId = expectedPlayerId;
        }

        internal long LastSequence { get { return lastSequence; } }

        internal InputDecision Accept(long sequence, int commandEpoch, int commandPlayerId,
            float horizontal, float vertical)
        {
            if (commandEpoch != epoch || commandPlayerId != playerId || sequence <= 0)
                return InputDecision.Reject;
            if (float.IsNaN(horizontal) || float.IsInfinity(horizontal)
                || float.IsNaN(vertical) || float.IsInfinity(vertical)
                || horizontal < -1.01f || horizontal > 1.01f
                || vertical < -1.01f || vertical > 1.01f)
                return InputDecision.Reject;
            if (hasSequence && sequence <= lastSequence)
                return InputDecision.Stale;
            hasSequence = true;
            lastSequence = sequence;
            return InputDecision.Apply;
        }
    }
}
