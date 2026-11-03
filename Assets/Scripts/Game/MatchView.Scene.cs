// Объекты на сцене: бойцы и снаряды из снапшотов, арена, камера. Всё строится из примитивов.
using System.Collections.Generic;
using UGame.Net;
using UnityEngine;
using static UGame.Game.GameRules;

namespace UGame.Game
{
    partial class MatchView
    {
        const float ShotHeight = 0.7f;   // снаряды летят на уровне «ствола»

        static readonly Color GroundColor = new Color(0.32f, 0.68f, 0.27f);
        static readonly Color BorderColor = new Color(0.18f, 0.45f, 0.16f);
        static readonly Color AllyColor = new Color(0.20f, 0.55f, 1.00f);
        static readonly Color EnemyColor = new Color(1.00f, 0.25f, 0.20f);
        static readonly Color AllyShotColor = new Color(0.45f, 0.95f, 1.00f);
        static readonly Color EnemyShotColor = new Color(1.00f, 0.60f, 0.10f);
        static readonly Color SuperShotColor = new Color(1.00f, 0.85f, 0.10f);
        static readonly Color SuperGlowColor = new Color(1.00f, 0.85f, 0.10f);
        static readonly Color DodgeRingColor = new Color(0.85f, 1.00f, 1.00f);
        static readonly Color GunColor = new Color(0.15f, 0.15f, 0.15f);

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

        /// <summary>Боец или снаряд на сцене.</summary>
        sealed class View
        {
            public GameObject Root;
            public EntityKind Kind;
            public ushort Id;
            public byte Team;
            public bool Seen;          // есть в текущем снапшоте; нет — объект удаляется
            public Vector3 WorldPos;
            public Transform Body, Gun, Ring, SuperGlow;
            public Color BodyColor;
            public bool Dodging;

            // здоровье и отклик на попадания — см. MatchView.Health.cs
            public float Hp01;
            public float ServerHp01 = -1f;        // здоровье из последнего снапшота; -1 — ещё не видели
            public float GhostHp01;               // до куда тянется белый «призрачный» кусок полоски
            public float GhostHoldUntil;          // Time.time, до которого он не стекает
            public float LastPredictedHit = -10f; // Time.time попадания своего снаряда на экране
            public float FlashUntil;              // Time.time, до которого тело подсвечено попаданием
            public bool Flashing;
        }

        readonly Dictionary<ushort, View> views = new Dictionary<ushort, View>();
        readonly Dictionary<ushort, EntityState> fromById = new Dictionary<ushort, EntityState>();
        readonly List<ushort> toRemove = new List<ushort>();
        Camera cam;
        Vector3 camTarget;

        // ------------------------------------------------------------------ сущности

        void UpdateViews()
        {
            if (!TryGetInterpolationPair(out var from, out var to, out float t)) return;

            fromById.Clear();
            foreach (var e in from.Entities) fromById[e.Id] = e;
            foreach (var v in views.Values) v.Seen = false;

            foreach (var e in to.Entities)
            {
                // свои снаряды рисуем по предсказанию (MatchView.Shots.cs), серверную копию — нет
                if (e.Kind == EntityKind.Projectile && e.Owner == myEntityId) continue;
                SyncView(e, t);
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

        void SyncView(EntityState e, float t)
        {
            if (!views.TryGetValue(e.Id, out var view))
            {
                view = CreateView(e);
                views[e.Id] = view;
            }
            view.Seen = true;
            view.Team = e.Team;

            bool isBrawler = e.Kind == EntityKind.Brawler;
            bool isOwn = isBrawler && e.Id == myEntityId;
            Vector2 pos;
            float aim;
            if (isOwn && predicting)
            {
                pos = OwnRenderPos();
                aim = myAim;
            }
            else
            {
                Interpolate(e, t, out pos, out aim);
            }
            view.WorldPos = new Vector3(pos.x, 0f, pos.y);
            view.Root.transform.SetPositionAndRotation(view.WorldPos,
                Quaternion.LookRotation(new Vector3(Mathf.Cos(aim), 0f, Mathf.Sin(aim))));
            if (!isBrawler) return;

            // Жив ли боец — по интерполированному снапшоту (в такт его позиции), а полоска
            // здоровья — по последнему, как у своего: иначе урон по врагам виден на 100 мс позже.
            bool aliveInView = e.Hp01 > 0f;
            if (view.Root.activeSelf != aliveInView) view.Root.SetActive(aliveInView);
            view.Hp01 = ApplyServerHp(view, latestHp.TryGetValue(e.Id, out float hp) ? hp : e.Hp01);
            ApplyBrawlerFlags(view, isOwn ? OwnFlags(e.Flags) : e.Flags);
            UpdateHitFlash(view);
        }

        void Interpolate(EntityState e, float t, out Vector2 pos, out float aim)
        {
            if (fromById.TryGetValue(e.Id, out var a))
            {
                pos = Vector2.Lerp(a.Pos, e.Pos, t);
                aim = Mathf.LerpAngle(a.Aim * Mathf.Rad2Deg, e.Aim * Mathf.Rad2Deg, t) * Mathf.Deg2Rad;
            }
            else
            {
                pos = e.Pos;
                aim = e.Aim;
            }
        }

        /// <summary>Флаги своего бойца — без задержки интерполяции, рывок — по предсказанию.</summary>
        EntityFlags OwnFlags(EntityFlags interpolated)
        {
            var flags = FindOwn(Latest)?.Flags ?? interpolated;
            if (predicting) flags = Dashing ? flags | EntityFlags.Dodging : flags & ~EntityFlags.Dodging;
            return flags;
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
            v.Body.localScale = dodging ? new Vector3(1.15f, 0.45f, 1.15f) : new Vector3(1f, 0.8f, 1f);
            v.Body.localPosition = new Vector3(0f, dodging ? 0.45f : 0.8f, 0f);
            if (v.Gun.gameObject.activeSelf == dodging) v.Gun.gameObject.SetActive(!dodging);
            v.Ring.GetComponent<Renderer>().material.color = dodging ? DodgeRingColor : v.Team == myTeam ? AllyColor : EnemyColor;
            if (v.SuperGlow.gameObject.activeSelf != superReady) v.SuperGlow.gameObject.SetActive(superReady);
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
                view.Ring = Primitive(PrimitiveType.Cylinder, root.transform, ally ? AllyColor : EnemyColor);
                view.Ring.localScale = new Vector3(1.5f, 0.02f, 1.5f);
                view.Ring.localPosition = new Vector3(0f, 0.02f, 0f);

                // свечение заряженного супера
                view.SuperGlow = NoShadow(Primitive(PrimitiveType.Cylinder, root.transform, SuperGlowColor));
                view.SuperGlow.localScale = new Vector3(2.1f, 0.01f, 2.1f);
                view.SuperGlow.localPosition = new Vector3(0f, 0.01f, 0f);
                view.SuperGlow.gameObject.SetActive(false);

                // тело — свой цвет у каждого персонажа
                view.BodyColor = BrawlerPalette[e.Id % BrawlerPalette.Length];
                view.Body = Primitive(PrimitiveType.Capsule, root.transform, view.BodyColor);
                view.Body.localScale = new Vector3(1f, 0.8f, 1f);
                view.Body.localPosition = new Vector3(0f, 0.8f, 0f);

                // «ствол» показывает направление прицела
                view.Gun = Primitive(PrimitiveType.Cube, root.transform, GunColor);
                view.Gun.localScale = new Vector3(0.22f, 0.22f, 0.8f);
                view.Gun.localPosition = new Vector3(0f, 0.9f, 0.6f);
            }
            else
            {
                bool superShot = (e.Flags & EntityFlags.SuperShot) != 0;
                var shot = CreateShotBody(root.transform, superShot, ally ? AllyShotColor : EnemyShotColor);
                shot.localPosition = new Vector3(0f, ShotHeight, 0f);
            }
            return view;
        }

        /// <summary>Шарик снаряда: и для серверных снарядов, и для своих предсказанных.</summary>
        static Transform CreateShotBody(Transform parent, bool isSuper, Color color)
        {
            var shot = NoShadow(Primitive(PrimitiveType.Sphere, parent, isSuper ? SuperShotColor : color));
            shot.localScale = Vector3.one * (isSuper ? 0.65f : 0.35f);
            return shot;
        }

        // ------------------------------------------------------------------ примитивы и арена

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

        static Transform NoShadow(Transform t)
        {
            t.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return t;
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
    }
}
