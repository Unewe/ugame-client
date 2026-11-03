// Ввод: клавиатура и мышь или сенсорные джойстики. Нажатия запоминаются в кадре, а команды
// уходят серверу 30 раз в секунду (по одной на тик клиента) — вместе с тремя предыдущими,
// чтобы потеря UDP-пакета не теряла нажатие.
//
// Буфер нажатий короткий: нажатие, которое не ушло вовремя, устаревает (BufferedPress).
// Пока боец мёртв, нажатия выбрасываются, включая зажатые джойстики, — после возрождения
// действие требует нового нажатия, и случайный супер «из прошлой жизни» невозможен.
using System.Collections.Generic;
using UGame.Net;
using UnityEngine;
using static UGame.Game.GameRules;

namespace UGame.Game
{
    partial class MatchView
    {
        const float AutoAimRange = 10f;
        // сколько живёт нажатие, которое ещё не ушло в команде
        const float AbilityBufferSec = 0.2f;   // супер и рывок: не сработали сразу — значит, не нужно
        const float AttackBufferSec = AttackCooldownSec + 0.1f; // атака ждёт конца перезарядки, но не дольше

        /// <summary>Нажатие, которое ждёт отправки в команде. Старше срока — не срабатывает.</summary>
        struct BufferedPress
        {
            float pressedAt;
            bool active;

            public void Press()
            {
                active = true;
                pressedAt = Time.time;
            }

            public void Clear() => active = false;

            /// <summary>Есть нажатие не старше maxAgeSec? Устаревшее заодно сбрасывается.</summary>
            public bool IsFresh(float maxAgeSec)
            {
                if (active && Time.time - pressedAt > maxAgeSec) active = false;
                return active;
            }
        }

        readonly List<InputCommand> history = new List<InputCommand>(InputPacket.MaxCommands);
        ushort nextSeq = 1;
        float sendAcc;          // накопленное время до следующей команды
        float myAim;
        int lastShotSeq = -1;   // seq последней атаки — кулдаун считается в командах, как на сервере

        // нажатия, которые ещё не ушли в команде
        BufferedPress superPress, dodgePress;
        BufferedPress attackPress;  // клик короче интервала команд или на перезарядке
        BufferedPress touchShot;    // выстрел с телефона ждёт конца перезарядки…
        float touchShotAim;         // …с тем прицелом, с которым его сделали

        bool superInFlight;     // супер отправлен, сервер ещё не подтвердил команду
        ushort superSentSeq;

        TouchControls touch;    // сенсорное управление (телефон или Device Simulator); null — мышь
        readonly Transform[] aimLines = new Transform[3]; // центр + края веера супера

        bool SuperReady => Latest != null && Latest.SuperReady && !superInFlight;
        float SuperCharge01 => Latest != null && !superInFlight ? Latest.SuperCharge / 255f : 0f;

        // ------------------------------------------------------------------ кадр

        /// <summary>Нажатия этого кадра — до ближайшей команды.</summary>
        void ReadFrameInput()
        {
            if (touch != null)
            {
                touch.SuperReady = SuperReady;
                touch.SuperCharge01 = SuperCharge01;
                touch.DodgeReady = DodgeReady;
                touch.DodgeCooldownSec = DodgeCooldownLeftSec;
                touch.Update();
                if (touch.ConsumeDodge()) dodgePress.Press();
            }
            if (InputReader.SuperPressed(includeMouse: touch == null)) superPress.Press();
            if (InputReader.AttackPressed(includeMouse: touch == null)) attackPress.Press();
            if (InputReader.DodgePressed()) dodgePress.Press();

            // команды не уходят (мёртв, матч не идёт) — нажатия этого времени не должны сработать потом
            if (!Running || !MyAlive) DiscardQueuedInput();
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
            Vector2 move = ReadMove();
            Buttons buttons = ReadButtons(move); // может повернуть прицел (джойстик, автоприцел, мышь)
            var cmd = new InputCommand
            {
                Seq = nextSeq,
                MoveX = (sbyte)Quantize.AxisToI8(move.x),
                MoveY = (sbyte)Quantize.AxisToI8(move.y),
                Aim = (ushort)Quantize.AngleToU16(myAim),
                Buttons = buttons,
            };
            GetViewTick(out cmd.ViewTick, out cmd.ViewFrac); // что игрок видел, нажимая кнопку
            nextSeq++;
            if (nextSeq == 0) nextSeq = 1;
            RememberSent(cmd);

            history.Insert(0, cmd);
            if (history.Count > InputPacket.MaxCommands) history.RemoveAt(history.Count - 1);
            conn.SendInputs(Latest != null ? Latest.Tick : 0u, history); // + подтверждение снапшота

            if (predicting) PredictCommand(cmd);
        }

        /// <summary>Движение с клавиатуры или джойстика (что сильнее), в координатах арены.</summary>
        Vector2 ReadMove()
        {
            Vector2 move = InputReader.Move();
            if (touch != null && touch.Move.sqrMagnitude > move.sqrMagnitude) move = touch.Move;
            return ScreenToWorldDir(move);
        }

        Buttons ReadButtons(Vector2 worldMove)
        {
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
                fire = !super && (TouchFire(worldMove, attackReady)
                                  | (attackReady && (attackPress.IsFresh(AttackBufferSec) || InputReader.Attack(includeMouse: false))));
            }
            else
            {
                UpdateMouseAim();
                fire = attackReady && (attackPress.IsFresh(AttackBufferSec) || InputReader.Attack(includeMouse: true));
            }
            if (superPress.IsFresh(AbilityBufferSec) && SuperReady) super = true;
            superPress.Clear();

            // готовность рывка проверяет Step — ровно так же, как сервер; на кулдауне нажатие ничего не делает
            bool dodge = dodgePress.IsFresh(AbilityBufferSec) && !Dashing;
            dodgePress.Clear();

            var buttons = Buttons.None;
            if (fire) buttons |= Buttons.Attack;
            if (super) buttons |= Buttons.Super;
            if (dodge) buttons |= Buttons.Dodge;
            return buttons;
        }

        void RememberSent(InputCommand cmd)
        {
            if ((cmd.Buttons & Buttons.Attack) != 0)
            {
                lastShotSeq = cmd.Seq;
                attackPress.Clear();
            }
            if ((cmd.Buttons & Buttons.Super) != 0)
            {
                superInFlight = true;
                superSentSeq = cmd.Seq;
            }
        }

        /// <summary>Сервер применил команду с супером — заряд в снапшоте снова верный.</summary>
        void ConfirmSentSuper(ushort ack)
        {
            if (superInFlight && !Quantize.SeqGreater(superSentSeq, ack)) superInFlight = false;
        }

        /// <summary>
        /// Выбросить всё, что нажато и ещё не ушло: боец мёртв, нажатия этого времени не должны
        /// сработать после возрождения. Зажатые джойстики атаки и супера тоже отпускаются без выстрела.
        /// </summary>
        void DiscardQueuedInput()
        {
            superPress.Clear();
            dodgePress.Clear();
            attackPress.Clear();
            touchShot.Clear();
            superInFlight = false;
            touch?.CancelActions();
        }

        // ------------------------------------------------------------------ прицел

        /// <summary>Направление «на экране» → направление на арене (камера команды 1 развёрнута на 180°).</summary>
        Vector2 ScreenToWorldDir(Vector2 v) => myTeam == 1 ? -v : v;

        void SetAim(Vector2 worldDir)
        {
            if (worldDir.sqrMagnitude > 0.0001f) myAim = Mathf.Atan2(worldDir.y, worldDir.x);
        }

        /// <summary>Мышь: целимся в точку на земле под курсором.</summary>
        void UpdateMouseAim()
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

        /// <summary>
        /// Выстрел с телефона — разовое нажатие. Если оно пришлось на перезарядку, сервер бы его
        /// проигнорировал, поэтому выстрел ждёт конца перезарядки и уходит с тем же прицелом.
        /// </summary>
        bool TouchFire(Vector2 worldMove, bool attackReady)
        {
            if (TouchAim(worldMove))
            {
                touchShot.Press();
                touchShotAim = myAim;
            }
            if (!touchShot.IsFresh(AttackBufferSec) || !attackReady) return false;
            touchShot.Clear();
            myAim = touchShotAim;
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

        bool NearestEnemy(out Vector2 pos)
        {
            pos = default;
            Vector2 own = OwnRenderPos();
            float best = AutoAimRange * AutoAimRange;
            bool found = false;
            foreach (var v in views.Values)
            {
                if (v.Team == myTeam || !v.Root.activeSelf) continue;
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
                if (aimLines[i] == null) aimLines[i] = NoShadow(Primitive(PrimitiveType.Cube, transform, Color.white));
                var line = aimLines[i];
                line.gameObject.SetActive(true);
                line.GetComponent<Renderer>().material.color = superAim ? SuperShotColor : Color.white;

                Vector2 dir = ScreenToWorldDir(superAim ? touch.SuperDirection : touch.AimDirection);
                float offset = i == 0 ? 0f : (i == 1 ? -0.5f : 0.5f) * SuperSpreadDeg;
                float range = Shot(superAim).Range;
                Vector3 d3 = Quaternion.Euler(0f, offset, 0f) * new Vector3(dir.x, 0f, dir.y);
                Vector2 own = OwnRenderPos();
                line.position = new Vector3(own.x, 0.05f, own.y) + d3 * (range / 2f + BrawlerRadius);
                line.rotation = Quaternion.LookRotation(d3);
                line.localScale = new Vector3(superAim && i == 0 ? 0.45f : 0.3f, 0.02f, range);
            }
        }
    }
}
