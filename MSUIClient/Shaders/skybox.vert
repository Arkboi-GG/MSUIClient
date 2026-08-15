#version 330 core

// The zone skybox M2 (PLAN_18 Phase 2). Drawn camera-centred, over the sky
// gradient + clouds and before the world, so it reads as an infinitely far sky.
// Emissive/unlit - a skybox is self-illuminated - so only the UV animation and a
// per-batch colour tint reach the fragment stage.

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUv;

uniform mat4 uMVP;
uniform vec2 uUvOffset;

out vec2 vUv;

void main()
{
    gl_Position = uMVP * vec4(aPos, 1.0);
    vUv = aUv + uUvOffset;
}
