// Drift: glowing bokeh rising through a dusky haze in three depth layers.
// Cursor: lights near it scatter away and glow brighter; clicks push a ring through them.

float3 bokehLayer(float2 p, float scale, float speed, float t, float2 m, float push, float layer)
{
    float3 col = 0;
    float2 q = p * scale;
    q.y -= t * speed;
    float2 cell = floor(q);
    [unroll] for (int y = -1; y <= 1; y++)
    [unroll] for (int x = -1; x <= 1; x++)
    {
        float2 id = cell + float2(x, y);
        float2 h = hash22(id + layer * 17.0);
        if (h.x < 0.35) continue;
        float2 pos = id + 0.5 + (h - 0.5) * 0.8 + float2(sin(t * 0.5 + h.y * TAU) * 0.15, 0);
        // Back to scene space to compare with the cursor.
        float2 world = float2(pos.x, pos.y + t * speed) / scale;
        float2 away = world - m;
        float dist = length(away) + 1e-3;
        float repel = push * exp(-dist * dist * 40.0);
        pos += away / dist * repel * scale * 0.06;
        float r = 0.12 + h.y * 0.22;
        float d = length(q - pos);
        float disc = smoothstep(r, r * 0.75, d) * 0.55 + smoothstep(r * 1.8, 0.0, d) * 0.12;
        float3 tint = lerp(float3(1.0, 0.62, 0.35), float3(0.55, 0.65, 1.0), frac(h.x * 7.0 + layer * 0.3));
        col += tint * disc * (0.6 + 0.4 * sin(t * (1.0 + h.x) + h.y * 9.0)) * (1.0 + repel * 3.0);
    }
    return col;
}

float3 scene(float2 uv, float2 p)
{
    float t = Time;
    float active = MouseActive * Intensity;
    float2 m = mouseP();
    float2 par = (Mouse - 0.5) * active;
    float rip = ripples(p);

    float3 col = lerp(float3(0.04, 0.03, 0.08), float3(0.16, 0.07, 0.12), smoothstep(0.6, -0.5, p.y));
    col += float3(0.25, 0.10, 0.10) * fbm3(p * 1.5 + float2(t * 0.02, 0)) * 0.25;
    float2 pp = p + float2(0, rip * 0.01);
    col += bokehLayer(pp - par * 0.010, 3.0, 0.035, t, m, active * 1.0, 0.0) * 0.36;
    col += bokehLayer(pp - par * 0.025, 5.5, 0.050, t, m, active * 0.8, 1.0) * 0.46;
    col += bokehLayer(pp - par * 0.045, 9.0, 0.070, t, m, active * 0.6, 2.0) * 0.38;
    col += float3(1.0, 0.6, 0.4) * mouseInfluence(p, 0.12) * 0.05;
    col *= 1.0 + Audio.y * 0.35 + Audio.x * 0.2;

    float v = smoothstep(1.3, 0.25, length((uv - 0.5) * float2(1.1, 1.0)));
    return col * lerp(0.65, 1.0, v);
}
