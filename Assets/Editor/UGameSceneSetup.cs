// Сцены клиента: Menu (меню, вход, лобби) и Game (матч).
//
// Создаются автоматически при первой загрузке редактора (или вручную: меню UGame → «Создать сцены»)
// и ставятся первыми в списке сцен сборки. В сценах только камера — экраны и логику
// GameClient добавляет сам при загрузке сцены.
//
// Кнопка Play всегда запускает игру со сцены Menu, какая бы сцена ни была открыта.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UGame.EditorTools
{
    [InitializeOnLoad]
    static class UGameSceneSetup
    {
        const string Dir = "Assets/Scenes";
        const string MenuPath = Dir + "/Menu.unity";
        const string GamePath = Dir + "/Game.unity";

        static UGameSceneSetup()
        {
            EditorApplication.delayCall += AutoSetup;
        }

        static void AutoSetup()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (!File.Exists(MenuPath) || !File.Exists(GamePath)) CreateScenes();
            else if (!BuildListOk()) UpdateBuildList();
            SetPlayModeStartScene();
        }

        [MenuItem("UGame/Создать сцены")]
        static void CreateScenes()
        {
            if (EditorApplication.isPlaying) return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Directory.CreateDirectory(Dir);

            if (!File.Exists(GamePath)) CreateScene(GamePath, new Color(0.18f, 0.35f, 0.2f));
            if (!File.Exists(MenuPath)) CreateScene(MenuPath, new Color(0.12f, 0.16f, 0.24f));
            AssetDatabase.Refresh();

            UpdateBuildList();
            SetPlayModeStartScene();
            EditorSceneManager.OpenScene(MenuPath);
            Debug.Log("[UGame] Созданы сцены Menu и Game, они добавлены в список сцен сборки");
        }

        static void CreateScene(string path, Color background)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = background;
            camGo.AddComponent<AudioListener>();
            camGo.transform.position = new Vector3(0f, 10f, -10f);
            camGo.transform.rotation = Quaternion.Euler(45f, 0f, 0f);
            EditorSceneManager.SaveScene(scene, path);
        }

        static bool BuildListOk()
        {
            var scenes = EditorBuildSettings.scenes;
            return scenes.Length >= 2
                   && scenes[0].path == MenuPath && scenes[0].enabled
                   && scenes[1].path == GamePath && scenes[1].enabled;
        }

        /// <summary>Menu — первая сцена сборки, Game — вторая; остальные сцены остаются, но выключаются.</summary>
        static void UpdateBuildList()
        {
            var list = new List<EditorBuildSettingsScene>
            {
                new EditorBuildSettingsScene(MenuPath, true),
                new EditorBuildSettingsScene(GamePath, true),
            };
            list.AddRange(EditorBuildSettings.scenes
                .Where(s => s.path != MenuPath && s.path != GamePath)
                .Select(s => new EditorBuildSettingsScene(s.path, false)));
            EditorBuildSettings.scenes = list.ToArray();
        }

        static void SetPlayModeStartScene()
        {
            var menu = AssetDatabase.LoadAssetAtPath<SceneAsset>(MenuPath);
            if (menu != null) EditorSceneManager.playModeStartScene = menu;
        }
    }
}
