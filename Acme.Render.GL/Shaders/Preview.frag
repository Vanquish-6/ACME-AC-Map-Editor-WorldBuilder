#version 300 es
precision highp float;
precision highp sampler2DArray;

uniform sampler2DArray xOverlays;
uniform float uAlpha;
uniform float xAmbient;

in vec3 vTexCoord;

out vec4 FragColor;

void main() {
    // Sample preview texture using array index
    vec4 previewColor = texture(xOverlays, vTexCoord);

    // Apply ghostly effect (slightly desaturated, semi-transparent)
    vec3 desaturated = mix(
        previewColor.rgb,
        vec3(dot(previewColor.rgb, vec3(0.299, 0.587, 0.114))),
        0.3 // 30% desaturation
    );

    FragColor = vec4(desaturated, previewColor.a * uAlpha);
}
