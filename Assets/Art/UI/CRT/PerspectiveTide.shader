Shader "UI/CRT/Perspective Tide"
{
 Properties
 {
  [PerRendererData] _MainTex("Texture",2D)="white"{}
  _Clock("Unscaled clock",Float)=0
  _Horizon("Horizon",Range(.3,.8))=.57
  _GridBrightness("Grid brightness",Range(0,1))=.16
  _DotBrightness("Character brightness",Range(0,1))=.65
  _Columns("ASCII columns",Range(80,240))=160
  _FrameRate("ASCII frames per second",Range(6,60))=18
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
   float _Clock,_Horizon,_GridBrightness,_DotBrightness,_Speed,_WaveHeight,_Columns,_FrameRate;

   // 字符在屏幕空间保持等宽，浪面在透视空间采样，远处不会挤成摩尔纹。
   float Glyph(float2 p, float level)
   {
    float2 q = floor(p * float2(5,7));
    float cx = step(abs(q.x-2),.1);
    float cy = step(abs(q.y-3),.1);
    float dot = cx * step(abs(q.y-1),.1);
    float colon = dot + cx * step(abs(q.y-5),.1);
    float dash = cy * step(abs(q.x-2),1.1);
    float plus = dash + cx * step(abs(q.y-3),2.1);
    float star = plus + step(abs(abs(q.x-2)-abs(q.y-3)),.1)*step(abs(q.y-3),2.1);
    float hash = step(abs(abs(q.x-2)-1),.1) + step(abs(abs(q.y-3)-1),.1);
    float glyph = level < 1 ? dot : level < 2 ? colon : level < 3 ? dash : level < 4 ? plus : level < 5 ? star : hash;
    return saturate(glyph) * step(p.x,.92) * step(p.y,.92);
   }

   fixed4 frag(v2f_img i):SV_Target
   {
    float2 uv=i.uv;
    float aspect=_ScreenParams.x/_ScreenParams.y;
    float2 cells=float2(_Columns,_Columns/aspect*.5);
    float2 sampleUV=(floor(uv*cells)+.5)/cells;
    float depth=_Horizon-sampleUV.y;
    if(depth<=.002)return fixed4(0,0,0,1);
    float t=floor(_Clock*_FrameRate)/_FrameRate;
    float z=.55/max(depth,.008);
    float x=(sampleUV.x-.5)*z*aspect;
    float breath=.5+.5*sin(t*_Speed);
    float front=lerp(15,2,breath);
    float swell=exp(-pow((z-front)/3.5,2));
    float phase=z*2.8+t*2.1+sin(x*.85)*1.5;
    float wave=sin(phase)+.35*sin(z*5.3+t*2.7+x*1.2);
    float crest=pow(saturate(.5+.5*wave),3);
    float density=saturate(.08+crest*(.35+.65*swell)+swell*.16);
    float level=floor(density*6);
    float glyph=Glyph(frac(uv*cells),level);
    float fog=smoothstep(.005,.10,depth);
    float2 world=float2((uv.x-.5)*aspect,.55)/max(_Horizon-uv.y,.008);
    world.y+=wave*_WaveHeight*swell;
    float2 grid=abs(frac(world)-.5)/max(fwidth(world),.001);
    float gridLine=1-smoothstep(.35,1.05,min(grid.x,grid.y));
    float vignette=1-smoothstep(.4,.85,length((uv-.5)*float2(.8,1)));
    float value=(gridLine*_GridBrightness*.4+glyph*_DotBrightness*(.12+density*.88))*fog*vignette;
    return fixed4(value,value,value,1);
   }
   ENDCG
  }
 }
}
