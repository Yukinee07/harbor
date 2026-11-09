// Petals: blossoms drifting down through warm evening light in three depth layers.
// Cursor: moving it creates a gust that blows nearby petals along; clicks scatter them outward.

float petal(float2 q, float size)
{
    // A soft teardrop with a small notch at the tip, in petal-local coordinates.
    float2 a = q / size;
    float body = length(float2(a.x * 1.15, (a.y + 0.15) * 0.8)) - 0.55;
    float notch = length(a - float2(0.0, 0.62)) - 0.16;
    return max(body, -notch);
}

float3 petalLayer(float2 p, float scale, float fall, float t, float layer, float2 gust, out float alpha)
{
    float3 col = 0;
    alpha = 0;
    float2 q = p * scale;
    q.y += t * fall;
    float2 base = floor(q);
    [unroll] for (int y = -1; y <= 1; y++)
    [unroll] for (int x = -1; x <= 1; x++)
    {
        float2 cell = base + float2(x, y);
        float2 h = hash22(cell + layer * 31.0);
        if (h.x < 0.45) continue;
        float sway = sin(t * (0.7 + h.y) + h.x * TAU) * 0.35;
        float2 pos = cell + 0.5 + float2(sway, (h.y - 0.5) * 0.6);
        // Gust: petals near the cursor get pushed along its motion.
        float2 world = float2(pos.x, pos.y - t * fall) / scale;
        float2 dm = world - mouseP();
        pos += gust * scale * exp(-dot(dm, dm) * 18.0);
        [unroll] for (int r = 0; r < 4; r++)
        {
            float4 rp = Ripples[r];
            if (rp.w <= 0) continue;
            float2 d = world - toP(rp.xy);
            float dist = length(d) + 1e-3;
            pos += d / dist * exp(-pow(dist - rp.z * 0.5, 2.0) * 60.0) * exp(-rp.z) * rp.w * scale * 0.08;
        }
        float ang = t * (0.6 + h.x) * (h.y > 0.5 ? 1.0 : -1.0) + h.y * TAU;
        float2 rel = q - pos;
        float c = cos(ang), s = sin(ang);
        rel = float2(rel.x * c - rel.y * s, rel.x * s + rel.y * c);
        float d = petal(rel, 0.18 + h.y * 0.1);
        float a = smoothstep(0.02 * scale / 3.0, -0.02, d);
        float3 pink = lerp(float3(0.98, 0.72, 0.80), float3(0.96, 0.52, 0.66), saturate(rel.y * 2.0 + 0.5));
        pink += float3(0.05, 0.03, 0.02) * smoothstep(0.05, 0.0, abs(rel.x)) * 0.8; // centre vein
        col = lerp(col, pink, a);
        alpha = max(alpha, a);
    }
    return col;
}

float3 scene(float2 uv, float2 p)
{
    float t = Time;
    float active = MouseActive * Intensity;
    float2 gust = clamp(MouseVel * float2(1, -1), -2.0, 2.0) * 0.06 * active;

    float3 col = lerp(float3(0.98, 0.78, 0.62), float3(0.55, 0.45, 0.72), smoothstep(-0.55, 0.6, p.y));
    col = lerp(col, float3(1.0, 0.86, 0.72), exp(-dot(p - float2(0.35 * Aspect, -0.15), p - float2(0.35 * Aspect, -0.15)) * 3.0) * 0.5);

    float a;
    float3 far = petalLayer(p, 9.0, 0.35, t, 0.0, gust * 0.5, a);
    col = lerp(col, lerp(col, far, 0.55), a);
    float3 mid = petalLayer(p, 5.5, 0.5, t, 1.0, gust * 0.8, a);
    col = lerp(col, mid, a * 0.85);
    float3 near = petalLayer(p, 3.2, 0.7, t, 2.0, gust, a);
    col = lerp(col, near, a);

    float v = smoothstep(1.4, 0.3, length((uv - 0.5) * float2(1.1, 1.0)));
    return col * lerp(0.82, 1.0, v);
}
