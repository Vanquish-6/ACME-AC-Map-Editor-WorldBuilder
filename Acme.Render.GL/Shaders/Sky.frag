#version 300 es
precision highp float;

uniform vec3 uZenithColor;   // color at the top of the sky
uniform vec3 uHorizonColor;  // color at the horizon

in float vNdcY;

out vec4 FragColor;

void main() {
    // vNdcY in [-1, +1]: remap to [0, 1] and apply a power curve so the
    // horizon band is wider (more haze low down) and the zenith is richer.
    float t = clamp((vNdcY + 1.0) * 0.5, 0.0, 1.0); // 0 = horizon, 1 = zenith
    t = t * t;                                         // ease-in curve
    FragColor = vec4(mix(uHorizonColor, uZenithColor, t), 1.0);
}
