// HTTP-клиент мета-сервиса (Spring WebFlux): регистрация, вход, профиль, матчмейкинг.
using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace UGame.Net
{
    [Serializable] class RegisterRequest { public string username; public string email; public string password; }
    [Serializable] class LoginRequest { public string username; public string password; }
    [Serializable] class RefreshRequest { public string refreshToken; }

    [Serializable]
    public class TokenResponse
    {
        public string accessToken;
        public string refreshToken;
        public long expiresIn;
        public long playerId;
        public string username;
    }

    [Serializable]
    public class Profile
    {
        public long id;
        public string username;
        public int trophies;
        public int matchesPlayed;
        public int wins;
    }

    [Serializable]
    public class MatchmakingStatus
    {
        public string state;   // IDLE | QUEUED | MATCHED
        public string matchId;
        public string host;
        public int port;
        public string ticket;
        public int team;

        public bool IsMatched => state == "MATCHED";
        public bool IsQueued => state == "QUEUED";
    }

    [Serializable] class ApiError { public string code; public string detail; public string[] fields; }

    public sealed class ApiException : Exception
    {
        public readonly long Status;
        public readonly string Code;
        public readonly string[] Fields;

        public ApiException(long status, string code, string message, string[] fields = null) : base(message)
        {
            Status = status;
            Code = code;
            Fields = fields;
        }
    }

    public sealed class MetaApi
    {
        public string BaseUrl { get; }
        public TokenResponse Tokens { get; private set; }
        public bool LoggedIn => Tokens != null;

        public MetaApi(string baseUrl) { BaseUrl = baseUrl.Trim().TrimEnd('/'); }

        public async Task Register(string username, string email, string password) =>
            Tokens = await Send<TokenResponse>("POST", "/api/auth/register",
                new RegisterRequest { username = username, email = string.IsNullOrWhiteSpace(email) ? null : email, password = password },
                auth: false);

        public async Task Login(string username, string password) =>
            Tokens = await Send<TokenResponse>("POST", "/api/auth/login",
                new LoginRequest { username = username, password = password }, auth: false);

        public async Task Logout()
        {
            if (Tokens == null) return;
            var refresh = Tokens.refreshToken;
            Tokens = null;
            try { await Send<object>("POST", "/api/auth/logout", new RefreshRequest { refreshToken = refresh }, auth: false); }
            catch (ApiException) { /* токен и так забыт на клиенте */ }
        }

        public Task<Profile> Me() => Send<Profile>("GET", "/api/me", null);
        public Task<MatchmakingStatus> Queue() => Send<MatchmakingStatus>("POST", "/api/matchmaking/queue", null);
        public Task<MatchmakingStatus> CancelQueue() => Send<MatchmakingStatus>("DELETE", "/api/matchmaking/queue", null);
        public Task<MatchmakingStatus> Status() => Send<MatchmakingStatus>("GET", "/api/matchmaking/status", null);

        async Task<T> Send<T>(string method, string path, object body, bool auth = true)
        {
            try
            {
                return await SendOnce<T>(method, path, body, auth);
            }
            catch (ApiException e) when (auth && e.Status == 401 && Tokens?.refreshToken != null)
            {
                // access-токен живёт 15 минут — обновляем по refresh-токену и повторяем один раз
                Tokens = await SendOnce<TokenResponse>("POST", "/api/auth/refresh",
                    new RefreshRequest { refreshToken = Tokens.refreshToken }, auth: false);
                return await SendOnce<T>(method, path, body, auth);
            }
        }

        async Task<T> SendOnce<T>(string method, string path, object body, bool auth)
        {
            using var req = new UnityWebRequest(BaseUrl + path, method);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = 10;
            if (body != null)
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body)));
                req.SetRequestHeader("Content-Type", "application/json");
            }
            if (auth && Tokens != null)
                req.SetRequestHeader("Authorization", "Bearer " + Tokens.accessToken);

            var tcs = new TaskCompletionSource<bool>();
            req.SendWebRequest().completed += _ => tcs.TrySetResult(true);
            await tcs.Task;

            string text = req.downloadHandler.text;
            if (req.result != UnityWebRequest.Result.Success)
            {
                if (req.result == UnityWebRequest.Result.ConnectionError)
                    throw new ApiException(0, "network_error", req.error);

                ApiError err = null;
                try { if (!string.IsNullOrEmpty(text)) err = JsonUtility.FromJson<ApiError>(text); }
                catch (ArgumentException) { /* не JSON */ }
                throw new ApiException(req.responseCode, err?.code ?? "http_" + req.responseCode,
                    err?.detail ?? req.error, err?.fields);
            }
            return string.IsNullOrEmpty(text) ? default : JsonUtility.FromJson<T>(text);
        }
    }
}
