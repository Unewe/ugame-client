// Чужие снаряды — союзников и врагов. Сервер присылает не их позиции, а события: «выстрел»
// (точка, угол, тик) и «попадание» (тик). Полёт по прямой клиент считает сам, в той же шкале
// времени, что и бойцов (renderTick), поэтому снаряд и цель на экране всегда согласованы.
//
// События повторяются в снапшотах, пока клиент их не подтвердит, — потеря пакета их не теряет,
// а повторы узнаются по id. Выстрел, пришедший с опозданием, сразу появляется в нужной точке
// полёта: положение зависит только от тика. Конец дальности клиент считает сам; событие
// «попадание» приходит, только когда снаряд в кого-то попал.
using System.Collections.Generic;
using UGame.Net;
using UnityEngine;
using static UGame.Game.GameRules;

namespace UGame.Game
{
    partial class MatchView
    {
        // Запись о снаряде храним дольше его полёта (~16 тиков) и дольше, чем сервер повторяет
        // события (60 тиков): повтор «выстрела» не должен создать уже исчезнувший снаряд заново.
        const double RemoteShotForgetTicks = 90;

        sealed class RemoteShot
        {
            public uint SpawnTick;
            public Vector2 Origin, Dir;
            public bool IsSuper;
            public byte Team;
            public double HitTick = double.MaxValue; // попал в кого-то; MaxValue — летит до конца дальности
            public Transform Body;                   // null — ещё не вылетел на экране или уже исчез
        }

        readonly Dictionary<ushort, RemoteShot> remoteShots = new Dictionary<ushort, RemoteShot>();
        readonly List<ushort> forgottenShots = new List<ushort>();

        void ApplyShotEvents(Snapshot s)
        {
            foreach (var e in s.ShotSpawns)
            {
                if (e.Owner == myEntityId) continue; // свои рисуем сами (MatchView.Shots.cs)
                if (remoteShots.TryGetValue(e.Id, out var known))
                {
                    if (known.SpawnTick == e.Tick) continue; // повтор того же события
                    if (known.Body != null) Destroy(known.Body.gameObject); // id снаряда пошёл по кругу
                }
                remoteShots[e.Id] = new RemoteShot
                {
                    SpawnTick = e.Tick,
                    Origin = e.Origin,
                    Dir = new Vector2(Mathf.Cos(e.Angle), Mathf.Sin(e.Angle)),
                    IsSuper = e.IsSuper,
                    Team = e.Team,
                };
            }
            foreach (var hit in s.ShotHits)
                if (remoteShots.TryGetValue(hit.Id, out var shot)) shot.HitTick = hit.Tick;
        }

        void UpdateRemoteShots()
        {
            if (!clockStarted) return;
            forgottenShots.Clear();
            foreach (var kv in remoteShots)
            {
                var shot = kv.Value;
                // как на сервере: в конце тика выстрела снаряд уже пролетел один шаг
                double flightTicks = renderTick - shot.SpawnTick + 1;
                if (flightTicks > RemoteShotForgetTicks)
                {
                    forgottenShots.Add(kv.Key);
                    continue;
                }
                var spec = Shot(shot.IsSuper);
                float traveled = (float)(flightTicks * tickDt) * spec.Speed;
                Vector2 p = shot.Origin + shot.Dir * Mathf.Max(0f, traveled);
                bool visible = flightTicks > 0 && traveled < spec.Range && renderTick < shot.HitTick && InsideArena(p);
                if (visible)
                {
                    if (shot.Body == null)
                        shot.Body = CreateShotBody(transform, shot.IsSuper, shot.Team == myTeam ? AllyShotColor : EnemyShotColor);
                    shot.Body.position = new Vector3(p.x, ShotHeight, p.y);
                }
                else if (shot.Body != null)
                {
                    Destroy(shot.Body.gameObject);
                    shot.Body = null;
                }
            }
            foreach (var id in forgottenShots)
            {
                var shot = remoteShots[id];
                if (shot.Body != null) Destroy(shot.Body.gameObject);
                remoteShots.Remove(id);
            }
        }
    }
}
