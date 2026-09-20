#version 300 es
precision highp float;

in vec3 vColor;
in vec3 vWorldPos;
in vec3 vLocalPos;

uniform float uAlpha;
uniform float uBrightness;
uniform vec3 uHighlightColor;
uniform float uHighlightMix;

out vec4 FragColor;

void main() {
    vec3 baseColor = vColor * uBrightness;
    vec3 finalColor = mix(baseColor, uHighlightColor, uHighlightMix);

    float edgeDarken = 1.0 - smoothstep(0.0, 0.6, length(vLocalPos) * 0.15);
    finalColor *= mix(0.92, 1.0, edgeDarken);

    FragColor = vec4(finalColor, uAlpha);
}
