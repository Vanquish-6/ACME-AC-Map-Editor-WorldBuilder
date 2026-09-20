#version 300 es
precision highp float;

uniform mat4 xWorld;
uniform mat4 xView;
uniform mat4 xProjection;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inTexCoord;
layout(location = 2) in float inTexIndex;

out vec3 vTexCoord;

void main() {
    gl_Position = xProjection * xView * xWorld * vec4(inPosition, 1.0);
    vTexCoord = vec3(inTexCoord, inTexIndex);
}
