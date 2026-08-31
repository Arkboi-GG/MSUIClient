#version 330 core

// Isolated current MSUIClient environment-map sampling.  The harness draws
// the real environment batch with the production vertex shader and the same
// SrcAlpha/One state, but onto black so only ArmorReflect3 is visible.
in vec3 vViewPosition;
in vec3 vViewNormal;
uniform sampler2D uTexture;
out vec4 FragColor;

void main()
{
    vec3 viewDir = normalize(vViewPosition);
    vec3 x = normalize(vec3(viewDir.z, 0.0, -viewDir.x));
    vec3 y = cross(viewDir, x);
    vec3 n = normalize(vViewNormal);
    vec2 uv = vec2(dot(x, n), dot(y, n)) * 0.495 + vec2(0.5);
    FragColor = texture(uTexture, uv);
}
