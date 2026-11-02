// Lava: lava-lamp blobs that rise, stretch and merge in warm light.
// Cursor: a heat source; blobs drift toward it and swell, clicks pop a ring through the wax.
// Music: blobs breathe with the bass.

float3 scene(float2 uv, float2 p)
{
    float t = Time * 0.18;
    float2 m = mouseP();
    float active = MouseActive * Intensity;
    float rip = ripples(p);

    float field = 0;
    [unroll] for (int i = 0; i < 9; i++)
    {
        float fi = i;
        float2 h = hash22(float2(fi, 11.0));
        // Each blob loops slowly up and down the lamp with a little side-to-side wander.
        float y = sin(t * (0.6 + h.x * 0.7) + h.y * TAU) * 0.42;
        float x = (h.x - 0.5) * Aspect * 0.8 + sin(t * 0.9 + fi * 1.7) * 0.08;
        float2 c = float2(x, y);
        float2 toM = m - c;
        c += toM * 0.22 * exp(-length(toM) * 3.0) * active;
        float r = 0.05 + h.y * 0.07 + Audio.x * 0.02;
        r *= 1.0 + 0.6 * exp(-length(toM) * 6.0) * active;
        float2 d = p - c;
        field += r * r / (dot(d, d) + 1e-4);
    }
    // The cursor itself is a faint blob of heat.
    float2 dm = p - m;
    field += 0.0016 / (dot(dm, dm) + 1e-3) * active;
    field += rip * 0.6;

    float3 bg = lerp(float3(0.10, 0.03, 0.12), float3(0.24, 0.06, 0.16), smoothstep(-0.6, 0.6, p.y));
    float glow = saturate(field * 0.35);
    float3 col = bg + float3(0.9, 0.25, 0.3) * glow * glow * 0.45;

    float body = smoothstep(0.9, 1.1, field);
    float core = smoothstep(1.0, 7.0, field);
    float3 wax = lerp(float3(0.92, 0.20, 0.40), float3(1.0, 0.58, 0.30), pow(core, 0.7));
    wax *= 0.85 + 0.15 * smoothstep(-0.4, 0.4, p.y); // light from above
    // A rim of light where the wax surface curves away.
    float rim = smoothstep(1.4, 1.0, field) * body;
    wax += float3(1.0, 0.85, 0.6) * rim * 0.35;
    col = lerp(col, wax, body);

    float v = smoothstep(1.3, 0.25, length((uv - 0.5) * float2(1.1, 1.0)));
    return col * lerp(0.6, 1.0, v);
}
