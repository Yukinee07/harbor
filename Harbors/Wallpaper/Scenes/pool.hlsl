// Pool: a real water surface. A height field is simulated every frame (PSSim); moving the cursor leaves a
// wake, clicks splash, light rain keeps it alive, and the tiled floor refracts through the waves.
// SimTex: R = height now, G = height on the previous step.

float simHeight(float2 uv) { return SimTex.SampleLevel(LinearClamp, uv, 0).r; }

float drop(float2 p, float2 c, float radius)
{
    float2 d = p - c;
    return exp(-dot(d, d) / (radius * radius));
}

float4 PSSim(VSOut i) : SV_Target
{
    uint w, h;
    SimTex.GetDimensions(w, h);
    float2 tx = 1.0 / float2(w, h);
    float2 uv = i.uv;
    float2 now = SimTex.SampleLevel(LinearClamp, uv, 0).rg;
    float n = simHeight(uv - float2(tx.x, 0)) + simHeight(uv + float2(tx.x, 0))
            + simHeight(uv - float2(0, tx.y)) + simHeight(uv + float2(0, tx.y));
    // Discrete wave equation: neighbours pull, the previous step gives momentum, damping calms it.
    float next = (n * 0.5 - now.g) * 0.985;

    float2 p = toP(uv);
    float active = MouseActive * Intensity;
    // Wake behind a moving cursor; a still cursor leaves the water alone.
    float speed = saturate(length(MouseVel) * 0.7);
    next -= drop(p, mouseP(), 0.012) * speed * 0.9 * active;

    // Fresh clicks splash.
    [unroll] for (int k = 0; k < 4; k++)
    {
        float4 r = Ripples[k];
        if (r.w > 0 && r.z < 0.05) next -= drop(p, toP(r.xy), 0.02) * 2.5 * active;
    }

    // Gentle rain, a little heavier on the beat when music reactivity is on.
    float rate = 1.6 + Audio.x * 4.0;
    float cell = floor(Time * rate);
    float phase = frac(Time * rate);
    float2 rain = hash22(float2(cell, 3.7)) * float2(Aspect, 1.0) - float2(Aspect * 0.5, 0.5);
    next -= drop(p, rain, 0.008) * step(phase, 0.12) * (0.35 + Audio.x * 0.8);

    return float4(next, now.r, 0, 1);
}

float ridged(float2 q) { return 1.0 - abs(noise(q) * 2.0 - 1.0); }

float3 scene(float2 uv, float2 p)
{
    uint w, h;
    SimTex.GetDimensions(w, h);
    float2 tx = 1.0 / float2(w, h);
    float hx = simHeight(uv + float2(tx.x, 0)) - simHeight(uv - float2(tx.x, 0));
    float hy = simHeight(uv + float2(0, tx.y)) - simHeight(uv - float2(0, tx.y));
    float lap = simHeight(uv + float2(tx.x, 0)) + simHeight(uv - float2(tx.x, 0)) + simHeight(uv + float2(0, tx.y))
              + simHeight(uv - float2(0, tx.y)) - 4.0 * simHeight(uv);
    float3 n = normalize(float3(-hx * 3.0, hy * 3.0, 1.0));

    // Refract the floor through the surface.
    float2 f = p + n.xy * 0.035;
    float t = Time;

    // Floor: deep-to-shallow gradient with a fine tile grid that makes refraction readable.
    float depth = smoothstep(-0.7, 0.6, f.y + f.x * 0.15);
    float3 floorCol = lerp(float3(0.02, 0.18, 0.26), float3(0.10, 0.52, 0.58), depth);
    float2 g = abs(frac(f * 9.0) - 0.5);
    float grout = smoothstep(0.47, 0.5, max(g.x, g.y));
    floorCol = lerp(floorCol, floorCol * 1.25 + 0.03, grout * 0.55);

    // Sunlight dapple: two drifting ridged-noise webs multiplied, plus focusing from the surface curvature.
    float web = pow(ridged(f * 5.5 + float2(t * 0.15, t * 0.08)) * ridged(f * 4.3 - float2(t * 0.1, -t * 0.12)), 5.0);
    float focus = saturate(-lap * 18.0);
    floorCol += float3(0.55, 0.85, 0.80) * (web * 0.35 + focus * 0.6) * (0.6 + 0.4 * depth);

    // Surface: sky reflection grows at glancing angles, plus a sharp sun glint.
    float fresnel = pow(1.0 - n.z, 3.0);
    float3 l = normalize(float3(-0.35, 0.55, 0.75));
    float spec = pow(saturate(dot(reflect(-l, n), float3(0, 0, 1))), 90.0);
    float3 col = floorCol + float3(0.55, 0.75, 0.95) * fresnel * 0.6 + spec * float3(1.0, 0.97, 0.9) * 0.9;

    col *= 1.0 + Audio.w * 0.08;
    float v = smoothstep(1.35, 0.3, length((uv - 0.5) * float2(1.1, 1.0)));
    return col * lerp(0.72, 1.0, v);
}
