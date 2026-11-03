// Здоровье и отклик на попадания. Здоровье показываем только по серверу, а чтобы урон «читался»,
// хотя приходит через пинг:
//   * при попадании своего снаряда на экране враг сразу вспыхивает (MatchView.Shots.cs);
//   * когда сервер прислал урон, отнятый кусок полоски остаётся белым и стекает;
//   * над бойцом всплывает цифра урона (рисуется в MatchView.Overlay.cs).
using System.Collections.Generic;
using UnityEngine;
using static UGame.Game.GameRules;

namespace UGame.Game
{
    partial class MatchView
    {
        const float HitFlashSec = 0.12f;
        const float PredictedFlashSec = 0.6f;      // после своей вспышки урон с сервера второй раз не мигает
        const float GhostHoldSec = 0.25f;          // «призрачный» кусок полоски сначала стоит на месте…
        const float GhostDrainPerSec = 1.2f;       // …потом стекает (доля полоски в секунду)
        const float DamageNumberSec = 0.8f;
        const float DamageNumberMergeSec = 0.15f;  // урон, пришедший почти сразу (веер супера), — одной цифрой
        const int MaxDamageNumbers = 32;
        static readonly Color HitFlashColor = Color.white;

        /// <summary>Всплывающая цифра урона над бойцом.</summary>
        sealed class DamageNumber
        {
            public ushort Id;
            public float Raw;         // урон в единицах здоровья (из снапшота, до округления)
            public float Born;        // Time.time
            public Vector3 WorldPos;  // где показать, если бойца уже не видно (умер)
            public bool ToUs;         // урон по нам или союзнику — красная
        }

        readonly List<DamageNumber> damageNumbers = new List<DamageNumber>();

        /// <summary>
        /// Новое здоровье с сервера; вызывается каждый кадр. Падение запускает «призрачную» полоску
        /// и цифру урона. Возвращает здоровье для полоски.
        /// </summary>
        float ApplyServerHp(View v, float serverHp)
        {
            float now = Time.time;
            if (v.ServerHp01 < 0f)
            {
                v.ServerHp01 = v.GhostHp01 = serverHp; // первое появление
                return serverHp;
            }
            if (serverHp < v.ServerHp01 - 0.001f)
            {
                v.GhostHp01 = Mathf.Max(v.GhostHp01, v.ServerHp01);
                v.GhostHoldUntil = now + GhostHoldSec;
                AddDamageNumber(v, (v.ServerHp01 - serverHp) * BrawlerMaxHp);
                if (now - v.LastPredictedHit > PredictedFlashSec) Flash(v); // своё попадание уже мигнуло
            }
            else if (serverHp > v.ServerHp01 + 0.001f)
            {
                v.GhostHp01 = serverHp; // возрождение
            }
            v.ServerHp01 = serverHp;

            if (v.GhostHp01 > serverHp && now >= v.GhostHoldUntil)
                v.GhostHp01 = Mathf.MoveTowards(v.GhostHp01, serverHp, GhostDrainPerSec * Time.deltaTime);
            if (v.GhostHp01 < serverHp) v.GhostHp01 = serverHp;
            return serverHp;
        }

        void AddDamageNumber(View v, float amount)
        {
            float now = Time.time;
            foreach (var n in damageNumbers)
            {
                if (n.Id == v.Id && now - n.Born < DamageNumberMergeSec)
                {
                    n.Raw += amount;
                    n.WorldPos = v.WorldPos;
                    return;
                }
            }
            if (damageNumbers.Count >= MaxDamageNumbers) damageNumbers.RemoveAt(0);
            damageNumbers.Add(new DamageNumber
            {
                Id = v.Id, Raw = amount, Born = now, WorldPos = v.WorldPos, ToUs = v.Team == myTeam,
            });
        }

        /// <summary>
        /// Здоровье в снапшоте квантовано по 1/255 (≈ 12 единиц), поэтому разница даёт 694 или 706 вместо 700.
        /// Урон в игре кратен 50 — округляем до 50 (малый остаток при добивании — до 10).
        /// </summary>
        static int ShownDamage(float raw)
        {
            float r50 = Mathf.Round(raw / 50f) * 50f;
            return Mathf.RoundToInt(r50 > 0f ? r50 : Mathf.Round(raw / 10f) * 10f);
        }

        static void Flash(View v) => v.FlashUntil = Time.time + HitFlashSec;

        static void UpdateHitFlash(View v)
        {
            bool flash = Time.time < v.FlashUntil;
            if (flash == v.Flashing) return;
            v.Flashing = flash;
            v.Body.GetComponent<Renderer>().material.color = flash ? HitFlashColor : v.BodyColor;
        }
    }
}
