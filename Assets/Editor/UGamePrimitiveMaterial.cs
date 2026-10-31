// Материал для объектов, которые MatchView создаёт в коде (CreatePrimitive).
//
// Зачем: CreatePrimitive берёт материал по умолчанию из URP, но на его шейдер
// не ссылается ни одна сцена — и при сборке Unity вырезает шейдер. На телефоне
// всё становится фиолетовым. Материал в папке Resources попадает в сборку всегда,
// а вместе с ним и шейдер URP/Lit с нужными вариантами.
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UGame.EditorTools
{
    [InitializeOnLoad]
    sealed class UGamePrimitiveMaterial : IPreprocessBuildWithReport
    {
        const string Folder = "Assets/Resources/UGame";
        const string Path = Folder + "/PrimitiveLit.mat"; // = Resources.Load("UGame/PrimitiveLit")

        static UGamePrimitiveMaterial()
        {
            EditorApplication.delayCall += Ensure;
        }

        public int callbackOrder => 0;
        public void OnPreprocessBuild(BuildReport report) => Ensure();

        static void Ensure()
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(Path) != null) return;

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[UGame] Не найден шейдер Universal Render Pipeline/Lit");
                return;
            }
            if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/Resources", "UGame");

            var mat = new Material(shader) { name = "PrimitiveLit" };
            mat.SetFloat("_Smoothness", 0.2f);
            AssetDatabase.CreateAsset(mat, Path);
            AssetDatabase.SaveAssets();
            Debug.Log("[UGame] Создан материал " + Path);
        }
    }
}
