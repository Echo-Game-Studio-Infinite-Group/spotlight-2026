// PropMatTable.Generated.cs — 由 工业级渲染/scripts/props_race/export_delivery.py 生成，勿手改。
// 数值是 Blender 线性色基线；源材质里的程序细节（粗糙度噪声/边缘磨损/警示斜纹）不随 FBX 迁移，
// Unity 端按纯色基线建 URP/Lit 材质，procedural 槽由导入工具告警。
using System.Collections.Generic;
using UnityEngine;

namespace HST.EditorTools
{
    public static class PropMatTable
    {
        public struct Def
        {
            public Color baseColor;
            public float metallic;
            public float smoothness;
            public Color emission;
            public float emissionIntensity;
            public bool glass;
        }

        public static readonly Dictionary<string, Def> All = new Dictionary<string, Def>
        {
            { "AccentOrange", new Def { baseColor = new Color(0.42f, 0.13f, 0.02f, 1f), metallic = 0f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "AccentOrangeGlow", new Def { baseColor = new Color(0.05f, 0.02f, 0.01f, 1f), metallic = 0f, smoothness = 0.6f, emission = new Color(2.6f, 0.55f, 0.05f, 1f), emissionIntensity = 7f, glass = false } },
            { "B_Accent", new Def { baseColor = new Color(0.95f, 0.3f, 0.05f, 1f), metallic = 0.25f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "B_Bolt", new Def { baseColor = new Color(0.1f, 0.1f, 0.11f, 1f), metallic = 1f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "B_Cheek", new Def { baseColor = new Color(0.16f, 0.17f, 0.185f, 1f), metallic = 0.7f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "B_Dark", new Def { baseColor = new Color(0.025f, 0.026f, 0.03f, 1f), metallic = 0.35f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "B_Floor", new Def { baseColor = new Color(0.045f, 0.047f, 0.052f, 1f), metallic = 0.25f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "B_Glow", new Def { baseColor = new Color(0.05f, 0.55f, 0.95f, 1f), metallic = 0f, smoothness = 0.7f, emission = new Color(0.05f, 0.55f, 0.95f, 1f), emissionIntensity = 4.5f, glass = false } },
            { "B_Hull", new Def { baseColor = new Color(0.3f, 0.32f, 0.35f, 1f), metallic = 0.9f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "B_Red", new Def { baseColor = new Color(1.1f, 0.05f, 0.03f, 1f), metallic = 0f, smoothness = 0.7f, emission = new Color(1.1f, 0.05f, 0.03f, 1f), emissionIntensity = 4f, glass = false } },
            { "B_Tread", new Def { baseColor = new Color(0.075f, 0.078f, 0.085f, 1f), metallic = 0.55f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "DarkAnodized", new Def { baseColor = new Color(0.075f, 0.078f, 0.082f, 1f), metallic = 0.9f, smoothness = 0.58f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "DarkGlass", new Def { baseColor = new Color(0.008f, 0.01f, 0.012f, 1f), metallic = 0f, smoothness = 0.86f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "GlowCyan", new Def { baseColor = new Color(0.01f, 0.03f, 0.03f, 1f), metallic = 0f, smoothness = 0.7f, emission = new Color(0.12f, 2.4f, 2.7f, 1f), emissionIntensity = 9f, glass = false } },
            { "GlowRed", new Def { baseColor = new Color(0.04f, 0.01f, 0.01f, 1f), metallic = 0f, smoothness = 0.65f, emission = new Color(3f, 0.1f, 0.06f, 1f), emissionIntensity = 6f, glass = false } },
            { "HS_Bolt_Dark", new Def { baseColor = new Color(0.012f, 0.013f, 0.015f, 1f), metallic = 0.95f, smoothness = 0.66f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "HS_Glow_Amber", new Def { baseColor = new Color(0.02f, 0.02f, 0.02f, 1f), metallic = 0f, smoothness = 0.6f, emission = new Color(1f, 0.52f, 0.14f, 1f), emissionIntensity = 7f, glass = false } },
            { "HS_Glow_Cyan", new Def { baseColor = new Color(0.02f, 0.02f, 0.02f, 1f), metallic = 0f, smoothness = 0.6f, emission = new Color(0.08f, 0.48f, 1f, 1f), emissionIntensity = 3.5f, glass = false } },
            { "HS_Nosing_Amber", new Def { baseColor = new Color(0.52f, 0.18f, 0.02f, 1f), metallic = 0.5f, smoothness = 0.58f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "HS_Panel_Light", new Def { baseColor = new Color(0.36f, 0.365f, 0.35f, 1f), metallic = 0.25f, smoothness = 0.58f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "HS_Struct_Dark", new Def { baseColor = new Color(0.03f, 0.033f, 0.038f, 1f), metallic = 0.85f, smoothness = 0.54f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "HS_Tread_A", new Def { baseColor = new Color(0.145f, 0.152f, 0.16f, 1f), metallic = 0.55f, smoothness = 0.48f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "HS_Tread_B", new Def { baseColor = new Color(0.055f, 0.066f, 0.062f, 1f), metallic = 0.6f, smoothness = 0.52f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "M_Armor", new Def { baseColor = new Color(0.5f, 0.5f, 0.5f, 1f), metallic = 1f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "M_BoltSteel", new Def { baseColor = new Color(0.5f, 0.5f, 0.5f, 1f), metallic = 1f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "M_DarkRubber", new Def { baseColor = new Color(0.006f, 0.006f, 0.008f, 1f), metallic = 0.1f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "M_EmiCyan", new Def { baseColor = new Color(0.05f, 0.8f, 1.05f, 1f), metallic = 0f, smoothness = 0.7f, emission = new Color(0.05f, 0.8f, 1.05f, 1f), emissionIntensity = 10f, glass = false } },
            { "M_EmiHot", new Def { baseColor = new Color(0.05f, 0.85f, 1.1f, 1f), metallic = 0f, smoothness = 0.7f, emission = new Color(0.05f, 0.85f, 1.1f, 1f), emissionIntensity = 22f, glass = false } },
            { "M_EmiSoft", new Def { baseColor = new Color(0.3f, 0.9f, 1f, 1f), metallic = 0f, smoothness = 0.7f, emission = new Color(0.3f, 0.9f, 1f, 1f), emissionIntensity = 3.5f, glass = false } },
            { "M_Glass", new Def { baseColor = new Color(0.02f, 0.03f, 0.035f, 1f), metallic = 0f, smoothness = 0.93f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = true } },
            { "M_Hazard", new Def { baseColor = new Color(0.5f, 0.5f, 0.5f, 1f), metallic = 0f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "M_Orange", new Def { baseColor = new Color(0.5f, 0.5f, 0.5f, 1f), metallic = 0f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "M_Paint", new Def { baseColor = new Color(0.5f, 0.5f, 0.5f, 1f), metallic = 0f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "M_Prop_Accent", new Def { baseColor = new Color(0.52f, 0.058f, 0.012f, 1f), metallic = 0.1f, smoothness = 0.6f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "M_Prop_Cavity", new Def { baseColor = new Color(0.016f, 0.018f, 0.021f, 1f), metallic = 0.1f, smoothness = 0.22f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "M_Prop_EmitCyan", new Def { baseColor = new Color(0.18f, 0.9f, 1f, 1f), metallic = 0f, smoothness = 0.7f, emission = new Color(0.18f, 0.9f, 1f, 1f), emissionIntensity = 3.5f, glass = false } },
            { "M_Prop_Glass", new Def { baseColor = new Color(0.03f, 0.07f, 0.09f, 1f), metallic = 0.2f, smoothness = 0.84f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "M_Prop_Hazard", new Def { baseColor = new Color(0.78f, 0.33f, 0.03f, 1f), metallic = 0.2f, smoothness = 0.45f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "M_Prop_MetalDark", new Def { baseColor = new Color(0.085f, 0.098f, 0.115f, 1f), metallic = 0.78f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "M_Prop_MetalMid", new Def { baseColor = new Color(0.19f, 0.205f, 0.225f, 1f), metallic = 0.72f, smoothness = 0.48f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "M_Prop_PolymerDark", new Def { baseColor = new Color(0.105f, 0.112f, 0.122f, 1f), metallic = 0.08f, smoothness = 0.48f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "M_Prop_PolymerGrey", new Def { baseColor = new Color(0.33f, 0.345f, 0.365f, 1f), metallic = 0.06f, smoothness = 0.52f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "M_Prop_PolymerWhite", new Def { baseColor = new Color(0.585f, 0.6f, 0.618f, 1f), metallic = 0.04f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "M_Prop_Steel", new Def { baseColor = new Color(0.47f, 0.492f, 0.52f, 1f), metallic = 0.86f, smoothness = 0.46f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "RubberDark", new Def { baseColor = new Color(0.016f, 0.016f, 0.017f, 1f), metallic = 0f, smoothness = 0.12f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "STAIR_accent", new Def { baseColor = new Color(0.75f, 0.38f, 0.03f, 1f), metallic = 0.2f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "STAIR_bolt", new Def { baseColor = new Color(0.14f, 0.14f, 0.15f, 1f), metallic = 0.95f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "STAIR_glow", new Def { baseColor = new Color(0.2f, 0.2f, 0.2f, 1f), metallic = 0f, smoothness = 0.5f, emission = new Color(0.16f, 0.72f, 1f, 1f), emissionIntensity = 30f, glass = false } },
            { "STAIR_grating", new Def { baseColor = new Color(0.026f, 0.027f, 0.03f, 1f), metallic = 0.9f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "STAIR_hull", new Def { baseColor = new Color(0.028f, 0.03f, 0.035f, 1f), metallic = 0.9f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "STAIR_inset", new Def { baseColor = new Color(0.01f, 0.01f, 0.012f, 1f), metallic = 0.6f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "STAIR_panel", new Def { baseColor = new Color(0.04f, 0.05f, 0.065f, 1f), metallic = 0.55f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "ShellPolymer", new Def { baseColor = new Color(0.044f, 0.054f, 0.046f, 1f), metallic = 0f, smoothness = 0.5f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
            { "SteelPin", new Def { baseColor = new Color(0.52f, 0.53f, 0.55f, 1f), metallic = 1f, smoothness = 0.68f, emission = new Color(0f, 0f, 0f, 1f), emissionIntensity = 0f, glass = false } },
        };
    }
}
