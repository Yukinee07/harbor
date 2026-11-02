// Bloom: a soft full-screen mesh gradient of drifting colour points. The cheapest scene; good on battery.
// Cursor: one colour point follows it softly, the others drift aside.

float3 scene(float2 uv, float2 p)
{
    float t = Time * 0.22;
    float2 m = mouseP();
    float active = MouseActive * Intensity;

    static const float3 colors[6] =
    {
        float3(0.99, 0.42, 0.45),   // coral
        float3(1.00, 0.70, 0.36),   // apricot
        float3(0.42, 0.36, 0.96),   // indigo
        float3(0.22, 0.70, 0.92),   // sky
        float3(0.78, 0.36, 0.86),   // orchid
        float3(0.10, 0.08, 0.22),   // night, keeps depth in the gradient
    };

    float3 acc = 0;
    float wsum = 0;
    [unroll] for (int i = 0; i < 6; i++)
    {
        float fi = i;
        float2 c = float2(sin(t * (0.7 + fi * 0.17) + fi * 1.9) * 0.62 * Aspect * 0.55,
                          cos(t * (0.6 + fi * 0.13) + fi * 2.7) * 0.42);
        float2 toM = m - c;
        float dm = length(toM) + 1e-3;
        c += (i == 0 ? toM * 0.6 : -toM / dm * 0.07 * exp(-dm * 3.0)) * active;
        // Inverse-distance weights blend every point everywhere: no edges, just a smooth mesh gradient.
        float d2 = dot(p - c, p - c);
        float w = 1.0 / pow(d2 + 0.015 - Audio.x * 0.006 * (i == 0 ? 1.0 : 0.4), 1.6);
        acc += colors[i] * w;
        wsum += w;
    }
    float3 col = acc / wsum;
    // Gentle luminance falloff toward the corners and a printed grain.
    col *= lerp(0.78, 1.0, smoothstep(1.2, 0.2, length((uv - 0.5) * float2(1.1, 1.0))));
    col += ripples(p) * 0.04;
    col *= 1.0 + Audio.w * 0.12;
    col *= 0.97 + 0.03 * hash12(uv * Resolution * 0.5);
    return col;
}
