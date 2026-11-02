// Сцена Game: сам матч (MatchView), счёт и таймер, экран итогов. Создаётся GameClient, когда
// сервер принял игрока в матч. Соединение живёт в GameClient, сюда приходят только события.
using UnityEngine;

namespace UGame.Game
{
    public sealed class GameController : MonoBehaviour
    {
        GameClient client;
        MatchView match;

        void Start()
        {
            client = GameClient.Instance;
            if (client.Join == null) { client.OpenMenu(); return; }

            match = MatchView.Create(client.Connection, client.Join);
            client.Match = match;
            // MatchStart мог прийти, пока грузилась сцена
            if (client.MatchStart != null) match.OnMatchStart(client.MatchStart);
            if (client.MatchEnd != null) match.OnMatchEnd();
        }

        void OnDestroy()
        {
            if (client != null && client.Match == match) client.Match = null;
        }

        // GameClient уже принял пакеты в этом кадре (DefaultExecutionOrder -100)
        void Update() => match?.Tick(Time.deltaTime);

        // ------------------------------------------------------------------ интерфейс

        void OnGUI()
        {
            if (match == null) return;
            Ui.Begin();
            DrawHud();
            if (client.MatchEnd != null) DrawResult();
        }

        void DrawHud()
        {
            match.DrawOverlay(Ui.Scale, Ui.NameTag);

            if (!match.Running && !match.Finished)
            {
                Ui.Message("Ожидание игроков…");
                return;
            }

            var s = match.Latest;
            if (s != null)
            {
                int mine = match.MyTeam == 0 ? s.Score0 : s.Score1;
                int theirs = match.MyTeam == 0 ? s.Score1 : s.Score0;
                var box = new Rect((Ui.W - 300) / 2, 10, 300, 70);
                GUI.Box(box, GUIContent.none);
                var center = new GUIStyle(Ui.Label) { alignment = TextAnchor.MiddleCenter, fontSize = 20, fontStyle = FontStyle.Bold };
                GUI.Label(new Rect(box.x, box.y + 4, box.width, 28), $"{s.TimeLeftSec / 60}:{s.TimeLeftSec % 60:00}", center);
                var prev = GUI.color;
                GUI.color = new Color(0.45f, 0.75f, 1f);
                GUI.Label(new Rect(box.x, box.y + 34, box.width / 2 - 10, 30), $"Наши  {mine}", new GUIStyle(center) { alignment = TextAnchor.MiddleRight });
                GUI.color = new Color(1f, 0.5f, 0.45f);
                GUI.Label(new Rect(box.x + box.width / 2 + 10, box.y + 34, box.width / 2 - 10, 30), $"{theirs}  Враги", new GUIStyle(center) { alignment = TextAnchor.MiddleLeft });
                GUI.color = prev;
            }

            if (match.Running && !match.MyAlive)
                GUI.Label(new Rect(0, Ui.H / 2 - 40, Ui.W, 60), "Возрождение…", Ui.Big);
        }

        void DrawResult()
        {
            var end = client.MatchEnd;
            string headline = end.WinnerTeam < 0 ? "Ничья"
                : end.WinnerTeam == match.MyTeam ? "Победа!" : "Поражение";

            Ui.BeginPanel(460, 160 + 30 * end.Results.Count);
            GUILayout.Label(headline, Ui.Big);
            GUILayout.Space(8);
            foreach (var r in end.Results)
            {
                bool ally = r.Team == match.MyTeam;
                var prev = GUI.color;
                GUI.color = ally ? new Color(0.55f, 0.8f, 1f) : new Color(1f, 0.6f, 0.55f);
                GUILayout.Label($"{match.NameOf(r.PlayerId)}    убийств: {r.Kills}    смертей: {r.Deaths}", Ui.Label);
                GUI.color = prev;
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("В лобби", GUILayout.Height(44))) client.LeaveMatch();
            GUILayout.EndArea();
        }
    }
}
