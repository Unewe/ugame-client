// Настройки проекта, без которых клиент не работает с локальным сервером.
// Применяются автоматически при загрузке редактора и сохраняются в ProjectSettings.
using UnityEditor;
using UnityEngine;

namespace UGame.EditorTools
{
    [InitializeOnLoad]
    static class UGameProjectSetup
    {
        static UGameProjectSetup()
        {
            // Unity по умолчанию запрещает http:// — а локальный meta-service работает без TLS.
            // Для продакшена поставьте сервер за HTTPS и верните NotAllowed.
            if (PlayerSettings.insecureHttpOption != InsecureHttpOption.AlwaysAllowed)
            {
                PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
                Debug.Log("[UGame] Разрешены HTTP-запросы (Player Settings → Allow downloads over HTTP)");
            }

            // Чтобы клиент в неактивном окне продолжал играть (удобно для теста двумя клиентами)
            if (!PlayerSettings.runInBackground)
            {
                PlayerSettings.runInBackground = true;
                Debug.Log("[UGame] Включён Run In Background");
            }
        }
    }
}
