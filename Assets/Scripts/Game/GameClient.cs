// Сессия клиента — живёт всё время работы игры и переходит из сцены в сцену (DontDestroyOnLoad).
//
// Хранит сервер, аккаунт, профиль и соединение с матчем. Экраны — в контроллерах сцен:
// * сцена Menu → MenuController (сервер, вход, лобби, регистрация, поиск матча);
// * сцена Game → GameController (сам матч и итоги).
// Контроллеры создаются автоматически при загрузке сцены, в сценах их расставлять не нужно.
// Сцены создаёт Editor/UGameSceneSetup.cs.
//
// Вход: при первом запуске создаётся гостевой аккаунт со сгенерированными именем и паролем,
// зарегистрироваться (задать своё имя и пароль, сохранив прогресс) можно позже из лобби.
using System;
using System.Threading.Tasks;
using UGame.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UGame.Game
{
    [DefaultExecutionOrder(-100)] // принимает пакеты раньше, чем контроллеры сцен обновляют матч
    public sealed class GameClient : MonoBehaviour
    {
        public const string MenuScene = "Menu";
        public const string GameScene = "Game";
        public const string DefaultHost = "http://193.238.44.107:8888";

        const string PrefHost = "ugame.host";
        const float ConnectTimeoutSec = 10f;

        public static GameClient Instance { get; private set; }

        /// <summary>
        /// Сброс статики: в редакторе бывает выключена перезагрузка домена (Enter Play Mode Options),
        /// и тогда статические поля переживают выход из Play.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Instance = null;
            Ui.Reset();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null) return;
            var go = new GameObject("UGame Client");
            DontDestroyOnLoad(go);
            go.AddComponent<GameClient>();
        }

        // ------------------------------------------------------------------ состояние

        public string Host { get; private set; }
        public MetaApi Api { get; private set; }
        public AccountStore Accounts { get; private set; }
        public Profile Profile { get; private set; }
        public bool SignedIn => Api != null && Api.LoggedIn;
        public bool IsGuest => Api?.Tokens?.guest ?? true;

        // матч
        public GameConnection Connection { get; private set; }
        public JoinResult Join { get; private set; }
        public MatchStart MatchStart { get; private set; }
        public MatchEnd MatchEnd { get; private set; }
        public MatchView Match { get; set; }   // создаёт GameController в сцене Game
        public bool Connecting => Connection != null && Join == null;
        float connectStartedAt;

        /// <summary>Сообщение для меню после возврата из матча (ошибка, разрыв соединения).</summary>
        public string Notice { get; set; }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            Application.runInBackground = true; // второй клиент в фоне не должен «замерзать»
            Application.targetFrameRate = 120;
            if (Application.isMobilePlatform)
            {
                // играем только в альбомной ориентации, экран не гаснет
                Screen.autorotateToPortrait = false;
                Screen.autorotateToPortraitUpsideDown = false;
                Screen.autorotateToLandscapeLeft = true;
                Screen.autorotateToLandscapeRight = true;
                Screen.orientation = ScreenOrientation.AutoRotation;
                Screen.sleepTimeout = SleepTimeout.NeverSleep;
                Application.targetFrameRate = 60; // бережём батарею
            }

            UseHost(PlayerPrefs.GetString(PrefHost, DefaultHost));
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        // Первую сцену обрабатываем явно: sceneLoaded для неё приходит не всегда — объект создан
        // до её загрузки. Повторная обработка той же сцены отсекается сравнением с handledScene.
        void Start() => HandleScene(SceneManager.GetActiveScene());

        void OnDestroy()
        {
            if (Instance == this) SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        // ------------------------------------------------------------------ сцены

        Scene handledScene; // по умолчанию — недействительная сцена

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single) HandleScene(scene);
        }

        void HandleScene(Scene scene)
        {
            // сравниваем сами сцены: тип Scene.handle в Unity 6.6 сменился с int на SceneHandle
            if (!scene.IsValid() || scene == handledScene) return;
            handledScene = scene;
            Debug.Log($"[UGame] Сцена {scene.name}");

            if (scene.name == GameScene)
            {
                if (Join == null) { OpenMenu(); return; } // нажали Play в сцене Game — матча нет
                EnsureController<GameController>();
            }
            else if (scene.name == MenuScene) EnsureController<MenuController>();
            else if (Application.CanStreamedLevelBeLoaded(MenuScene)) SceneManager.LoadScene(MenuScene);
            else
            {
                // сцен ещё нет в Build Profiles — работаем в текущей сцене
                Debug.LogWarning("[UGame] Сцены Menu/Game не добавлены в сборку: меню UGame → «Создать сцены»");
                EnsureController<MenuController>();
            }
        }

        static void EnsureController<T>() where T : MonoBehaviour
        {
            if (FindAnyObjectByType<T>() == null) new GameObject(typeof(T).Name).AddComponent<T>();
        }

        public void OpenMenu() => SwitchScene<MenuController, GameController>(MenuScene);
        void OpenGame() => SwitchScene<GameController, MenuController>(GameScene);

        static void SwitchScene<TOpen, TClose>(string scene) where TOpen : MonoBehaviour where TClose : MonoBehaviour
        {
            if (Application.CanStreamedLevelBeLoaded(scene))
            {
                SceneManager.LoadScene(scene);
                return;
            }
            // запасной вариант без сцен в сборке: меняем контроллер в текущей сцене
            var old = FindAnyObjectByType<TClose>();
            if (old != null) Destroy(old.gameObject);
            EnsureController<TOpen>();
        }

        // ------------------------------------------------------------------ сервер

        public static string NormalizeHost(string host)
        {
            host = (host ?? "").Trim().TrimEnd('/');
            if (host.Length > 0 && !host.Contains("://")) host = "http://" + host;
            return host;
        }

        void UseHost(string host)
        {
            Host = NormalizeHost(host);
            Api = new MetaApi(Host);
            Api.TokensChanged += OnTokensChanged;
            Accounts = new AccountStore(Host);
            Profile = null;
        }

        /// <summary>Проверить сервер; если доступен — сделать текущим и запомнить.</summary>
        public async Task<bool> TryHost(string host)
        {
            host = NormalizeHost(host);
            if (string.IsNullOrEmpty(host)) return false;
            if (!await new MetaApi(host).Ping()) return false;
            if (host != Host) UseHost(host);
            PlayerPrefs.SetString(PrefHost, Host);
            PlayerPrefs.Save();
            return true;
        }

        public Task<bool> PingHost() => Api.Ping();

        // ------------------------------------------------------------------ аккаунт

        void OnTokensChanged(TokenResponse t)
        {
            // refresh-токен одноразовый — запоминаем каждый новый
            if (!t.guest && Accounts.UseAccount) Accounts.SaveAccount(t.username, t.refreshToken);
        }

        /// <summary>Нужно войти паролем: сохранённый вход в зарегистрированный аккаунт устарел.</summary>
        public sealed class LoginRequiredException : Exception
        {
            public readonly string Username;
            public LoginRequiredException(string username) : base("login_required") { Username = username; }
        }

        /// <summary>
        /// Автоматический вход: в сохранённый аккаунт, иначе в гостевой, а если его нет — создаём гостя.
        /// </summary>
        public async Task SignIn()
        {
            if (Accounts.UseAccount)
            {
                string token = Accounts.AccountRefreshToken;
                if (string.IsNullOrEmpty(token)) throw new LoginRequiredException(Accounts.AccountUsername);
                try { await Api.Resume(token); }
                catch (ApiException e) when (e.Code == "invalid_refresh_token")
                {
                    Accounts.ForgetAccountToken();
                    throw new LoginRequiredException(Accounts.AccountUsername);
                }
            }
            else await SignInAsGuest();
            await RefreshProfile();
        }

        async Task SignInAsGuest()
        {
            Accounts.UseAccount = false;
            if (Accounts.HasGuest)
            {
                try
                {
                    await Api.Login(Accounts.GuestUsername, Accounts.GuestPassword);
                    return;
                }
                // базу на сервере очистили — гостя больше нет, заводим нового
                catch (ApiException e) when (e.Code == "invalid_credentials") { Accounts.ClearGuest(); }
            }
            var g = await Api.Guest();
            Accounts.SaveGuest(g.username, g.password);
        }

        /// <summary>Вернуться в гостевой аккаунт (или создать его).</summary>
        public async Task PlayAsGuest()
        {
            await SignInAsGuest();
            await RefreshProfile();
        }

        /// <summary>Войти в зарегистрированный аккаунт. Гостевой остаётся на устройстве.</summary>
        public async Task Login(string username, string password)
        {
            bool wasAccount = Accounts.UseAccount;
            Accounts.UseAccount = true; // до запроса — чтобы OnTokensChanged сохранил refresh-токен
            try { await Api.Login(username, password); }
            catch
            {
                Accounts.UseAccount = wasAccount;
                throw;
            }
            await RefreshProfile();
        }

        /// <summary>Гость регистрируется: своё имя, email и пароль. Прогресс сохраняется.</summary>
        public async Task Register(string username, string email, string password)
        {
            bool wasAccount = Accounts.UseAccount;
            Accounts.UseAccount = true; // до запроса — чтобы OnTokensChanged сохранил refresh-токен
            try { await Api.Upgrade(username, email, password); }
            catch
            {
                Accounts.UseAccount = wasAccount;
                throw;
            }
            Accounts.ClearGuest(); // гостевого аккаунта больше нет — он стал этим
            await RefreshProfile();
        }

        /// <summary>Выйти из аккаунта и вернуться к гостю.</summary>
        public async Task Logout()
        {
            await Api.Logout();
            Accounts.ForgetAccountToken();
            Accounts.UseAccount = false;
            Profile = null;
            await PlayAsGuest();
        }

        public async Task RefreshProfile() => Profile = await Api.Me();

        // ------------------------------------------------------------------ матч

        public void ConnectToMatch(MatchmakingStatus status)
        {
            CloseMatch();
            var conn = new GameConnection();
            conn.JoinResultReceived += OnJoinResult;
            conn.MatchStarted += msg => { MatchStart = msg; Match?.OnMatchStart(msg); };
            conn.SnapshotReceived += s => Match?.OnSnapshot(s);
            conn.MatchEnded += msg => { MatchEnd = msg; Match?.OnMatchEnd(); };
            conn.Kicked += reason => AbortMatch("Сервер отключил: " + reason);
            conn.Disconnected += OnDisconnected;
            Connection = conn;
            connectStartedAt = Time.unscaledTime;
            conn.Connect(status.host, status.port, status.ticket);
        }

        void OnJoinResult(JoinResult r)
        {
            if (!r.Ok)
            {
                AbortMatch(r.Error == "invalid_ticket"
                    ? "Билет на матч недействителен — найдите матч заново"
                    : "Не удалось войти в матч: " + r.Error);
                return;
            }
            Join = r;
            OpenGame();
        }

        void OnDisconnected(string reason)
        {
            if (MatchEnd != null) { Connection = null; return; } // после матча сервер закрывает соединение сам
            AbortMatch(Join != null ? "Соединение с матчем потеряно" : "Не удалось подключиться к игровому серверу");
        }

        void AbortMatch(string message)
        {
            Notice = message;
            bool inGame = Join != null;
            CloseMatch();
            if (inGame) OpenMenu();
        }

        /// <summary>Выйти из матча (или с экрана итогов) в меню.</summary>
        public void LeaveMatch()
        {
            CloseMatch();
            OpenMenu();
        }

        public void CloseMatch()
        {
            var c = Connection;
            Connection = null;
            c?.Disconnect();
            Join = null;
            MatchStart = null;
            MatchEnd = null;
            Match = null; // сам объект уничтожится вместе со сценой Game
        }

        // ------------------------------------------------------------------ кадр

        void Update()
        {
            Connection?.Tick(); // приём пакетов → события
            if (Connecting && Time.unscaledTime - connectStartedAt > ConnectTimeoutSec)
                AbortMatch("Игровой сервер не отвечает");
            UpdateFps();
        }

        void LateUpdate() => Connection?.Flush(); // отправка накопленного за кадр

        void OnApplicationQuit() => Connection?.Disconnect();

        // ------------------------------------------------------------------ FPS и ping

        const float FpsWindowSec = 0.5f; // среднее за полсекунды, чтобы цифра не прыгала
        float fpsAccTime, fps;
        int fpsAccFrames;
        GUIStyle statsStyle;

        void UpdateFps()
        {
            fpsAccTime += Time.unscaledDeltaTime;
            fpsAccFrames++;
            if (fpsAccTime < FpsWindowSec) return;
            fps = fpsAccFrames / fpsAccTime;
            fpsAccTime = 0f;
            fpsAccFrames = 0;
        }

        void OnGUI()
        {
            GUI.depth = -100; // поверх экранов меню и матча
            Ui.Begin();
            if (statsStyle == null)
                statsStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperRight };

            Rect safe = UnityEngine.Device.Screen.safeArea; // не под вырезом камеры
            float right = safe.xMax / Ui.Scale - 10f;
            float top = (Screen.height - safe.yMax) / Ui.Scale + 6f;
            const float w = 120f, h = 20f;

            Color fpsColor = fps >= 50f ? new Color(0.5f, 1f, 0.5f) : fps >= 30f ? new Color(1f, 0.9f, 0.3f) : new Color(1f, 0.45f, 0.4f);
            Shadowed(new Rect(right - w, top, w, h), $"FPS {Mathf.RoundToInt(fps)}", fpsColor);

            if (Connection != null)
            {
                int ping = Connection.PingMs;
                Color pingColor = ping < 0 ? Color.white
                    : ping <= 80 ? new Color(0.5f, 1f, 0.5f)
                    : ping <= 150 ? new Color(1f, 0.9f, 0.3f)
                    : new Color(1f, 0.45f, 0.4f);
                Shadowed(new Rect(right - w, top + h, w, h), ping < 0 ? "Ping —" : $"Ping {ping} мс", pingColor);
            }
        }

        /// <summary>Текст с тенью — читается и на светлой траве, и на тёмных панелях.</summary>
        void Shadowed(Rect r, string text, Color color)
        {
            var prev = GUI.contentColor;
            GUI.contentColor = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, statsStyle);
            GUI.contentColor = color;
            GUI.Label(r, text, statsStyle);
            GUI.contentColor = prev;
        }
    }
}
