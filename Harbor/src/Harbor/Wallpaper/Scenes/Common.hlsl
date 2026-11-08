// Shared header for Harbor wallpaper scenes. A scene implements:
//     float3 scene(float2 uv, float2 p)
// uv: 0..1 texture coordinates (y down). p: aspect-correct, centered, y up (height spans -0.5..0.5).

cbuffer Frame : register(b0)
{
    float2 Resolution;
    float  Time;          // animation time in seconds (already scaled by speed, frozen while paused)
    float  Delta;
    float2 Mouse;         // smoothed cursor, uv space
    float2 MouseVel;      // smoothed cursor velocity, uv per second
    float  Intensity;     // how strongly scenes react to the cursor (0 = not at all)
    float  Aspect;        // width / height
    float  MouseActive;   // 0..1, fades out when interactivity is off
    float  Interact;      // 0..1, rises while the cursor is moving over the desktop
    float4 Audio;         // x bass, y mids, z treble, w level; all 0 when music reactivity is off
    float4 Grade;         // x: hue shift (radians), y: saturation, z: brightness, w: unused
    float4 Params;        // scene specific
    float4 Ripples[4];    // xy: uv, z: age in seconds, w: strength (0 = unused)
};

Texture2D    SourceTex : register(t0);
Texture2D    NoiseTex : register(t1);      // 256x256 tileable random values
Texture2D    SimTex : register(t2);        // simulation state for simulated scenes
Texture2D    BakeFar : register(t3);       // layers a scene painted once, premultiplied alpha
Texture2D    BakeTree : register(t4);
Texture2D    BakeNear : register(t5);
SamplerState LinearClamp : register(s0);
SamplerState LinearWrap : register(s1);

static const float PI = 3.14159265;
static const float TAU = 6.2831853;

struct VSOut
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.uv = uv;
    return o;
}

float2 toP(float2 uv) { return float2((uv.x - 0.5) * Aspect, 0.5 - uv.y); }
float2 mouseP() { return toP(Mouse); }

float hash12(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float2 hash22(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.xx + p3.yz) * p3.zy);
}

// Value noise from one filtered texture fetch: snapping to texel centres and re-easing the fraction
// gives the same smooth result as four hashes, at a fraction of the cost (it matters at 4K).
float noise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    return NoiseTex.SampleLevel(LinearWrap, (i + f + 0.5) / 256.0, 0).r;
}

// Exact (ALU) value noise. Texture filtering quantises interpolation weights to 1/256, which is
// invisible in colour but shows up as hatching when a scene draws iso-lines of the noise.
float noiseHq(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(hash12(i), hash12(i + float2(1, 0)), u.x),
                lerp(hash12(i + float2(0, 1)), hash12(i + float2(1, 1)), u.x), u.y);
}

static const float2x2 OctaveRot = float2x2(0.80, -0.60, 0.60, 0.80);

float fbm3hq(float2 p)
{
    float v = 0, a = 0.5;
    [unroll] for (int i = 0; i < 3; i++) { v += a * noiseHq(p); p = mul(OctaveRot, p) * 2.03 + 17.1; a *= 0.5; }
    return v;
}

float fbm3(float2 p)
{
    float v = 0, a = 0.5;
    [unroll] for (int i = 0; i < 3; i++) { v += a * noise(p); p = mul(OctaveRot, p) * 2.03 + 17.1; a *= 0.5; }
    return v;
}

float fbm5(float2 p)
{
    float v = 0, a = 0.5;
    [unroll] for (int i = 0; i < 5; i++) { v += a * noise(p); p = mul(OctaveRot, p) * 2.03 + 17.1; a *= 0.5; }
    return v;
}

float3 palette(float t, float3 a, float3 b, float3 c, float3 d) { return a + b * cos(TAU * (c * t + d)); }

// Expanding ring left by a click, as a signed displacement amount at p.
float rippleAt(float2 p, float4 r)
{
    if (r.w <= 0.0) return 0.0;
    float2 c = toP(r.xy);
    float d = length(p - c);
    float age = r.z;
    float radius = age * 0.42;
    float ring = exp(-pow((d - radius) * 18.0, 2.0));
    return sin((d - radius) * 70.0) * ring * exp(-age * 1.7) * r.w;
}

float ripples(float2 p)
{
    float v = 0;
    [unroll] for (int i = 0; i < 4; i++) v += rippleAt(p, Ripples[i]);
    return v * MouseActive;
}

// Soft influence of the cursor: 1 at the cursor, fading to 0 at `radius`.
float mouseInfluence(float2 p, float radius)
{
    float d = length(p - mouseP());
    return MouseActive * Intensity * exp(-d * d / (radius * radius));
}

float3 grade(float3 col)
{
    // Hue rotation in YIQ, then saturation and brightness.
    const float3x3 toYIQ = float3x3(0.299, 0.587, 0.114, 0.596, -0.274, -0.322, 0.211, -0.523, 0.312);
    const float3x3 toRGB = float3x3(1.0, 0.956, 0.621, 1.0, -0.272, -0.647, 1.0, -1.106, 1.703);
    float3 yiq = mul(toYIQ, col);
    float h = Grade.x, s = Grade.y;
    float c = cos(h), sn = sin(h);
    yiq.yz = float2(yiq.y * c - yiq.z * sn, yiq.y * sn + yiq.z * c) * s;
    return max(mul(toRGB, yiq), 0.0) * Grade.z;
}
