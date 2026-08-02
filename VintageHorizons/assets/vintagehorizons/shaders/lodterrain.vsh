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

// Which of the four sides of this section touch an area with NO captured data.
// The order is -X, +X, -Z, +Z, and a value of 1 means open.
//
// This mod is client-side only. Thus its coverage is what the server streamed to
// it, and the cache stops in the air at the edges of the area that the player
// visited. The shader fades those boundaries into the horizon. It does not leave
// them as cliffs.
uniform vec4 openEdges;
uniform float sectionSize;

// The shader finds the tint for each VERTEX, and not for each fragment. The slot is
// the same across one quad, and the altitude blend is linear in the height. Thus an
// interpolation of the result gives the same value, and it removes two indexed
// lookups in a uniform array for each fragment.
//
// CAUTION: This value must equal LodTintRegistry.MaxSlots. LoadShader records an
// error when it does not.
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

    // This is 0 at the start of the LOD band, which is inside the vanilla terrain.
    // It is 1 at the far edge.
    float distStart = viewDistance * 0.785;
    float radial = length(worldPos.xz);
    dist = (radial - distStart) / (farViewDistance - distStart - 512.0);

    // Move the LOD terrain down into the ground near the transition ring. Thus the
    // seam with the real chunks looks like terrain, and not like a shelf in the air.
    //
    // The distance is in BLOCKS from the start of the band. It is not a fraction of the
    // band. The value dist is normalized over the full cache, and that cache grows as
    // the player explores.
    //
    // Thus a ramp that used a fraction changed its width with the quantity of the world
    // that the player visited. It was 86 blocks at an edge of 5000 blocks, and 390
    // blocks at 20000.
    //
    // This code uses smoothstep, and not a linear ramp. A straight rise stops at its
    // full height, and it leaves a visible line at that point. smoothstep reaches zero
    // slope at both ends. Thus the terrain still moves down, and the eye cannot find
    // the top of the bend.
    const float SINK_DEPTH = 5.0;
    const float SINK_FADE_BLOCKS = 110.0;
    float intoBand = radial - distStart;
    worldPos.y -= SINK_DEPTH * (1.0 - smoothstep(0.0, SINK_FADE_BLOCKS, intoBand));

    // The distance into the section from each open side, as a ramp from 0 to 1 over
    // the outer third. A vertex position is local to the section. Thus this value is
    // the local x or z.
    float fadeWidth = max(8.0, sectionSize * 0.34);
    vec4 inset = vec4(
        vertexPositionIn.x,
        sectionSize - vertexPositionIn.x,
        vertexPositionIn.z,
        sectionSize - vertexPositionIn.z);
    vec4 nearness = clamp(1.0 - inset / fadeWidth, 0.0, 1.0) * openEdges;
    edgeFade = max(max(nearness.x, nearness.y), max(nearness.z, nearness.w));

    // Do this past the transition ring only. Real chunks cover the terrain beside the
    // player. A fade there looks like a hole, and not like haze.
    edgeFade *= clamp(dist * 4.0, 0.0, 1.0);

    fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
    rgbaFog = rgbaFogIn;

    vec4 camPos = viewMatrix * worldPos;
    gl_Position = projectionMatrix * camPos;
}
