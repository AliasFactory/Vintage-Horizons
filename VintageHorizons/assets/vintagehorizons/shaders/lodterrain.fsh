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

uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform vec3 sunPosition;
uniform vec3 sunColor;
uniform float dayLight;

// Live seasonal state: tints applied to untinted base colors, decoded from the
// alpha byte (255 = none, 254 = grass, 253 = foliage, < 200 = water).
uniform vec3 grassTint;
uniform vec3 foliageTint;
uniform float snowLineY;

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

    // Seasonal tint by class (alpha byte), then snow line on up-facing terrain.
    float aByte = vertexColor.a * 255.0;
    vec3 albedo = vertexColor.rgb;
    float outAlpha = 1.0;

    if (aByte < 200.0) {
        outAlpha = vertexColor.a; // water: translucent, untinted
    } else if (aByte < 253.5) {
        albedo *= foliageTint;
    } else if (aByte < 254.5) {
        albedo *= grassTint;
    }

    if (aByte >= 200.0) {
        float upness = clamp(normal.y, 0.0, 1.0);
        float snowMix = smoothstep(snowLineY, snowLineY + 24.0, yLevel) * upness;
        albedo = mix(albedo, vec3(0.93, 0.94, 0.97), snowMix);
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

    float fade = smoothstep(0.75, 1.0, dist);
    outColor = mix(terraColor, skyColor, fade);
    outGlow = mix(vec4(0.0), skyGlow, fade);

#if SSAOLEVEL > 0
    outGPosition = vec4(0.0);
    outGNormal = vec4(0.0);
#endif
}
