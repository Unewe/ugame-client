// Буфер снапшотов и часы отрисовки чужих сущностей. Чужих бойцов и снаряды показываем
// с задержкой InterpDelayTicks, интерполируя между двумя снапшотами: так движение плавное, даже
// если пакеты приходят неровно.
using System.Collections.Generic;
using UGame.Net;
using UnityEngine;

namespace UGame.Game
{
    partial class MatchView
    {
        // 100 мс при 30 Гц; должно совпадать с lagcomp.interp-delay-ticks в game-server.properties
        const float InterpDelayTicks = 3f;
        const int MaxSnapshots = 40;

        readonly List<Snapshot> buffer = new List<Snapshot>();
        double renderTick;      // какой тик сервера сейчас на экране (дробный)
        bool clockStarted;
        double latestArrival;   // Time.timeAsDouble прихода Latest
        // здоровье бойцов из ПОСЛЕДНЕГО снапшота (без задержки интерполяции)
        readonly Dictionary<ushort, float> latestHp = new Dictionary<ushort, float>();

        /// <summary>Кладёт снапшот в буфер. false — старый или дубль.</summary>
        bool AddSnapshot(Snapshot s)
        {
            if (Latest != null && s.Tick <= Latest.Tick) return false;
            buffer.Add(s);
            if (buffer.Count > MaxSnapshots) buffer.RemoveAt(0);
            Latest = s;
            latestArrival = Time.timeAsDouble;
            latestHp.Clear();
            foreach (var e in s.Entities)
                if (e.Kind == EntityKind.Brawler) latestHp[e.Id] = e.Hp01;

            if (!clockStarted)
            {
                renderTick = s.Tick - InterpDelayTicks;
                clockStarted = true;
            }
            return true;
        }

        /// <summary>
        /// Время отрисовки чужих сущностей. Цель — «сейчас на сервере» минус задержка интерполяции;
        /// «сейчас» = тик последнего снапшота + сколько прошло с его прихода. Без этой поправки цель
        /// прыгала бы на тик с каждым снапшотом, и чужие бойцы ехали бы рывками (то быстрее, то медленнее).
        /// Подстраиваемся скоростью хода часов (±10 %), а не сдвигом, — так движение остаётся плавным.
        /// </summary>
        void AdvanceClock(float dt)
        {
            if (!clockStarted || Latest == null) return;
            double serverNow = Latest.Tick + (Time.timeAsDouble - latestArrival) * tickRate;
            double target = serverNow - InterpDelayTicks;
            double diff = target - renderTick;
            if (diff > 10 || diff < -10) renderTick = target;               // сильно отстали/убежали
            else renderTick += dt * tickRate * (1.0 + System.Math.Clamp(diff * 0.1, -0.1, 0.1));
            if (renderTick > Latest.Tick) renderTick = Latest.Tick;        // без экстраполяции
        }

        /// <summary>
        /// Тик, который сейчас на экране, — уходит в каждой команде: по нему сервер проверяет выстрел
        /// ровно по тем позициям врагов, что видел игрок (младшие 16 бит + дробь в 1/256).
        /// </summary>
        void GetViewTick(out ushort tick, out byte frac)
        {
            double view = clockStarted ? System.Math.Max(0.0, renderTick) : 0.0;
            long whole = (long)System.Math.Floor(view);
            tick = (ushort)(whole & 0xFFFF);
            frac = (byte)Mathf.Clamp((int)((view - whole) * 256.0), 0, 255);
        }

        /// <summary>Два снапшота вокруг renderTick и доля пути между ними (0..1).</summary>
        bool TryGetInterpolationPair(out Snapshot from, out Snapshot to, out float t)
        {
            from = to = null;
            t = 0f;
            if (buffer.Count == 0) return false;

            from = buffer[0];
            to = buffer[0];
            for (int i = 0; i < buffer.Count; i++)
            {
                if (buffer[i].Tick <= renderTick) from = buffer[i];
                else { to = buffer[i]; break; }
                to = from;
            }
            if (to.Tick > from.Tick) t = Mathf.Clamp01((float)((renderTick - from.Tick) / (to.Tick - from.Tick)));
            return true;
        }
    }
}
