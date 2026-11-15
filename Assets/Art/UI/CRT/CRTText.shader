Shader "UI/CRT/Text"
{
 Properties
 {
  _MainTex("Texture",2D)="white"{}
  _ScanStrength("Scanline strength",Range(0,1))=.22
  _ScanCount("Scanline count",Float)=90
  _Glitch("Glitch",Range(0,1))=0
  _Clock("Unscaled clock",Float)=0
  _GlowRadius("Pixel glow radius",Range(0,48))=0
  _GlowStrength("Pixel glow strength",Range(0,2))=.65
  _GlowBreathPeriod("Glow breath period (seconds)",Range(.2,10))=3
  _GlowBreathDepth("Glow breath depth",Range(0,1))=0
  _GlowColor("Pixel glow color",Color)=(.45,.07,.10,1)
 }
 SubShader
 {
  Tags {"Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True"}
  Cull Off ZWrite Off ZTest [unity_GUIZTestMode] Blend SrcAlpha OneMinusSrcAlpha
  Pass
  {
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   #include "UnityUI.cginc"
   #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
   struct app {float4 vertex:POSITION;float2 uv:TEXCOORD0;float4 color:COLOR;};
   struct output {float4 vertex:SV_POSITION;float2 uv:TEXCOORD0;float4 color:COLOR;float4 worldPosition:TEXCOORD1;};
   float4 _ClipRect;
   sampler2D _MainTex;float4 _MainTex_TexelSize,_GlowColor;float _ScanStrength,_ScanCount,_Glitch,_Clock,_GlowRadius,_GlowStrength;
   float _CRTMenuUnscaledClock,_GlowBreathPeriod,_GlowBreathDepth;
   output vert(app v){output o;o.worldPosition=v.vertex;o.vertex=UnityObjectToClipPos(v.vertex);o.uv=v.uv;o.color=v.color;return o;}
   fixed4 frag(output i):SV_Target
   {
    float band=floor(i.uv.y*25);float n=frac(sin(band*73.7+floor(_Clock*18)*31.3)*43758.5);
    float2 uv=i.uv;uv.x+=(n-.5)*.13*_Glitch*step(.65,n);
    fixed4 c=tex2D(_MainTex,uv)*i.color;
    float scan=1-_ScanStrength*(1-smoothstep(.35,.65,frac(i.uv.y*_ScanCount)));
    c.rgb*=scan;
    c.r+=tex2D(_MainTex,uv+float2(.012*_Glitch,0)).a*_Glitch*.25;
    c.a*=1-_Glitch*step(.8,n)*.8;
    // 整数像素采样保留点阵感，远圈衰减避免形成硬描边。
    if (_GlowRadius > 0)
    {
     float halo=0;
     [unroll] for(int ring=1;ring<=3;ring++)
     {
      float radius=max(1,round(_GlowRadius*ring/3));
      [unroll] for(int tap=0;tap<8;tap++)
      {
       float angle=tap*.785398;
       float2 offset=round(float2(cos(angle),sin(angle))*radius)*_MainTex_TexelSize.xy;
       halo+=tex2D(_MainTex,uv+offset).a*(4-ring)/48;
      }
     }
     float breath=1-_GlowBreathDepth*(.5+.5*cos(_CRTMenuUnscaledClock*6.283185/max(.2,_GlowBreathPeriod)));
     float a=saturate(halo*_GlowStrength*breath)*(1-c.a)*i.color.a;
     float total=c.a+a;
     c.rgb=(c.rgb*c.a+_GlowColor.rgb*a*scan)/max(total,.0001);
     c.a=total;
    }
    #ifdef UNITY_UI_CLIP_RECT
    c.a*=UnityGet2DClipping(i.worldPosition.xy,_ClipRect);
    #endif
    return c;
   }
   ENDCG
  }
 }
}
