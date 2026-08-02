#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// The fog and the structure of the far sky fade come from region.fsh of Farseer
// (github.com/ViciousBadger/VSMod-Farseer, MIT, (c) Badgerson).
//
// This shader is different from Farseer in one way. It has a real surface color for
// each vertex, and it shades with normals from the screen-space derivatives.

in vec4 worldPos;
in vec4 vertexColor;
in float yLevel;
in vec4 rgbaFog;
in float dist;
in float fogAmount;
in float edgeFade;
in vec3 tint;

uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform vec3 sunPosition;
uniform vec3 sunColor;
uniform float dayLight;

// The live tint table. The alpha byte carries a tint SLOT and a blend band:
//   0..63    opaque,     slot = alpha
//   64..127  water,      slot = alpha - 64
//   128..191 thin plant, slot = alpha - 128
//
// Slot 0 is the tint that changes nothing. There is one slot for each distinct pair
// of a climate map and a season map. Leaves take a season map for each species, and
// water has its own map. One shared tint for foliage gave each tree the same color,
// and it left water grey with no tint.
//
// The mod samples the table at two heights, and it blends by the height of the
// vertex. The climate maps use the temperature as their index, and the temperature
// decreases with the altitude. Thus one sample at the feet of the player gave a
// mountain top the same green as the valley floor.
//
// CAUTION: This value must equal LodTintRegistry.MaxSlots. LoadShader records an
// error when it does not.
const int TINT_SLOTS = 64;
uniform float snowLineY;

// The blend factor for each band. The alpha now carries the slot, and not an
// opacity.
//
// In vanilla, a flower is a pair of crossed quads. As a solid cube it looks like a
// grey shape. Thus the shader draws it mostly transparent, and the ground is visible
// through it.
const float WATER_ALPHA = 0.66;
const float THIN_ALPHA = 0.50;

// The number of blocks in one column of the section that the shader draws. It is 1
// at level 0, and it doubles at each level.
//
// A coarse section merges a full neighbourhood into one color. Then the greedy mesh
// joins those into large quads of one color. A small variation in world space, at
// the scale of the column, divides those plates. It invents no detail.
//
// The scale by the column size keeps the pattern approximately constant on the
// screen. Without that scale, the pattern becomes a shimmer at a distance.
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

    // A flat normal for the facet, from the derivatives of the position. The mesh
    // holds no normals.
    vec3 normal = normalize(cross(dFdx(worldPos.xyz), dFdy(worldPos.xyz)));

    float sunAngle = max(0.0, dot(normal, normalize(sunPosition)));
    float shade = 0.55 + 0.45 * sunAngle;

    // Decode the tint slot. Then apply the snow line to the terrain that faces up.
    // Only the blend band is necessary here. The tint itself arrives interpolated.
    int band = int(vertexColor.a * 255.0 + 0.5) / TINT_SLOTS;  // 0 opaque, 1 water, 2 thin
    bool translucent = band > 0;

    vec3 albedo = vertexColor.rgb * tint;
    float outAlpha = band == 2 ? THIN_ALPHA : (band == 1 ? WATER_ALPHA : 1.0);

    if (!translucent) {
        float upness = clamp(normal.y, 0.0, 1.0);
        float snowMix = smoothstep(snowLineY, snowLineY + 24.0, yLevel) * upness;
        albedo = mix(albedo, vec3(0.93, 0.94, 0.97), snowMix);
    }

    // Water is a smooth surface. Divide the land only.
    if (!translucent) {
        float period = max(4.0, columnBlocks * 6.0);
        float n = valuenoise(worldPos.xyz / period);
        albedo *= 1.0 + 0.10 * (n - 0.5);
    }

    vec4 terraColor = vec4(albedo, outAlpha);
    terraColor.rgb *= shade * clamp(sunColor * dayLight, 0.02, 1.0);

    terraColor = applyFog(terraColor, fogAmount);
    terraColor = applySpheresFog(terraColor, fogAmount, worldPos.xyz);

    // Approximate the real color of the sky. Thus the far edge fades into the
    // horizon.
    vec4 skyColor = vec4(1.0);
    vec4 skyGlow = vec4(1.0);
    vec3 worldPosInSky = normalize(worldPos.xyz) * 250.0;
    getSkyColorAt(worldPosInSky, sunPosition, 0.25, clamp(dayLight, 0.0, 1.0), horizonFog, skyColor, skyGlow);
    float murkiness = max(0.0, getSkyMurkiness() - 14.0 * fogDensityIn);
    skyColor.rgb = applyUnderwaterEffects(skyColor.rgb, murkiness);
    skyGlow.y *= clamp((dayLight - 0.05) * 2.0 - 50.0 * murkiness, 0.0, 1.0);

    // Fade the far edge of the cache into the sky. Fade the edges of the explored
    // area into the sky also. Thus neither one ends as a visible wall.
    float fade = max(smoothstep(0.75, 1.0, dist), edgeFade);
    outColor = mix(terraColor, skyColor, fade);
    outGlow = mix(vec4(0.0), skyGlow, fade);

#if SSAOLEVEL > 0
    outGPosition = vec4(0.0);
    outGNormal = vec4(0.0);
#endif
}
