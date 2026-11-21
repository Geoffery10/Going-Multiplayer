using System;
using GoingMultiplayer;

internal static class InputCommandLedgerTests
{
    private static int checks;
    private static void Check(bool value)
    {
        checks++;
        if (!value) throw new Exception("Input command ledger check " + checks);
    }

    private static void Main()
    {
        var ledger = new InputCommandLedger(1, 2);
        Check(ledger.Accept(1, 1, 2, 0f, 1f) == InputDecision.Apply);
        Check(ledger.Accept(2, 1, 2, 1f, -1f) == InputDecision.Apply);
        Check(ledger.Accept(2, 1, 2, 0f, 0f) == InputDecision.Stale);
        Check(ledger.Accept(1, 1, 2, 0f, 0f) == InputDecision.Stale);
        Check(ledger.LastSequence == 2); // stale commands do not advance the window
        Check(ledger.Accept(3, 0, 2, 0f, 1f) == InputDecision.Reject);
        Check(ledger.Accept(3, 2, 2, 0f, 1f) == InputDecision.Reject);
        Check(ledger.Accept(3, 1, 1, 0f, 1f) == InputDecision.Reject);
        Check(ledger.Accept(0, 1, 2, 0f, 1f) == InputDecision.Reject);
        Check(ledger.Accept(-4, 1, 2, 0f, 1f) == InputDecision.Reject);
        Check(ledger.Accept(3, 1, 2, float.NaN, 0f) == InputDecision.Reject);
        Check(ledger.Accept(3, 1, 2, 0f, float.NaN) == InputDecision.Reject);
        Check(ledger.Accept(3, 1, 2, float.PositiveInfinity, 0f) == InputDecision.Reject);
        Check(ledger.Accept(3, 1, 2, 0f, float.NegativeInfinity) == InputDecision.Reject);
        Check(ledger.Accept(3, 1, 2, -1.02f, 0f) == InputDecision.Reject);
        Check(ledger.Accept(3, 1, 2, 0f, 1.02f) == InputDecision.Reject);
        Check(ledger.LastSequence == 2); // rejected commands did not consume a sequence
        Check(ledger.Accept(3, 1, 2, -1f, 1f) == InputDecision.Apply);
        Check(ledger.Accept(4, 1, 2, 1.01f, -1.01f) == InputDecision.Apply);
        Check(ledger.Accept(5, 1, 2, 0f, 0f) == InputDecision.Apply); // explicit stop
        var other = new InputCommandLedger(1, 2);
        Check(other.Accept(100, 1, 2, 0f, 1f) == InputDecision.Apply); // independent session
        Check(new InputCommandLedger(2, 2).Accept(1, 1, 2, 0f, 1f) == InputDecision.Reject);
        Console.WriteLine("INPUT_LEDGER PASS checks=" + checks);
    }
}
