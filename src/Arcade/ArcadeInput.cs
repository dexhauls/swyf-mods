using UnityEngine.InputSystem;

namespace Arcade
{
    /// <summary>
    /// Keyboard state for one frame. "Pressed" flags are true only for the first fixed step of the frame,
    /// so a single key press never counts twice.
    /// </summary>
    internal sealed class ArcadeInput
    {
        public bool Left, Right, Up, Down;                 // held (arrows or WASD)
        public bool LeftPressed, RightPressed, UpPressed, DownPressed;
        public bool Action;                                // Space pressed
        public bool Confirm;                               // Space or Enter pressed
        public bool Pause;                                 // P pressed
        public bool RotateBack;                            // Z / Q pressed (Tetris)
        public bool RotateKey;                             // X pressed (Tetris)

        public void Read(bool enabled)
        {
            var k = enabled ? Keyboard.current : null;
            if (k == null) { Clear(); return; }
            Left = k.leftArrowKey.isPressed || k.aKey.isPressed;
            Right = k.rightArrowKey.isPressed || k.dKey.isPressed;
            Up = k.upArrowKey.isPressed || k.wKey.isPressed;
            Down = k.downArrowKey.isPressed || k.sKey.isPressed;
            LeftPressed = k.leftArrowKey.wasPressedThisFrame || k.aKey.wasPressedThisFrame;
            RightPressed = k.rightArrowKey.wasPressedThisFrame || k.dKey.wasPressedThisFrame;
            UpPressed = k.upArrowKey.wasPressedThisFrame || k.wKey.wasPressedThisFrame;
            DownPressed = k.downArrowKey.wasPressedThisFrame || k.sKey.wasPressedThisFrame;
            Action = k.spaceKey.wasPressedThisFrame;
            Confirm = Action || k.enterKey.wasPressedThisFrame || k.numpadEnterKey.wasPressedThisFrame;
            Pause = k.pKey.wasPressedThisFrame;
            RotateBack = k.zKey.wasPressedThisFrame || k.qKey.wasPressedThisFrame;
            RotateKey = k.xKey.wasPressedThisFrame;
        }

        public bool AnyDirectionPressed => LeftPressed || RightPressed || UpPressed || DownPressed;

        public void ConsumePresses()
        {
            LeftPressed = RightPressed = UpPressed = DownPressed = false;
            Action = Confirm = Pause = RotateBack = RotateKey = false;
        }

        public void Clear()
        {
            Left = Right = Up = Down = false;
            ConsumePresses();
        }
    }
}
