// Aurora: curtains of northern lights over layered mountain ridges.
// Cursor: the sky and ridges shift with parallax, the curtains lean and brighten toward the cursor,
// clicks send ripples through the lights.

float ridge(float x, float seed, float height, float rough)
{
    return height + (fbm5(float2(x * rough + seed, seed * 3.7)) - 0.5) * 0.22
                  + (noise(float2(x * rough * 6.0 + seed, seed)) - 0.5) * 0.015;
}

float3 stars(float2 p, float t)
{
    float3 col = 0;
    [unroll] for (int layer = 0; layer < 2; layer++)
    {
        float scale = 90.0 + layer * 70.0;
        float2 q = p * scale + layer * 13.7;
        float2 cell = floor(q);
        float2 h = hash22(cell);
        float2 f = frac(q) - h;
        float bright = step(0.80, hash12(cell + 7.1));
        float tw = 0.6 + 0.4 * sin(t * (1.5 + h.x * 3.0) + h.y * TAU);
        col += bright * tw * smoothstep(0.08, 0.0, length(f)) * (0.5 + 0.5 * layer);
    }
    return col * float3(0.85, 0.9, 1.0);
}

float3 scene(float2 uv, float2 p)
{
    float t = Time;
    float2 m = mouseP();
    float active = MouseActive * Intensity;
    float2 par = (Mouse - 0.5) * active;                 // parallax driver
    float rip = ripples(p);

    // Sky: deep indigo to a cold teal horizon.
    float3 col = lerp(float3(0.015, 0.03, 0.07), float3(0.02, 0.10, 0.14), smoothstep(0.55, -0.35, p.y));
    col += stars(p + par * 0.01, t) * smoothstep(-0.15, 0.30, p.y) * 1.6;

    // Curtains: three layers at different depths.
    float3 aur = 0;
    [unroll] for (int i = 0; i < 3; i++)
    {
        float depth = 1.0 + i * 0.55;
        float2 q = p * depth - par * 0.05 * (3 - i);
        // Lean toward the cursor.
        float pull = exp(-pow((q.x - m.x * depth) * 2.2, 2.0)) * active;
        q.x += (m.x * depth - q.x) * pull * 0.10;
        q.y += rip * 0.02;

        float drift = t * (0.035 + i * 0.012);
        // A slow sinuous fold plus noise gives the ribbon its curtain shape.
        float base = -0.06 + i * 0.07
                   + 0.09 * sin(q.x * (1.1 + i * 0.35) + drift * 3.0 + i * 2.1)
                   + (fbm3(float2(q.x * 0.9 + drift + i * 5.3, t * 0.05)) - 0.5) * 0.30;
        float above = q.y - base;
        float fold = fbm3(float2(q.x * 2.4 - drift * 2.0, i * 3.1 + t * 0.07));
        float rays = pow(noise(float2(q.x * 26.0 + fold * 9.0, i * 9.0 + t * 0.2)), 2.2);
        float curtain = exp(-max(above, 0.0) * (4.5 + i)) * smoothstep(-0.015, 0.02, above);
        float glow = exp(-abs(above) * 22.0) * 0.6;
        float energy = (curtain * (0.35 + 0.9 * rays) + glow) * (0.55 + 0.45 * fold);
        energy *= (1.0 + pull * 1.4) * (1.0 + Audio.x * 0.9) + rays * Audio.z * 0.6;

        float3 tint = lerp(float3(0.15, 1.0, 0.55), float3(0.35, 0.55, 1.0), saturate(above * 3.0 + i * 0.25));
        tint = lerp(tint, float3(0.85, 0.30, 0.85), saturate(above * 2.2 - 0.4));
        aur += tint * energy / depth;
    }
    col += aur * 0.62;

    // Soft halo around the cursor in the sky.
    col += float3(0.10, 0.35, 0.30) * mouseInfluence(p, 0.18) * 0.25 * smoothstep(-0.1, 0.2, p.y);

    // Mountain ridges with depth parallax; snow catches the aurora light.
    float3 auroraLight = aur * 0.15 + float3(0.02, 0.05, 0.06);
    [unroll] for (int r = 0; r < 3; r++)
    {
        float k = 1.0 - r * 0.3;
        float x = p.x + par.x * 0.04 * (r + 1);
        float h = ridge(x, 11.0 + r * 7.0, -0.27 - r * 0.07, 1.6 + r * 0.7) - par.y * 0.02 * (r + 1);
        float mask = smoothstep(h + 0.002, h - 0.002, p.y);
        float3 rock = lerp(float3(0.012, 0.02, 0.035), float3(0.03, 0.05, 0.07), k) + auroraLight * (0.6 * k);
        float snow = smoothstep(0.035, 0.0, h - p.y) * k * 0.35;
        rock += snow * (auroraLight * 2.0 + 0.02);
        // Rim light where the ridge meets the glowing sky.
        float rim = exp(-max(h - p.y, 0.0) * 140.0) * k;
        rock += rim * (aur * 0.35 + float3(0.02, 0.06, 0.06));
        col = lerp(col, rock, mask);
        // Atmospheric haze between ridges.
        col = lerp(col, float3(0.03, 0.08, 0.10), mask * 0.08 * r);
    }

    // Vignette.
    float v = smoothstep(1.25, 0.25, length((uv - 0.5) * float2(1.2, 1.0)));
    return col * lerp(0.65, 1.0, v);
}
