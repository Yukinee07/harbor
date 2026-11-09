// Nebula: drifting gas clouds and three star layers.
// Cursor: a gentle gravitational lens bends the starlight around it and the layers parallax by depth.

float3 starLayer(float2 p, float scale, float density, float t)
{
    float2 q = p * scale;
    float2 cell = floor(q);
    float2 h = hash22(cell);
    float2 f = frac(q) - (0.2 + 0.6 * h);
    float on = step(1.0 - density, hash12(cell + 3.3));
    float tw = 0.65 + 0.35 * sin(t * (0.8 + h.x * 2.5) + h.y * TAU);
    float core = smoothstep(0.06, 0.0, length(f)) + smoothstep(0.3, 0.0, length(f)) * 0.08;
    float3 tint = lerp(float3(0.75, 0.85, 1.0), float3(1.0, 0.85, 0.7), h.y);
    return on * tw * core * tint;
}

float3 scene(float2 uv, float2 p)
{
    float t = Time;
    float2 m = mouseP();
    float active = MouseActive * Intensity;
    float2 par = (Mouse - 0.5) * active;

    // Lens: pull sample positions toward the cursor, falling off with distance.
    float2 d = p - m;
    float r2 = dot(d, d) + 0.002;
    float2 lens = -d * (0.0018 / r2) * active * smoothstep(0.0, 0.02, r2);
    float2 lp = p + lens + ripples(p) * 0.01 * d / sqrt(r2);

    float2 q = lp * 1.6 - par * 0.03 + float2(t * 0.006, 0);
    float n1 = fbm5(q + fbm3(q * 1.5 + t * 0.02) * 1.3);
    float n2 = fbm5(q * 2.1 + 7.0 - t * 0.01);
    float dust = smoothstep(0.35, 0.9, n1);
    float3 col = float3(0.008, 0.008, 0.02);
    col += float3(0.45, 0.12, 0.40) * pow(dust, 1.6) * 0.9;
    col += float3(0.10, 0.30, 0.55) * pow(smoothstep(0.3, 0.95, n2), 2.0) * 0.8;
    col += float3(0.95, 0.55, 0.30) * pow(saturate(n1 * n2 * 1.9 - 0.45), 3.0) * 0.9;
    // Dark lanes.
    col *= 0.55 + 0.45 * smoothstep(0.25, 0.6, fbm3(q * 3.0 + 2.0));

    col += starLayer(lp - par * 0.004, 70.0, 0.10, t) * 0.7;
    col += starLayer(lp - par * 0.012, 38.0, 0.08, t) * 0.9;
    col += starLayer(lp - par * 0.030, 18.0, 0.05, t) * 1.2;

    // A faint glow where the lens sits.
    col += float3(0.30, 0.25, 0.45) * exp(-r2 * 900.0) * active * 0.25;

    float v = smoothstep(1.3, 0.2, length((uv - 0.5) * float2(1.1, 1.0)));
    return col * lerp(0.6, 1.0, v);
}
