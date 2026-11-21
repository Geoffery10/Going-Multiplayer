using System;
using GoingMultiplayer;

internal static class DamageLedgerTests
{
    private static int checks;
    private static void Check(bool value)
    {
        checks++;
        if (!value) throw new Exception("Damage ledger check " + checks);
    }

    private static void Main()
    {
        var ledger = new DamageLedger(1);
        Check(ledger.Accept(1, 1, 2, 1f) == DamageDecision.Apply);
        Check(ledger.Accept(1, 1, 2, 1f) == DamageDecision.Duplicate);
        Check(ledger.Accept(1, 1, 1, 2f) == DamageDecision.Duplicate);
        Check(ledger.Accept(0, 2, 2, 1f) == DamageDecision.Reject);
        Check(ledger.Accept(2, 2, 2, 1f) == DamageDecision.Reject);
        Check(ledger.Accept(1, 0, 2, 1f) == DamageDecision.Reject);
        Check(ledger.Accept(1, -1, 2, 1f) == DamageDecision.Reject);
        Check(ledger.Accept(1, 2, 0, 1f) == DamageDecision.Reject);
        Check(ledger.Accept(1, 2, 3, 1f) == DamageDecision.Reject);
        Check(ledger.Accept(1, 2, 2, 0f) == DamageDecision.Reject);
        Check(ledger.Accept(1, 2, 2, -1f) == DamageDecision.Reject);
        Check(ledger.Accept(1, 2, 2, float.NaN) == DamageDecision.Reject);
        Check(ledger.Accept(1, 2, 2, float.PositiveInfinity) == DamageDecision.Reject);
        Check(ledger.Accept(1, 2, 2, 101f) == DamageDecision.Reject);
        Check(ledger.Accept(1, 2, 2, 1f) == DamageDecision.Apply); // Invalid attempts did not consume id.
        Check(new DamageLedger(2).Accept(2, 1, 2, 1f) == DamageDecision.Apply); // New session.
        var four = new DamageLedger(1, 4);
        Check(four.Accept(1, 1, 4, 1f) == DamageDecision.Apply);
        Check(four.Accept(1, 2, 5, 1f) == DamageDecision.Reject);
        Console.WriteLine("DAMAGE_LEDGER PASS checks=" + checks);
    }
}
