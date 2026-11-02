// Сохранённые на устройстве данные входа — отдельно для каждого сервера (аккаунты на разных
// серверах разные).
//
// * Гость: сгенерированные сервером имя и пароль. Пароль хранится, потому что другого способа
//   вернуться в гостевой аккаунт нет. Он случайный и нигде больше не используется.
// * Зарегистрированный игрок: имя и refresh-токен (30 дней, продлевается при каждом входе).
//   Свой пароль игрока на устройстве не хранится — если токен истёк, просим войти заново.
using UnityEngine;

namespace UGame.Game
{
    public sealed class AccountStore
    {
        readonly string prefix;

        public AccountStore(string host) { prefix = "ugame.acc." + host.Trim().TrimEnd('/').ToLowerInvariant() + "."; }

        string Get(string key) => PlayerPrefs.GetString(prefix + key, "");
        void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(value)) PlayerPrefs.DeleteKey(prefix + key);
            else PlayerPrefs.SetString(prefix + key, value);
            PlayerPrefs.Save();
        }

        public string GuestUsername => Get("guest.user");
        public string GuestPassword => Get("guest.pass");
        public bool HasGuest => GuestUsername != "" && GuestPassword != "";

        public string AccountUsername => Get("account.user");
        public string AccountRefreshToken => Get("account.refresh");

        /// <summary>Последний вход был в зарегистрированный аккаунт (а не гостем).</summary>
        public bool UseAccount
        {
            get => Get("mode") == "account";
            set => Set("mode", value ? "account" : "guest");
        }

        public void SaveGuest(string username, string password)
        {
            Set("guest.user", username);
            Set("guest.pass", password);
        }

        public void ClearGuest() => SaveGuest(null, null);

        public void SaveAccount(string username, string refreshToken)
        {
            Set("account.user", username);
            Set("account.refresh", refreshToken);
        }

        public void ForgetAccountToken() => Set("account.refresh", null);
    }
}
