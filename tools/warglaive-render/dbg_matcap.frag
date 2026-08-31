#version 330 core
// DEBUG: output the matcap (normal-only) env UV AS COLOR (u->red, v->green) to show
// how much the sampling varies across the blade. Flat color = whole-blade uniform = pulse.
in vec3 vWorldPos; in vec3 vNormal; in vec2 vUV; in vec3 vViewPosition; in vec3 vViewNormal;
uniform sampler2D uTexture;
out vec4 FragColor;
void main()
{
    vec3 viewDir = normalize(vViewPosition);
    vec3 x = normalize(vec3(viewDir.z, 0.0, -viewDir.x));
    vec3 y = cross(viewDir, x);
    vec3 n = normalize(vViewNormal);
    vec2 uv = vec2(dot(x, n), dot(y, n)) * 0.495 + vec2(0.5);
    FragColor = vec4(uv, 0.0, 1.0);
}
