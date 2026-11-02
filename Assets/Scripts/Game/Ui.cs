// Общие стили и разметка IMGUI для меню, матча и оверлея FPS/ping.
// Интерфейс масштабируется под высоту 720 px — одинаково на мониторе и телефоне.
using UnityEngine;

namespace UGame.Game
{
    public static class Ui
    {
        public static float Scale { get; private set; } = 1f;
        public static float W => Screen.width / Scale;
        public static float H => Screen.height / Scale;

        public static GUIStyle Title, Big, Label, Small, Error, NameTag, Panel, Center;

        /// <summary>Забыть стили (новый запуск Play без перезагрузки домена).</summary>
        public static void Reset() => Title = null;

        /// <summary>Вызывать в начале каждого OnGUI: масштаб и стили.</summary>
        public static void Begin()
        {
            Scale = Mathf.Max(0.5f, Screen.height / 720f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(Scale, Scale, 1f));
            if (Title != null) return;

            Title = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            Big = new GUIStyle(Title) { fontSize = 44 };
            Label = new GUIStyle(GUI.skin.label) { fontSize = 17, wordWrap = true };
            Small = new GUIStyle(Label) { fontSize = 14 };
            Center = new GUIStyle(Label) { alignment = TextAnchor.MiddleCenter };
            Error = new GUIStyle(Label) { normal = { textColor = new Color(1f, 0.45f, 0.4f) } };
            NameTag = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            GUI.skin.button.fontSize = 18;
            GUI.skin.textField.fontSize = 18;
            GUI.skin.toggle.fontSize = 16;
            Panel = new GUIStyle(GUI.skin.box) { padding = new RectOffset(24, 24, 20, 20) };
        }

        /// <summary>Панель по центру экрана; закрывать GUILayout.EndArea().</summary>
        public static void BeginPanel(float width, float height)
        {
            width = Mathf.Min(width, W - 20f);
            height = Mathf.Min(height, H - 20f);
            GUI.color = new Color(1f, 1f, 1f, 0.97f);
            GUILayout.BeginArea(new Rect((W - width) / 2, (H - height) / 2, width, height), Panel);
            GUI.color = Color.white;
        }

        public static void Message(string text)
        {
            BeginPanel(400, 120);
            GUILayout.FlexibleSpace();
            GUILayout.Label(text, Title);
            GUILayout.FlexibleSpace();
            GUILayout.EndArea();
        }

        /// <summary>Нажат Enter (для форм на компьютере).</summary>
        public static bool EnterPressed =>
            Event.current.type == EventType.KeyDown
            && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
    }
}
