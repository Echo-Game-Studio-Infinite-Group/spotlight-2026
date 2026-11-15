inline float2 randVec(float2 uv, float time = 0.0)
{
    float a = sin(dot(uv, float2(12.9898, 78.233))) * 43758.5453 * 6.283 + time;
    return float2(sin(a), cos(a));
}

inline float singlePerlin(float2 uv, float time = 0.0)
{
    float2 i = floor(uv);
    float2 o = frac(uv);
    
    float2 posbl = i;
    float2 posbr = i + float2(1, 0);
    float2 postl = i + float2(0, 1);
    float2 postr = i + float2(1, 1);
    
    float valbl = dot(randVec(posbl, time), o);
    float valbr = dot(randVec(posbr, time), o - float2(1, 0));
    float valtl = dot(randVec(postl, time), o - float2(0, 1));
    float valtr = dot(randVec(postr, time), o - float2(1, 1));
    
    o = o * o * (3 - 2 * o);
    
    float bottom = lerp(valbl, valbr, o.x);
    float top = lerp(valtl, valtr, o.x);
    float finalval = lerp(bottom, top, o.y) * 0.5 + 0.5;
    
    return finalval;
}

float perlinNoise(float2 uv, float scale, float time = 0.0f)
{
    float2 scaleduv = uv * scale;
    return singlePerlin(scaleduv, time) + singlePerlin(scaleduv / 2.0, time) * 0.5;
}

void voronoiNoise(float2 uv, float scale, out float centerDist, out float edgeDist, float time = 0.0f)
{
    float2 scaleduv = uv * scale;
    
    float2 i = floor(scaleduv);
    float2 o = frac(scaleduv);
    centerDist = 10.0;
    edgeDist = 10.0;
    
    int x = 0, y = 0;
    float2 vec[3][3];
    float2 closest = 0;
    [unroll(9)]
    for (x = -1; x <= 1; ++x)
    {
        for (y = -1; y <= 1; ++y)
        {
            float2 cur = float2(x, y);
            float2 rnd = randVec(i + cur, time) * 0.5 + 0.5;
            vec[x + 1][y + 1] = cur - o + rnd;
        }
    }
    
    [unroll(9)]
    for (x = -1; x <= 1; ++x)
    {
        for (y = -1; y <= 1; ++y)
        {
            float2 cur = vec[x + 1][y + 1];
            float pointDist = dot(cur, cur);
            if (pointDist < centerDist)
            {
                centerDist = pointDist;
                closest = cur;
            }
        }
    }
    
    [unroll(9)]
    for (x = -1; x <= 1; ++x)
    {
        for (y = -1; y <= 1; ++y)
        {
            float2 cur = vec[x + 1][y + 1];
            float curEdgeDist = dot(0.5 * (cur + closest), normalize(cur - closest));
            if (curEdgeDist < edgeDist)
            {
                edgeDist = curEdgeDist;
            }
        }
    }
}