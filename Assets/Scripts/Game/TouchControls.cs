// Сенсорное управление в стиле Brawl Stars.
//
// * Левая половина экрана — джойстик движения: появляется там, куда поставили палец.
// * Правая половина — джойстик атаки: тянете — целитесь (на земле видна линия выстрела),
//   отпускаете — выстрел. Короткое касание без перетаскивания — выстрел с автоприцелом
//   в ближайшего врага. Если вернуть палец в центр и отпустить — выстрел отменяется.
// * Жёлтый джойстик левее атаки — супер удар. Работает так же, как атака, но только когда
//   супер заряжен; пока не заряжен, показывает процент заряда.
// * Кнопка над атакой — уклонение (рывок). Во время перезарядки показывает оставшиеся секунды.
//
// Координаты тут экранные (x — вправо, y — вверх); в мир их переводит MatchView.
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.EnhancedTouch;
using ETouch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using ETouchPhase = UnityEngine.InputSystem.TouchPhase;
#endif

namespace UGame.Game
{
    public sealed class TouchControls
    {
        const float MoveDeadZone = 0.15f;
        const float AimCancelZone = 0.25f;   // отпустили ближе к центру — выстрела нет
        const float TapMaxTravel = 0.3f;     // в радиусах джойстика: меньше — это «тап»

        /// <summary>
        /// Телефон или планшет. UnityEngine.Device.Application учитывает Device Simulator, поэтому
        /// джойстики можно проверить прямо в редакторе. Ноутбук с сенсорным экраном остаётся на мыши.
        /// </summary>
        public static bool Available => UnityEngine.Device.Application.isMobilePlatform;

        public enum FireKind { None, Aimed, Auto }

        sealed class Stick
        {
            public int FingerId = -1;
            public Vector2 Origin, Current;
            public float MaxTravel;   // в радиусах — чтобы отличить тап от перетаскивания
            public bool Held => FingerId >= 0;
        }

        struct TouchPoint
        {
            public int Id;
            public Vector2 Pos;
            public bool Began, Ended;
        }

        readonly Stick move = new Stick();
        readonly Stick attack = new Stick();
        readonly Stick super = new Stick();
        readonly List<TouchPoint> touches = new List<TouchPoint>(10);
        FireKind pendingFire, pendingSuper;
        Vector2 pendingFireDir, pendingSuperDir;
        bool pendingDodge;

        // состояние способностей — выставляет MatchView каждый кадр, нужно для отрисовки
        public bool SuperReady;
        public float SuperCharge01;
        public bool DodgeReady;
        public float DodgeCooldownSec;

        public TouchControls()
        {
#if ENABLE_INPUT_SYSTEM
            if (!EnhancedTouchSupport.enabled) EnhancedTouchSupport.Enable();
#endif
        }

        static float Radius => Mathf.Max(60f, Screen.height * 0.11f);

        // раскладка правой стороны экрана (экранные пиксели, y вверх)
        static Vector2 AttackHome { get { Rect s = UnityEngine.Device.Screen.safeArea; float r = Radius; return new Vector2(s.xMax - r * 1.6f, s.yMin + r * 1.6f); } }
        static Vector2 SuperHome => AttackHome + new Vector2(-Radius * 2.5f, -Radius * 0.5f);
        static Vector2 DodgeHome => AttackHome + new Vector2(0f, Radius * 2.4f);
        static float SuperGrabRadius => Radius * 1.0f;
        static float DodgeRadius => Radius * 0.6f;

        // ------------------------------------------------------------------ результат для MatchView

        /// <summary>Направление движения, длина 0..1.</summary>
        public Vector2 Move
        {
            get
            {
                if (!move.Held) return Vector2.zero;
                Vector2 v = Value(move);
                return v.magnitude < MoveDeadZone ? Vector2.zero : v;
            }
        }

        /// <summary>Игрок сейчас целится джойстиком атаки (показываем линию выстрела).</summary>
        public bool Aiming => attack.Held && attack.MaxTravel >= TapMaxTravel && Value(attack).magnitude >= AimCancelZone;

        public Vector2 AimDirection => Value(attack).normalized;

        /// <summary>Игрок целится супером (показываем веер).</summary>
        public bool SuperAiming => super.Held && super.MaxTravel >= TapMaxTravel && Value(super).magnitude >= AimCancelZone;

        public Vector2 SuperDirection => Value(super).normalized;

        /// <summary>Забрать выстрел, если он был. Вызывается при отправке очередной команды.</summary>
        public FireKind ConsumeFire(out Vector2 direction)
        {
            var kind = pendingFire;
            direction = pendingFireDir;
            pendingFire = FireKind.None;
            return kind;
        }

        /// <summary>Забрать супер удар, если игрок его запросил.</summary>
        public FireKind ConsumeSuper(out Vector2 direction)
        {
            var kind = pendingSuper;
            direction = pendingSuperDir;
            pendingSuper = FireKind.None;
            return kind;
        }

        /// <summary>Забрать нажатие кнопки уклонения.</summary>
        public bool ConsumeDodge()
        {
            bool d = pendingDodge;
            pendingDodge = false;
            return d;
        }

        /// <summary>
        /// Выбросить несработавшие нажатия и отпустить джойстики атаки и супера без выстрела (боец умер).
        /// Палец, который так и остался на экране, выстрела уже не даст — нужно коснуться заново.
        /// Джойстик движения не трогаем: после возрождения можно сразу бежать.
        /// </summary>
        public void CancelActions()
        {
            pendingFire = FireKind.None;
            pendingSuper = FireKind.None;
            pendingDodge = false;
            attack.FingerId = -1;
            super.FingerId = -1;
        }

        static Vector2 Value(Stick s) => Vector2.ClampMagnitude((s.Current - s.Origin) / Radius, 1f);

        // ------------------------------------------------------------------ обработка касаний

        public void Update()
        {
            Gather();
            foreach (var t in touches)
            {
                if (t.Began)
                {
                    if (t.Pos.x < Screen.width * 0.5f) { if (!move.Held) Grab(move, t); }
                    else if ((t.Pos - DodgeHome).magnitude <= DodgeRadius) pendingDodge = true;
                    else if (SuperReady && !super.Held && (t.Pos - SuperHome).magnitude <= SuperGrabRadius) Grab(super, t);
                    else if (!attack.Held) Grab(attack, t);
                }

                if (t.Id == move.FingerId) Drag(move, t);
                else if (t.Id == attack.FingerId) Drag(attack, t);
                else if (t.Id == super.FingerId) Drag(super, t);

                if (t.Ended)
                {
                    if (t.Id == attack.FingerId) Release(attack, ref pendingFire, ref pendingFireDir);
                    if (t.Id == super.FingerId) Release(super, ref pendingSuper, ref pendingSuperDir);
                    if (t.Id == move.FingerId) move.FingerId = -1;
                }
            }

            // палец «потерялся» (сворачивание приложения и т. п.) — отпускаем джойстик без выстрела
            if (move.Held && !Contains(move.FingerId)) move.FingerId = -1;
            if (attack.Held && !Contains(attack.FingerId)) attack.FingerId = -1;
            if (super.Held && !Contains(super.FingerId)) super.FingerId = -1;
        }

        static void Grab(Stick s, TouchPoint t)
        {
            s.FingerId = t.Id;
            s.Origin = s.Current = t.Pos;
            s.MaxTravel = 0f;
        }

        static void Drag(Stick s, TouchPoint t)
        {
            s.Current = t.Pos;
            s.MaxTravel = Mathf.Max(s.MaxTravel, (s.Current - s.Origin).magnitude / Radius);
        }

        static void Release(Stick s, ref FireKind pending, ref Vector2 dir)
        {
            Vector2 v = Value(s);
            if (s.MaxTravel < TapMaxTravel) pending = FireKind.Auto;
            else if (v.magnitude >= AimCancelZone)
            {
                pending = FireKind.Aimed;
                dir = v.normalized;
            }
            s.FingerId = -1;
        }

        bool Contains(int id)
        {
            foreach (var t in touches)
                if (t.Id == id) return true;
            return false;
        }

        void Gather()
        {
            touches.Clear();
#if ENABLE_INPUT_SYSTEM
            foreach (var t in ETouch.activeTouches)
            {
                touches.Add(new TouchPoint
                {
                    Id = t.touchId,
                    Pos = t.screenPosition,
                    Began = t.phase == ETouchPhase.Began,
                    Ended = t.phase == ETouchPhase.Ended || t.phase == ETouchPhase.Canceled,
                });
            }
#else
            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                touches.Add(new TouchPoint
                {
                    Id = t.fingerId,
                    Pos = t.position,
                    Began = t.phase == TouchPhase.Began,
                    Ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled,
                });
            }
#endif
        }

        // ------------------------------------------------------------------ отрисовка (IMGUI)

        static Texture2D disc, ring;
        static GUIStyle badge;

        /// <summary>Рисует джойстики. Вызывается из OnGUI, guiScale — масштаб GUI.matrix.</summary>
        public void Draw(float guiScale)
        {
            if (disc == null)
            {
                disc = MakeCircle(128, filled: true);
                ring = MakeCircle(128, filled: false);
            }
            Rect safe = UnityEngine.Device.Screen.safeArea; // не рисуем под вырезом камеры
            float r = Radius;

            Vector2 moveHome = new Vector2(safe.xMin + r * 1.6f, safe.yMin + r * 1.6f);

            DrawStick(move, moveHome, new Color(1f, 1f, 1f, 0.35f), new Color(1f, 1f, 1f, 0.8f), guiScale);
            DrawStick(attack, AttackHome, new Color(1f, 0.35f, 0.3f, 0.35f), new Color(1f, 0.35f, 0.3f, 0.85f), guiScale);

            if (badge == null)
                badge = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 15 };

            // супер: заряжен — яркий жёлтый джойстик, нет — серый круг с процентом заряда
            if (SuperReady)
            {
                DrawStick(super, SuperHome, new Color(1f, 0.85f, 0.1f, 0.6f), new Color(1f, 0.85f, 0.1f, 1f), guiScale, r * 0.8f);
                if (!super.Held) Badge(SuperHome, r * 0.8f, "СУПЕР", Color.black, guiScale);
            }
            else
            {
                var prev = GUI.color;
                GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.45f);
                GUI.DrawTexture(ToGui(SuperHome, r * 0.8f, guiScale), ring);
                GUI.color = new Color(1f, 0.85f, 0.1f, 0.55f);
                GUI.DrawTexture(ToGui(SuperHome, r * 0.8f * Mathf.Sqrt(SuperCharge01), guiScale), disc); // площадь ~ заряду
                GUI.color = prev;
                Badge(SuperHome, r * 0.8f, Mathf.FloorToInt(SuperCharge01 * 100f) + "%", Color.white, guiScale);
            }

            // уклонение: готово — голубая кнопка, иначе — серая с секундами до готовности
            {
                var prev = GUI.color;
                GUI.color = DodgeReady ? new Color(0.35f, 0.85f, 1f, 0.85f) : new Color(0.4f, 0.4f, 0.4f, 0.6f);
                GUI.DrawTexture(ToGui(DodgeHome, DodgeRadius, guiScale), disc);
                GUI.color = prev;
                Badge(DodgeHome, DodgeRadius, DodgeReady ? "РЫВОК" : DodgeCooldownSec.ToString("0.0"),
                    DodgeReady ? Color.black : Color.white, guiScale);
            }
        }

        static void Badge(Vector2 center, float radius, string text, Color color, float guiScale)
        {
            var prev = GUI.contentColor;
            GUI.contentColor = color;
            badge.fontSize = Mathf.RoundToInt(Mathf.Clamp(radius / guiScale * 0.38f, 11f, 22f));
            GUI.Label(ToGui(center, radius, guiScale), text, badge);
            GUI.contentColor = prev;
        }

        static void DrawStick(Stick s, Vector2 home, Color baseColor, Color knobColor, float guiScale, float drawRadius = 0f)
        {
            float r = drawRadius > 0f ? drawRadius : Radius;
            Vector2 origin = s.Held ? s.Origin : home;
            Vector2 knob = s.Held ? origin + Value(s) * r : origin;
            float alpha = s.Held ? 1f : 0.6f;

            var prev = GUI.color;
            GUI.color = new Color(baseColor.r, baseColor.g, baseColor.b, baseColor.a * alpha);
            GUI.DrawTexture(ToGui(origin, r, guiScale), ring);
            GUI.color = new Color(knobColor.r, knobColor.g, knobColor.b, knobColor.a * alpha);
            GUI.DrawTexture(ToGui(knob, r * 0.45f, guiScale), disc);
            GUI.color = prev;
        }

        /// <summary>Экранные пиксели (y вверх) → прямоугольник IMGUI (y вниз, с учётом масштаба).</summary>
        static Rect ToGui(Vector2 center, float radius, float guiScale)
        {
            float x = (center.x - radius) / guiScale;
            float y = (Screen.height - center.y - radius) / guiScale;
            float size = radius * 2f / guiScale;
            return new Rect(x, y, size, size);
        }

        static Texture2D MakeCircle(int size, bool filled)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float c = (size - 1) / 2f, outer = size / 2f - 1f, inner = outer - size * 0.06f;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(c, c));
                float a = Mathf.Clamp01(outer - d + 0.5f);                    // сглаженный край
                if (!filled) a = Mathf.Min(a, Mathf.Clamp01(d - inner + 0.5f)) + 0.25f * Mathf.Clamp01(inner - d);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255));
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }
    }
}
