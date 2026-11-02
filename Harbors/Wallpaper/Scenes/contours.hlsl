// Contours: an animated topographic map in fine lines, coloured by elevation.
// Cursor: raises a hill under it; clicks drop a crater ring. Music: the terrain swells with the bass.

float terrain(float2 p, float t)
{
    float2 m = mouseP();
    float active = MouseActive * Intensity;
    float h = fbm3hq(p * 1.1 + float2(t * 0.02, -t * 0.015)) * 1.4;
    h += 0.25 * sin(p.x * 2.1 + t * 0.05) * cos(p.y * 1.7 - t * 0.04);
    float2 d = p - m;
    h += 0.45 * exp(-dot(d, d) * 14.0) * active;
    h += ripples(p) * 0.25;
    return h * (1.0 + Audio.x * 0.25);
}

float3 scene(float2 uv, float2 p)
{
    float t = Time;
    float h = terrain(p, t);
    float levels = 16.0;
    float f = h * levels;
    // Pixel-accurate line width at any resolution.
    float w = fwidth(f);
    float d = abs(frac(f + 0.5) - 0.5) / max(w, 1e-4);
    float thin = 1.0 - smoothstep(0.6, 1.6, d);
    float major = step(frac((floor(f + 0.5)) / 5.0), 0.1);
    float majorLine = 1.0 - smoothstep(1.0, 2.4, d);

    // Elevation tint: deep teal valleys, gold mid slopes, coral peaks.
    float e = saturate(h * 0.75 + 0.05);
    float3 tint = e < 0.5 ? lerp(float3(0.20, 0.62, 0.66), float3(0.95, 0.78, 0.42), e * 2.0)
                          : lerp(float3(0.95, 0.78, 0.42), float3(0.98, 0.45, 0.42), e * 2.0 - 1.0);

    // Soft hillshade under the lines gives the map depth.
    float hx = terrain(p + float2(0.003, 0), t) - h;
    float hy = terrain(p + float2(0, 0.003), t) - h;
    float shade = saturate(0.5 + (hx * -0.6 + hy * 0.8) * 60.0);

    float3 col = lerp(float3(0.035, 0.045, 0.07), float3(0.07, 0.08, 0.11), shade);
    col += tint * 0.05 * shade;
    col = lerp(col, tint, saturate(thin * 0.45 + major * majorLine * 0.5));

    float v = smoothstep(1.35, 0.25, length((uv - 0.5) * float2(1.1, 1.0)));
    return col * lerp(0.65, 1.0, v);
}
