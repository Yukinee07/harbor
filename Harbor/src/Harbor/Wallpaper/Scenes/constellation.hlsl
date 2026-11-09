// Constellation: a slowly drifting network of stars linked by faint lines.
// Cursor: nearby stars lean in and the network links up to the cursor; clicks send a shockwave.
// Music: lines brighten with the treble, stars pulse with the bass.

float2 starPos(float2 cell, float t)
{
    float2 h = hash22(cell);
    return cell + 0.5 + 0.38 * float2(sin(t * (0.3 + h.x * 0.4) + h.y * TAU), cos(t * (0.25 + h.y * 0.35) + h.x * TAU));
}

float segment(float2 p, float2 a, float2 b)
{
    float2 pa = p - a, ba = b - a;
    float h = saturate(dot(pa, ba) / dot(ba, ba));
    return length(pa - ba * h);
}

float3 scene(float2 uv, float2 p)
{
    const float scale = 7.0;
    float t = Time;
    float active = MouseActive * Intensity;
    float2 m = mouseP() * scale;
    float2 q = p * scale;
    float2 base = floor(q);
    float px = scale / Resolution.y;  // one pixel in grid units, for crisp lines at any resolution

    // Gather the 3x3 neighbourhood of stars, pulled toward the cursor and pushed by click rings.
    float2 pts[9];
    [unroll] for (int k = 0; k < 9; k++)
    {
        float2 cell = base + float2(k % 3 - 1, k / 3 - 1);
        float2 s = starPos(cell, t);
        float2 toM = m - s;
        s += toM * 0.18 * exp(-dot(toM, toM) * 0.35) * active;
        [unroll] for (int r = 0; r < 4; r++)
        {
            float4 rp = Ripples[r];
            if (rp.w <= 0) continue;
            float2 c = toP(rp.xy) * scale;
            float2 d = s - c;
            float dist = length(d) + 1e-3;
            float ring = exp(-pow(dist - rp.z * 4.0, 2.0) * 2.0) * exp(-rp.z * 1.5) * rp.w;
            s += d / dist * ring * 0.35;
        }
        pts[k] = s;
    }

    float3 col = lerp(float3(0.01, 0.02, 0.06), float3(0.04, 0.06, 0.14), smoothstep(-0.6, 0.6, p.y));
    float lines = 0;
    // Centre star to every neighbour, plus the ring of neighbours to each other.
    [unroll] for (int a = 0; a < 9; a++)
    {
        if (a == 4) continue;
        float len = length(pts[4] - pts[a]);
        lines += smoothstep(1.5 * px, 0.0, segment(q, pts[4], pts[a])) * smoothstep(1.45, 0.6, len);
    }
    static const int ring[8] = { 0, 1, 2, 5, 8, 7, 6, 3 };
    [unroll] for (int e = 0; e < 8; e++)
    {
        float2 A = pts[ring[e]], B = pts[ring[(e + 1) % 8]];
        lines += smoothstep(1.5 * px, 0.0, segment(q, A, B)) * smoothstep(1.45, 0.6, length(A - B)) * 0.8;
    }
    col += float3(0.35, 0.65, 0.95) * saturate(lines) * 0.32;

    // Links from the cursor to stars within reach.
    float reach = 0;
    [unroll] for (int c2 = 0; c2 < 9; c2++)
    {
        float len = length(pts[c2] - m);
        reach += smoothstep(1.5 * px, 0.0, segment(q, m, pts[c2])) * smoothstep(2.2, 0.8, len);
    }
    col += float3(1.0, 0.75, 0.45) * saturate(reach) * 0.55 * active;

    // Stars.
    [unroll] for (int s2 = 0; s2 < 9; s2++)
    {
        float d = length(q - pts[s2]);
        float tw = 0.7 + 0.3 * sin(t * 2.0 + hash12(floor(pts[s2])) * 30.0);
        col += float3(0.85, 0.92, 1.0) * (smoothstep(2.6 * px, 0.0, d) + exp(-d * d * 40.0) * 0.25) * tw;
    }

    col += float3(0.6, 0.45, 0.3) * mouseInfluence(p, 0.15) * 0.08;
    float v = smoothstep(1.35, 0.25, length((uv - 0.5) * float2(1.1, 1.0)));
    return col * lerp(0.65, 1.0, v);
}
