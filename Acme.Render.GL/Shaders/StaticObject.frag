#version 300 es
precision highp float;
precision highp int;
precision highp sampler2D;
precision highp sampler2DArray;

in vec3 Normal;
in vec2 TexCoord;
in float TextureIndex;
in float LightingFactor;
in vec3 vWorldPos;
in float vAlpha;

uniform sampler2DArray uTextureArray;
uniform vec3 uHighlightColor;
uniform float uHighlightIntensity;
uniform vec3 uCameraPosition;

// Fog
uniform bool uFogEnabled;
uniform vec3 uFogColor;
uniform float uFogStart;
uniform float uFogEnd;

out vec4 FragColor;

void main() {
    vec4 color = texture(uTextureArray, vec3(TexCoord, TextureIndex));
    // For normal objects vAlpha == 1.0; for animated particles vAlpha is the interpolated translucency.
    float effectiveAlpha = color.a * vAlpha;
    if (effectiveAlpha < 0.01) discard;
    color.rgb *= clamp(LightingFactor, 0.0, 1.0);

    if (uHighlightIntensity > 0.0) {
        vec3 viewDir = normalize(uCameraPosition - vWorldPos);
        float rim = 1.0 - clamp(dot(normalize(Normal), viewDir), 0.0, 1.0);
        float edge = smoothstep(0.2, 0.8, rim);
        color.rgb = mix(color.rgb, uHighlightColor, uHighlightIntensity * 0.35);
        color.rgb += uHighlightColor * edge * uHighlightIntensity * 0.6;
    }

    if (uFogEnabled) {
        float fogDist = length(vWorldPos.xy - uCameraPosition.xy);
        float fogFactor = clamp((fogDist - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
        color.rgb = mix(color.rgb, uFogColor, fogFactor);
    }

    color.a = effectiveAlpha;
    FragColor = color;
}