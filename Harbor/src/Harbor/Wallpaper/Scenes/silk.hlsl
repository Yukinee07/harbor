// Silk: slow, smooth folds of satin in deep dusk colours with a glossy sheen.
// Cursor: twists the fabric around it like a finger dragged through liquid; clicks ripple it.

float field(float2 q, float t)
{
    // Long warped sine folds read as draped fabric once lit.
    float2 w = float2(fbm3(q * 0.7 + float2(0.0, t * 0.05)), fbm3(q * 0.7 + float2(5.2, 1.3) - t * 0.04));
    float bands = sin((q.x * 1.4 + q.y * 0.8) * 5.2 + w.x * 7.0 + w.y * 3.0 + t * 0.2);
    return 0.5 + 0.5 * bands;
}

float3 scene(float2 uv, float2 p)
{
    float t = Time;
    float2 m = mouseP();
    float infl = mouseInfluence(p, 0.30);

    // Swirl around the cursor, stronger when it moves.
    float2 d = p - m;
    float spin = infl * (0.8 + 2.2 * saturate(length(MouseVel) * 0.8));
    float c = cos(spin), s = sin(spin);
    float2 q = m + float2(d.x * c - d.y * s, d.x * s + d.y * c);
    q += ripples(p) * 0.012;

    float2 sp = q * 1.25 + float2(0.0, 0.25);
    float f = field(sp, t);
    // Lighting from the field gradient gives the satin its folds and highlights.
    float e = 0.003;
    float fx = field(sp + float2(e, 0), t) - f;
    float fy = field(sp + float2(0, e), t) - f;
    float3 n = normalize(float3(-fx / e * 0.2, -fy / e * 0.2, 1.0));
    float3 l = normalize(float3(-0.5, 0.65, 0.6));
    float diff = saturate(dot(n, l));
    float spec = pow(saturate(dot(reflect(-l, n), float3(0, 0, 1))), 36.0);
    float rim = pow(1.0 - n.z, 2.0);

    float3 deep = float3(0.05, 0.02, 0.11);
    float3 plum = float3(0.42, 0.10, 0.40);
    float3 rose = float3(0.92, 0.38, 0.48);
    float3 peach = float3(1.00, 0.74, 0.58);
    float k = saturate(f * 0.75 + fbm3(sp * 0.6 + 4.0) * 0.45 - 0.1);
    float3 base = k < 0.5 ? lerp(deep, plum, k * 2.0) : lerp(plum, rose, k * 2.0 - 1.0);
    base = lerp(base, peach, smoothstep(0.82, 1.05, f) * 0.6);

    float3 col = base * (0.35 + 0.85 * diff);
    col += spec * float3(1.0, 0.86, 0.80) * 0.55;
    col += rim * rose * 0.25;
    col += float3(0.95, 0.55, 0.50) * infl * 0.05;

    float v = smoothstep(1.3, 0.3, length((uv - 0.5) * float2(1.1, 1.0)));
    return col * lerp(0.65, 1.0, v);
}
