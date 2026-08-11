#version 330 core

// MSUI Client - collision debug wireframe, fragment stage.
//
// Green means the character controller would treat this surface as standable,
// red means it would treat it as a wall. The threshold is the SAME value
// ResolveGround and MoveHorizontal test against, passed in as uSlopeLimit, so
// the colours are not decorative - they are the decision the controller makes.
//
// uSourceFilter isolates one model instance. Negative draws everything.
//
// ASCII ONLY - see collision.vert.

in vec3 vWorldPos;
flat in float vNormalZ;
flat in float vSource;

uniform vec3  uCameraPos;
uniform float uSlopeLimit;    // cos(maxSlopeDegrees)
uniform float uFadeStart;
uniform float uFadeEnd;
uniform float uSourceFilter;  // -1 draws all
uniform int   uHighlight;     // 1 = yellow physics surface, 2 = cyan player marker, 3 = red aggro beam

out vec4 FragColor;

void main()
{
    if (uHighlight == 1)
    {
        FragColor = vec4(1.0, 0.95, 0.15, 1.0);
        return;
    }

    if (uHighlight == 2)
    {
        FragColor = vec4(0.20, 0.85, 1.0, 1.0);
        return;
    }

    if (uHighlight == 3)
    {
        FragColor = vec4(1.0, 0.22, 0.12, 1.0);
        return;
    }

    if (uSourceFilter >= 0.0 && abs(vSource - uSourceFilter) > 0.5) discard;

    vec3 wall      = vec3(0.95, 0.25, 0.25);
    vec3 standable = vec3(0.30, 0.95, 0.40);

    vec3 color = vNormalZ > uSlopeLimit ? standable : wall;

    // Dim with distance rather than blending, so no sorted pass is needed.
    float dist = distance(uCameraPos, vWorldPos);
    float t = clamp((dist - uFadeStart) / max(uFadeEnd - uFadeStart, 1.0), 0.0, 1.0);

    FragColor = vec4(color * mix(1.0, 0.18, t), 1.0);
}
