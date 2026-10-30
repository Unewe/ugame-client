// Отрисовка матча и ввод игрока.
//
// Сервер авторитарный: клиент шлёт только ввод (30 раз в секунду), сервер присылает снапшоты.
// * Свой боец — client-side prediction: двигаем его сразу, а по снапшоту берём позицию сервера
//   и заново применяем ещё не подтверждённые команды (reconciliation). Расхождение сглаживается.
// * Чужие бойцы и снаряды — интерполяция между снапшотами с задержкой ~100 мс.
//
// Константы движения должны совпадать с World.java на сервере.
using System.Collections.Generic;
using UGame.Net;
using UnityEngine;

namespace UGame.Game
{
    public sealed class MatchView : MonoBehaviour
    {
        // --- как в World.java ---
        public const float ArenaWidth = 20f;
        public const float ArenaHeight = 30f;
        const float BrawlerRadius = 0.5f;
        const float BrawlerSpeed = 5f;

        const float InterpDelayTicks = 3f;   // 100 мс при 30 Гц
        const int MaxSnapshots = 40;
        const int MaxPending = 120;

        static readonly Color GroundColor = new Color(0.32f, 0.68f, 0.27f);
        static readonly Color BorderColor = new Color(0.18f, 0.45f, 0.16f);
        static readonly Color AllyColor = new Color(0.20f, 0.55f, 1.00f);
        static readonly Color EnemyColor = new Color(1.00f, 0.25f, 0.20f);
        static readonly Color AllyShotColor = new Color(0.45f, 0.95f, 1.00f);
        static readonly Color EnemyShotColor = new Color(1.00f, 0.60f, 0.10f);

        // разные цвета персонажей — по id сущности
        static readonly Color[] BrawlerPalette =
        {
            new Color(0.95f, 0.80f, 0.20f), // жёлтый
            new Color(0.60f, 0.35f, 0.90f), // фиолетовый
            new Color(0.95f, 0.45f, 0.70f), // розовый
            new Color(0.25f, 0.85f, 0.80f), // бирюзовый
            new Color(0.98f, 0.55f, 0.20f), // оранжевый
            new Color(0.55f, 0.35f, 0.20f), // коричневый
            new Color(0.90f, 0.90f, 0.95f), // белый
            new Color(0.20f, 0.30f, 0.65f), // тёмно-синий
        };

        // ------------------------------------------------------------------ состояние

        GameConnection conn;
        uint myEntityId;
        int myTeam;
        int tickRate;
        float tickDt;

        public bool Running { get; private set; }
        public bool Finished { get; private set; }
        public int MyTeam => myTeam;
        public Snapshot Latest { get; private set; }
        public bool MyAlive { get; private set; }
        public readonly Dictionary<uint, PlayerInfo> Players = new Dictionary<uint, PlayerInfo>();

        // интерполяция
        readonly List<Snapshot> buffer = new List<Snapshot>();
        readonly Dictionary<ushort, EntityState> fromById = new Dictionary<ushort, EntityState>();
        double renderTick;
        bool clockStarted;

        // предсказание
        struct Pending { public ushort Seq; public float Mx, My; }
        readonly List<Pending> pending = new List<Pending>();
        readonly List<InputCommand> history = new List<InputCommand>(InputPacket.MaxCommands);
        ushort nextSeq = 1;
        float sendAcc;
        bool predicting;
        Vector2 predicted, prevPredicted, smoothOffset;
        float myAim;

        // объекты сцены
        sealed class View
        {
            public GameObject Root;
            public EntityKind Kind;
            public bool Seen;
            public Vector3 WorldPos;
            public float Hp01;
            public byte Team;
            public ushort Id;
        }
        readonly Dictionary<ushort, View> views = new Dictionary<ushort, View>();
        readonly List<ushort> toRemove = new List<ushort>();
        Camera cam;
        Vector3 camTarget;

        // сенсорное управление (телефон или Device Simulator)
        TouchControls touch;
        Transform aimLine;
        const float ProjectileRange = 8f;   // как PROJECTILE_RANGE в World.java
        const float AutoAimRange = 10f;
        const float AttackCooldown = 0.5f;  // как ATTACK_COOLDOWN_SEC в World.java
        float fireReadyAt;
        bool queuedFire;
        float queuedAim;
        public bool TouchMode => touch != null;

        // ------------------------------------------------------------------ жизненный цикл

        public static MatchView Create(GameConnection connection, JoinResult join)
        {
            var go = new GameObject("Match");
            var view = go.AddComponent<MatchView>();
            view.conn = connection;
            view.myEntityId = join.EntityId;
            view.myTeam = (int)join.Team;
            view.tickRate = join.TickRate > 0 ? (int)join.TickRate : 30;
            view.tickDt = 1f / view.tickRate;
            view.BuildArena();
            view.SetupCamera();
            if (TouchControls.Available) view.touch = new TouchControls();
            return view;
        }

        public void OnMatchStart(MatchStart msg)
        {
            Players.Clear();
            foreach (var p in msg.Players) Players[p.EntityId] = p;
            Running = true;
        }

        public void OnMatchEnd()
        {
            Running = false;
            Finished = true;
        }

        public string NameOf(long playerId)
        {
            foreach (var p in Players.Values)
                if (p.PlayerId == playerId) return p.Username;
            return "игрок " + playerId;
        }

        // ------------------------------------------------------------------ сеть

        public void OnSnapshot(Snapshot s)
        {
            if (Latest != null && s.Tick <= Latest.Tick) return; // старый или дубль (UDP)
            buffer.Add(s);
            if (buffer.Count > MaxSnapshots) buffer.RemoveAt(0);
            Latest = s;

            if (!clockStarted)
            {
                renderTick = s.Tick - InterpDelayTicks;
                clockStarted = true;
            }
            Reconcile(s);
        }

        void Reconcile(Snapshot s)
        {
            EntityState? own = FindOwn(s);
            if (own == null || own.Value.Hp01 <= 0f)
            {
                MyAlive = false;
                predicting = false;
                pending.Clear();
                queuedFire = false;
                return;
            }
            MyAlive = true;

            // команды, которые сервер уже применил, больше не нужны
            pending.RemoveAll(p => !Quantize.SeqGreater(p.Seq, s.AckInputSeq));

            Vector2 pos = own.Value.Pos;
            foreach (var p in pending) pos = Step(pos, p.Mx, p.My, tickDt);

            if (predicting)
            {
                Vector2 delta = predicted - pos;
                if (delta.sqrMagnitude > 4f) delta = Vector2.zero; // сильно разошлись (респаун) — без сглаживания
                smoothOffset += delta;
                prevPredicted -= predicted - pos;
            }
            else
            {
                prevPredicted = pos;
                smoothOffset = Vector2.zero;
                myAim = own.Value.Aim;
            }
            predicted = pos;
            predicting = true;
        }

        EntityState? FindOwn(Snapshot s)
        {
            foreach (var e in s.Entities)
                if (e.Id == myEntityId && e.Kind == EntityKind.Brawler) return e;
            return null;
        }

        /// <summary>Шаг движения — точная копия World.moveBrawler на сервере.</summary>
        static Vector2 Step(Vector2 p, float mx, float my, float dt)
        {
            float len = Mathf.Sqrt(mx * mx + my * my);
            if (len > 1f) { mx /= len; my /= len; }
            p.x = Mathf.Clamp(p.x + mx * BrawlerSpeed * dt, BrawlerRadius, ArenaWidth - BrawlerRadius);
            p.y = Mathf.Clamp(p.y + my * BrawlerSpeed * dt, BrawlerRadius, ArenaHeight - BrawlerRadius);
            return p;
        }

        // ------------------------------------------------------------------ кадр

        /// <summary>Вызывается GameClient каждый кадр после приёма пакетов.</summary>
        public void Tick(float dt)
        {
            touch?.Update();
            if (Running && MyAlive) SendInputs(dt);
            AdvanceClock(dt);
            smoothOffset = Vector2.Lerp(smoothOffset, Vector2.zero, 1f - Mathf.Exp(-12f * dt));
            UpdateViews();
            UpdateAimLine();
            UpdateCamera(dt);
        }

        void SendInputs(float dt)
        {
            sendAcc += dt;
            int steps = 0;
            while (sendAcc >= tickDt && steps < 3)
            {
                sendAcc -= tickDt;
                steps++;
                SendOneCommand();
            }
            if (sendAcc > tickDt * 3) sendAcc = 0; // долгий кадр — не догоняем бесконечно
        }

        void SendOneCommand()
        {
            Vector2 move = InputReader.Move();
            if (touch != null && touch.Move.sqrMagnitude > move.sqrMagnitude) move = touch.Move;
            move = ScreenToWorldDir(move);

            bool fire;
            if (touch != null) fire = TouchFire(move) | InputReader.Attack(includeMouse: false);
            else
            {
                UpdateAim();
                fire = InputReader.Attack(includeMouse: true);
            }
            int qx = Quantize.AxisToI8(move.x);
            int qy = Quantize.AxisToI8(move.y);

            var cmd = new InputCommand
            {
                Seq = nextSeq++,
                MoveX = (sbyte)qx,
                MoveY = (sbyte)qy,
                Aim = (ushort)Quantize.AngleToU16(myAim),
                Buttons = fire ? Buttons.Attack : Buttons.None,
            };
            if (nextSeq == 0) nextSeq = 1;

            history.Insert(0, cmd);
            if (history.Count > InputPacket.MaxCommands) history.RemoveAt(history.Count - 1);
            conn.SendInputs(history);

            if (predicting)
            {
                float mx = Quantize.I8ToAxis(qx), my = Quantize.I8ToAxis(qy);
                prevPredicted = predicted;
                predicted = Step(predicted, mx, my, tickDt);
                pending.Add(new Pending { Seq = cmd.Seq, Mx = mx, My = my });
                if (pending.Count > MaxPending) pending.RemoveAt(0);
            }
        }

        /// <summary>Направление «на экране» → направление на арене (камера команды 1 развёрнута на 180°).</summary>
        Vector2 ScreenToWorldDir(Vector2 v) => myTeam == 1 ? -v : v;

        /// <summary>
        /// Выстрел с телефона — разовое нажатие. Если оно пришлось на перезарядку, сервер бы его
        /// проигнорировал, поэтому выстрел ждёт конца перезарядки и уходит с тем же прицелом.
        /// </summary>
        bool TouchFire(Vector2 worldMove)
        {
            if (TouchAim(worldMove))
            {
                queuedFire = true;
                queuedAim = myAim;
            }
            if (!queuedFire || Time.time < fireReadyAt) return false;
            queuedFire = false;
            myAim = queuedAim;
            fireReadyAt = Time.time + AttackCooldown + 0.05f;
            return true;
        }

        /// <summary>Прицел при сенсорном управлении. Возвращает true, если игрок запросил выстрел.</summary>
        bool TouchAim(Vector2 worldMove)
        {
            var fire = touch.ConsumeFire(out Vector2 fireDir);
            if (fire == TouchControls.FireKind.Aimed)
            {
                SetAim(ScreenToWorldDir(fireDir));
                return true;
            }
            if (fire == TouchControls.FireKind.Auto)
            {
                if (NearestEnemy(out Vector2 enemy)) SetAim(enemy - OwnRenderPos());
                return true;
            }
            if (touch.Aiming) SetAim(ScreenToWorldDir(touch.AimDirection));
            else if (worldMove.sqrMagnitude > 0.01f) SetAim(worldMove); // без прицеливания смотрим, куда идём
            return false;
        }

        void SetAim(Vector2 worldDir)
        {
            if (worldDir.sqrMagnitude > 0.0001f) myAim = Mathf.Atan2(worldDir.y, worldDir.x);
        }

        bool NearestEnemy(out Vector2 pos)
        {
            pos = default;
            Vector2 own = OwnRenderPos();
            float best = AutoAimRange * AutoAimRange;
            bool found = false;
            foreach (var v in views.Values)
            {
                if (v.Kind != EntityKind.Brawler || v.Team == myTeam || !v.Root.activeSelf) continue;
                Vector2 p = new Vector2(v.WorldPos.x, v.WorldPos.z);
                float d = (p - own).sqrMagnitude;
                if (d < best) { best = d; pos = p; found = true; }
            }
            return found;
        }

        /// <summary>Линия выстрела на земле, пока игрок целится джойстиком.</summary>
        void UpdateAimLine()
        {
            bool show = touch != null && touch.Aiming && Running && MyAlive && predicting;
            if (!show)
            {
                if (aimLine != null && aimLine.gameObject.activeSelf) aimLine.gameObject.SetActive(false);
                return;
            }
            if (aimLine == null)
            {
                aimLine = Primitive(PrimitiveType.Cube, transform, new Color(1f, 1f, 1f));
                aimLine.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            aimLine.gameObject.SetActive(true);
            Vector2 dir = ScreenToWorldDir(touch.AimDirection);
            Vector2 own = OwnRenderPos();
            Vector3 d3 = new Vector3(dir.x, 0f, dir.y);
            aimLine.position = new Vector3(own.x, 0.05f, own.y) + d3 * (ProjectileRange / 2f + BrawlerRadius);
            aimLine.rotation = Quaternion.LookRotation(d3);
            aimLine.localScale = new Vector3(0.3f, 0.02f, ProjectileRange);
        }

        void UpdateAim()
        {
            if (cam == null || !predicting) return;
            Ray ray = cam.ScreenPointToRay(InputReader.MousePosition());
            var ground = new Plane(Vector3.up, Vector3.zero);
            if (!ground.Raycast(ray, out float dist)) return;
            Vector3 hit = ray.GetPoint(dist);
            Vector2 own = OwnRenderPos();
            Vector2 dir = new Vector2(hit.x - own.x, hit.z - own.y);
            if (dir.sqrMagnitude > 0.01f) myAim = Mathf.Atan2(dir.y, dir.x);
        }

        Vector2 OwnRenderPos()
        {
            float t = Mathf.Clamp01(sendAcc / tickDt);
            return Vector2.Lerp(prevPredicted, predicted, t) + smoothOffset;
        }

        void AdvanceClock(float dt)
        {
            if (!clockStarted || Latest == null) return;
            renderTick += dt * tickRate;
            double target = Latest.Tick - InterpDelayTicks;
            double diff = target - renderTick;
            if (diff > 10 || diff < -10) renderTick = target;               // сильно отстали/убежали
            else renderTick += diff * Mathf.Min(1f, dt * 2f);              // мягкая подстройка
            if (renderTick > Latest.Tick) renderTick = Latest.Tick;        // без экстраполяции
        }

        // ------------------------------------------------------------------ объекты

        void UpdateViews()
        {
            if (buffer.Count == 0) return;

            Snapshot from = buffer[0], to = buffer[0];
            for (int i = 0; i < buffer.Count; i++)
            {
                if (buffer[i].Tick <= renderTick) from = buffer[i];
                else { to = buffer[i]; break; }
                to = from;
            }
            float t = to.Tick > from.Tick ? (float)((renderTick - from.Tick) / (to.Tick - from.Tick)) : 0f;
            t = Mathf.Clamp01(t);

            fromById.Clear();
            foreach (var e in from.Entities) fromById[e.Id] = e;

            foreach (var v in views.Values) v.Seen = false;

            foreach (var e in to.Entities)
            {
                bool isOwn = e.Id == myEntityId && e.Kind == EntityKind.Brawler;
                Vector2 pos;
                float aim;
                float hp = e.Hp01;

                if (isOwn && predicting)
                {
                    pos = OwnRenderPos();
                    aim = myAim;
                }
                else if (fromById.TryGetValue(e.Id, out var a))
                {
                    pos = Vector2.Lerp(a.Pos, e.Pos, t);
                    aim = Mathf.LerpAngle(a.Aim * Mathf.Rad2Deg, e.Aim * Mathf.Rad2Deg, t) * Mathf.Deg2Rad;
                }
                else
                {
                    pos = e.Pos;
                    aim = e.Aim;
                }
                if (isOwn) hp = FindOwn(Latest)?.Hp01 ?? hp; // своё здоровье — без задержки

                if (!views.TryGetValue(e.Id, out var view))
                {
                    view = CreateView(e);
                    views[e.Id] = view;
                }
                view.Seen = true;
                view.Hp01 = hp;
                view.Team = e.Team;

                bool visible = e.Kind != EntityKind.Brawler || hp > 0f;
                if (view.Root.activeSelf != visible) view.Root.SetActive(visible);

                view.WorldPos = new Vector3(pos.x, 0f, pos.y);
                view.Root.transform.position = view.WorldPos;
                view.Root.transform.rotation = Quaternion.LookRotation(new Vector3(Mathf.Cos(aim), 0f, Mathf.Sin(aim)));
            }

            toRemove.Clear();
            foreach (var kv in views)
                if (!kv.Value.Seen) toRemove.Add(kv.Key);
            foreach (var id in toRemove)
            {
                Destroy(views[id].Root);
                views.Remove(id);
            }
        }

        View CreateView(EntityState e)
        {
            var root = new GameObject(e.Kind + " " + e.Id);
            root.transform.SetParent(transform, false);
            bool ally = e.Team == myTeam;

            if (e.Kind == EntityKind.Brawler)
            {
                // кольцо под ногами — цвет команды
                var ring = Primitive(PrimitiveType.Cylinder, root.transform, ally ? AllyColor : EnemyColor);
                ring.localScale = new Vector3(1.5f, 0.02f, 1.5f);
                ring.localPosition = new Vector3(0f, 0.02f, 0f);

                // тело — свой цвет у каждого персонажа
                var body = Primitive(PrimitiveType.Capsule, root.transform, BrawlerPalette[e.Id % BrawlerPalette.Length]);
                body.localScale = new Vector3(1f, 0.8f, 1f);
                body.localPosition = new Vector3(0f, 0.8f, 0f);

                // «ствол» показывает направление прицела
                var gun = Primitive(PrimitiveType.Cube, root.transform, new Color(0.15f, 0.15f, 0.15f));
                gun.localScale = new Vector3(0.22f, 0.22f, 0.8f);
                gun.localPosition = new Vector3(0f, 0.9f, 0.6f);
            }
            else
            {
                var shot = Primitive(PrimitiveType.Sphere, root.transform, ally ? AllyShotColor : EnemyShotColor);
                shot.localScale = Vector3.one * 0.35f;
                shot.localPosition = new Vector3(0f, 0.7f, 0f);
                shot.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            return new View { Root = root, Kind = e.Kind, Id = e.Id };
        }

        static Transform Primitive(PrimitiveType type, Transform parent, Color color)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>()); // физика не нужна — всё считает сервер
            go.transform.SetParent(parent, false);
            // материал по умолчанию подходит к текущему рендер-пайплайну (URP);
            // .color пишет в основной цвет шейдера (_BaseColor у URP Lit)
            go.GetComponent<Renderer>().material.color = color;
            return go.transform;
        }

        void BuildArena()
        {
            var ground = Primitive(PrimitiveType.Plane, transform, GroundColor);
            ground.localScale = new Vector3(ArenaWidth / 10f, 1f, ArenaHeight / 10f); // Plane — 10×10 м
            ground.localPosition = new Vector3(ArenaWidth / 2f, 0f, ArenaHeight / 2f);

            const float h = 0.4f, w = 0.4f;
            Border(new Vector3(ArenaWidth / 2f, h / 2f, -w / 2f), new Vector3(ArenaWidth + 2 * w, h, w));
            Border(new Vector3(ArenaWidth / 2f, h / 2f, ArenaHeight + w / 2f), new Vector3(ArenaWidth + 2 * w, h, w));
            Border(new Vector3(-w / 2f, h / 2f, ArenaHeight / 2f), new Vector3(w, h, ArenaHeight));
            Border(new Vector3(ArenaWidth + w / 2f, h / 2f, ArenaHeight / 2f), new Vector3(w, h, ArenaHeight));

            if (FindAnyObjectByType<Light>() == null)
            {
                var light = new GameObject("Sun").AddComponent<Light>();
                light.type = LightType.Directional;
                light.transform.SetParent(transform, false);
                light.transform.rotation = Quaternion.Euler(55f, -30f, 0f);
            }
        }

        void Border(Vector3 pos, Vector3 size)
        {
            var b = Primitive(PrimitiveType.Cube, transform, BorderColor);
            b.localPosition = pos;
            b.localScale = size;
        }

        // ------------------------------------------------------------------ камера

        void SetupCamera()
        {
            cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Main Camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
            }
            cam.fieldOfView = 50f;
            camTarget = new Vector3(ArenaWidth / 2f, 0f, myTeam == 1 ? ArenaHeight - 6f : 6f);
            PlaceCamera(camTarget);
        }

        void UpdateCamera(float dt)
        {
            if (cam == null) return;
            Vector3 target = camTarget;
            if (views.TryGetValue((ushort)myEntityId, out var own) && own.Root.activeSelf)
                target = own.WorldPos;
            camTarget = Vector3.Lerp(camTarget, target, 1f - Mathf.Exp(-6f * dt));
            PlaceCamera(camTarget);
        }

        void PlaceCamera(Vector3 target)
        {
            // вид сверху под углом, как в Brawl Stars; команда 1 смотрит «с другой стороны»
            var rot = Quaternion.Euler(58f, myTeam == 1 ? 180f : 0f, 0f);
            cam.transform.rotation = rot;
            cam.transform.position = target - rot * Vector3.forward * 20f;
        }

        // ------------------------------------------------------------------ подписи над головами

        /// <summary>Имена и полоски здоровья. Вызывается из OnGUI (координаты уже в масштабе GUI).</summary>
        public void DrawOverlay(float guiScale, GUIStyle nameStyle)
        {
            if (cam == null) return;
            if (touch != null && Running && MyAlive) touch.Draw(guiScale);
            foreach (var v in views.Values)
            {
                if (v.Kind != EntityKind.Brawler || !v.Root.activeSelf) continue;
                Vector3 sp = cam.WorldToScreenPoint(v.WorldPos + Vector3.up * 2.2f);
                if (sp.z < 0) continue;
                float x = sp.x / guiScale, y = (Screen.height - sp.y) / guiScale;

                bool own = v.Id == myEntityId;
                Players.TryGetValue(v.Id, out var info);
                string name = own ? "Вы" : info?.Username ?? "";

                var prev = GUI.color;
                GUI.color = own ? new Color(1f, 0.95f, 0.4f) : Color.white;
                GUI.Label(new Rect(x - 80, y - 34, 160, 22), name, nameStyle);

                const float bw = 70f, bh = 9f;
                GUI.color = new Color(0f, 0f, 0f, 0.7f);
                GUI.DrawTexture(new Rect(x - bw / 2 - 1, y - 11, bw + 2, bh + 2), Texture2D.whiteTexture);
                GUI.color = v.Team == myTeam ? new Color(0.3f, 0.9f, 0.35f) : new Color(0.95f, 0.25f, 0.2f);
                GUI.DrawTexture(new Rect(x - bw / 2, y - 10, bw * v.Hp01, bh), Texture2D.whiteTexture);
                GUI.color = prev;
            }
        }
    }
}
