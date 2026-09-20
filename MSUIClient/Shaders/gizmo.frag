#version 330 core

// MSUI Client - creator gizmo lines, fragment stage. Flat vertex colour;
// the alpha carries the dim/bold state of the line.
//
// ASCII ONLY - see gizmo.vert.

in vec4 vColor;

out vec4 FragColor;

void main()
{
    FragColor = vColor;
}
