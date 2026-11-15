using UnityEditor;
public static class AddSceneToBuild
{
    public static void Add()
    {
        var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        if (scenes.FindIndex(s => s.path == "Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity") < 0)
        {
            scenes.Add(new EditorBuildSettingsScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
        UnityEngine.Debug.Log("[HST-BUILD] 场景已加入 Build Settings");
    }
}
