#version 300 es
precision highp float;

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec4 aInstanceData; // xyz = position, w = radius

uniform mat4 uViewProjection;

out vec3 vNormal;
out vec3 vFragPos;

void main() {
    // Scale the sphere by per-instance radius and translate to instance position
    vec3 scaledPosition = aPosition * aInstanceData.w + aInstanceData.xyz;
    gl_Position = uViewProjection * vec4(scaledPosition, 1.0);
    vFragPos = scaledPosition;
    vNormal = normalize(aNormal);
}