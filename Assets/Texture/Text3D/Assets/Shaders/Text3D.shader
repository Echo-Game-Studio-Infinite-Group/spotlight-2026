// 伪 3D 文字：把 TMP 的 SDF 图集当作二维距离场，沿视线在 _Depth 范围内步进，
// 用命中深度做明暗，得到字体被挤出（挤向视线深处）的立体感。
//
// 关键约束：图集里每个字形只在自己的单元格（字形矩形 + padding）内有效，单元格之外就是别的字形。
// 所以步进的采样点必须始终留在当前片元所属的单元格内，并且"物体空间位移 -> 图集 UV"的换算
// 必须使用单元格的尺寸，不能使用 TMP 的 SDF 缩放值（TEXCOORD1.y），否则采样点会飞出几十个字形。
Shader "GameJam/Flow/Text3D"
{
    Properties
    {
        _Right ("Right Border", float) = 0.5
        _Depth ("Depth", float) = 0.1
        _Color ("Color", Color) = (1,1,1,1)
        _MaxSteps ("Max Steps", int) = 30
        _MaxShade ("Max Shade", Range(0, 1)) = 0.8
        _MinShade ("Min Shade", Range(0, 1)) = 0.5
        _Outline ("Outline", float) = 0.5
        _Epsilon ("Epsilon", float) = 0.03
        _MainTex ("Texture", 2D) = "white" {}
        [HideInInspector] _FlowTextBounds ("Text Bounds", Vector) = (0,20,-2,2)
        [HideInInspector] _FlowTextUseBounds ("Use Text Bounds", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }

        Cull Off
        ZWrite On
        Blend Off

        Pass
        {
            HLSLPROGRAM

            // 需要像素着色器导数（ddx/ddy）来还原物体空间到图集 UV 的映射
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 uv       : TEXCOORD0;
                float2 uv1      : TEXCOORD1;
            };
    
            struct v2f
            {
                float4 vertex    : SV_POSITION;
                float4 color     : COLOR;
                float2 uv        : TEXCOORD0;   // 字形在图集里的 UV
                float3 localPos  : TEXCOORD1;   // 物体空间坐标
                float2 quadN     : TEXCOORD2;   // 片元在自身字形单元格内的归一化坐标
                float  quadValid : TEXCOORD3;   // quadN 是否可信
            };
            
            sampler2D _MainTex;
            int _MaxSteps;
            float _Right;
            float _Depth;
            float _Epsilon;
            float _Outline;
            float _MaxShade;
            float _MinShade;
            float4 _FlowTextBounds;
            float _FlowTextUseBounds;

            half4 _Color;

            // TMP 把字形单元格的归一化角点打包进 TEXCOORD1.x：
            //   packed = floor(n.x * 511) * 4096 + floor(n.y * 511)
            float2 UnpackQuadCoord(float packed)
            {
                float x = floor(packed / 4096.0 + 0.0001);
                float y = packed - x * 4096.0;
                return float2(x, y) / 511.0;
            }
    
            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv;
                o.localPos = v.vertex.xyz / v.vertex.w;

                // 默认的 Character 映射模式下四个角点分别是 (0,0) (0,1) (1,1) (1,0)，
                // 插值后就是片元在单元格内的归一化坐标。其它映射模式下解码结果不是角点，
                // 此时关闭越界判断（宁可放弃这层保护，也不要把有效像素裁掉）。
                o.quadN = UnpackQuadCoord(v.uv1.x);
                float2 nRounded = floor(o.quadN + 0.5);
                bool isCorner = abs(o.quadN.x - nRounded.x) < 0.001 && abs(o.quadN.y - nRounded.y) < 0.001;
                o.quadValid = isCorner ? 1.0 : 0.0;
                return o;
            }

            // 求物体空间位移 d 引起的量 P 的变化量。
            // dPdx/dPdy 是 P 在屏幕 x/y 方向的导数，dposDx/dposDy 是物体空间 xy 在同一方向的导数。
            // 同一个字形四边形内 P 与物体空间 xy 是仿射关系，所以这个比值就是精确的线性映射。
            float2 AffineDelta(float2 d, float2 dPdx, float2 dPdy, float2 dposDx, float2 dposDy)
            {
                float det = dposDx.x * dposDy.y - dposDx.y * dposDy.x;
                if (abs(det) < 1e-12)
                    return float2(0.0, 0.0);

                float invDet = 1.0 / det;
                // d = a * dposDx + b * dposDy
                float a = ( d.x * dposDy.y - d.y * dposDy.x) * invDet;
                float b = (-d.x * dposDx.y + d.y * dposDx.x) * invDet;
                return a * dPdx + b * dPdy;
            }

            half4 frag (v2f f) : SV_Target
            {   
                float3 fragLocal = f.localPos;
                float right = _FlowTextUseBounds > 0.5 ? lerp(_FlowTextBounds.x, _FlowTextBounds.y, saturate(_Right + 0.5)) : 20 * _Right;
                clip(right - fragLocal.x);

                // 整个步进循环共用的线性映射，这里只算一次。
                float2 dposDx = ddx(f.localPos.xy);
                float2 dposDy = ddy(f.localPos.xy);
                float2 duvDx  = ddx(f.uv);
                float2 duvDy  = ddy(f.uv);
                float2 dnDx   = ddx(f.quadN);
                float2 dnDy   = ddy(f.quadN);

                float3 camLocal = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos.xyz, 1)).xyz;
                float3 viewDir = normalize(fragLocal - camLocal);
                
                float result = -1;
                float3 p = fragLocal;

                // 固定步长：步长不会随位移增大而失控，采样点不会一步跨过整个单元格。
                float stepSize = _Depth / max((float)_MaxSteps, 1.0);

                [loop]
                for (int i = 0; i < _MaxSteps; i++)
                {
                    if (p.z > _Depth) break;

                    float2 drift = p.xy - fragLocal.xy;

                    // 采样点必须仍在当前字形自己的单元格内：图集里相邻单元格是别的字形，
                    // 越界后采到的 alpha 属于邻居，继续判定就会把邻近字符也渲染出来。
                    if (f.quadValid > 0.5)
                    {
                        float2 n = f.quadN + AffineDelta(drift, dnDx, dnDy, dposDx, dposDy);
                        if (n.x < -0.002 || n.x > 1.002 || n.y < -0.002 || n.y > 1.002)
                            break;
                    }

                    // 位移换算成图集 UV。必须用单元格的物体空间尺寸，不能用 uv1.y：
                    // uv1.y 是 TMP 给 SDF 用的屏幕缩放值（xScale），拿它当"每单位长度的 UV 偏移"
                    // 会让采样点飞出几十个字形远，这正是渲染出邻近字符的原因。
                    float2 uv = f.uv + AffineDelta(drift, duvDx, duvDy, dposDx, dposDy);
                    
                    float sdf = tex2D(_MainTex, uv).a;
                    float dist = (_Outline - sdf);

                    if (dist <= _Epsilon)
                    {
                        result = p.z;
                        break;
                    }
                    
                    p += viewDir * stepSize;
                }
                
                if (result < 0)
                {
                    discard;
                }

                half3 finalColor;

                if (result <= _Epsilon)
                {
                    finalColor = _Color.rgb * f.color.rgb;
                }
                else
                {
                    // 越深入（result 越大），颜色越深
                    float shade = (result) / _Depth * (_MaxShade - _MinShade) + _MinShade;
                    finalColor = _Color.rgb * f.color.rgb * shade;
                }

                return half4(finalColor, 1);
            }
    
            ENDHLSL
        }
    }
    FallBack "Diffuse"
}
