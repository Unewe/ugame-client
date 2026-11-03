// Интерфейс поверх сцены (OnGUI): имена и полоски здоровья, цифры урона, полоска способностей
// для мыши и клавиатуры (на телефоне её рисуют джойстики TouchControls).
using UnityEngine;

namespace UGame.Game
{
    partial class MatchView
    {
        static readonly Color OwnNameColor = new Color(1f, 0.95f, 0.4f);
        static readonly Color AllyHpColor = new Color(0.3f, 0.9f, 0.35f);
        static readonly Color EnemyHpColor = new Color(0.95f, 0.25f, 0.2f);
        static readonly Color GhostBarColor = new Color(1f, 1f, 1f, 0.85f);
        static readonly Color DamageToEnemyColor = Color.white;
        static readonly Color DamageToUsColor = new Color(1f, 0.35f, 0.3f);

        // стили создаём один раз: OnGUI вызывается несколько раз за кадр
        GUIStyle abilityStyle;
        GUIStyle damageStyle;

        /// <summary>Вызывается из OnGUI (координаты уже в масштабе GUI).</summary>
        public void DrawOverlay(float guiScale, GUIStyle nameStyle)
        {
            if (cam == null) return;
            if (Running && MyAlive)
            {
                if (touch != null) touch.Draw(guiScale);
                else DrawAbilityBar(guiScale);
            }
            foreach (var v in views.Values)
                if (v.Root.activeSelf) DrawNameAndHealth(v, guiScale, nameStyle);
            DrawDamageNumbers(guiScale);
        }

        void DrawNameAndHealth(View v, float guiScale, GUIStyle nameStyle)
        {
            Vector3 sp = cam.WorldToScreenPoint(v.WorldPos + Vector3.up * 2.2f);
            if (sp.z < 0) return;
            float x = sp.x / guiScale, y = (Screen.height - sp.y) / guiScale;

            bool own = v.Id == myEntityId;
            players.TryGetValue(v.Id, out var info);
            string name = own ? "Вы" : info?.Username ?? "";

            var prev = GUI.color;
            GUI.color = own ? OwnNameColor : Color.white;
            GUI.Label(new Rect(x - 80, y - 34, 160, 22), name, nameStyle);

            const float bw = 70f, bh = 9f;
            GUI.color = new Color(0f, 0f, 0f, 0.7f);
            GUI.DrawTexture(new Rect(x - bw / 2 - 1, y - 11, bw + 2, bh + 2), Texture2D.whiteTexture);
            if (v.GhostHp01 > v.Hp01) // только что отнятое — белым, стекает за текущей полоской
            {
                GUI.color = GhostBarColor;
                GUI.DrawTexture(new Rect(x - bw / 2, y - 10, bw * v.GhostHp01, bh), Texture2D.whiteTexture);
            }
            GUI.color = v.Team == myTeam ? AllyHpColor : EnemyHpColor;
            GUI.DrawTexture(new Rect(x - bw / 2, y - 10, bw * v.Hp01, bh), Texture2D.whiteTexture);
            GUI.color = prev;
        }

        /// <summary>Цифры урона: всплывают над бойцом, в начале чуть крупнее, к концу гаснут.</summary>
        void DrawDamageNumbers(float guiScale)
        {
            damageStyle ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 18,
                normal = { textColor = Color.white },
            };
            float now = Time.time;
            var prev = GUI.color;
            for (int i = damageNumbers.Count - 1; i >= 0; i--)
            {
                var n = damageNumbers[i];
                float age = now - n.Born;
                if (age > DamageNumberSec)
                {
                    damageNumbers.RemoveAt(i);
                    continue;
                }
                Vector3 anchor = views.TryGetValue(n.Id, out var nv) && nv.Root.activeSelf ? nv.WorldPos : n.WorldPos;
                Vector3 sp = cam.WorldToScreenPoint(anchor + Vector3.up * (2.9f + age * 1.5f));
                if (sp.z < 0) continue;
                float x = sp.x / guiScale, y = (Screen.height - sp.y) / guiScale;
                float alpha = 1f - Mathf.Clamp01((age - DamageNumberSec * 0.5f) / (DamageNumberSec * 0.5f));
                damageStyle.fontSize = Mathf.RoundToInt(age < 0.12f ? 24f - age * 50f : 18f); // «удар» при появлении
                string text = ShownDamage(n.Raw).ToString();
                var rect = new Rect(x - 50, y - 15, 100, 30);
                GUI.color = new Color(0f, 0f, 0f, 0.8f * alpha); // тень
                GUI.Label(new Rect(rect.x + 1.5f, rect.y + 1.5f, rect.width, rect.height), text, damageStyle);
                var c = n.ToUs ? DamageToUsColor : DamageToEnemyColor;
                GUI.color = new Color(c.r, c.g, c.b, alpha);
                GUI.Label(rect, text, damageStyle);
            }
            GUI.color = prev;
        }

        /// <summary>Заряд супера и кулдаун рывка внизу экрана (управление мышью и клавиатурой).</summary>
        void DrawAbilityBar(float guiScale)
        {
            float w = Screen.width / guiScale, h = Screen.height / guiScale;
            const float bw = 260f, bh = 18f;
            float x = (w - bw) / 2f, y = h - 70f;
            var prev = GUI.color;

            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(x - 2, y - 2, bw + 4, bh + 4), Texture2D.whiteTexture);
            GUI.color = SuperReady ? new Color(1f, 0.85f, 0.1f) : new Color(0.75f, 0.65f, 0.2f);
            GUI.DrawTexture(new Rect(x, y, bw * SuperCharge01, bh), Texture2D.whiteTexture);

            GUI.color = Color.white;
            abilityStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 13 };
            GUI.Label(new Rect(x, y - 1, bw, bh + 2),
                SuperReady ? "СУПЕР ГОТОВ — E / ПКМ" : $"Супер {Mathf.FloorToInt(SuperCharge01 * 100f)}%", abilityStyle);
            GUI.Label(new Rect(x, y + bh + 4, bw, 20),
                DodgeReady ? "Рывок готов — Shift" : $"Рывок через {DodgeCooldownLeftSec:0.0} с", abilityStyle);
            GUI.color = prev;
        }
    }
}
