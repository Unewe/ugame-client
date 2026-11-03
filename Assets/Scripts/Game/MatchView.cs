// Матч на клиенте: ввод игрока, предсказание и отрисовка. Сервер авторитарный: клиент шлёт только
// ввод (30 команд в секунду), сервер присылает снапшоты.
//
// Класс разбит на части по задачам:
//   MatchView.cs               — жизненный цикл и порядок работы за кадр (этот файл)
//   MatchView.Input.cs         — ввод: клавиатура, мышь, джойстики, отправка команд
//   MatchView.Prediction.cs    — свой боец: предсказание и сверка со снапшотом (reconciliation)
//   MatchView.Shots.cs         — свои выстрелы: сразу на экране, сверка с сервером
//   MatchView.RemoteShots.cs   — чужие снаряды: полёт по событиям «выстрел» и «попадание»
//   MatchView.Interpolation.cs — буфер снапшотов и часы отрисовки чужих (с задержкой ~100 мс)
//   MatchView.Scene.cs         — бойцы на сцене, арена, камера
//   MatchView.Health.cs        — здоровье: «призрачная» полоска, цифры урона, вспышки
//   MatchView.Overlay.cs       — интерфейс поверх сцены (OnGUI)
// Правила игры (скорости, радиусы, кулдауны) — в GameRules.cs, они должны совпадать с сервером.
using System.Collections.Generic;
using UGame.Net;
using UnityEngine;
using static UGame.Game.GameRules;

namespace UGame.Game
{
    public sealed partial class MatchView : MonoBehaviour
    {
        GameConnection conn;
        uint myEntityId;
        int myTeam;
        int tickRate;
        float tickDt;
        int dashTicks, dodgeCooldownTicks, attackCooldownTicks;
        float dashStep;   // м за тик рывка
        readonly Dictionary<uint, PlayerInfo> players = new Dictionary<uint, PlayerInfo>();

        public bool Running { get; private set; }
        public bool Finished { get; private set; }
        public int MyTeam => myTeam;
        public bool MyAlive { get; private set; }
        /// <summary>Последний снапшот (счёт, время, состояние своего бойца).</summary>
        public Snapshot Latest { get; private set; }

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
            view.dashTicks = Mathf.Max(1, SecToTicks(DashDurationSec, view.tickRate));
            view.dodgeCooldownTicks = SecToTicks(DodgeCooldownSec, view.tickRate);
            view.attackCooldownTicks = Mathf.Max(1, SecToTicks(AttackCooldownSec, view.tickRate));
            view.dashStep = DashDistance / view.dashTicks;
            view.BuildArena();
            view.SetupCamera();
            if (TouchControls.Available) view.touch = new TouchControls();
            return view;
        }

        public void OnMatchStart(MatchStart msg)
        {
            players.Clear();
            foreach (var p in msg.Players) players[p.EntityId] = p;
            Running = true;
        }

        public void OnMatchEnd()
        {
            Running = false;
            Finished = true;
        }

        public string NameOf(long playerId)
        {
            foreach (var p in players.Values)
                if (p.PlayerId == playerId) return p.Username;
            return "игрок " + playerId;
        }

        public void OnSnapshot(Snapshot s)
        {
            if (!AddSnapshot(s)) return; // старый или дубль (UDP)
            ApplyShotEvents(s);
            Reconcile(s);
        }

        /// <summary>Вызывается GameController каждый кадр, после того как GameClient принял пакеты.</summary>
        public void Tick(float dt)
        {
            ReadFrameInput();
            if (Running && MyAlive) SendInputs(dt);
            AdvanceClock(dt);
            DecayCorrection(dt);
            UpdateViews();
            UpdateRemoteShots();
            UpdateShots(dt);
            UpdateAimLine();
            UpdateCamera(dt);
        }
    }
}
