// Свой боец — client-side prediction. Каждую команду клиент сразу применяет к своему бойцу (Step),
// а со снапшотом берёт позицию сервера и заново применяет ещё не подтверждённые команды
// (reconciliation). Сервер выполняет команды по одной за тик тем же шагом (World.runCommand),
// поэтому поправок почти не бывает — только когда вмешался кто-то другой. Остаток расхождения
// плавно гасится (smoothOffset). Рывок предсказывается так же: сервер присылает своему игроку
// кулдаун и остаток рывка.
using System.Collections.Generic;
using UGame.Net;
using UnityEngine;
using static UGame.Game.GameRules;

namespace UGame.Game
{
    partial class MatchView
    {
        const int MaxPending = 120;

        /// <summary>Команда, которую сервер ещё не подтвердил, — повторяем её при сверке.</summary>
        struct Pending
        {
            public ushort Seq;
            public float Mx, My, Aim;
            public bool Dodge;
        }

        /// <summary>Состояние рывка своего бойца — как поля Brawler на сервере.</summary>
        struct DashState
        {
            public int Cooldown;   // тиков до готовности уклонения
            public int DashLeft;   // тиков рывка осталось
            public float Dx, Dy;
        }

        readonly List<Pending> pending = new List<Pending>();
        bool predicting;                               // есть позиция с сервера, предсказываем от неё
        Vector2 predicted, prevPredicted, smoothOffset;
        DashState dash;

        bool DodgeReady => dash.Cooldown == 0;
        float DodgeCooldownLeftSec => dash.Cooldown * tickDt;
        bool Dashing => dash.DashLeft > 0;

        /// <summary>Применить только что отправленную команду к своему бойцу.</summary>
        void PredictCommand(InputCommand cmd)
        {
            float mx = Quantize.I8ToAxis(cmd.MoveX), my = Quantize.I8ToAxis(cmd.MoveY);
            float aim = Quantize.U16ToAngle(cmd.Aim);
            bool dodge = (cmd.Buttons & Buttons.Dodge) != 0;
            prevPredicted = predicted;
            predicted = Step(predicted, ref dash, mx, my, aim, dodge);
            pending.Add(new Pending { Seq = cmd.Seq, Mx = mx, My = my, Aim = aim, Dodge = dodge });
            if (pending.Count > MaxPending) pending.RemoveAt(0);

            // выстрелы — с позиции после шага, как на сервере (сначала движение, потом стрельба)
            if ((cmd.Buttons & Buttons.Attack) != 0) SpawnShot(cmd.Seq, false, aim);
            if ((cmd.Buttons & Buttons.Super) != 0) SpawnSuperFan(cmd.Seq, aim);
        }

        /// <summary>Сверка со снапшотом: позиция сервера + повтор неподтверждённых команд.</summary>
        void Reconcile(Snapshot s)
        {
            ResolveShots(s);
            EntityState? own = FindOwn(s);
            if (own == null || own.Value.Hp01 <= 0f)
            {
                StopPredicting(s);
                return;
            }
            MyAlive = true;
            ConfirmSentSuper(s.AckInputSeq);
            DropAcknowledged(s.AckInputSeq);
            Vector2 pos = ReplayPending(own.Value.Pos, s);
            CorrectTo(pos, own.Value.Aim);
        }

        void StopPredicting(Snapshot s)
        {
            MyAlive = false;
            predicting = false;
            pending.Clear();
            ResetQueuedInput();
            dash = new DashState { Cooldown = s.DodgeCooldownTicks };
        }

        /// <summary>Команды, которые сервер уже применил, больше не нужны.</summary>
        void DropAcknowledged(ushort ack)
        {
            int acked = 0;
            while (acked < pending.Count && !Quantize.SeqGreater(pending[acked].Seq, ack)) acked++;
            if (acked > 0) pending.RemoveRange(0, acked);
        }

        /// <summary>Состояние сервера на момент снапшота + повтор неподтверждённых команд.</summary>
        Vector2 ReplayPending(Vector2 serverPos, Snapshot s)
        {
            var d = new DashState { Cooldown = s.DodgeCooldownTicks, DashLeft = s.DashTicksLeft };
            if (d.DashLeft > 0)
            {
                float a = Quantize.U16ToAngle(s.DashAngle);
                d.Dx = Mathf.Cos(a);
                d.Dy = Mathf.Sin(a);
            }
            Vector2 pos = serverPos;
            foreach (var p in pending) pos = Step(pos, ref d, p.Mx, p.My, p.Aim, p.Dodge);
            dash = d;
            return pos;
        }

        /// <summary>Перейти на исправленную позицию; разницу показываем не скачком, а гасим плавно.</summary>
        void CorrectTo(Vector2 pos, float serverAim)
        {
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
                myAim = serverAim;
            }
            predicted = pos;
            predicting = true;
        }

        void DecayCorrection(float dt)
        {
            smoothOffset = Vector2.Lerp(smoothOffset, Vector2.zero, 1f - Mathf.Exp(-12f * dt));
        }

        EntityState? FindOwn(Snapshot s)
        {
            foreach (var e in s.Entities)
                if (e.Id == myEntityId && e.Kind == EntityKind.Brawler) return e;
            return null;
        }

        /// <summary>Один шаг своего бойца — то же, что World.runCommand и World.moveBrawler на сервере.</summary>
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

        /// <summary>Где рисовать своего бойца: между двумя последними шагами + гаснущая поправка.</summary>
        Vector2 OwnRenderPos()
        {
            float t = Mathf.Clamp01(sendAcc / tickDt);
            return Vector2.Lerp(prevPredicted, predicted, t) + smoothOffset;
        }
    }
}
