using System.Collections.Generic;
using Rewired;

namespace GoingMultiplayer
{
    internal static class ControllerSeats
    {
        private sealed class JoystickPad : ISeatPad
        {
            internal Joystick Joystick;
            internal IGamepadTemplate Template;
            public string Description
            {
                get { return Joystick.isConnected ? Joystick.name + " (#" + Joystick.id + ")" : "Controller disconnected — F10 to reassign"; }
            }

            public PadFrame Read()
            {
                if (!Joystick.isConnected) return new PadFrame();
                var frame = new PadFrame { Connected = true, X = Template.leftStick.horizontal.value,
                    Y = Template.leftStick.vertical.value, LookX = Template.rightStick.horizontal.value,
                    LookY = Template.rightStick.vertical.value };
                Add(ref frame, Template.x, PadButtons.Attack);
                Add(ref frame, Template.y, PadButtons.Interact);
                Add(ref frame, Template.a, PadButtons.Dodge);
                Add(ref frame, Template.b, PadButtons.Throw);
                Add(ref frame, Template.leftBumper, PadButtons.PreviousItem);
                Add(ref frame, Template.rightBumper, PadButtons.NextItem);
                Trigger(ref frame, Template.rightTrigger, PadButtons.Block);
                Trigger(ref frame, Template.leftTrigger, PadButtons.Focus);
                return frame;
            }

            private static void Add(ref PadFrame frame, IControllerTemplateButton source, PadButtons button)
            {
                if (source == null) return;
                if (source.value) frame.Held |= button;
                if (source.justPressed) frame.Pressed |= button;
                if (source.justReleased) frame.Released |= button;
            }

            private static void Trigger(ref PadFrame frame, IControllerTemplateAxis source, PadButtons button)
            {
                if (source == null) return;
                bool now = source.value > 0.4f, previous = source.valuePrev > 0.4f;
                if (now) frame.Held |= button;
                if (now && !previous) frame.Pressed |= button;
                if (!now && previous) frame.Released |= button;
            }
        }

        private static LocalSeats.Seat seat;
        private static JoystickPad current;
        private static readonly List<Rewired.Player> previousOwners = new List<Rewired.Player>();
        internal static string Status { get { return current != null ? current.Description : "Keyboard IJKL (F10 selects a gamepad)"; } }

        private static List<JoystickPad> Available()
        {
            var pads = new List<JoystickPad>();
            foreach (Joystick joystick in ReInput.controllers.Joysticks)
            {
                IGamepadTemplate template = joystick.GetTemplate<IGamepadTemplate>();
                if (joystick.isConnected && template != null
                    && template.leftStick != null && template.rightStick != null)
                    pads.Add(new JoystickPad { Joystick = joystick, Template = template });
            }
            return pads;
        }

        internal static void AssignPreferred(LocalSeats.Seat target)
        {
            List<JoystickPad> pads = Available();
            // Two pads: leave the first for P1. One pad: P1 can use keyboard/mouse.
            Bind(target, pads.Count == 0 ? null : pads[pads.Count > 1 ? 1 : 0]);
        }

        internal static void Cycle()
        {
            if (seat == null) return;
            LocalSeats.Seat target = seat;
            List<JoystickPad> pads = Available();
            int index = -1;
            if (current != null)
                for (int i = 0; i < pads.Count; i++)
                    if (ReferenceEquals(current.Joystick, pads[i].Joystick)) index = i;
            Bind(target, index + 1 < pads.Count ? pads[index + 1] : null);
        }

        private static void Bind(LocalSeats.Seat target, JoystickPad pad)
        {
            Release();
            seat = target;
            current = pad;
            seat.Pad = pad;
            if (pad == null) return;
            try
            {
                foreach (Rewired.Player player in ReInput.players.AllPlayers)
                    if (player.controllers.ContainsController(pad.Joystick))
                        previousOwners.Add(player);
                ReInput.InputSourceUpdateEvent += EnforceExclusive;
                EnforceExclusive();
            }
            catch { Release(); throw; }
        }

        private static void EnforceExclusive()
        {
            if (current == null || !current.Joystick.isConnected) return;
            foreach (Rewired.Player player in ReInput.players.AllPlayers)
                if (player.controllers.ContainsController(current.Joystick))
                    player.controllers.RemoveController(current.Joystick);
        }

        internal static bool IsExclusive()
        {
            if (current == null) return false;
            foreach (Rewired.Player player in ReInput.players.AllPlayers)
                if (player.controllers.ContainsController(current.Joystick)) return false;
            return true;
        }

        internal static bool ProbeOwnership(LocalSeats.Seat target, out int hardwareCount)
        {
            List<JoystickPad> pads = Available();
            hardwareCount = pads.Count;
            if (hardwareCount == 0) return true; // Explicitly reported as unavailable, not a hardware PASS.
            JoystickPad pad = pads[pads.Count > 1 ? 1 : 0];
            var owners = new Dictionary<Rewired.Player, bool>();
            foreach (Rewired.Player player in ReInput.players.AllPlayers)
                owners.Add(player, player.controllers.ContainsController(pad.Joystick));
            bool exclusive;
            try { Bind(target, pad); exclusive = IsExclusive() && pad.Read().Connected; }
            finally { Release(); }
            foreach (var owner in owners)
                exclusive &= owner.Key.controllers.ContainsController(pad.Joystick) == owner.Value;
            return exclusive;
        }

        internal static void Release()
        {
            ReInput.InputSourceUpdateEvent -= EnforceExclusive;
            if (seat != null) seat.Pad = null;
            if (current != null && current.Joystick.isConnected)
                foreach (Rewired.Player player in previousOwners)
                    player.controllers.AddController(current.Joystick, false);
            previousOwners.Clear();
            seat = null;
            current = null;
        }
    }
}
