#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Fog handling, transition push-down and structure adapted from Farseer's region.vsh
// (github.com/ViciousBadger/VSMod-Farseer, MIT, (c) Badgerson).

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec4 vertexColorIn;

uniform mat4 modelMatrix;
uniform mat4 viewMatrix;
uniform mat4 projectionMatrix;

uniform vec4 rgbaFogIn;
uniform float fogMinIn;
uniform float fogDensityIn;

uniform float farViewDistance;

// Which of this section's four sides border on area we have NO captured data for
// (-X, +X, -Z, +Z; 1 = open). Client-side-only means coverage is whatever the
// server has streamed us, so the cache genuinely runs out mid-air along the edges
// of wherever the player has been. Those boundaries are dissolved into the
// horizon rather than left standing as cliffs.
uniform vec4 openEdges;
uniform float sectionSize;

out vec4 worldPos;
out vec4 vertexColor;
out float yLevel;
out vec4 rgbaFog;
out float dist;
out float fogAmount;
out float edgeFade;

#include vertexflagbits.ash
#include colorutil.ash
#include shadowcoords.vsh
#include fogandlight.vsh
#include vertexwarp.vsh

void main()
{
    yLevel = vertexPositionIn.y;
    vertexColor = vertexColorIn;

    worldPos = modelMatrix * vec4(vertexPositionIn, 1.0);
    worldPos = applyGlobalWarping(worldPos);

    // 0 at the start of the LOD band (inside vanilla terrain), 1 at the far edge
    float distStart = viewDistance * 0.785;
    dist = (length(worldPos.xz) - distStart) / (farViewDistance - distStart - 512.0);

    // Sink LOD terrain into the ground near the transition ring so the seam with
    // real chunks reads as terrain, not a floating shelf.
    worldPos.y -= max(0.0, mix(5.0, 0.0, dist * 50.0));

    // Distance into the section from each open side, as a 0..1 ramp over the outer
    // third. Vertex positions are section-local, so this is just the local x/z.
    float fadeWidth = max(8.0, sectionSize * 0.34);
    vec4 inset = vec4(
        vertexPositionIn.x,
        sectionSize - vertexPositionIn.x,
        vertexPositionIn.z,
        sectionSize - vertexPositionIn.z);
    vec4 nearness = clamp(1.0 - inset / fadeWidth, 0.0, 1.0) * openEdges;
    edgeFade = max(max(nearness.x, nearness.y), max(nearness.z, nearness.w));

    // Only past the transition ring: terrain beside the player is covered by real
    // chunks, and dissolving it there would read as a hole rather than haze.
    edgeFade *= clamp(dist * 4.0, 0.0, 1.0);

    fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
    rgbaFog = rgbaFogIn;

    vec4 camPos = viewMatrix * worldPos;
    gl_Position = projectionMatrix * camPos;
}
