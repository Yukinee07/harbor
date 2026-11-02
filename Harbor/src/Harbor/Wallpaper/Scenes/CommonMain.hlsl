// Appended after the scene source.

float4 PSMain(VSOut i) : SV_Target
{
    float2 uv = i.uv;
    float3 col = grade(scene(uv, toP(uv)));
    // Triangular dither removes banding from the long smooth gradients these scenes are made of.
    float n = hash12(i.pos.xy + frac(Time * 7.13) * 97.0) + hash12(i.pos.yx * 1.37 + 11.0) - 1.0;
    col += n / 255.0;
    return float4(saturate(col), 1.0);
}

