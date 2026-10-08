#ifndef NPR_SHADOW_PATTERN_INCLUDED
#define NPR_SHADOW_PATTERN_INCLUDED
float NPRShadowPatternAttenuationAA(float2 pixel, float band, float strength, float spacing, float mode, float aa)
{
    spacing=max(spacing,3);
    if(mode<.5)
    {
        float wave=abs(frac((pixel.x+pixel.y*.35)/spacing)-.5);
        float lineCoverage=1-smoothstep(.08,.16,wave);
        return 1-lineCoverage*strength*(1-band)*.35;
    }
    // 点径随阴影连续生长；导数在 frac 之前求取，避免周期接缝的虚假宽边。
    float2 grid=float2(pixel.x+pixel.y,pixel.y-pixel.x)*.70710678/spacing;
    aa=max(aa,.0001);
    float distanceToCenter=length(frac(grid)-.5);
    float radius=.44*sqrt(saturate(1-band));
    float coverage=(1-smoothstep(radius-aa,radius+aa,distanceToCenter))*saturate(radius/aa);
    return 1-coverage*strength*.35;
}
float NPRShadowPatternAttenuation(float2 pixel, float band, float strength, float spacing, float mode)
{
    float2 grid=float2(pixel.x+pixel.y,pixel.y-pixel.x)*.70710678/max(spacing,3);
    float aa=max(max(fwidth(grid.x),fwidth(grid.y))*.5,.0001);
    return NPRShadowPatternAttenuationAA(pixel,band,strength,spacing,mode,aa);
}
#endif