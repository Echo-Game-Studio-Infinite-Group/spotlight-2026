// RuinsFireFlipbook —— 废墟氛围火专用的手写 URP flipbook 粒子片着色器
//
// 为什么自己写而不用 URP/Particles/Unlit：
//   · 本项目是 Deferred + 自研 NPR 管线，透明物不在 NPR 卡通光照里（NPR 光照 pass 跑在 401，
//     半透明在其后走 forward）。火必须"自洽"，所以这里走 Unlit + 黑体自发光，而不是受光粒子。
//   · flipbook 图集是本项目按"数据贴图"生成的（R=密度 / G=温度 / B=高温闪点掩码），
//     温度到颜色走黑体（Planck 轨迹近似）而不是美术手调渐变 —— 与业界"blackbody node"同源。
//   · 高速跑酷用（一闪而过 / 赛车路边精细度），所以成本压到最低：**一次贴图采样 + 一次深度采样**，
//     其余全部解析计算（黑体、软粒子、距离淡出、风弯曲、逐实例相位）。
//
// 关键设计（都对应 docs/技术/游戏特效与火焰调研.md §六）：
//   1) 单次采样、四通道打包：R=密度 G=温度 B=闪点 A=备用。省 samplers。
//   2) 逐实例相位去同步：相位由物体世界坐标哈希得到 —— 一份材质、零配置，
//      几十堆火共享一个 material，就能各自不同步（SRP Batcher 友好，不需要 MPB/实例数据）。
//   3) 圆柱朝向（billboard）可选：高速侧面掠过时保证火"总是正对着玩家"。
//   4) 风弯曲在顶点做：四边形的上半部按二次曲线顺风倒，形成"被气流拉扯"的剪影，零额外采样。
//   5) 软粒子 + 近相机淡出 + 距离淡出：贴着瓦砾不穿帮、贴脸不糊屏、远处不硬弹跳（LOD 切换靠它兜底）。
//   6) 黑体温度色：物理味道的暖色，且温度范围可调（废墟冷调里的唯一暖点）。

Shader "OurFunction/VFX/RuinsFireFlipbook"
{
    Properties
    {
        [Header(Sprite)]
        [MainTexture] _BaseMap("Flipbook (R=density G=temperature B=hotspot)", 2D) = "white" {}
        _BaseColor("Tint", Color) = (1,1,1,1)
        [HDR] _EmissionColor("Emission (HDR)", Color) = (1,0.55,0.18,1)
        _Brightness("HDR Brightness", Range(0,32)) = 4
        _Opacity("Opacity", Range(0,1)) = 1
        _DensityPower("Density Contrast", Range(0.25,6)) = 1.35

        [Header(Flipbook)]
        _FlipbookCols("Columns", Float) = 8
        _FlipbookRows("Rows", Float) = 8
        _Speed("Frames / second", Range(0,120)) = 26
        _RandomPhase("Per-instance Random Phase (0/1)", Range(0,1)) = 1
        _PhaseSeed("Phase Seed", Float) = 0

        [Header(Blackbody)]
        _ColorMode("Color Mode (0=Blackbody 1=Flat Tint)", Range(0,1)) = 0
        _KelvinLow("Kelvin at temp=0", Range(600,20000)) = 950
        _KelvinHigh("Kelvin at temp=1", Range(600,20000)) = 2400
        _Saturation("Kelvin Saturation Boost", Range(0.5,3)) = 1.35
        _HotspotBoost("Hotspot Boost", Range(0,8)) = 1.6

        [Header(Wind)]
        _WindStrength("Wind Bend", Range(0,4)) = 0.35
        _SwayAmp("Sway Amplitude", Range(0,1)) = 0.06
        _SwaySpeed("Sway Speed", Range(0,8)) = 1.7

        [Header(Soft Particles)]
        _SoftParticlesNear("Soft Near Fade", Float) = 0.0
        _SoftParticlesFar("Soft Far Fade", Float) = 2.0
        _CameraNearFade("Camera Near Fade", Float) = 2.5
        _CameraFarFade("Camera Far Fade", Float) = 1.0
        _FadeStart("LOD Fade Start (m)", Float) = 60
        _FadeEnd("LOD Fade End (m)", Float) = 110

        [Header(Blending)]
        [HideInInspector] _SrcBlend("__src", Float) = 1
        [HideInInspector] _DstBlend("__dst", Float) = 10
        [HideInInspector] _Cull("__cull", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }

        Pass
        {
            Name "RuinsFire"
            Tags { "LightMode" = "UniversalForwardOnly" }

            Blend[_SrcBlend][_DstBlend]
            ZWrite Off
            ZTest LEqual
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex FireVert
            #pragma fragment FireFrag
            #pragma shader_feature_local_fragment _PREMULTIPLY_ON
            #pragma shader_feature_local_vertex _BILLBOARD_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            // 全局风（由 RuinsFireWind.cs 通过 Shader.SetGlobalVector 写）。必须在 UnityPerMaterial 之外，
            // 否则 SRP Batcher 会把它锁在材质 CBUFFER 里，SetGlobal 不生效。
            float4 _RuinsFireWind;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                half4  _EmissionColor;
                half   _Brightness;
                half   _Opacity;
                half   _DensityPower;
                float  _FlipbookCols;
                float  _FlipbookRows;
                half   _Speed;
                half   _RandomPhase;
                half   _PhaseSeed;
                half   _ColorMode;
                half   _KelvinLow;
                half   _KelvinHigh;
                half   _Saturation;
                half   _HotspotBoost;
                half   _WindStrength;
                half   _SwayAmp;
                half   _SwaySpeed;
                half   _SoftParticlesNear;
                half   _SoftParticlesFar;
                half   _CameraNearFade;
                half   _CameraFarFade;
                half   _FadeStart;
                half   _FadeEnd;
                half   _SrcBlend;
                half   _DstBlend;
                half   _Cull;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float4 screenPos  : TEXCOORD1;
                half   phase      : TEXCOORD2;   // 每实例相位（帧偏移）
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            // ---------------------------------------------------------------
            // 黑体（Planck 轨迹近似，Tanner Helland 式的经典拟合，1000–40000K）
            // 火的温度区间（~900–2400K）落在这条轨迹的红→橙→黄段，比手调渐变"更对"，
            // 这也是业界用 blackbody node 给火灾/爆炸配色的原因。
            // ---------------------------------------------------------------
            half3 KelvinToRGB(half kelvin)
            {
                half t = clamp(kelvin, 1000.0h, 40000.0h) / 100.0h;
                half r, g, b;

                r = (t <= 66.0h) ? 255.0h : 329.698727446h * pow(max(t - 60.0h, 1e-3h), -0.1332047592h);
                g = (t <= 66.0h) ? (99.4708025861h * log(max(t, 1e-3h)) - 161.1195681661h)
                                 : (288.1221695283h * pow(max(t - 60.0h, 1e-3h), -0.0755148492h));
                b = (t >= 66.0h) ? 255.0h
                                : ((t <= 19.0h) ? 0.0h : (138.5177312231h * log(max(t - 10.0h, 1e-3h)) - 305.0447927307h));

                half3 c = saturate(half3(r, g, b) / 255.0h);

                // 提饱和：纯黑体的低色温偏"脏红"，艺术上把饱和度推一点更像火。
                half lum = dot(c, half3(0.2126h, 0.7152h, 0.0722h));
                c = saturate(lum + (c - lum) * _Saturation);
                return c;
            }

            // 逐实例相位：由世界位置哈希 → 一份材质、零配置，几十堆火自然不同步。
            half InstancePhase(float3 wp)
            {
                if (_RandomPhase < 0.5h) return _PhaseSeed;
                float h = dot(wp, float3(12.9898, 78.233, 37.719)) + _PhaseSeed * 7.13;
                return frac(sin(h) * 43758.5453);
            }

            Varyings FireVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 posOS = input.positionOS.xyz;
                float3 centerWS = TransformObjectToWorld(float3(0.0, 0.0, 0.0));

                // 物体 lossy scale：让 billboard 模式下也能用 transform 的缩放控制卡片大小
                float3x3 m = (float3x3)GetObjectToWorldMatrix();
                float sx = length(m[0]);
                float sy = length(m[1]);

                half ph = InstancePhase(centerWS);
                output.phase = ph;

                float bend = saturate(input.uv.y);
                float bend2 = bend * bend;

                float3 posWS;

                #if defined(_BILLBOARD_ON)
                    // 圆柱朝向（绕世界 Y）：高速侧面掠过时火始终正对玩家 → 剪影可读。
                    float3 toCam = _WorldSpaceCameraPos - centerWS;
                    toCam.y = 0.0;
                    float3 fwd = (length(toCam) > 1e-4) ? normalize(toCam) : float3(0, 0, 1);
                    float3 right = normalize(cross(float3(0, 1, 0), fwd));
                    float3 up = float3(0, 1, 0);

                    // 风：世界空间顺风倒（二次曲线），剪影被"拉扯"
                    float3 wind = _RuinsFireWind.xyz * _WindStrength * bend2;
                    float sway = sin(_Time.y * _SwaySpeed + ph * 6.2831853) * _SwayAmp * bend2;
                    // 逐实例朝向微扰：避免所有火完全平行
                    float2 rotSeed = float2(sin(ph * 12.3), cos(ph * 7.7)) * 0.35;

                    posWS = centerWS
                          + right * (posOS.x * sx + rotSeed.x * bend2)
                          + up    * (posOS.y * sy)
                          + wind
                          + right * sway;
                #else
                    posWS = TransformObjectToWorld(posOS);
                    posWS += _RuinsFireWind.xyz * _WindStrength * bend2
                           + float3(sin(_Time.y * _SwaySpeed + ph * 6.2831853) * _SwayAmp * bend2, 0, 0);
                #endif

                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.positionCS = TransformWorldToHClip(posWS);
                output.screenPos = ComputeScreenPos(output.positionCS);
                return output;
            }

            half4 FireFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // ---- flipbook 取帧（在整张图上按行列取 tile，再往内缩半个纹素防 mip 渗色）----
                float cols = max(_FlipbookCols, 1.0);
                float rows = max(_FlipbookRows, 1.0);
                float total = cols * rows;
                float tile = max(_Speed, 0.0) > 0.0
                           ? floor(_Time.y * _Speed + input.phase * total)
                           : 0.0;
                float f = fmod(tile, total);
                float col = fmod(f, cols);
                float row = floor(f / cols);

                float2 tileSize = float2(1.0 / cols, 1.0 / rows);
                // 图集每行从上到下，纹理 v=0 在底部 → 行偏移翻过来
                float2 tileOrigin = float2(col * tileSize.x, 1.0 - (row + 1.0) * tileSize.y);
                float2 uv = tileOrigin + saturate(input.uv) * tileSize;
                // 半纹素内缩（整图尺寸未知，用 tile 尺寸的极小比例近似即可，目的是挡 mip 相邻 tile 渗色）
                uv = clamp(uv, tileOrigin + tileSize * 0.002, tileOrigin + tileSize * 0.998);

                half4 sheet = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);

                half density = saturate(sheet.r);
                half temp    = saturate(sheet.g);
                half hotspot = saturate(sheet.b);

                density = pow(density, _DensityPower);

                // ---- 颜色 ----
                half3 rgb;
                if (_ColorMode < 0.5h)
                    rgb = KelvinToRGB(lerp(_KelvinLow, _KelvinHigh, temp));
                else
                    rgb = _EmissionColor.rgb;

                rgb *= _BaseColor.rgb;
                // 高温闪点（余烬"跳一下"的高光），加色上去而不是替换颜色
                rgb += rgb * hotspot * _HotspotBoost;

                // HDR 自发光：靠工程的 Bloom（阈值 1.0）把火"点亮"
                rgb *= (_ColorMode < 0.5h ? _Brightness : 1.0h) * _EmissionColor.a;

                // ---- 淡出三件套 ----
                half fade = 1.0h;

                // 软粒子：贴近几何时淡出，贴着瓦砾/断墙不出现硬边
                if (_SoftParticlesNear > 0.0h || _SoftParticlesFar > 0.0h)
                {
                    float2 suv = input.screenPos.xy / max(input.screenPos.w, 1e-5);
                    float rawDepth = SampleSceneDepth(suv);
                    float sceneZ = (unity_OrthoParams.w == 0.0)
                                 ? LinearEyeDepth(rawDepth, _ZBufferParams)
                                 : LinearDepthToEyeDepth(rawDepth);
                    float thisZ = LinearEyeDepth(input.screenPos.z / max(input.screenPos.w, 1e-5), _ZBufferParams);
                    fade *= saturate(_SoftParticlesFar * ((sceneZ - _SoftParticlesNear) - thisZ));
                }

                // 近相机淡出：贴脸不糊屏
                {
                    float thisZ = LinearEyeDepth(input.screenPos.z / max(input.screenPos.w, 1e-5), _ZBufferParams);
                    fade *= saturate((thisZ - _CameraNearFade) * _CameraFarFade);
                }

                // 距离淡出：远处平滑消失，LOD 切换/剔除不会"啪"地弹一下
                {
                    float3 wp = TransformObjectToWorld(float3(0, 0, 0));
                    float dist = distance(wp, _WorldSpaceCameraPos);
                    fade *= 1.0h - smoothstep(_FadeStart, _FadeEnd, dist);
                }

                half alpha = saturate(density * _Opacity * fade);

                half3 outRGB = rgb;
                #if defined(_PREMULTIPLY_ON)
                    outRGB *= alpha;
                #endif

                return half4(outRGB, alpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
