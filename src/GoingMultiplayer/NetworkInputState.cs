using System;

namespace GoingMultiplayer
{
    // Latest host-side command for one remote player. NetSession owns sequence
    // validation; this state only exposes the accepted input to the native
    // PlayerInputRewired path.
    internal sealed class NetworkInputState
    {
        internal InputCommand Command;
        internal uint LastSequence;

        internal void Apply(InputCommand command, uint sequence)
        {
            if (sequence <= LastSequence) return;
            Command = command;
            Command.MoveX = PadFrame.Clean(command.MoveX);
            Command.MoveY = PadFrame.Clean(command.MoveY);
            Command.CameraX = PadFrame.Clean(command.CameraX);
            Command.CameraY = PadFrame.Clean(command.CameraY);
            LastSequence = sequence;
        }

        internal bool Held(PlayerButtons button) { return (Command.Held & button) != 0; }
        internal bool Pressed(PlayerButtons button) { return (Command.Pressed & button) != 0; }
        internal bool Released(PlayerButtons button) { return (Command.Released & button) != 0; }
        internal bool AnyActionPressed { get { return Command.Pressed != PlayerButtons.None; } }
    }
}
