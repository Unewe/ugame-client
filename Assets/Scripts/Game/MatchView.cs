// Отрисовка матча и ввод игрока.
//
// Сервер авторитарный: клиент шлёт только ввод (30 раз в секунду), сервер присылает снапшоты.
// * Свой боец — client-side prediction: двигаем его сразу, а по снапшоту берём позицию сервера
//   и заново применяем ещё не подтверждённые команды (reconciliation). Расхождение сглаживается.
// * Чужие бойцы и снаряды — интерполяция между снапшотами с задержкой ~100 мс.
// * Свои выстрелы тоже предсказываются: снаряд появляется сразу при нажатии, а серверная копия
//   (у снаряда в снапшоте есть id владельца) не рисуется. Сервер проверяет попадания по позициям
//   целей, отмотанным на RTT стрелка + InterpDelayTicks (компенсация лагов в World.java), поэтому
//   то, что игрок видит у себя, совпадает с тем, что посчитает сервер. Выстрел, которого сервер
//   не сделал (LastAttackSeq/LastSuperSeq в снапшоте), убирается.
// * Рывок (уклонение) тоже предсказывается: сервер присылает своему игроку кулдаун и остаток
//   рывка, клиент повторяет неподтверждённые команды той же логикой, что World.moveBrawler.
//
// Константы движения, рывка и супера должны совпадать с World.java на сервере.
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
        const float DodgeCooldownSec = 5f;
        const float DashDurationSec = 0.2f;
        const float DashDistance = 4f;
        const float SuperRange = 9f;
        const float SuperSpreadDeg = 40f;
        const float SuperSpeed = 18f;
        const float SuperRadius = 0.3f;
        const int SuperShots = 5;
        const float ProjectileSpeed = 15f;
        const float ProjectileRadius = 0.15f;
        const float AttackCooldownSec = 0.5f;

        // 100 мс при 30 Гц; должно совпадать с lagcomp.interp-delay-ticks в game-server.properties
        const float InterpDelayTicks = 3f;
        const int MaxSnapshots = 40;
        const int MaxPending = 120;

        static readonly Color GroundColor = new Color(0.32f, 0.68f, 0.27f);
        static readonly Color BorderColor = new Color(0.18f, 0.45f, 0.16f);
        static readonly Color AllyColor = new Color(0.20f, 0.55f, 1.00f);
        static readonly Color EnemyColor = new Color(1.00f, 0.25f, 0.20f);
        static readonly Color AllyShotColor = new Color(0.45f, 0.95f, 1.00f);
        static readonly Color EnemyShotColor = new Color(1.00f, 0.60f, 0.10f);
        static readonly Color SuperShotColor = new Color(1.00f, 0.85f, 0.10f);
        static readonly Color SuperGlowColor = new Color(1.00f, 0.85f, 0.10f);
        static readonly Color DodgeRingColor = new Color(0.85f, 1.00f, 1.00f);

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
        int dashTicks, dodgeCooldownTicks, attackCooldownTicks;
        float dashStep;

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
        struct Pending { public ushort Seq; public float Mx, My, Aim; public bool Dodge; }

        /// <summary>Состояние рывка своего бойца — как поля Brawler в World.java.</summary>
        struct DashState
        {
            public int Cooldown;   // тиков до готовности уклонения
            public int DashLeft;   // тиков рывка осталось
            public float Dx, Dy;
        }
        readonly List<Pending> pending = new List<Pending>();
        readonly List<InputCommand> history = new List<InputCommand>(InputPacket.MaxCommands);
        ushort nextSeq = 1;
        float sendAcc;
        bool predicting;
        Vector2 predicted, prevPredicted, smoothOffset;
        DashState dash;
        float myAim;

        // способности: нажатия запоминаются в кадре и уходят со следующей командой
        bool superQueued, dodgeQueued;
        bool superInFlight;     // супер отправлен, сервер ещё не подтвердил команду
        ushort superSentSeq;
        public bool SuperReady => Latest != null && Latest.SuperReady && !superInFlight;
        public float SuperCharge01 => Latest != null && !superInFlight ? Latest.SuperCharge / 255f : 0f;
        public bool DodgeReady => dash.Cooldown == 0;
        public float DodgeCooldownLeftSec => dash.Cooldown * tickDt;
        public bool Dashing => dash.DashLeft > 0;

        // предсказанные свои выстрелы
        sealed class PredictedShot
        {
            public ushort Seq;        // команда, в которой нажали атаку/супер
            public bool IsSuper;
            public bool Confirmed;    // сервер подтвердил, что выстрел был
            public Vector2 Origin, Dir;
            public float Age;         // секунд с выстрела
            public Transform Body;    // null — снаряд уже попал или долетел
        }
        readonly List<PredictedShot> shots = new List<PredictedShot>();
        int lastShotSeq = -1;         // seq последней атаки — кулдаун считается в командах, как на сервере

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
            public Transform Body, Gun, Ring, SuperGlow;
            public bool Dodging;
        }
        readonly Dictionary<ushort, View> views = new Dictionary<ushort, View>();
        readonly List<ushort> toRemove = new List<ushort>();
        Camera cam;
        Vector3 camTarget;

        // сенсорное управление (телефон или Device Simulator)
        TouchControls touch;
        readonly Transform[] aimLines = new Transform[3]; // центр + края веера супера
        const float ProjectileRange = 8f;   // как PROJECTILE_RANGE в World.java
        const float AutoAimRange = 10f;
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
            // Math.round из Java: округление половины вверх
            view.dashTicks = Mathf.Max(1, Mathf.FloorToInt(DashDurationSec * view.tickRate + 0.5f));
            view.dodgeCooldownTicks = Mathf.FloorToInt(DodgeCooldownSec * view.tickRate + 0.5f);
            view.attackCooldownTicks = Mathf.Max(1, Mathf.FloorToInt(AttackCooldownSec * view.tickRate + 0.5f));
            view.dashStep = DashDistance / view.dashTicks;
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
            ResolveShots(s);
            EntityState? own = FindOwn(s);
            if (own == null || own.Value.Hp01 <= 0f)
            {
                MyAlive = false;
                predicting = false;
                pending.Clear();
                queuedFire = false;
                superQueued = dodgeQueued = superInFlight = false;
                dash = new DashState { Cooldown = s.DodgeCooldownTicks };
                return;
            }
            MyAlive = true;

            if (superInFlight && !Quantize.SeqGreater(superSentSeq, s.AckInputSeq)) superInFlight = false;

            // команды, которые сервер уже применил, больше не нужны
            pending.RemoveAll(p => !Quantize.SeqGreater(p.Seq, s.AckInputSeq));

            // состояние сервера на момент снапшота + повтор неподтверждённых команд
            var d = new DashState { Cooldown = s.DodgeCooldownTicks, DashLeft = s.DashTicksLeft };
            if (d.DashLeft > 0)
            {
                float a = Quantize.U16ToAngle(s.DashAngle);
                d.Dx = Mathf.Cos(a);
                d.Dy = Mathf.Sin(a);
            }
            Vector2 pos = own.Value.Pos;
            foreach (var p in pending) pos = Step(pos, ref d, p.Mx, p.My, p.Aim, p.Dodge);
            dash = d;

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

        /// <summary>
        /// Сверка предсказанных выстрелов: когда сервер применил команду выстрела (ack), он уже решил,
        /// вылетел ли снаряд. Если LastAttackSeq/LastSuperSeq до неё не дошёл — выстрела не было
        /// (умер раньше, супер не был заряжен, команда потерялась), и снаряд-«призрак» убираем.
        /// </summary>
        void ResolveShots(Snapshot s)
        {
            for (int i = shots.Count - 1; i >= 0; i--)
            {
                var shot = shots[i];
                if (shot.Confirmed || Quantize.SeqGreater(shot.Seq, s.AckInputSeq)) continue;
                ushort fired = shot.IsSuper ? s.LastSuperSeq : s.LastAttackSeq;
                if (!Quantize.SeqGreater(shot.Seq, fired))
                {
                    shot.Confirmed = true;
                    if (shot.Body == null) shots.RemoveAt(i);
                }
                else
                {
                    if (shot.Body != null) Destroy(shot.Body.gameObject);
                    shots.RemoveAt(i);
                }
            }
        }

        EntityState? FindOwn(Snapshot s)
        {
            foreach (var e in s.Entities)
                if (e.Id == myEntityId && e.Kind == EntityKind.Brawler) return e;
            return null;
        }

        /// <summary>Один тик своего бойца — копия World.step (кулдаун, начало рывка) и World.moveBrawler.</summary>
        Vector2 Step(Vector2 p, ref DashState d, float mx, float my, float aim, bool dodge)
        {
            if (d.Cooldown > 0) d.Cooldown--;
            if (dodge && d.Cooldown == 0 && d.DashLeft == 0)
            {
                // как World.startDash: идём — рывок по движению, стоим — по прицелу; угол квантуется
                float angle = (Mathf.Abs(mx) > 0.01f || Mathf.Abs(my) > 0.01f) ? Mathf.Atan2(my, mx) : aim;
                float a = Quantize.U16ToAngle(Quantize.AngleToU16(angle));
                d.Dx = Mathf.Cos(a);
                d.Dy = Mathf.Sin(a);
                d.DashLeft = dashTicks;
                d.Cooldown = dodgeCooldownTicks;
            }

            float sx, sy;
            if (d.DashLeft > 0)
            {
                sx = d.Dx * dashStep;
                sy = d.Dy * dashStep;
                d.DashLeft--;
            }
            else
            {
                float len = Mathf.Sqrt(mx * mx + my * my);
                if (len > 1f) { mx /= len; my /= len; }
                sx = mx * BrawlerSpeed * tickDt;
                sy = my * BrawlerSpeed * tickDt;
            }
            p.x = Mathf.Clamp(p.x + sx, BrawlerRadius, ArenaWidth - BrawlerRadius);
            p.y = Mathf.Clamp(p.y + sy, BrawlerRadius, ArenaHeight - BrawlerRadius);
            return p;
        }

        // ------------------------------------------------------------------ кадр

        /// <summary>Вызывается GameController каждый кадр, после того как GameClient принял пакеты.</summary>
        public void Tick(float dt)
        {
            if (touch != null)
            {
                touch.SuperReady = SuperReady;
                touch.SuperCharge01 = SuperCharge01;
                touch.DodgeReady = DodgeReady;
                touch.DodgeCooldownSec = DodgeCooldownLeftSec;
                touch.Update();
                if (touch.ConsumeDodge()) dodgeQueued = true;
            }
            if (InputReader.SuperPressed(includeMouse: touch == null)) superQueued = true;
            if (InputReader.DodgePressed()) dodgeQueued = true;
            if (Running && MyAlive) SendInputs(dt);
            AdvanceClock(dt);
            smoothOffset = Vector2.Lerp(smoothOffset, Vector2.zero, 1f - Mathf.Exp(-12f * dt));
            UpdateViews();
            UpdateShots(dt);
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

            // кулдаун атаки — в командах, ровно как World.attackReady: тогда предсказанный выстрел
            // сервер тоже сделает, а «лишних» нажатий на кулдауне мы просто не отправляем
            bool attackReady = lastShotSeq < 0 || ((nextSeq - lastShotSeq) & 0xFFFF) >= attackCooldownTicks;
            bool fire;
            bool super = false;
            if (touch != null)
            {
                // супер и обычный выстрел в одной команде не шлём: у них разный прицел,
                // а выстрел из очереди спокойно уйдёт следующей командой
                super = TouchSuper();
                fire = !super && (TouchFire(move, attackReady) | (attackReady && InputReader.Attack(includeMouse: false)));
            }
            else
            {
                UpdateAim();
                fire = attackReady && InputReader.Attack(includeMouse: true);
            }
            if (superQueued && SuperReady) super = true;
            superQueued = false;

            // готовность рывка проверяет Step — ровно так же, как сервер; на кулдауне нажатие просто ничего не делает
            bool dodge = dodgeQueued && !Dashing;
            dodgeQueued = false;

            int qx = Quantize.AxisToI8(move.x);
            int qy = Quantize.AxisToI8(move.y);

            var buttons = Buttons.None;
            if (fire) buttons |= Buttons.Attack;
            if (super) buttons |= Buttons.Super;
            if (dodge) buttons |= Buttons.Dodge;

            var cmd = new InputCommand
            {
                Seq = nextSeq++,
                MoveX = (sbyte)qx,
                MoveY = (sbyte)qy,
                Aim = (ushort)Quantize.AngleToU16(myAim),
                Buttons = buttons,
            };
            if (nextSeq == 0) nextSeq = 1;
            if (fire) lastShotSeq = cmd.Seq;
            if (super)
            {
                superInFlight = true;
                superSentSeq = cmd.Seq;
            }

            history.Insert(0, cmd);
            if (history.Count > InputPacket.MaxCommands) history.RemoveAt(history.Count - 1);
            conn.SendInputs(history);

            if (predicting)
            {
                float mx = Quantize.I8ToAxis(qx), my = Quantize.I8ToAxis(qy);
                float aim = Quantize.U16ToAngle(cmd.Aim);
                prevPredicted = predicted;
                predicted = Step(predicted, ref dash, mx, my, aim, dodge);
                pending.Add(new Pending { Seq = cmd.Seq, Mx = mx, My = my, Aim = aim, Dodge = dodge });
                if (pending.Count > MaxPending) pending.RemoveAt(0);

                // выстрелы — с позиции после этого шага, как в World.step (сначала движение, потом стрельба)
                if (fire) SpawnShot(cmd.Seq, false, aim);
                if (super)
                    for (int i = 0; i < SuperShots; i++)
                    {
                        float t = SuperShots == 1 ? 0f : (float)i / (SuperShots - 1) - 0.5f;
                        SpawnShot(cmd.Seq, true, aim + t * SuperSpreadDeg * Mathf.Deg2Rad);
                    }
            }
        }

        /// <summary>Направление «на экране» → направление на арене (камера команды 1 развёрнута на 180°).</summary>
        Vector2 ScreenToWorldDir(Vector2 v) => myTeam == 1 ? -v : v;

        /// <summary>
        /// Выстрел с телефона — разовое нажатие. Если оно пришлось на перезарядку, сервер бы его
        /// проигнорировал, поэтому выстрел ждёт конца перезарядки и уходит с тем же прицелом.
        /// </summary>
        bool TouchFire(Vector2 worldMove, bool attackReady)
        {
            if (TouchAim(worldMove))
            {
                queuedFire = true;
                queuedAim = myAim;
            }
            if (!queuedFire || !attackReady) return false;
            queuedFire = false;
            myAim = queuedAim;
            return true;
        }

        /// <summary>Супер удар с телефона. Прицел берётся из джойстика супера или по ближайшему врагу.</summary>
        bool TouchSuper()
        {
            var kind = touch.ConsumeSuper(out Vector2 dir);
            if (kind == TouchControls.FireKind.None || !SuperReady) return false;
            if (kind == TouchControls.FireKind.Aimed) SetAim(ScreenToWorldDir(dir));
            else if (NearestEnemy(out Vector2 enemy)) SetAim(enemy - OwnRenderPos());
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
            else if (touch.SuperAiming) SetAim(ScreenToWorldDir(touch.SuperDirection));
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

        /// <summary>
        /// Линия выстрела на земле, пока игрок целится джойстиком. Для супера — три жёлтые линии:
        /// центр и края веера.
        /// </summary>
        void UpdateAimLine()
        {
            bool alive = touch != null && Running && MyAlive && predicting;
            bool superAim = alive && touch.SuperAiming;
            bool attackAim = alive && !superAim && touch.Aiming;
            int count = superAim ? 3 : attackAim ? 1 : 0;

            for (int i = 0; i < aimLines.Length; i++)
            {
                if (i >= count)
                {
                    if (aimLines[i] != null && aimLines[i].gameObject.activeSelf) aimLines[i].gameObject.SetActive(false);
                    continue;
                }
                if (aimLines[i] == null)
                {
                    aimLines[i] = Primitive(PrimitiveType.Cube, transform, Color.white);
                    aimLines[i].GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                var line = aimLines[i];
                line.gameObject.SetActive(true);
                line.GetComponent<Renderer>().material.color = superAim ? SuperShotColor : Color.white;

                Vector2 dir = ScreenToWorldDir(superAim ? touch.SuperDirection : touch.AimDirection);
                float offset = i == 0 ? 0f : (i == 1 ? -0.5f : 0.5f) * SuperSpreadDeg;
                float range = superAim ? SuperRange : ProjectileRange;
                Vector3 d3 = Quaternion.Euler(0f, offset, 0f) * new Vector3(dir.x, 0f, dir.y);
                Vector2 own = OwnRenderPos();
                line.position = new Vector3(own.x, 0.05f, own.y) + d3 * (range / 2f + BrawlerRadius);
                line.rotation = Quaternion.LookRotation(d3);
                line.localScale = new Vector3(superAim && i == 0 ? 0.45f : 0.3f, 0.02f, range);
            }
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
                // свои снаряды рисуем по предсказанию (UpdateShots), серверную копию — нет
                if (e.Kind == EntityKind.Projectile && e.Owner == myEntityId) continue;
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
                var flags = e.Flags;
                if (isOwn)
                {
                    var latestOwn = FindOwn(Latest);
                    hp = latestOwn?.Hp01 ?? hp; // своё здоровье — без задержки
                    if (latestOwn != null) flags = latestOwn.Value.Flags;
                    if (predicting) // рывок своего бойца — по предсказанию, а не с опозданием на RTT
                        flags = Dashing ? flags | EntityFlags.Dodging : flags & ~EntityFlags.Dodging;
                }

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
                if (e.Kind == EntityKind.Brawler) ApplyBrawlerFlags(view, flags);
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

        /// <summary>
        /// Рывок — боец «прижимается» к земле и подсвечивается кольцом (снаряды пролетают насквозь).
        /// Заряженный супер — жёлтое свечение под ногами, видно и союзникам, и врагам.
        /// </summary>
        void ApplyBrawlerFlags(View v, EntityFlags flags)
        {
            bool dodging = (flags & EntityFlags.Dodging) != 0;
            bool superReady = (flags & EntityFlags.SuperReady) != 0;
            v.Dodging = dodging;
            if (v.Body != null)
            {
                v.Body.localScale = dodging ? new Vector3(1.15f, 0.45f, 1.15f) : new Vector3(1f, 0.8f, 1f);
                v.Body.localPosition = new Vector3(0f, dodging ? 0.45f : 0.8f, 0f);
            }
            if (v.Gun != null && v.Gun.gameObject.activeSelf == dodging) v.Gun.gameObject.SetActive(!dodging);
            if (v.Ring != null)
                v.Ring.GetComponent<Renderer>().material.color = dodging ? DodgeRingColor : v.Team == myTeam ? AllyColor : EnemyColor;
            if (v.SuperGlow != null && v.SuperGlow.gameObject.activeSelf != superReady) v.SuperGlow.gameObject.SetActive(superReady);
        }

        // ------------------------------------------------------------------ свои выстрелы

        void SpawnShot(ushort seq, bool isSuper, float angle)
        {
            var dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            float radius = isSuper ? SuperRadius : ProjectileRadius;
            var body = Primitive(PrimitiveType.Sphere, transform, isSuper ? SuperShotColor : AllyShotColor);
            body.localScale = Vector3.one * (isSuper ? 0.65f : 0.35f);
            body.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var shot = new PredictedShot
            {
                Seq = seq,
                IsSuper = isSuper,
                Origin = predicted + dir * (BrawlerRadius + radius), // как World.spawnProjectile
                Dir = dir,
                Body = body,
            };
            body.position = new Vector3(shot.Origin.x, 0.7f, shot.Origin.y);
            shots.Add(shot);
        }

        /// <summary>
        /// Полёт предсказанных снарядов. Попадание здесь только косметическое (снаряд исчезает),
        /// урон считает сервер — по тем же позициям врагов, что видны на экране.
        /// </summary>
        void UpdateShots(float dt)
        {
            for (int i = shots.Count - 1; i >= 0; i--)
            {
                var s = shots[i];
                if (s.Body == null) continue; // ждёт подтверждения в ResolveShots
                s.Age += dt;
                float speed = s.IsSuper ? SuperSpeed : ProjectileSpeed;
                float range = s.IsSuper ? SuperRange : ProjectileRange;
                float hitDist = BrawlerRadius + (s.IsSuper ? SuperRadius : ProjectileRadius);
                float traveled = speed * s.Age;
                Vector2 p = s.Origin + s.Dir * traveled;
                bool done = traveled > range || p.x < 0 || p.y < 0 || p.x > ArenaWidth || p.y > ArenaHeight
                            || HitsVisibleEnemy(p, hitDist);
                if (done)
                {
                    Destroy(s.Body.gameObject);
                    s.Body = null;
                    if (s.Confirmed) shots.RemoveAt(i);
                    continue;
                }
                s.Body.position = new Vector3(p.x, 0.7f, p.y);
            }
            if (shots.Count > 64) // подтверждения не приходят (связь пропала) — не копим бесконечно
            {
                if (shots[0].Body != null) Destroy(shots[0].Body.gameObject);
                shots.RemoveAt(0);
            }
        }

        bool HitsVisibleEnemy(Vector2 p, float hitDist)
        {
            foreach (var v in views.Values)
            {
                if (v.Kind != EntityKind.Brawler || v.Team == myTeam || v.Dodging || !v.Root.activeSelf) continue;
                float dx = v.WorldPos.x - p.x, dy = v.WorldPos.z - p.y;
                if (dx * dx + dy * dy <= hitDist * hitDist) return true;
            }
            return false;
        }

        View CreateView(EntityState e)
        {
            var root = new GameObject(e.Kind + " " + e.Id);
            root.transform.SetParent(transform, false);
            bool ally = e.Team == myTeam;
            var view = new View { Root = root, Kind = e.Kind, Id = e.Id, Team = e.Team };

            if (e.Kind == EntityKind.Brawler)
            {
                // кольцо под ногами — цвет команды
                var ring = Primitive(PrimitiveType.Cylinder, root.transform, ally ? AllyColor : EnemyColor);
                ring.localScale = new Vector3(1.5f, 0.02f, 1.5f);
                ring.localPosition = new Vector3(0f, 0.02f, 0f);
                view.Ring = ring;

                // свечение заряженного супера
                var glow = Primitive(PrimitiveType.Cylinder, root.transform, SuperGlowColor);
                glow.localScale = new Vector3(2.1f, 0.01f, 2.1f);
                glow.localPosition = new Vector3(0f, 0.01f, 0f);
                glow.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                glow.gameObject.SetActive(false);
                view.SuperGlow = glow;

                // тело — свой цвет у каждого персонажа
                var body = Primitive(PrimitiveType.Capsule, root.transform, BrawlerPalette[e.Id % BrawlerPalette.Length]);
                body.localScale = new Vector3(1f, 0.8f, 1f);
                body.localPosition = new Vector3(0f, 0.8f, 0f);
                view.Body = body;

                // «ствол» показывает направление прицела
                var gun = Primitive(PrimitiveType.Cube, root.transform, new Color(0.15f, 0.15f, 0.15f));
                gun.localScale = new Vector3(0.22f, 0.22f, 0.8f);
                gun.localPosition = new Vector3(0f, 0.9f, 0.6f);
                view.Gun = gun;
            }
            else
            {
                bool superShot = (e.Flags & EntityFlags.SuperShot) != 0;
                var shot = Primitive(PrimitiveType.Sphere, root.transform,
                    superShot ? SuperShotColor : ally ? AllyShotColor : EnemyShotColor);
                shot.localScale = Vector3.one * (superShot ? 0.65f : 0.35f);
                shot.localPosition = new Vector3(0f, 0.7f, 0f);
                shot.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            return view;
        }

        // Материал из Resources (создаёт Editor/UGamePrimitiveMaterial.cs). Материал по умолчанию
        // от CreatePrimitive брать нельзя: его шейдер вырезается из сборки и на телефоне всё фиолетовое.
        static Material primitiveMaterial;

        static Transform Primitive(PrimitiveType type, Transform parent, Color color)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>()); // физика не нужна — всё считает сервер
            go.transform.SetParent(parent, false);

            if (primitiveMaterial == null)
            {
                primitiveMaterial = Resources.Load<Material>("UGame/PrimitiveLit");
                if (primitiveMaterial == null)
                    Debug.LogError("[UGame] Нет Resources/UGame/PrimitiveLit.mat — объекты в сборке будут фиолетовыми");
            }
            var r = go.GetComponent<Renderer>();
            if (primitiveMaterial != null) r.sharedMaterial = primitiveMaterial;
            // .material создаёт копию для объекта; .color пишет в _BaseColor у URP Lit
            r.material.color = color;
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
            var center = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 13 };
            GUI.Label(new Rect(x, y - 1, bw, bh + 2),
                SuperReady ? "СУПЕР ГОТОВ — E / ПКМ" : $"Супер {Mathf.FloorToInt(SuperCharge01 * 100f)}%", center);

            GUI.Label(new Rect(x, y + bh + 4, bw, 20),
                DodgeReady ? "Рывок готов — Shift" : $"Рывок через {DodgeCooldownLeftSec:0.0} с", center);
            GUI.color = prev;
        }

        /// <summary>Имена и полоски здоровья. Вызывается из OnGUI (координаты уже в масштабе GUI).</summary>
        public void DrawOverlay(float guiScale, GUIStyle nameStyle)
        {
            if (cam == null) return;
            if (touch != null && Running && MyAlive) touch.Draw(guiScale);
            if (touch == null && Running && MyAlive) DrawAbilityBar(guiScale);
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
