Shader "UI/CRT/Text"
{
 Properties
 {
  _MainTex("Texture",2D)="white"{}
  _ScanStrength("Scanline strength",Range(0,1))=.22
  _ScanCount("Scanline count",Float)=90
  _Glitch("Glitch",Range(0,1))=0
  _Clock("Unscaled clock",Float)=0
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
   struct app {float4 vertex:POSITION;float2 uv:TEXCOORD0;float4 color:COLOR;};
   struct output {float4 vertex:SV_POSITION;float2 uv:TEXCOORD0;float4 color:COLOR;};
   sampler2D _MainTex;float _ScanStrength,_ScanCount,_Glitch,_Clock;
   output vert(app v){output o;o.vertex=UnityObjectToClipPos(v.vertex);o.uv=v.uv;o.color=v.color;return o;}
   fixed4 frag(output i):SV_Target
   {
    float band=floor(i.uv.y*25);float n=frac(sin(band*73.7+floor(_Clock*18)*31.3)*43758.5);
    float2 uv=i.uv;uv.x+=(n-.5)*.13*_Glitch*step(.65,n);
    fixed4 c=tex2D(_MainTex,uv)*i.color;
    float scan=1-_ScanStrength*(.5+.5*sin(i.uv.y*_ScanCount*6.283));
    c.rgb*=scan;
    c.r+=tex2D(_MainTex,uv+float2(.012*_Glitch,0)).a*_Glitch*.25;
    c.a*=1-_Glitch*step(.8,n)*.8;
    return c;
   }
   ENDCG
  }
 }
}
