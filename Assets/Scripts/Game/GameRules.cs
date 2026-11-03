// Правила игры — копия констант World.java и ShotType.java на сервере. От совпадения зависит
// предсказание: свой боец и свои выстрелы считаются на клиенте той же логикой, что на сервере.
// Меняете на сервере — меняйте здесь.
using UnityEngine;

namespace UGame.Game
{
    static class GameRules
    {
        public const float ArenaWidth = 20f;
        public const float ArenaHeight = 30f;

        public const float BrawlerRadius = 0.5f;
        public const float BrawlerSpeed = 5f;      // м/с
        public const float BrawlerMaxHp = 3000f;

        public const float AttackCooldownSec = 0.5f;

        public const int SuperShots = 5;           // снарядов в веере супера
        public const float SuperSpreadDeg = 40f;   // ширина веера

        public const float DodgeCooldownSec = 5f;
        public const float DashDurationSec = 0.2f;
        public const float DashDistance = 4f;

        public static readonly ShotSpec NormalShot = new ShotSpec(speed: 15f, range: 8f, radius: 0.15f);
        public static readonly ShotSpec SuperShot = new ShotSpec(speed: 18f, range: 9f, radius: 0.3f);

        public static ShotSpec Shot(bool isSuper) => isSuper ? SuperShot : NormalShot;

        /// <summary>Секунды → тики, с округлением как Math.round в Java (половина — вверх).</summary>
        public static int SecToTicks(float sec, int tickRate) => Mathf.FloorToInt(sec * tickRate + 0.5f);
    }

    /// <summary>Параметры снаряда (ShotType на сервере).</summary>
    readonly struct ShotSpec
    {
        public readonly float Speed;   // м/с
        public readonly float Range;   // м
        public readonly float Radius;  // м

        public ShotSpec(float speed, float range, float radius)
        {
            Speed = speed;
            Range = range;
            Radius = radius;
        }

        /// <summary>Расстояние между центрами снаряда и бойца, при котором это попадание.</summary>
        public float HitDistance => GameRules.BrawlerRadius + Radius;
    }
}
