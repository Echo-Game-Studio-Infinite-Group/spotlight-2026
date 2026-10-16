using System.IO;
using UnityEditor;
using UnityEngine;

public static class WwiseEventBindingSetup
{
    private const string AssetPath =
        "Assets/Resources/Audio/WwiseEventBindings.asset";

    [MenuItem("超高速行者/音频/创建 Wwise 事件绑定资产")]
    public static void Ensure()
    {
        WwiseEventBindings bindings =
            AssetDatabase.LoadAssetAtPath<WwiseEventBindings>(AssetPath);
        if (bindings == null)
        {
            string directory = Path.GetDirectoryName(AssetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            bindings = ScriptableObject.CreateInstance<WwiseEventBindings>();
            AssetDatabase.CreateAsset(bindings, AssetPath);
            AssetDatabase.SaveAssets();
        }

        Selection.activeObject = bindings;
        EditorGUIUtility.PingObject(bindings);
    }
}
