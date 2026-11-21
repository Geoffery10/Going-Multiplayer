namespace GoingMultiplayer
{
    // Latest host-side command for one remote player. NetSession owns sequence
    // validation; this state exposes the accepted input to the native
    // PlayerInputRewired path.
    //
    // Edges matter: the game polls AttackPressed/DodgePressed once per frame and
    // acts on them. Commands arrive at ~30 Hz, so a plain latch would repeat the
    // action for several frames. Instead every edge is readable for exactly one
    // frame (every poll in that frame sees it) and then clears.
    internal sealed class NetworkInputState
    {
        private const int ButtonSlots = 16;

        // Current game frame, advanced by Plugin.Update. Kept out of UnityEngine
        // so the core stays compilable in the plain .NET test harness.
        internal static int Frame;
        internal static int InventoryReads;
        internal static int InventoryHits;

        internal InputCommand Command;
        internal uint LastSequence;

        private PlayerButtons pendingPressed;
        private PlayerButtons pendingReleased;
        private readonly int[] pressedFrame = new int[ButtonSlots];
        private readonly bool[] pressedSeen = new bool[ButtonSlots];
        private readonly int[] releasedFrame = new int[ButtonSlots];
        private readonly bool[] releasedSeen = new bool[ButtonSlots];
        private int inventoryFrame = -1;
        private bool inventorySeen;

        internal void Apply(InputCommand command, uint sequence)
        {
            if (sequence <= LastSequence) return;
            Command = command;
            Command.MoveX = PadFrame.Clean(command.MoveX);
            Command.MoveY = PadFrame.Clean(command.MoveY);
            Command.CameraX = PadFrame.Clean(command.CameraX);
            Command.CameraY = PadFrame.Clean(command.CameraY);
            Command.Inventory = command.Inventory > 0 ? 1 : command.Inventory < 0 ? -1 : 0;
            LastSequence = sequence;

            int frame = Frame;
            pendingPressed |= command.Pressed;
            pendingReleased |= command.Released;
            for (int i = 0; i < ButtonSlots; i++)
            {
                PlayerButtons bit = (PlayerButtons)(1 << i);
                if ((command.Pressed & bit) != 0)
                {
                    pressedFrame[i] = frame;
                    pressedSeen[i] = false;
                }
                if ((command.Released & bit) != 0)
                {
                    releasedFrame[i] = frame;
                    releasedSeen[i] = false;
                }
            }
            if (command.Inventory != 0)
            {
                inventoryFrame = frame;
                inventorySeen = false;
            }
        }

        internal bool Held(PlayerButtons button)
        {
            return (Command.Held & button) != 0;
        }

        internal bool Pressed(PlayerButtons button)
        {
            return Edge(button, ref pendingPressed, pressedFrame, pressedSeen);
        }

        internal bool Released(PlayerButtons button)
        {
            return Edge(button, ref pendingReleased, releasedFrame, releasedSeen);
        }

        internal bool AnyActionPressed
        {
            get { return pendingPressed != PlayerButtons.None; }
        }

        // Inventory steps are edges too: holding the axis would scroll every
        // frame the command stays current.
        internal int Inventory()
        {
            InventoryReads++;
            int value = Command.Inventory;
            if (value == 0) return 0;
            int slot = ButtonSlots - 1;
            if (inventoryFrame == Frame)
            {
                inventorySeen = true;
                InventoryHits++;
                return value;
            }
            if (!inventorySeen)
            {
                inventoryFrame = Frame;
                inventorySeen = true;
                InventoryHits++;
                return value;
            }
            inventoryFrame = -1;
            inventorySeen = false;
            Command.Inventory = 0;
            return 0;
        }

        private static bool Edge(PlayerButtons button, ref PlayerButtons pending,
            int[] frame, bool[] seen)
        {
            if ((pending & button) == 0) return false;
            int index = Index(button);
            int now = Frame;
            if (frame[index] == now)
            {
                seen[index] = true;
                return true;
            }
            if (!seen[index])
            {
                frame[index] = now;
                seen[index] = true;
                return true;
            }
            pending &= ~button;
            return false;
        }

        private static int Index(PlayerButtons button)
        {
            int value = (int)button;
            int index = 0;
            while (value > 1)
            {
                value >>= 1;
                index++;
            }
            return index < ButtonSlots ? index : ButtonSlots - 1;
        }
    }
}
