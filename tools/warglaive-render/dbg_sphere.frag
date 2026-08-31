#version 330 core
// DEBUG: output the reflection-vector sphere-map env UV AS COLOR (u->red, v->green).
// A gradient across the blade = the streak crosses it as a moving line (not a uniform flash).
in vec3 vWorldPos; in vec3 vNormal; in vec2 vUV; in vec3 vViewPosition; in vec3 vViewNormal;
uniform sampler2D uTexture;
out vec4 FragColor;
void main()
{
    vec3 n = normalize(vViewNormal);
    vec3 incident = normalize(-vViewPosition);   // camera -> surface, in view space
    vec3 r = reflect(incident, n);
    float m = 2.0 * sqrt(r.x*r.x + r.y*r.y + (r.z + 1.0)*(r.z + 1.0));
    vec2 uv = r.xy / max(m, 1e-4) + vec2(0.5);
    FragColor = vec4(uv, 0.0, 1.0);
}
