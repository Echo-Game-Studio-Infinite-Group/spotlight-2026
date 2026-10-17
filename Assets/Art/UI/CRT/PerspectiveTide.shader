Shader "UI/CRT/Perspective Tide"
{
 Properties
 {
  [PerRendererData] _MainTex("Texture",2D)="white"{}
  _Clock("Unscaled clock",Float)=0
  _Horizon("Horizon",Range(.3,.8))=.57
  _GridBrightness("Grid brightness",Range(0,1))=.16
  _DotBrightness("Dot brightness",Range(0,1))=.65
  _Speed("Breathing speed",Float)=.5
  _WaveHeight("Wave height",Range(0,.2))=.045
 }
 SubShader
 {
  Tags {"Queue"="Transparent" "RenderType"="Transparent"}
  Cull Off ZWrite Off ZTest [unity_GUIZTestMode]
  Pass
  {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   float _Clock,_Horizon,_GridBrightness,_DotBrightness,_Speed,_WaveHeight;
   fixed4 frag(v2f_img i):SV_Target
   {
    float2 uv=i.uv;float depth=_Horizon-uv.y;
    if(depth<=.002)return fixed4(0,0,0,1);
    float z=.55/max(depth,.008);float x=(uv.x-.5)*z*(_ScreenParams.x/_ScreenParams.y);
    float2 world=float2(x,z);
    float2 grid=abs(frac(world)-.5)/max(fwidth(world),.001);
    float gridLine=1-smoothstep(.35,1.05,min(grid.x,grid.y));
    float breath=.5+.5*sin(_Clock*_Speed);
    float front=lerp(12,2,breath);
    float swell=exp(-pow((z-front)/2.8,2));
    float height=sin(z*2.3-_Clock*1.8+sin(x*.8))*_WaveHeight*swell;
    float zd=.55/max(depth+height,.008);
    float2 dots=float2((uv.x-.5)*zd*(_ScreenParams.x/_ScreenParams.y),zd)*3;
    float2 cell=(frac(dots)-.5)/max(fwidth(dots),.001);
    float pointLight=1-smoothstep(.65,1.4,length(cell));
    float fog=smoothstep(.005,.16,depth);
    float vignette=1-smoothstep(.4,.85,length((uv-.5)*float2(.8,1)));
    float value=(gridLine*_GridBrightness+pointLight*_DotBrightness*(.17+swell*.83))*fog*vignette;
    return fixed4(value,value,value,1);
   }
   ENDCG
  }
 }
}
