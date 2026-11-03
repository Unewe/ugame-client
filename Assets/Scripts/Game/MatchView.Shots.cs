// Свои выстрелы. Снаряд появляется сразу при нажатии, а событие «выстрел» от сервера для своих
// снарядов пропускается (MatchView.RemoteShots.cs). Сервер проверяет попадания по тем позициям врагов, что игрок
// видел на экране (компенсация лагов), поэтому попадание на экране почти всегда совпадает
// с серверным. Выстрел, которого сервер не сделал (LastAttackSeq/LastSuperSeq в снапшоте), убирается.
using System.Collections.Generic;
using UGame.Net;
using UnityEngine;
using static UGame.Game.GameRules;

namespace UGame.Game
{
    partial class MatchView
    {
        const int MaxShots = 64;

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

        void SpawnSuperFan(ushort seq, float aim)
        {
            for (int i = 0; i < SuperShots; i++) // веер симметрично относительно прицела, как на сервере
            {
                float t = SuperShots == 1 ? 0f : (float)i / (SuperShots - 1) - 0.5f;
                SpawnShot(seq, true, aim + t * SuperSpreadDeg * Mathf.Deg2Rad);
            }
        }

        void SpawnShot(ushort seq, bool isSuper, float angle)
        {
            var dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            var shot = new PredictedShot
            {
                Seq = seq,
                IsSuper = isSuper,
                Origin = predicted + dir * Shot(isSuper).HitDistance, // как World.spawnProjectile
                Dir = dir,
                Body = CreateShotBody(transform, isSuper, AllyShotColor),
            };
            shot.Body.position = new Vector3(shot.Origin.x, ShotHeight, shot.Origin.y);
            shots.Add(shot);
        }

        /// <summary>
        /// Полёт предсказанных снарядов. Попадание здесь только косметическое (снаряд исчезает,
        /// враг вспыхивает), урон считает сервер — по тем же позициям врагов, что видны на экране.
        /// Проверяется отрезок полёта за кадр, обрезанный по дальности, — как World.stepProjectiles.
        /// </summary>
        void UpdateShots(float dt)
        {
            for (int i = shots.Count - 1; i >= 0; i--)
            {
                var s = shots[i];
                if (s.Body == null) continue; // ждёт подтверждения в ResolveShots
                var spec = Shot(s.IsSuper);
                Vector2 prev = s.Origin + s.Dir * Mathf.Min(spec.Speed * s.Age, spec.Range);
                s.Age += dt;
                float traveled = spec.Speed * s.Age;
                Vector2 p = s.Origin + s.Dir * traveled;
                Vector2 hitEnd = s.Origin + s.Dir * Mathf.Min(traveled, spec.Range);
                bool done = HitVisibleEnemy(prev, hitEnd, spec.HitDistance)
                            || traveled >= spec.Range || !InsideArena(p);
                if (done)
                {
                    Destroy(s.Body.gameObject);
                    s.Body = null;
                    if (s.Confirmed) shots.RemoveAt(i);
                    continue;
                }
                s.Body.position = new Vector3(p.x, ShotHeight, p.y);
            }
            if (shots.Count > MaxShots) // подтверждения не приходят (связь пропала) — не копим бесконечно
            {
                if (shots[0].Body != null) Destroy(shots[0].Body.gameObject);
                shots.RemoveAt(0);
            }
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

        /// <summary>
        /// Попал ли снаряд на отрезке ab во врага на экране. Если да — враг сразу вспыхивает. Здоровье
        /// не трогаем: оно только с сервера (ApplyServerHp), подтверждение придёт примерно через пинг.
        /// </summary>
        bool HitVisibleEnemy(Vector2 a, Vector2 b, float hitDist)
        {
            foreach (var v in views.Values)
            {
                if (v.Team == myTeam || v.Dodging || !v.Root.activeSelf) continue;
                if (SegmentDistSq(a, b, new Vector2(v.WorldPos.x, v.WorldPos.z)) > hitDist * hitDist) continue;
                v.LastPredictedHit = Time.time;
                Flash(v);
                return true;
            }
            return false;
        }

        static bool InsideArena(Vector2 p) => p.x >= 0 && p.y >= 0 && p.x <= ArenaWidth && p.y <= ArenaHeight;

        /// <summary>Квадрат расстояния от точки p до отрезка ab (как World.segmentDistSq).</summary>
        static float SegmentDistSq(Vector2 a, Vector2 b, Vector2 p)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
            return (a + ab * t - p).sqrMagnitude;
        }
    }
}
