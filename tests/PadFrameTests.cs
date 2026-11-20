using System;
using GoingMultiplayer;

internal static class PadFrameTests
{
    private static int count;
    private static void Check(bool condition, string label)
    {
        count++;
        if (!condition) throw new Exception(label);
    }

    private static void Main()
    {
        var frame = new PadFrame { Connected = true, X = 1 };
        Check(frame.Axis("GetHorizontalAxis") == 1, "full axis");
        frame.X = 0.1f;
        Check(frame.Axis("GetHorizontalAxis") == 0, "dead zone");
        frame.X = frame.Y = 1;
        float x = frame.Axis("GetHorizontalAxis"), y = frame.Axis("GetVerticalAxis");
        Check(Math.Abs(x * x + y * y - 1) < 0.001, "diagonal normalized");
        frame.X = float.NaN; frame.Y = float.PositiveInfinity;
        Check(frame.Axis("GetHorizontalAxis") == 0 && frame.Axis("GetVerticalAxis") == 0, "invalid axes");
        string[] names = { "Attack", "Interact", "Dodge", "Throw", "AltAttack", "Focus" };
        PadButtons[] buttons = { PadButtons.Attack, PadButtons.Interact, PadButtons.Dodge,
            PadButtons.Throw, PadButtons.Block, PadButtons.Focus };
        for (int i = 0; i < names.Length; i++)
        {
            frame = new PadFrame { Connected = true, Pressed = buttons[i] };
            Check(frame.Button(names[i] + "Pressed") && !frame.Button(names[i] + "Held"), "press " + names[i]);
            frame.Pressed = PadButtons.None; frame.Held = buttons[i];
            Check(!frame.Button(names[i] + "Pressed") && frame.Button(names[i] + "Held"), "hold " + names[i]);
            frame.Held = PadButtons.None; frame.Released = buttons[i];
            Check(frame.Button(names[i] + "Released") && !frame.Button(names[i] + "Held"), "release " + names[i]);
        }
        frame = new PadFrame { Connected = true, Pressed = PadButtons.NextItem };
        Check(frame.Inventory() == 1 && frame.Button("AnyActionPressed"), "next item");
        frame.Pressed = PadButtons.PreviousItem;
        Check(frame.Inventory() == -1, "previous item");
        frame.LookX = -0.7f; frame.LookY = 0.25f;
        Check(frame.Button("FocusSwapLeft") && !frame.Button("FocusSwapRight"), "look left");
        Check(PadFrame.Clean(frame.LookY) == 0.25f, "vertical look");
        frame.Connected = false; frame.X = 1;
        Check(frame.Axis("GetHorizontalAxis") == 0 && !frame.Button("AttackPressed")
            && !frame.Button("IsEnabled") && frame.Inventory() == 0, "disconnect neutral");
        Console.WriteLine("PADFRAME PASS checks=" + count);
    }
}
