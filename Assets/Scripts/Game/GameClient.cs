// Главный объект клиента: экраны (вход, регистрация, лобби, поиск, матч, итоги) и переходы между ними.
//
// Создаётся автоматически при запуске любой сцены — достаточно открыть проект и нажать Play.
// Интерфейс на IMGUI: ничего не нужно настраивать в редакторе. Для продакшена экраны стоит
// переделать на UI Toolkit/uGUI, логика переходов при этом останется той же.
using System;
using System.Threading.Tasks;
using UGame.Net;
using UnityEngine;

namespace UGame.Game
{
    public sealed class GameClient : MonoBehaviour
    {
        enum Page { Login, Lobby, Searching, Connecting, Match, Result }

        const string PrefServerUrl = "ugame.serverUrl";
        const string PrefUsername = "ugame.username";
        const float StatusPollSec = 1f;
        const float ConnectTimeoutSec = 10f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindAnyObjectByType<GameClient>() != null) return;
            var go = new GameObject("UGame Client");
            DontDestroyOnLoad(go);
            go.AddComponent<GameClient>();
        }

        Page screen = Page.Login;
        MetaApi api;
        Profile profile;
        string error = "";
        bool busy;

        // форма входа
        string serverUrl, username, password = "", email = "";
        bool registerMode;

        // поиск матча
        float searchStartedAt, nextPollAt;
        bool pollInFlight;

        // матч
        GameConnection conn;
        MatchView match;
        MatchEnd matchEnd;
        float connectStartedAt;
        bool joined;

        void Awake()
        {
            Application.runInBackground = true; // второй клиент в фоне не должен «замерзать»
            Application.targetFrameRate = 120;
            serverUrl = PlayerPrefs.GetString(PrefServerUrl, "http://localhost:8080");
            username = PlayerPrefs.GetString(PrefUsername, "");
        }

        // ------------------------------------------------------------------ кадр

        void Update()
        {
            conn?.Tick();               // приём пакетов → события
            if (match != null) match.Tick(Time.deltaTime);

            switch (screen)
            {
                case Page.Searching:
                    if (!pollInFlight && Time.unscaledTime >= nextPollAt) PollStatus();
                    break;
                case Page.Connecting:
                    if (Time.unscaledTime - connectStartedAt > ConnectTimeoutSec)
                        AbortMatch("Игровой сервер не отвечает");
                    break;
            }
        }

        void LateUpdate() => conn?.Flush(); // отправка накопленного за кадр

        void OnApplicationQuit()
        {
            conn?.Disconnect();
            if (screen == Page.Searching) _ = api?.CancelQueue();
        }

        // ------------------------------------------------------------------ вход и регистрация

        async void Submit()
        {
            if (busy) return;
            error = "";
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                error = "Введите имя и пароль";
                return;
            }
            busy = true;
            try
            {
                api = new MetaApi(serverUrl);
                if (registerMode) await api.Register(username.Trim(), email.Trim(), password);
                else await api.Login(username.Trim(), password);

                PlayerPrefs.SetString(PrefServerUrl, serverUrl);
                PlayerPrefs.SetString(PrefUsername, username.Trim());
                password = "";
                await LoadProfile();
                screen = Page.Lobby;
            }
            catch (ApiException e) { error = Describe(e); }
            catch (Exception e) { error = e.Message; Debug.LogException(e); }
            finally { busy = false; }
        }

        async void Logout()
        {
            busy = true;
            try { await api.Logout(); }
            finally
            {
                busy = false;
                profile = null;
                screen = Page.Login;
            }
        }

        async Task LoadProfile()
        {
            try { profile = await api.Me(); }
            catch (ApiException e) { error = Describe(e); }
        }

        // ------------------------------------------------------------------ матчмейкинг

        async void StartSearch()
        {
            if (busy) return;
            busy = true;
            error = "";
            try
            {
                var status = await api.Queue();
                if (status.IsMatched) { ConnectToMatch(status); return; }
                screen = Page.Searching;
                searchStartedAt = Time.unscaledTime;
                nextPollAt = Time.unscaledTime + StatusPollSec;
            }
            catch (ApiException e) { error = Describe(e); }
            finally { busy = false; }
        }

        async void PollStatus()
        {
            pollInFlight = true;
            try
            {
                var status = await api.Status();
                if (screen != Page.Searching) return; // пока ждали ответа, нажали «Отмена»
                if (status.IsMatched) ConnectToMatch(status);
                else if (!status.IsQueued)
                {
                    error = "Поиск прерван";
                    screen = Page.Lobby;
                }
            }
            catch (ApiException e) { error = Describe(e); }
            finally
            {
                pollInFlight = false;
                nextPollAt = Time.unscaledTime + StatusPollSec;
            }
        }

        async void CancelSearch()
        {
            screen = Page.Lobby;
            try { await api.CancelQueue(); }
            catch (ApiException e) { error = Describe(e); }
        }

        // ------------------------------------------------------------------ матч

        void ConnectToMatch(MatchmakingStatus status)
        {
            joined = false;
            matchEnd = null;
            conn = new GameConnection();
            conn.JoinResultReceived += OnJoinResult;
            conn.MatchStarted += msg => match?.OnMatchStart(msg);
            conn.SnapshotReceived += s => match?.OnSnapshot(s);
            conn.MatchEnded += OnMatchEnd;
            conn.Kicked += reason => AbortMatch("Сервер отключил: " + reason);
            conn.Disconnected += OnDisconnected;

            screen = Page.Connecting;
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
            joined = true;
            match = MatchView.Create(conn, r);
            screen = Page.Match;
        }

        void OnMatchEnd(MatchEnd msg)
        {
            matchEnd = msg;
            match?.OnMatchEnd();
            screen = Page.Result;
        }

        void OnDisconnected(string reason)
        {
            if (screen == Page.Result) { conn = null; return; } // после матча сервер закрывает соединение сам
            AbortMatch(joined ? "Соединение с матчем потеряно" : "Не удалось подключиться к игровому серверу");
        }

        void AbortMatch(string message)
        {
            error = message;
            CloseMatch();
            screen = Page.Lobby;
        }

        async void BackToLobby()
        {
            CloseMatch();
            screen = Page.Lobby;
            await LoadProfile();
            await Task.Delay(1500); // результат матча доходит до профиля через Redis Stream за ~1 с
            if (screen == Page.Lobby) await LoadProfile();
        }

        void CloseMatch()
        {
            var c = conn;
            conn = null;
            c?.Disconnect();
            if (match != null) Destroy(match.gameObject);
            match = null;
        }

        // ------------------------------------------------------------------ ошибки API

        static string Describe(ApiException e)
        {
            switch (e.Code)
            {
                case "network_error": return "Нет связи с сервером. Запущен ли meta-service?";
                case "invalid_credentials": return "Неверное имя или пароль";
                case "username_taken": return "Это имя уже занято";
                case "email_taken": return "Этот email уже зарегистрирован";
                case "invalid_refresh_token": return "Сессия истекла, войдите заново";
                case "validation_failed":
                    return e.Fields != null && e.Fields.Length > 0
                        ? "Проверьте поля:\n" + string.Join("\n", e.Fields)
                        : "Некорректные данные";
                default: return string.IsNullOrEmpty(e.Message) ? e.Code : e.Message;
            }
        }

        // ------------------------------------------------------------------ интерфейс (IMGUI)

        float scale;
        float W => Screen.width / scale;
        float H => Screen.height / scale;
        GUIStyle title, label, small, nameTag, big, errorStyle, panel;

        void InitStyles()
        {
            if (title != null) return;
            title = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            big = new GUIStyle(title) { fontSize = 44 };
            label = new GUIStyle(GUI.skin.label) { fontSize = 17, wordWrap = true };
            small = new GUIStyle(label) { fontSize = 14 };
            errorStyle = new GUIStyle(label) { normal = { textColor = new Color(1f, 0.45f, 0.4f) } };
            nameTag = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
            };
            GUI.skin.button.fontSize = 18;
            GUI.skin.textField.fontSize = 18;
            GUI.skin.toggle.fontSize = 16;
            panel = new GUIStyle(GUI.skin.box) { padding = new RectOffset(24, 24, 20, 20) };
        }

        void OnGUI()
        {
            scale = Mathf.Max(0.5f, Screen.height / 720f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            InitStyles();

            switch (screen)
            {
                case Page.Login: DrawLogin(); break;
                case Page.Lobby: DrawLobby(); break;
                case Page.Searching: DrawSearching(); break;
                case Page.Connecting: DrawMessage("Подключение к матчу…"); break;
                case Page.Match: DrawMatchHud(); break;
                case Page.Result: DrawMatchHud(); DrawResult(); break;
            }
        }

        void BeginPanel(float width, float height)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.97f);
            GUILayout.BeginArea(new Rect((W - width) / 2, (H - height) / 2, width, height), panel);
            GUI.color = Color.white;
        }

        void DrawLogin()
        {
            BeginPanel(440, registerMode ? 520 : 460);
            GUILayout.Label(registerMode ? "Регистрация" : "Вход", title);
            GUILayout.Space(12);

            GUI.enabled = !busy;
            GUILayout.Label("Сервер", small);
            serverUrl = GUILayout.TextField(serverUrl, GUILayout.Height(32));
            GUILayout.Label("Имя игрока", small);
            username = GUILayout.TextField(username, 16, GUILayout.Height(32));
            if (registerMode)
            {
                GUILayout.Label("Email (необязательно)", small);
                email = GUILayout.TextField(email, 254, GUILayout.Height(32));
            }
            GUILayout.Label(registerMode ? "Пароль (от 8 символов)" : "Пароль", small);
            password = GUILayout.PasswordField(password, '•', 72, GUILayout.Height(32));
            GUILayout.Space(14);

            bool enter = Event.current.type == EventType.KeyDown
                         && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if (GUILayout.Button(busy ? "…" : registerMode ? "Создать аккаунт" : "Войти", GUILayout.Height(44)) || (enter && !busy))
                Submit();
            if (GUILayout.Button(registerMode ? "Уже есть аккаунт — войти" : "Нет аккаунта — зарегистрироваться", GUILayout.Height(34)))
            {
                registerMode = !registerMode;
                error = "";
            }
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(error)) GUILayout.Label(error, errorStyle);
            GUILayout.EndArea();
        }

        void DrawLobby()
        {
            BeginPanel(440, 400);
            GUILayout.Label(profile != null ? profile.username : api?.Tokens?.username ?? "", title);
            GUILayout.Space(8);
            if (profile != null)
            {
                GUILayout.Label($"Кубки: {profile.trophies}", label);
                GUILayout.Label($"Матчей: {profile.matchesPlayed}    Побед: {profile.wins}", label);
            }
            GUILayout.Space(20);

            GUI.enabled = !busy;
            if (GUILayout.Button(busy ? "…" : "ИГРАТЬ", GUILayout.Height(64))) StartSearch();
            GUILayout.Space(8);
            if (GUILayout.Button("Выйти из аккаунта", GUILayout.Height(34))) Logout();
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(error)) GUILayout.Label(error, errorStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label("Управление: WASD — движение, мышь — прицел, ЛКМ или пробел — выстрел", small);
            GUILayout.EndArea();
        }

        void DrawSearching()
        {
            BeginPanel(400, 220);
            int secs = Mathf.FloorToInt(Time.unscaledTime - searchStartedAt);
            GUILayout.Label("Поиск матча…", title);
            GUILayout.Label($"{secs / 60:00}:{secs % 60:00}", new GUIStyle(label) { alignment = TextAnchor.MiddleCenter });
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Отмена", GUILayout.Height(44))) CancelSearch();
            if (!string.IsNullOrEmpty(error)) GUILayout.Label(error, errorStyle);
            GUILayout.EndArea();
        }

        void DrawMessage(string text)
        {
            BeginPanel(400, 120);
            GUILayout.FlexibleSpace();
            GUILayout.Label(text, title);
            GUILayout.FlexibleSpace();
            GUILayout.EndArea();
        }

        void DrawMatchHud()
        {
            if (match == null) return;
            match.DrawOverlay(scale, nameTag);

            if (!match.Running && !match.Finished)
            {
                DrawMessage("Ожидание игроков…");
                return;
            }

            var s = match.Latest;
            if (s != null)
            {
                int mine = match.MyTeam == 0 ? s.Score0 : s.Score1;
                int theirs = match.MyTeam == 0 ? s.Score1 : s.Score0;
                var box = new Rect((W - 300) / 2, 10, 300, 70);
                GUI.Box(box, GUIContent.none);
                var center = new GUIStyle(label) { alignment = TextAnchor.MiddleCenter, fontSize = 20, fontStyle = FontStyle.Bold };
                GUI.Label(new Rect(box.x, box.y + 4, box.width, 28), $"{s.TimeLeftSec / 60}:{s.TimeLeftSec % 60:00}", center);
                var prev = GUI.color;
                GUI.color = new Color(0.45f, 0.75f, 1f);
                GUI.Label(new Rect(box.x, box.y + 34, box.width / 2 - 10, 30), $"Наши  {mine}", new GUIStyle(center) { alignment = TextAnchor.MiddleRight });
                GUI.color = new Color(1f, 0.5f, 0.45f);
                GUI.Label(new Rect(box.x + box.width / 2 + 10, box.y + 34, box.width / 2 - 10, 30), $"{theirs}  Враги", new GUIStyle(center) { alignment = TextAnchor.MiddleLeft });
                GUI.color = prev;
            }

            if (match.Running && !match.MyAlive)
                GUI.Label(new Rect(0, H / 2 - 40, W, 60), "Возрождение…", big);
        }

        void DrawResult()
        {
            if (matchEnd == null) return;
            string headline = matchEnd.WinnerTeam < 0 ? "Ничья"
                : matchEnd.WinnerTeam == match?.MyTeam ? "Победа!" : "Поражение";

            BeginPanel(460, 160 + 30 * matchEnd.Results.Count);
            GUILayout.Label(headline, big);
            GUILayout.Space(8);
            foreach (var r in matchEnd.Results)
            {
                string who = match != null ? match.NameOf(r.PlayerId) : r.PlayerId.ToString();
                bool ally = match != null && r.Team == match.MyTeam;
                var prev = GUI.color;
                GUI.color = ally ? new Color(0.55f, 0.8f, 1f) : new Color(1f, 0.6f, 0.55f);
                GUILayout.Label($"{who}    убийств: {r.Kills}    смертей: {r.Deaths}", label);
                GUI.color = prev;
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("В лобби", GUILayout.Height(44))) BackToLobby();
            GUILayout.EndArea();
        }
    }
}
