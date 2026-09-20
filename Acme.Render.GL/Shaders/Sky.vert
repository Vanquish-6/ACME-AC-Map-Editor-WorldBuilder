#version 300 es
precision highp float;

// Full-screen triangle-pair written directly in clip space.
// Vertex positions are passed in NDC so no matrices are needed.
layout(location = 0) in vec2 aPosition;

out float vNdcY; // -1 (bottom / horizon) to +1 (top / zenith)

void main() {
    gl_Position = vec4(aPosition, 0.9999, 1.0); // behind everything (near far plane)
    vNdcY = aPosition.y;
}
