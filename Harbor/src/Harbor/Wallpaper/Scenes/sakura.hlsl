// Sakura: cherry trees in full bloom beside a still lake. The trees are painted once (SakuraPainter) and only moved here;
// the sky, the water and the falling petals are drawn live. Cursor: moving it blows petals along, clicks ripple the lake.

static const float BakeAspect = 1.6;
static const float Horizon = 0.628;
static const float2 Sun = float2(0.62, 0.17);

// The paintings are 16:10; this crops them to fill any screen without stretching.
float2 toBake(float2 uv)
{
    float a = Aspect / BakeAspect;
    if (a > 1.0) uv.y = (uv.y - 0.6) / a + 0.6;
    else uv.x = (uv.x - 0.5) * a + 0.5;
    return uv;
}

float3 skyAt(float2 b, float t)
{
    float h = saturate(b.y / Horizon);
    float3 col = lerp(float3(0.42, 0.62, 0.91), float3(0.72, 0.82, 0.97), smoothstep(0.0, 0.62, h));
    col = lerp(col, float3(1.0, 0.87, 0.84), smoothstep(0.52, 1.0, h));

    float2 s = (b - Sun) * float2(BakeAspect, 1.0);
    float d = dot(s, s);
    col += float3(1.0, 0.86, 0.70) * (exp(-d * 60.0) * 0.32 + exp(-d * 7.0) * 0.10);

    // High, thin cloud, stretched sideways and drifting.
    float2 c = float2(b.x * BakeAspect * 2.4 + t * 0.010, b.y * 7.5 + t * 0.002);
    float n = fbm3(c + float2(noise(c * 0.6) * 0.8, 0.0));
    float cloud = smoothstep(0.50, 0.80, n) * (1.0 - h * 0.45);
    float3 white = lerp(float3(1.0, 0.97, 0.95), float3(1.0, 0.83, 0.80), h);
    return lerp(col, white, cloud * 0.8);
}

float4 layerAt(Texture2D tex, float2 b) { return tex.SampleLevel(LinearClamp, b, 0); }
float3 over(float4 top, float3 under) { return top.rgb + under * (1.0 - top.a); }

// Signed distance-like value for a cherry petal in unit coordinates: base at y = -1, notched tip at y = 1.
float petalShape(float2 q)
{
    float y = q.y * 0.5 + 0.5;
    float width = 0.72 * pow(saturate(y), 0.62) * pow(saturate(1.0 - y * 0.985), 0.34) * 1.45;
    float side = abs(q.x) - width;
    float notch = q.y - (1.0 - 0.34 * saturate(abs(q.x) / 0.26));
    float ends = max(-1.0 - q.y, q.y - 1.0);
    return max(max(side, ends), notch);
}

// How the cursor and clicks push petals aside at this pixel. Worked out once per pixel, not once per petal, because the
// petal loops run dozens of times a pixel and this is the most expensive part of what they would otherwise do.
float2 clickPush(float2 p)
{
    float2 push = 0;
    [unroll] for (int r = 0; r < 4; r++)
    {
        float4 rp = Ripples[r];
        if (rp.w <= 0.0) continue;
        float2 away = p - toP(rp.xy);
        float dist = length(away) + 1e-3;
        push += away / dist * exp(-pow(dist - rp.z * 0.5, 2.0) * 60.0) * exp(-rp.z) * rp.w * 0.07;
    }
    return push;
}

void fallingPetals(inout float3 col, float2 p, float scale, float speed, float t, float seed, float soft, float density, float breeze, float2 warp, float glow)
{
    float wind = t * speed * 0.62 + breeze * 0.55;
    float fall = t * speed;
    float2 q = (p - warp) * scale + float2(-wind, fall);
    float2 base = floor(q);
    [unroll] for (int y = -1; y <= 1; y++)
    [unroll] for (int x = -1; x <= 1; x++)
    {
        float2 cell = base + float2(x, y);
        float2 h = hash22(cell + seed * 37.0);
        if (h.x > density) continue;
        float2 fromCell = abs(q - cell - 0.5);
        if (max(fromCell.x, fromCell.y) > 0.76) continue;
        float2 k = hash22(cell + seed * 91.0 + 7.0);
        float phase = t * (0.8 + k.x * 1.5) + h.y * TAU;
        float2 pos = cell + 0.5 + (k - 0.5) * 0.46 + float2(sin(phase * 0.7 + k.y * TAU) * 0.26, cos(phase * 0.5) * 0.10);

        float2 rel = q - pos;
        float size = 0.11 + 0.07 * k.x;
        if (dot(rel, rel) > size * size * 2.4) continue;
        float a = k.y * TAU + t * (0.25 + h.y * 0.55) * (k.x > 0.5 ? 1.0 : -1.0);
        float c = cos(a), s = sin(a);
        rel = float2(rel.x * c - rel.y * s, rel.x * s + rel.y * c);

        // Tumbling: the petal turns edge-on and back, showing its paler or deeper side.
        float flip = cos(phase);
        rel.x /= max(abs(flip), 0.20);
        float2 u = rel / size;
        float d = petalShape(u) * size;
        float alpha = smoothstep(soft, -soft, d);
        if (alpha <= 0.0) continue;

        float3 pink = lerp(float3(0.92, 0.46, 0.62), float3(1.0, 0.86, 0.90), saturate(u.y * 0.75 + 0.62));
        pink *= lerp(0.78, 1.06, flip * 0.5 + 0.5);
        pink += float3(0.10, 0.07, 0.05) * glow;
        col = lerp(col, pink, alpha);
    }
}

void lakePetals(inout float3 col, float2 b, float depth, float t)
{
    float perspective = 1.0 / (depth + 0.14);
    float2 q = float2((b.x - 0.5) * BakeAspect * perspective * 3.0 + t * 0.012, perspective * 2.4 + sin(t * 0.05) * 0.2);
    float2 base = floor(q);
    [unroll] for (int y = -1; y <= 1; y++)
    [unroll] for (int x = -1; x <= 1; x++)
    {
        float2 cell = base + float2(x, y);
        float2 h = hash22(cell + 311.0);
        if (h.x > 0.42) continue;
        float2 k = hash22(cell + 733.0);
        float2 rel = q - (cell + 0.5 + (k - 0.5) * 0.7 + float2(sin(t * 0.3 + k.x * TAU), cos(t * 0.23 + k.y * TAU)) * 0.05);
        float a = k.y * TAU, c = cos(a), s = sin(a);
        rel = float2(rel.x * c - rel.y * s, rel.x * s + rel.y * c);
        float size = 0.09 + 0.05 * k.x;
        if (dot(rel, rel) > size * size * 1.5) continue;
        float d = petalShape(rel / size) * size;
        float alpha = smoothstep(0.02, -0.01, d);
        col = lerp(col, lerp(float3(0.96, 0.66, 0.76), float3(1.0, 0.88, 0.92), k.x), alpha * 0.92);
    }
}

float3 scene(float2 uv, float2 p)
{
    float t = Time;
    float active = MouseActive * Intensity;
    float2 look = (Mouse - 0.5) * active;
    float2 gust = clamp(MouseVel * float2(1.0, -1.0), -2.0, 2.0) * 0.055 * active;
    float breeze = sin(t * 0.31) * 0.6 + sin(t * 0.13 + 1.7) * 0.4 + Audio.x * 0.8;

    float2 b = toBake(uv);
    float2 back = (b - 0.5) * 0.985 + 0.5 - look * float2(0.005, 0.003);
    float3 col;
    float glow = saturate(1.2 - length((b - Sun) * float2(BakeAspect, 1.0)) * 1.5);

    if (back.y < Horizon)
    {
        col = over(layerAt(BakeFar, back), skyAt(back, t));
        // Haze gathering over the far shore.
        col = lerp(col, float3(1.0, 0.91, 0.90), smoothstep(0.34, 0.62, back.y) * 0.32);
    }
    else
    {
        // The lake mirrors everything above it, broken up more the closer the water is.
        float depth = (back.y - Horizon) / (1.0 - Horizon);
        float2 wave = float2(noise(float2(back.x * 34.0 + t * 0.2, back.y * 520.0 - t * 0.9)),
                             noise(float2(back.x * 26.0 - t * 0.16, back.y * 380.0 + t * 0.7))) - 0.5;
        float amount = lerp(0.0006, 0.012, pow(depth, 1.1));
        float click = ripples(p);
        float2 mirror = float2(back.x + wave.x * amount * 0.7 + click * 0.02, 2.0 * Horizon - back.y + wave.y * amount * 1.9 + click * 0.03);
        mirror.y = min(mirror.y, Horizon - 0.0005);
        col = over(layerAt(BakeFar, mirror), skyAt(mirror, t));
        // Only the crowns are reflected: the trunks stand on the banks, so their mirror images belong out of view.
        col = over(layerAt(BakeTree, (mirror - 0.5) * 0.97 + 0.5) * smoothstep(0.5, 0.3, mirror.y) * 0.8, col);
        col = lerp(col, float3(0.34, 0.50, 0.60), 0.16 + depth * 0.36);
        float sparkle = smoothstep(0.30, 0.48, wave.y) * exp(-pow((back.x - Sun.x) * 6.0, 2.0)) * (1.0 - depth * 0.7);
        col += float3(1.0, 0.92, 0.82) * sparkle * 0.55;
        lakePetals(col, back, depth, t);
    }

    float2 dm = p - mouseP();
    float2 blown = gust * exp(-dot(dm, dm) * 13.0);
    float2 pushed = clickPush(p);
    fallingPetals(col, p, 15.0, 0.24, t, 1.0, 0.012, 0.50, breeze, blown * 0.5 + pushed, glow);

    // Branches stir in the breeze, more the further they are from the trunk's foot.
    float2 tb = (b - 0.5) * 0.97 + 0.5 - look * 0.014;
    float reach = saturate((0.86 - tb.y) / 0.7);
    reach *= reach;
    float2 sway = float2(sin(t * 0.8 + tb.y * 5.0 + tb.x * 3.0), cos(t * 0.63 + tb.x * 6.0)) * 0.0017 * reach * (1.0 + breeze * 0.5);
    float2 flutter = float2(noise(tb * float2(90.0, 56.0) + t * 0.55), noise(tb * float2(70.0, 44.0) - t * 0.46)) - 0.5;
    sway += flutter * 0.0017 * reach + ripples(p) * 0.004;
    float4 tree = layerAt(BakeTree, tb + sway);
    tree.rgb *= 1.0 + glow * 0.08;
    col = over(tree, col);

    fallingPetals(col, p, 8.5, 0.32, t, 2.0, 0.016, 0.44, breeze, blown * 0.8 + pushed, glow);
    fallingPetals(col, p, 4.2, 0.46, t, 3.0, 0.050, 0.30, breeze, blown + pushed, glow);

    float2 nb = (b - 0.5) * 0.94 + 0.5 - look * 0.03 + sway * 2.2;
    col = over(layerAt(BakeNear, nb), col);

    // Soft shafts of light from the sun, in front of everything.
    float2 toPixel = (b - Sun) * float2(BakeAspect, 1.0);
    float dist = length(toPixel) + 1e-3;
    float2 dir = toPixel / dist;
    float shafts = noise(dir * 7.0 + float2(t * 0.03, 3.0)) * noise(dir * 17.0 - float2(t * 0.02, 9.0));
    col += float3(1.0, 0.90, 0.78) * shafts * exp(-dist * 1.6) * smoothstep(0.03, 0.4, dist) * 0.13;

    col *= 1.0 + Audio.w * 0.05;
    float v = smoothstep(1.35, 0.35, length((uv - 0.5) * float2(1.15, 1.05)));
    return col * lerp(0.84, 1.0, v);
}
