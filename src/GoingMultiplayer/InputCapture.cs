using UnityEngine;

namespace GoingMultiplayer
{
    // Samples the local player's real input into the wire format. The host
    // replays this command against the guest's actor, so it must contain
    // everything the native input path polls for one tick.
    internal static class InputCapture
    {
        internal static InputCommand From(Player player)
        {
            var command = new InputCommand();
            if (player == null) return command;
            PlayerInputRewired input = player.input;
            if (input == null) return command;

            // The game steers relative to CameraController.relativeControlSpot,
            // which is the local camera's yaw. Send that basis so the host can
            // replay movement in the sender's frame instead of its own.
            command.Yaw = player.transform.eulerAngles.y;
            if (CameraController.instance != null
                && CameraController.instance.relativeControlSpot != null)
                command.Yaw = CameraController.instance.relativeControlSpot.eulerAngles.y;
            if (!input.IsEnabled()) return command;

            command.MoveX = PadFrame.Clean(input.GetHorizontalAxis());
            command.MoveY = PadFrame.Clean(input.GetVerticalAxis());
            Vector2 camera = input.GetCameraAxisController();
            command.CameraX = PadFrame.Clean(camera.x);
            command.CameraY = PadFrame.Clean(camera.y);
            command.Inventory = input.GetInventoryAxis();

            if (input.AttackHeld()) command.Held |= PlayerButtons.Attack;
            if (input.AttackPressed()) command.Pressed |= PlayerButtons.Attack;
            if (input.AltAttackHeld()) command.Held |= PlayerButtons.AltAttack;
            if (input.AltAttackPressed()) command.Pressed |= PlayerButtons.AltAttack;
            if (input.AltAttackReleased()) command.Released |= PlayerButtons.AltAttack;
            if (input.DodgeHeld()) command.Held |= PlayerButtons.Dodge;
            if (input.DodgePressed()) command.Pressed |= PlayerButtons.Dodge;
            if (input.InteractHeld()) command.Held |= PlayerButtons.Interact;
            if (input.InteractPressed()) command.Pressed |= PlayerButtons.Interact;
            if (input.ThrowHeld()) command.Held |= PlayerButtons.Throw;
            if (input.ThrowPressed()) command.Pressed |= PlayerButtons.Throw;
            if (input.FocusHeld()) command.Held |= PlayerButtons.Focus;
            if (input.FocusPressed()) command.Pressed |= PlayerButtons.Focus;
            if (input.FocusSwapLeft()) command.Pressed |= PlayerButtons.FocusSwapLeft;
            if (input.FocusSwapRight()) command.Pressed |= PlayerButtons.FocusSwapRight;
            return command;
        }
    }
}
