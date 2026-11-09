// Video: the user's own clip (any resolution, 4K included), cover-fitted.
// Cursor: depth-style parallax (the frame is slightly overscanned and shifts against the cursor),
// clicks ripple the image. Params.xy = video width/height.

float3 scene(float2 uv, float2 p)
{
    float2 videoSize = max(Params.xy, float2(1, 1));
    float videoAspect = videoSize.x / videoSize.y;
    float active = MouseActive * Intensity;

    // Cover fit.
    float2 st = uv - 0.5;
    if (videoAspect > Aspect) st.x *= Aspect / videoAspect;
    else st.y *= videoAspect / Aspect;

    // Overscan 4% and shift opposite to the cursor for parallax.
    float zoom = 1.0 + 0.04 * active;
    st /= zoom;
    st -= (Mouse - 0.5) * 0.035 * active;

    float rip = ripples(p);
    float2 dir = normalize(p - mouseP() + 1e-4);
    st += dir * rip * 0.004 * float2(1.0 / Aspect, -1.0);

    float3 col = SourceTex.SampleLevel(LinearClamp, st + 0.5, 0).rgb;
    float v = smoothstep(1.4, 0.35, length(uv - 0.5));
    return col * lerp(0.85, 1.0, v);
}
