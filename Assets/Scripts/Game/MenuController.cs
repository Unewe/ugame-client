// Сцена Menu: выбор сервера, автоматический вход, лобби, регистрация, вход в аккаунт, поиск матча.
// Создаётся GameClient при загрузке сцены. Интерфейс на IMGUI.
//
// При запуске проверяем сервер (по умолчанию GameClient.DefaultHost). Если он не отвечает — по центру
// экрана выбор другого сервера. Затем входим сами: в сохранённый аккаунт или гостем (гость при первом
// запуске создаётся со сгенерированными именем и паролем).
using System;
using System.Threading.Tasks;
using UGame.Net;
using UnityEngine;

namespace UGame.Game
{
    public sealed class MenuController : MonoBehaviour
    {
        enum Page { CheckingHost, HostSelect, SigningIn, Login, Lobby, Register, Searching, Connecting }

        const float StatusPollSec = 1f;

        GameClient client;
        Page page = Page.CheckingHost;
        string error = "";
        bool busy;

        // формы
        string hostInput = "";
        string formUser = "", formEmail = "", formPassword = "";
        bool loginRequired; // сохранённый вход устарел — вместо «Назад» показываем «Играть как гость»

        // поиск матча
        float searchStartedAt, nextPollAt;
        bool pollInFlight;

        void Start()
        {
            client = GameClient.Instance;
            EnsureCamera();

            if (!string.IsNullOrEmpty(client.Notice))
            {
                error = client.Notice;
                client.Notice = null;
            }

            if (client.SignedIn)
            {
                // вернулись из матча: результат доходит до профиля через Redis Stream примерно за секунду
                page = Page.Lobby;
                _ = RefreshProfileTwice();
            }
            else _ = CheckHost();
        }

        static void EnsureCamera()
        {
            if (Camera.main != null) return;
            var cam = new GameObject("Main Camera").AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.16f, 0.24f);
        }

        void Update()
        {
            switch (page)
            {
                case Page.Searching:
                    if (!pollInFlight && Time.unscaledTime >= nextPollAt) _ = PollStatus();
                    break;
                case Page.Connecting:
                    // соединение оборвалось до входа в матч — GameClient оставил сообщение
                    if (!client.Connecting && client.Join == null)
                    {
                        error = client.Notice ?? "Не удалось подключиться к матчу";
                        client.Notice = null;
                        page = Page.Lobby;
                    }
                    break;
            }
        }

        void OnApplicationQuit()
        {
            if (page == Page.Searching) _ = client.Api.CancelQueue();
        }

        // ------------------------------------------------------------------ сервер и вход

        async Task CheckHost()
        {
            page = Page.CheckingHost;
            Debug.Log($"[UGame] Проверка сервера {client.Host}");
            if (await client.PingHost()) await SignIn();
            else
            {
                Debug.LogWarning($"[UGame] Сервер {client.Host} недоступен");
                ShowHostSelect($"Сервер {client.Host} недоступен");
            }
        }

        void ShowHostSelect(string message)
        {
            error = message;
            hostInput = client.Host;
            page = Page.HostSelect;
        }

        async Task ConnectToHost(string host)
        {
            busy = true;
            error = "";
            try
            {
                if (await client.TryHost(host)) await SignIn();
                else error = $"Сервер {GameClient.NormalizeHost(host)} недоступен";
            }
            finally { busy = false; }
        }

        async Task SignIn()
        {
            page = Page.SigningIn;
            try
            {
                await client.SignIn();
                Debug.Log($"[UGame] Вход выполнен: {client.Profile?.username} (гость: {client.IsGuest})");
                page = Page.Lobby;
            }
            catch (GameClient.LoginRequiredException e)
            {
                OpenLogin(e.Username, required: true);
                error = "Сессия истекла — войдите заново";
            }
            catch (ApiException e)
            {
                Debug.LogWarning($"[UGame] Вход не удался: {e.Code} {e.Message}");
                ShowHostSelect(e.Code == "network_error"
                    ? $"Сервер {client.Host} недоступен"
                    : "Не удалось войти: " + Describe(e));
            }
        }

        async Task RefreshProfileTwice()
        {
            await Run(client.RefreshProfile);
            await Task.Delay(1500);
            if (this != null && page == Page.Lobby) await Run(client.RefreshProfile);
        }

        /// <summary>Запуск действия с кнопки: блокировка интерфейса и разбор ошибок.</summary>
        async Task Run(Func<Task> action, Action onSuccess = null)
        {
            if (busy) return;
            busy = true;
            error = "";
            try
            {
                await action();
                onSuccess?.Invoke();
            }
            catch (ApiException e)
            {
                if (e.Code == "network_error") ShowHostSelect($"Связь с сервером {client.Host} потеряна");
                else error = Describe(e);
            }
            catch (Exception e)
            {
                error = e.Message;
                Debug.LogException(e);
            }
            finally { busy = false; }
        }

        void OpenLogin(string username, bool required)
        {
            formUser = username ?? "";
            formPassword = "";
            loginRequired = required;
            error = "";
            page = Page.Login;
        }

        void OpenRegister()
        {
            formUser = client.Profile?.username ?? "";
            formEmail = "";
            formPassword = "";
            error = "";
            page = Page.Register;
        }

        void SubmitLogin()
        {
            if (string.IsNullOrWhiteSpace(formUser) || string.IsNullOrEmpty(formPassword))
            {
                error = "Введите имя и пароль";
                return;
            }
            _ = Run(() => client.Login(formUser.Trim(), formPassword), () =>
            {
                formPassword = "";
                page = Page.Lobby;
            });
        }

        void SubmitRegister()
        {
            if (string.IsNullOrWhiteSpace(formUser) || formPassword.Length < 8)
            {
                error = "Укажите имя и пароль от 8 символов";
                return;
            }
            _ = Run(() => client.Register(formUser.Trim(), formEmail.Trim(), formPassword), () =>
            {
                formPassword = "";
                page = Page.Lobby;
            });
        }

        // ------------------------------------------------------------------ матчмейкинг

        void StartSearch() => _ = Run(async () =>
        {
            var status = await client.Api.Queue();
            if (status.IsMatched) { Connect(status); return; }
            page = Page.Searching;
            searchStartedAt = Time.unscaledTime;
            nextPollAt = Time.unscaledTime + StatusPollSec;
        });

        async Task PollStatus()
        {
            pollInFlight = true;
            try
            {
                var status = await client.Api.Status();
                if (page != Page.Searching) return; // пока ждали ответа, нажали «Отмена»
                if (status.IsMatched) Connect(status);
                else if (!status.IsQueued)
                {
                    error = "Поиск прерван";
                    page = Page.Lobby;
                }
            }
            catch (ApiException e)
            {
                if (e.Code == "network_error") ShowHostSelect($"Связь с сервером {client.Host} потеряна");
                else error = Describe(e);
            }
            finally
            {
                pollInFlight = false;
                nextPollAt = Time.unscaledTime + StatusPollSec;
            }
        }

        void CancelSearch()
        {
            page = Page.Lobby;
            _ = Run(() => client.Api.CancelQueue());
        }

        void Connect(MatchmakingStatus status)
        {
            page = Page.Connecting;
            client.ConnectToMatch(status); // после входа в матч GameClient сам откроет сцену Game
        }

        // ------------------------------------------------------------------ ошибки API

        static string Describe(ApiException e)
        {
            switch (e.Code)
            {
                case "network_error": return "Нет связи с сервером";
                case "invalid_credentials": return "Неверное имя или пароль";
                case "username_taken": return "Это имя уже занято";
                case "email_taken": return "Этот email уже зарегистрирован";
                case "already_registered": return "Аккаунт уже зарегистрирован";
                case "invalid_refresh_token": return "Сессия истекла, войдите заново";
                case "http_404": return "Сервер не поддерживает эту функцию — обновите meta-service";
                case "validation_failed":
                    return e.Fields != null && e.Fields.Length > 0
                        ? "Проверьте поля:\n" + string.Join("\n", e.Fields)
                        : "Некорректные данные";
                default: return string.IsNullOrEmpty(e.Message) ? e.Code : e.Message;
            }
        }

        // ------------------------------------------------------------------ интерфейс

        void OnGUI()
        {
            Ui.Begin();
            switch (page)
            {
                case Page.CheckingHost: Ui.Message("Подключение к серверу…"); break;
                case Page.SigningIn: Ui.Message("Вход…"); break;
                case Page.HostSelect: DrawHostSelect(); break;
                case Page.Login: DrawLogin(); break;
                case Page.Lobby: DrawLobby(); break;
                case Page.Register: DrawRegister(); break;
                case Page.Searching: DrawSearching(); break;
                case Page.Connecting: Ui.Message("Подключение к матчу…"); break;
            }
        }

        void ErrorLabel()
        {
            if (!string.IsNullOrEmpty(error)) GUILayout.Label(error, Ui.Error);
        }

        void DrawHostSelect()
        {
            Ui.BeginPanel(460, 340);
            GUILayout.Label("Выбор сервера", Ui.Title);
            GUILayout.Space(10);
            ErrorLabel();
            GUILayout.Label("Адрес сервера", Ui.Small);
            GUI.enabled = !busy;
            hostInput = GUILayout.TextField(hostInput, 200, GUILayout.Height(34));
            GUILayout.Space(10);
            if (GUILayout.Button(busy ? "Проверка…" : "Подключиться", GUILayout.Height(44)) || (Ui.EnterPressed && !busy))
                _ = ConnectToHost(hostInput);
            if (GUILayout.Button("Сервер по умолчанию", GUILayout.Height(34)))
            {
                hostInput = GameClient.DefaultHost;
                _ = ConnectToHost(hostInput);
            }
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.Label("Например: http://192.168.1.10:8080", Ui.Small);
            GUILayout.EndArea();
        }

        void DrawLobby()
        {
            var p = client.Profile;
            bool guest = client.IsGuest;
            Ui.BeginPanel(460, guest ? 500 : 440);
            GUILayout.Label(p?.username ?? client.Api.Tokens?.username ?? "", Ui.Title);
            if (guest)
                GUILayout.Label("Гостевой аккаунт. Зарегистрируйтесь, чтобы не потерять прогресс и играть с других устройств.",
                    new GUIStyle(Ui.Small) { normal = { textColor = new Color(1f, 0.85f, 0.4f) }, alignment = TextAnchor.MiddleCenter });
            GUILayout.Space(6);
            if (p != null)
            {
                GUILayout.Label($"Кубки: {p.trophies}", Ui.Label);
                GUILayout.Label($"Матчей: {p.matchesPlayed}    Побед: {p.wins}", Ui.Label);
            }
            GUILayout.Space(14);

            GUI.enabled = !busy;
            if (GUILayout.Button(busy ? "…" : "ИГРАТЬ", GUILayout.Height(64))) StartSearch();
            GUILayout.Space(8);
            if (guest)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Зарегистрироваться", GUILayout.Height(36))) OpenRegister();
                if (GUILayout.Button("Войти в аккаунт", GUILayout.Height(36))) OpenLogin("", required: false);
                GUILayout.EndHorizontal();
            }
            else if (GUILayout.Button("Выйти из аккаунта", GUILayout.Height(34)))
                _ = Run(client.Logout);
            GUI.enabled = true;

            ErrorLabel();
            GUILayout.FlexibleSpace();
            GUILayout.Label(TouchControls.Available
                ? "Левый джойстик — движение; правый — тяните, чтобы прицелиться, отпустите — выстрел; короткое касание — автоприцел. Жёлтый джойстик — супер, кнопка сверху — рывок"
                : "WASD — движение, мышь — прицел, ЛКМ или пробел — выстрел, E или ПКМ — супер, Shift — рывок", Ui.Small);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Сервер: " + client.Host, Ui.Small);
            GUI.enabled = !busy;
            if (GUILayout.Button("Сменить", GUILayout.Width(100), GUILayout.Height(26))) ShowHostSelect("");
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void DrawRegister()
        {
            Ui.BeginPanel(440, 470);
            GUILayout.Label("Регистрация", Ui.Title);
            GUILayout.Label("Кубки и статистика гостя сохранятся.", new GUIStyle(Ui.Small) { alignment = TextAnchor.MiddleCenter });
            GUILayout.Space(8);

            GUI.enabled = !busy;
            GUILayout.Label("Имя игрока (3–16 символов: латиница, цифры, _)", Ui.Small);
            formUser = GUILayout.TextField(formUser, 16, GUILayout.Height(32));
            GUILayout.Label("Email (необязательно)", Ui.Small);
            formEmail = GUILayout.TextField(formEmail, 254, GUILayout.Height(32));
            GUILayout.Label("Пароль (от 8 символов)", Ui.Small);
            formPassword = GUILayout.PasswordField(formPassword, '•', 72, GUILayout.Height(32));
            GUILayout.Space(12);
            if (GUILayout.Button(busy ? "…" : "Зарегистрироваться", GUILayout.Height(44)) || (Ui.EnterPressed && !busy))
                SubmitRegister();
            if (GUILayout.Button("Назад", GUILayout.Height(34)))
            {
                error = "";
                page = Page.Lobby;
            }
            GUI.enabled = true;
            ErrorLabel();
            GUILayout.EndArea();
        }

        void DrawLogin()
        {
            Ui.BeginPanel(440, 420);
            GUILayout.Label("Вход", Ui.Title);
            if (!loginRequired && client.IsGuest)
                GUILayout.Label("Гостевой аккаунт останется на этом устройстве: вернуться к нему можно, выйдя из аккаунта.",
                    new GUIStyle(Ui.Small) { alignment = TextAnchor.MiddleCenter });
            GUILayout.Space(8);

            GUI.enabled = !busy;
            GUILayout.Label("Имя игрока", Ui.Small);
            formUser = GUILayout.TextField(formUser, 16, GUILayout.Height(32));
            GUILayout.Label("Пароль", Ui.Small);
            formPassword = GUILayout.PasswordField(formPassword, '•', 72, GUILayout.Height(32));
            GUILayout.Space(12);
            if (GUILayout.Button(busy ? "…" : "Войти", GUILayout.Height(44)) || (Ui.EnterPressed && !busy))
                SubmitLogin();
            if (loginRequired)
            {
                if (GUILayout.Button("Играть как гость", GUILayout.Height(34)))
                    _ = Run(client.PlayAsGuest, () => page = Page.Lobby);
            }
            else if (GUILayout.Button("Назад", GUILayout.Height(34)))
            {
                error = "";
                page = Page.Lobby;
            }
            GUI.enabled = true;
            ErrorLabel();
            GUILayout.EndArea();
        }

        void DrawSearching()
        {
            Ui.BeginPanel(400, 220);
            int secs = Mathf.FloorToInt(Time.unscaledTime - searchStartedAt);
            GUILayout.Label("Поиск матча…", Ui.Title);
            GUILayout.Label($"{secs / 60:00}:{secs % 60:00}", Ui.Center);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Отмена", GUILayout.Height(44))) CancelSearch();
            ErrorLabel();
            GUILayout.EndArea();
        }
    }
}
