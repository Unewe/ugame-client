// Ввод игрока. В проекте включена новая Input System; ветка для старого Input Manager —
// на случай, если в Player Settings переключат Active Input Handling.
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace UGame.Game
{
    public static class InputReader
    {
        /// <summary>WASD / стрелки, вектор длиной не больше 1 (x — вправо, y — вперёд по экрану).</summary>
        public static Vector2 Move()
        {
            Vector2 v = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k != null)
            {
                if (k.aKey.isPressed || k.leftArrowKey.isPressed) v.x -= 1;
                if (k.dKey.isPressed || k.rightArrowKey.isPressed) v.x += 1;
                if (k.sKey.isPressed || k.downArrowKey.isPressed) v.y -= 1;
                if (k.wKey.isPressed || k.upArrowKey.isPressed) v.y += 1;
            }
#else
            v.x = Input.GetAxisRaw("Horizontal");
            v.y = Input.GetAxisRaw("Vertical");
#endif
            return Vector2.ClampMagnitude(v, 1f);
        }

        /// <summary>Атака: левая кнопка мыши или пробел (удержание — стрельба очередью).</summary>
        public static bool Attack()
        {
#if ENABLE_INPUT_SYSTEM
            return (Mouse.current != null && Mouse.current.leftButton.isPressed)
                   || (Keyboard.current != null && Keyboard.current.spaceKey.isPressed);
#else
            return Input.GetMouseButton(0) || Input.GetKey(KeyCode.Space);
#endif
        }

        public static Vector2 MousePosition()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
#else
            return Input.mousePosition;
#endif
        }
    }
}
