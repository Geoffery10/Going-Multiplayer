using System;
using System.Collections;
using UnityEngine;

namespace GoingMultiplayer
{
    internal static class ControllerProbe
    {
        private sealed class TestPad : ISeatPad
        {
            internal PadFrame Frame;
            public PadFrame Read() { return Frame; }
            public string Description { get { return "In-game QA pad snapshot"; } }
        }

        internal static IEnumerator Run(Plugin plugin, string runId, LocalSeats.Seat host,
            LocalSeats.Seat other, bool basePassed)
        {
            int hardwareCount;
            bool ownership = ControllerSeats.ProbeOwnership(other, out hardwareCount);
            var pad = new TestPad { Frame = new PadFrame { Connected = true, X = -1 } };
            other.Pad = pad;
            bool negative = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-negative-controller-route") >= 0;
            if (negative) host.Pad = pad;
            bool isolation = host.Player.input.GetHorizontalAxis() == 0f
                && other.Player.input.GetHorizontalAxis() == -1f;
            host.Pad = null; // Never let the negative control move player one.
            bool mapping = true;
            pad.Frame.Pressed = PadButtons.Attack | PadButtons.Interact | PadButtons.Dodge
                | PadButtons.Throw | PadButtons.Block | PadButtons.Focus | PadButtons.NextItem;
            mapping &= other.Player.input.AttackPressed() && other.Player.input.InteractPressed()
                && other.Player.input.DodgePressed() && other.Player.input.ThrowPressed()
                && other.Player.input.AltAttackPressed() && other.Player.input.FocusPressed()
                && other.Player.input.GetInventoryAxis() == 1 && !host.Player.input.AttackPressed();
            pad.Frame.Pressed = PadButtons.None;
            pad.Frame.Held = PadButtons.Block | PadButtons.Focus;
            mapping &= other.Player.input.AltAttackHeld() && other.Player.input.FocusHeld()
                && !other.Player.input.AltAttackPressed();
            pad.Frame.Held = PadButtons.None;
            pad.Frame.Released = PadButtons.Block;
            mapping &= other.Player.input.AltAttackReleased();
            pad.Frame.Released = PadButtons.None;
            Vector3 before = other.Player.transform.position;
            float until = Time.realtimeSinceStartup + 0.5f;
            while (Time.realtimeSinceStartup < until) yield return null;
            Vector3 delta = other.Player.transform.position - before;
            delta.y = 0;
            bool movement = delta.magnitude > 0.1f;
            pad.Frame.Connected = false;
            pad.Frame.Pressed = PadButtons.Attack | PadButtons.NextItem;
            bool disconnect = other.Player.input.GetHorizontalAxis() == 0f
                && !other.Player.input.AttackPressed() && other.Player.input.GetInventoryAxis() == 0;
            other.Pad = null;
            bool passed = basePassed && mapping && isolation && movement && disconnect && ownership;
            plugin.Report("GMP_CONTROLLER_RESULT " + (passed ? "PASS" : "FAIL") + " run=" + runId
                + " base=" + basePassed + " mapping=" + mapping + " isolation=" + isolation
                + " movement=" + movement + " disconnect=" + disconnect + " ownership=" + ownership
                + " hardware=" + hardwareCount);
            Application.Quit(passed ? 0 : 1);
        }
    }
}
