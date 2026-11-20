using System;

namespace GoingMultiplayer
{
    [Flags]
    internal enum PadButtons
    {
        None = 0, Attack = 1, Interact = 2, Dodge = 4, Throw = 8,
        Block = 16, Focus = 32, PreviousItem = 64, NextItem = 128
    }

    internal interface ISeatPad
    {
        PadFrame Read();
        string Description { get; }
    }

    // Device-independent mapping, also used by the in-game controller probe.
    internal struct PadFrame
    {
        internal bool Connected;
        internal float X, Y, LookX, LookY;
        internal PadButtons Held, Pressed, Released;

        internal float Axis(string name)
        {
            if (!Connected) return 0;
            if (name == "GetCameraHorizontalAxisController") return Clean(LookX);
            float x = Clean(X), y = Clean(Y);
            float magnitude = (float)Math.Sqrt(x * x + y * y);
            if (magnitude <= 0.2f) return 0;
            float scale = Math.Min(1f, (magnitude - 0.2f) / 0.8f) / magnitude;
            return name == "GetHorizontalAxis" ? x * scale : name == "GetVerticalAxis" ? y * scale : 0;
        }

        internal bool Button(string name)
        {
            if (!Connected) return false;
            if (name == "IsEnabled") return true;
            if (name == "AnyActionPressed") return Pressed != PadButtons.None;
            if (name == "FocusSwapLeft") return Clean(LookX) < -0.5f;
            if (name == "FocusSwapRight") return Clean(LookX) > 0.5f;
            PadButtons button = name.StartsWith("AltAttack") ? PadButtons.Block
                : name.StartsWith("Attack") ? PadButtons.Attack
                : name.StartsWith("Interact") ? PadButtons.Interact
                : name.StartsWith("Dodge") ? PadButtons.Dodge
                : name.StartsWith("Throw") ? PadButtons.Throw
                : name.StartsWith("Focus") ? PadButtons.Focus : PadButtons.None;
            PadButtons state = name.EndsWith("Held") ? Held
                : name.EndsWith("Released") ? Released : Pressed;
            return button != PadButtons.None && (state & button) != 0;
        }

        internal int Inventory()
        {
            if (!Connected) return 0;
            if ((Pressed & PadButtons.NextItem) != 0) return 1;
            return (Pressed & PadButtons.PreviousItem) != 0 ? -1 : 0;
        }

        internal static float Clean(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 0 : Math.Max(-1f, Math.Min(1f, value));
        }
    }
}
