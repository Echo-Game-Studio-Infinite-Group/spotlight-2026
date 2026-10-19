using System.IO;
using UnityEditor;
using UnityEngine;

namespace MapSkin
{
    /// <summary>
    /// NPR 母材质打 Lit tag（NPR 规范 §2.1 的唯一差异）。
    ///
    /// 为什么必须做：TA 母材质 <c>Assets/Shaders/NPR/NPRBase.shader</c> 的 SubShader 标签是
    /// <c>"UniversalMaterialType" = "Unlit"</c>，延迟管线下不进 GBuffer → NPR 光照拿不到反照率，
    /// 表面整片近黑（规范实测同机位 mean 0.043，Lit tag 版 0.323）。改成 "Lit" 即修好，其余一字不动。
    ///
    /// 口径：**就地改 shader 文件文本**（GUID 不变，所有引用自动跟随），不用 throwaway 克隆。
    /// 改前把原文件复制到 _bak/NPRBase.Unlit.shader.txt 备份（.txt 后缀 → Unity 不当 shader 导入）。
    /// 幂等：已是 Lit 时只打印现状，不重复备份。
    ///
    /// 菜单：MapSkin/① NPR 母材质打 Lit tag
    /// 批处理：-executeMethod MapSkin.NprLitTagFix.Run
    /// </summary>
    public static class NprLitTagFix
    {
        const string ShaderPath = "Assets/Shaders/NPR/NPRBase.shader";
        const string BakDir = "Assets/Shaders/NPR/_bak";
        const string BakPath = BakDir + "/NPRBase.Unlit.shader.txt";
        const string UnlitTag = "\"UniversalMaterialType\" = \"Unlit\"";
        const string LitTag = "\"UniversalMaterialType\" = \"Lit\"";

        [MenuItem("MapSkin/① NPR 母材质打 Lit tag")]
        public static void Run()
        {
            var full = Path.Combine(Directory.GetCurrentDirectory(), ShaderPath);
            if (!File.Exists(full))
            {
                Debug.LogError("[NprLitTag] 找不到 " + full);
                return;
            }
            var text = File.ReadAllText(full);

            bool hasUnlit = text.Contains(UnlitTag);
            bool hasLit = text.Contains(LitTag);
            if (hasLit && !hasUnlit)
            {
                Debug.Log("[NprLitTag] 已是 Lit tag，无需改动（幂等）。" + ShaderPath);
                return;
            }
            if (!hasUnlit)
            {
                Debug.LogError("[NprLitTag] 文件里既没有 Unlit 也没有 Lit tag，未按预期；不动它。" + ShaderPath);
                return;
            }

            // 备份（只在尚未备份时写；备份留的是"改前那份 Unlit 文本"）
            Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), BakDir));
            var bakFull = Path.Combine(Directory.GetCurrentDirectory(), BakPath);
            if (!File.Exists(bakFull))
            {
                File.WriteAllText(bakFull, text);
                Debug.Log("[NprLitTag] 原文件已备份 → " + BakPath);
            }
            else
            {
                Debug.Log("[NprLitTag] 备份已存在，不覆盖 → " + BakPath);
            }

            File.WriteAllText(full, text.Replace(UnlitTag, LitTag));
            AssetDatabase.ImportAsset(ShaderPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();

            var after = File.ReadAllText(full);
            Debug.Log($"[NprLitTag] 已就地改 {ShaderPath}: Unlit → Lit（GUID 不变）" +
                      $"；现含 Lit={after.Contains(LitTag)}、残留 Unlit={after.Contains(UnlitTag)}");
        }
    }
}
