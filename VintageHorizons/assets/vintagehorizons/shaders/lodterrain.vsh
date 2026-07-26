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

// Tint is resolved per VERTEX, not per fragment: the slot is constant across a quad
// and the altitude blend is linear in height, so interpolating the result is
// equivalent and saves two indexed uniform-array lookups per fragment.
// Must equal LodTintRegistry.MaxSlots; LoadShader logs an error if it does not.
const int TINT_SLOTS = 64;
uniform vec4 tintsLow[TINT_SLOTS];
uniform vec4 tintsHigh[TINT_SLOTS];
uniform float tintYLow;
uniform float tintYHigh;

out vec3 tint;
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

    int slotRaw = int(vertexColorIn.a * 255.0 + 0.5);
    int slot = clamp(slotRaw - (slotRaw / TINT_SLOTS) * TINT_SLOTS, 0, TINT_SLOTS - 1);
    float tintBlend = clamp((yLevel - tintYLow) / max(1.0, tintYHigh - tintYLow), 0.0, 1.0);
    tint = mix(tintsLow[slot].rgb, tintsHigh[slot].rgb, tintBlend);

    worldPos = modelMatrix * vec4(vertexPositionIn, 1.0);
    worldPos = applyGlobalWarping(worldPos);

    // 0 at the start of the LOD band (inside vanilla terrain), 1 at the far edge
    float distStart = viewDistance * 0.785;
    float radial = length(worldPos.xz);
    dist = (radial - distStart) / (farViewDistance - distStart - 512.0);

    // Sink LOD terrain into the ground near the transition ring so the seam with real
    // chunks reads as terrain, not a floating shelf.
    //
    // Measured in BLOCKS from the start of the band, not as a fraction of it: dist is
    // normalised over the whole cache, which grows as the player explores, so a
    // fractional ramp changed width depending on how much of the world had been
    // visited -- 86 blocks at a 5000-block edge, 390 at 20000.
    //
    // smoothstep rather than a linear ramp: a straight rise stops dead when it reaches
    // full height and leaves a visible crease right where it finishes. This eases out
    // to zero slope at both ends, so the sink is still there but the top of the bend
    // is not something the eye can catch.
    const float SINK_DEPTH = 5.0;
    const float SINK_FADE_BLOCKS = 110.0;
    float intoBand = radial - distStart;
    worldPos.y -= SINK_DEPTH * (1.0 - smoothstep(0.0, SINK_FADE_BLOCKS, intoBand));

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
