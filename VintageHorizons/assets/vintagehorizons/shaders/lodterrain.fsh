#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Fog application and far sky-fade structure adapted from Farseer's region.fsh
// (github.com/ViciousBadger/VSMod-Farseer, MIT, (c) Badgerson). Unlike Farseer we
// have real per-vertex surface colors, shaded with screen-space-derivative normals.

in vec4 worldPos;
in vec4 vertexColor;
in float yLevel;
in vec4 rgbaFog;
in float dist;
in float fogAmount;
in float edgeFade;

uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform vec3 sunPosition;
uniform vec3 sunColor;
uniform float dayLight;

// Live tint table. The alpha byte carries a tint SLOT plus a blend band:
//   0..63    opaque,     slot = alpha
//   64..127  water,      slot = alpha - 64
//   128..191 thin plant, slot = alpha - 128
// Slot 0 is the identity tint. One slot per distinct (climate map, season map) pair,
// because leaves pick a seasonal map per species and water has its own -- a single
// shared foliage tint left every tree the same colour and water untinted grey.
// Sampled at two heights and blended by vertex height: the climate maps are indexed
// by temperature, which drops with altitude, so one sample at the player's feet gave
// mountaintops the same lush green as the valley floor.
const int TINT_SLOTS = 64;
uniform vec4 tintsLow[TINT_SLOTS];
uniform vec4 tintsHigh[TINT_SLOTS];
uniform float tintYLow;
uniform float tintYHigh;
uniform float snowLineY;

// Blend factors per band, now that alpha carries the slot instead of an opacity.
// Flowers are crossed quads in vanilla; as a solid cube they read as a grey blob, so
// they are drawn mostly see-through and the ground shows through them.
const float WATER_ALPHA = 0.66;
const float THIN_ALPHA = 0.30;

// Blocks per column in the section being drawn (1 at level 0, doubling per level).
// Coarse sections merge whole neighbourhoods into one colour, and greedy meshing
// then fuses them into large single-colour quads; a little world-space variation
// scaled to the column size breaks those plates up without inventing detail.
// Scaling by column size is what keeps the pattern roughly constant on screen
// instead of aliasing into shimmer at distance.
uniform float columnBlocks;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

#include noise3d.ash
#include dither.fsh
#include fogandlight.fsh
#include skycolor.fsh
#include underwatereffects.fsh

void main()
{
    if (dist < 0.0 || dist > 1.0) discard;

    // Flat-shaded facet normal from position derivatives - no normals in the mesh.
    vec3 normal = normalize(cross(dFdx(worldPos.xyz), dFdy(worldPos.xyz)));

    float sunAngle = max(0.0, dot(normal, normalize(sunPosition)));
    float shade = 0.55 + 0.45 * sunAngle;

    // Decode the tint slot, then snow line on up-facing terrain.
    float aByte = vertexColor.a * 255.0;
    int slotRaw = int(aByte + 0.5);
    int band = slotRaw / TINT_SLOTS;          // 0 opaque, 1 water, 2 thin plant
    bool translucent = band > 0;
    int slot = clamp(slotRaw - band * TINT_SLOTS, 0, TINT_SLOTS - 1);

    float tintBlend = clamp((yLevel - tintYLow) / max(1.0, tintYHigh - tintYLow), 0.0, 1.0);
    vec3 albedo = vertexColor.rgb * mix(tintsLow[slot].rgb, tintsHigh[slot].rgb, tintBlend);
    float outAlpha = band == 2 ? THIN_ALPHA : (band == 1 ? WATER_ALPHA : 1.0);

    if (!translucent) {
        float upness = clamp(normal.y, 0.0, 1.0);
        float snowMix = smoothstep(snowLineY, snowLineY + 24.0, yLevel) * upness;
        albedo = mix(albedo, vec3(0.93, 0.94, 0.97), snowMix);
    }

    // Water is a smooth surface; only break up land.
    if (!translucent) {
        float period = max(4.0, columnBlocks * 6.0);
        float n = valuenoise(worldPos.xyz / period);
        albedo *= 1.0 + 0.10 * (n - 0.5);
    }

    vec4 terraColor = vec4(albedo, outAlpha);
    terraColor.rgb *= shade * clamp(sunColor * dayLight, 0.02, 1.0);

    terraColor = applyFog(terraColor, fogAmount);
    terraColor = applySpheresFog(terraColor, fogAmount, worldPos.xyz);

    // Approximate the real sky color so the far edge dissolves into the horizon.
    vec4 skyColor = vec4(1.0);
    vec4 skyGlow = vec4(1.0);
    vec3 worldPosInSky = normalize(worldPos.xyz) * 250.0;
    getSkyColorAt(worldPosInSky, sunPosition, 0.25, clamp(dayLight, 0.0, 1.0), horizonFog, skyColor, skyGlow);
    float murkiness = max(0.0, getSkyMurkiness() - 14.0 * fogDensityIn);
    skyColor.rgb = applyUnderwaterEffects(skyColor.rgb, murkiness);
    skyGlow.y *= clamp((dayLight - 0.05) * 2.0 - 50.0 * murkiness, 0.0, 1.0);

    // Dissolve both the far edge of the cache and the edges of the explored area
    // into the sky, so neither ends in a visible wall.
    float fade = max(smoothstep(0.75, 1.0, dist), edgeFade);
    outColor = mix(terraColor, skyColor, fade);
    outGlow = mix(vec4(0.0), skyGlow, fade);

#if SSAOLEVEL > 0
    outGPosition = vec4(0.0);
    outGNormal = vec4(0.0);
#endif
}
