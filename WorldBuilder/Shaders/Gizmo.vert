#version 300 es
precision highp float;

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aColor;

uniform mat4 uViewProjection;
uniform mat4 uModel;

out vec3 vColor;
out vec3 vWorldPos;
out vec3 vLocalPos;

void main() {
    vec4 worldPos = uModel * vec4(aPosition, 1.0);
    gl_Position = uViewProjection * worldPos;
    vColor = aColor;
    vWorldPos = worldPos.xyz;
    vLocalPos = aPosition;
}
