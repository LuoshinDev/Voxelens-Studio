cbuffer CameraBuffer : register(b0)
{
    row_major float4x4 ViewProjection;
    row_major float4x4 InverseViewProjection;
    row_major float4x4 SunViewProjection;
    row_major float4x4 ReflectionViewProjection;
    row_major float4x4 ReflectionViewProjection1;
    row_major float4x4 ReflectionViewProjection2;
    row_major float4x4 ReflectionViewProjection3;
    float4 ReflectionPlaneHeights;
    float4 CameraPosition;
    // xyz = translation-invariant camera axes. Right/Up w contain horizontal/vertical projection scale.
    float4 CameraRightAndHorizontalScale;
    float4 CameraUpAndVerticalScale;
    float4 CameraForward;
    float4 SunDirectionAndStrength;
    float4 MoonDirectionAndStrength;
    float4 SunlightColor;
    float4 MoonlightColor;
    float4 AmbientColorAndStrength;
    float4 SkyZenithColor;
    float4 SkyHorizonColor;
    float4 FogColorAndDensity;
    // x = elapsed seconds, y = hour, z = clouds enabled, w = rain enabled.
    float4 EnvironmentOptions;
    // x/y = viewport pixels, z = terrain mode, w = star visibility.
    float4 ViewportOptions;
    // x = shadow texel size, y = depth bias, z = enabled, w = daylight factor.
    float4 ShadowOptions;
    // x = fall speed, y = vertical span, z = top offset, w = horizontal radius.
    float4 RainOptions;
    // x = enhanced lighting enabled, y = exposure, z = saturation, w = maximum vignette strength.
    float4 QualityOptions;
    // x = water plane Y, y = reflection pass, z = planar reflection available.
    float4 ReflectionOptions;
    // xy = solar UV, zw = projected square-disc radii; x option = horizon/viewport fade and test enable.
    float4 SunOpticsProjection;
    float4 SunOpticsOptions;
};

cbuffer MaterialBuffer : register(b1)
{
    // x = alpha cutoff, y = opaque scene available, z = logical atlas columns (0 = standalone), w = tile pixels.
    float4 MaterialOptions;
};

Texture2D BlockAtlas : register(t0);
Texture2D<float> SunShadowMap : register(t1);
Texture2D SceneColor : register(t2);
Texture2D SceneSurface : register(t3);
Texture2D<float> SceneDepth : register(t4);
Texture2DArray PlanarReflection : register(t5);
Texture2D SceneMaterial : register(t6);
Texture2D OpaqueSceneColor : register(t7);
Texture2D<float> OpaqueSceneDepth : register(t8);
Texture2D<float2> SolarVisibility : register(t9);
cbuffer ObserverBodyShadowBuffer : register(b2)
{
    row_major float4x4 ObserverBodySunViewProjection;
    float4 ObserverBodyShadowOptions;
};
Texture2D<float> ObserverBodyShadowMap : register(t12);
cbuffer ReflectionCoverageBuffer : register(b3)
{
    float4 ReflectionCoverageOptions;
};
Texture2D<float> ReflectionCoverageMask : register(t10);
cbuffer PlanarRefractionBuffer : register(b4)
{
    float4 PlanarRefractionHeights[4];
    // x = completed background count, y = background clip height, z = background pass.
    float4 PlanarRefractionOptions;
};
Texture2DArray RefractionBackgrounds : register(t13);
Texture2D<float2> EmissionCoverage : register(t14);
Texture2D<float2> SunShadowRanges : register(t15);
RWTexture2D<float2> EffectRangeOutput : register(u0);
groupshared float2 EffectRangeShared[256];
Texture2D StaticSky : register(t17);
SamplerState BlockSampler : register(s0);
SamplerComparisonState SunShadowSampler : register(s1);
SamplerState SceneSampler : register(s2);

struct VertexInput
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float4 Color : COLOR;
    float2 TextureCoordinate : TEXCOORD0;
    float4 TextureRegion : TEXCOORD1;
    float Shade : TEXCOORD2;
    // x = sky light, y = block light, z = vertex AO, w = source emission.
    float4 Lighting : TEXCOORD3;
    // bit 0 = water surface.
    float MaterialFlags : TEXCOORD4;
};

struct PixelInput
{
    float4 Position : SV_POSITION;
    float3 Normal : NORMAL;
    float4 Color : COLOR;
    float2 TextureCoordinate : TEXCOORD0;
    float4 TextureRegion : TEXCOORD1;
    float3 WorldPosition : TEXCOORD2;
    float Shade : TEXCOORD3;
    float4 Lighting : TEXCOORD4;
    float4 SunPosition : TEXCOORD5;
    float MaterialFlags : TEXCOORD6;
};

struct ScenePixelOutput
{
    float4 Color : SV_TARGET0;
    // xyz = world normal encoded to 0..1, w = water mask.
    float4 Surface : SV_TARGET1;
    // rgb = linear emissive radiance, w = normalized source emission.
    float4 Material : SV_TARGET2;
};

struct ShadowPixelInput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR;
    float2 TextureCoordinate : TEXCOORD0;
    float4 TextureRegion : TEXCOORD1;
};

struct ScreenPixelInput
{
    float4 Position : SV_POSITION;
    float2 ScreenUv : TEXCOORD0;
};

struct RainPixelInput
{
    float4 Position : SV_POSITION;
    float3 WorldPosition : TEXCOORD0;
    float Opacity : TEXCOORD1;
};

float3 SrgbToLinear(float3 color)
{
    float3 low = color / 12.92f;
    float3 high = pow((color + 0.055f) / 1.055f, 2.4f);
    return lerp(high, low, step(color, 0.04045f));
}

float3 LinearToSrgb(float3 color)
{
    color = max(color, 0.0f);
    float3 low = color * 12.92f;
    float3 high = 1.055f * pow(color, 1.0f / 2.4f) - 0.055f;
    return saturate(lerp(high, low, step(color, 0.0031308f)));
}

float3 AcesToneMap(float3 color)
{
    const float A = 2.51f;
    const float B = 0.03f;
    const float C = 2.43f;
    const float D = 0.59f;
    const float E = 0.14f;
    return saturate((color * (A * color + B)) / (color * (C * color + D) + E));
}

float3 EncodeSceneColor(float3 linearColor)
{
    return QualityOptions.x >= 0.5f ? max(linearColor, 0.0f) : LinearToSrgb(linearColor);
}

PixelInput VSMain(VertexInput input)
{
    PixelInput output;
    output.Position = mul(float4(input.Position, 1.0f), ViewProjection);
    output.Normal = input.Normal;
    output.Color = input.Color;
    output.TextureCoordinate = input.TextureCoordinate;
    output.TextureRegion = input.TextureRegion;
    output.WorldPosition = input.Position;
    output.Shade = input.Shade;
    output.Lighting = input.Lighting;
    output.SunPosition = mul(float4(input.Position, 1.0f), SunViewProjection);
    output.MaterialFlags = input.MaterialFlags;
    return output;
}


ShadowPixelInput ShadowVS(VertexInput input)
{
    ShadowPixelInput output;
    output.Position = mul(float4(input.Position, 1.0f), SunViewProjection);
    output.Color = input.Color;
    output.TextureCoordinate = input.TextureCoordinate;
    output.TextureRegion = input.TextureRegion;
    return output;
}

float2 AtlasUv(float2 textureCoordinate, float4 textureRegion)
{
    uint width, height;
    BlockAtlas.GetDimensions(width, height);
    float2 inset = 0.5f / float2(width, height);
    return textureRegion.xy + inset + frac(textureCoordinate) * max(textureRegion.zw - 2.0f * inset, 0.0f);
}

float4 SampleBlockTexture(float2 textureCoordinate, float4 textureRegion)
{
    if(MaterialOptions.z > 0.5f)
    {
        uint width, height;
        BlockAtlas.GetDimensions(width, height);
        uint logicalColumns = (uint)MaterialOptions.z;
        uint slot = (uint)round(textureRegion.y * logicalColumns) * logicalColumns + (uint)round(textureRegion.x * logicalColumns);
        uint physicalColumns = width / (uint)MaterialOptions.w;
        float2 physicalSize = MaterialOptions.ww / float2(width, height);
        textureRegion = float4(float2(slot % physicalColumns, slot / physicalColumns) * physicalSize, physicalSize);
        if(QualityOptions.x < 0.5f)
        {
            // Standard preview matches the former 32-pixel nearest-neighbour atlas. Quantize within
            // the retained source tile so toggling enhancement never rebuilds world geometry/assets.
            float tilePixels = min(MaterialOptions.w, 32.0f);
            float2 tileUv = floor(frac(textureCoordinate) * tilePixels) / tilePixels;
            return BlockAtlas.SampleLevel(BlockSampler,
                textureRegion.xy + tileUv * textureRegion.zw + 0.5f / float2(width, height), 0.0f);
        }
    }
    // Derivatives must be taken before frac; otherwise every greedy-quad tile seam selects a distant mip.
    return BlockAtlas.SampleGrad(BlockSampler, AtlasUv(textureCoordinate, textureRegion), ddx(textureCoordinate) * textureRegion.zw, ddy(textureCoordinate) * textureRegion.zw);
}

float Hash21(float2 value)
{
    return frac(sin(dot(value, float2(127.1f, 311.7f))) * 43758.5453f);
}

float ValueNoise2D(float2 position)
{
    float2 cell = floor(position);
    float2 local = frac(position);
    float2 blend = local * local * (3.0f - 2.0f * local);
    float lower = lerp(Hash21(cell), Hash21(cell + float2(1.0f, 0.0f)), blend.x);
    float upper = lerp(Hash21(cell + float2(0.0f, 1.0f)), Hash21(cell + 1.0f), blend.x);
    return lerp(lower, upper, blend.y);
}

// Fixed latitude-longitude artwork: independent of world position, altitude and elapsed time.
float3 SampleStaticSky(float3 ray)
{
    float u = frac(atan2(ray.z, ray.x) * 0.159154943f + 0.5f);
    // The painted horizon is at 56% of this panorama. Only sample the upper hemisphere.
    float v = 0.56f - asin(saturate(ray.y)) * (1.12f / 3.14159265f);
    float3 color = StaticSky.SampleLevel(SceneSampler, float2(u, v), 0.0f).rgb;
    // Blend both edges into the same value, avoiding a seam without changing the source artwork.
    float edge = min(u, 1.0f - u);
    if(edge < 0.025f)
    {
        float3 opposite = StaticSky.SampleLevel(SceneSampler, float2(1.0f - u, v), 0.0f).rgb;
        color = lerp((color + opposite) * 0.5f, color, smoothstep(0.0f, 0.025f, edge));
    }
    float3 zenith = StaticSky.SampleLevel(SceneSampler, float2(0.5f, 0.002f), 0.0f).rgb;
    return lerp(color, zenith, smoothstep(0.975f, 0.999f, ray.y));
}

float StaticCloudTransmission(float3 ray)
{
    if(QualityOptions.x < 0.5f || EnvironmentOptions.z < 0.5f || ray.y < 0.02f) return 1.0f;
    float3 color = SampleStaticSky(ray);
    float cloud = smoothstep(0.20f, 0.72f, min(color.r, min(color.g, color.b)));
    return 1.0f - cloud * 0.85f;
}

float3 SkyColor(float3 ray);
float4 ShadowPS(ShadowPixelInput input) : SV_TARGET
{
    float alpha = SampleBlockTexture(input.TextureCoordinate, input.TextureRegion).a * input.Color.a;
    clip(alpha - MaterialOptions.x);
    return 0.0f;
}

float3 ApplyDistanceFog(float3 color, float3 worldPosition)
{
    float3 cameraToSurface = worldPosition - CameraPosition.xyz;
    float distanceToCamera = length(cameraToSurface);
    float fog = 1.0f - exp(-pow(distanceToCamera * FogColorAndDensity.w, 2.0f));
    if(QualityOptions.x >= 0.5f)
    {
        // Enhanced lighting works in linear HDR and is tone-mapped later. Allowing the legacy squared fog
        // curve to approach one replaces the complete distant scene with the bright horizon color, which the
        // exposure pass then turns into a white veil. Use gentler aerial perspective, retain vertical relief,
        // and keep enough scene contrast for silhouettes to remain readable through fog.
        float linearFog = 1.0f - exp(-distanceToCamera * FogColorAndDensity.w * 0.88f);
        float verticalSlope = abs(cameraToSurface.y) / max(distanceToCamera, 0.001f);
        float reliefRetention = lerp(1.0f, 0.62f, smoothstep(0.08f, 0.62f, verticalSlope));
        float densityVariation = lerp(
            0.84f,
            1.08f,
            ValueNoise2D(worldPosition.xz * 0.0065f + EnvironmentOptions.x * float2(0.0018f, 0.0007f)));
        float maximumFog = EnvironmentOptions.w > 0.5f ? 0.64f : 0.52f;
        fog = min(linearFog * reliefRetention * densityVariation, maximumFog);

        float daylight = saturate(ShadowOptions.w);
        float3 atmosphericColor = lerp(SkyZenithColor.rgb, FogColorAndDensity.rgb, 0.38f);
        float targetLuminance = lerp(0.032f, 0.19f, daylight) *
                                (EnvironmentOptions.w > 0.5f ? 0.72f : 1.0f);
        float atmosphericLuminance = max(dot(atmosphericColor, float3(0.2126f, 0.7152f, 0.0722f)), 0.0001f);
        atmosphericColor *= min(1.0f, targetLuminance / atmosphericLuminance);

        float3 viewRay = cameraToSurface / max(distanceToCamera, 0.001f);
        float forwardScattering = pow(saturate(dot(viewRay, normalize(-SunDirectionAndStrength.xyz))), 18.0f);
        atmosphericColor += SunlightColor.rgb * forwardScattering * SunDirectionAndStrength.w * 0.045f;
        return color * (1.0f - saturate(fog)) + atmosphericColor * saturate(fog);
    }
    return lerp(color, FogColorAndDensity.rgb, saturate(fog));
}

float MinecraftFaceLight(float3 normal)
{
    if(normal.y > 0.5f) return 1.0f;
    if(normal.y < -0.5f) return 0.50f;
    if(abs(normal.x) > 0.5f) return 0.60f;
    return 0.80f;
}

float SunShadow(float4 sunPosition, float3 normal)
{
    if(ShadowOptions.z < 0.5f || sunPosition.w <= 0.0f) return 1.0f;
    float3 projected = sunPosition.xyz / sunPosition.w;
    float2 uv = float2(projected.x * 0.5f + 0.5f, 0.5f - projected.y * 0.5f);
    if(projected.z <= 0.0f || projected.z >= 1.0f || any(uv < 0.0f) || any(uv > 1.0f)) return 1.0f;

    float lightFacing = saturate(dot(normal, -SunDirectionAndStrength.xyz));
    float normalBias = ShadowOptions.y * lerp(1.10f, 0.55f, lightFacing);
    float compareDepth = projected.z - normalBias;
    float sunElevation = saturate(-SunDirectionAndStrength.y);
    float filterScale = QualityOptions.x >= 0.5f
        ? lerp(2.35f, 1.25f, smoothstep(0.12f, 0.72f, sunElevation))
        : 1.0f;
    float filterExtent = (filterScale * 2.0f + 0.5f) * ShadowOptions.x;
    float edgeDistance = min(min(uv.x, uv.y), min(1.0f - uv.x, 1.0f - uv.y));
    if(edgeDistance <= filterExtent) return 1.0f;

    float visibility = 0.0f;
    float sampleWeight = 0.0f;
    [unroll]
    for(int y = -2; y <= 2; y++)
    {
        [unroll]
        for(int x = -2; x <= 2; x++)
        {
            if(QualityOptions.x < 0.5f && (abs(x) > 1 || abs(y) > 1)) continue;
            float radiusSquared = (float)(x * x + y * y);
            if(QualityOptions.x >= 0.5f && radiusSquared > 4.01f) continue;
            float weight = QualityOptions.x >= 0.5f ? 1.0f - radiusSquared / 5.0f : 1.0f;
            visibility += weight * SunShadowMap.SampleCmpLevelZero(
                SunShadowSampler,
                uv + float2(x, y) * ShadowOptions.x * filterScale,
                compareDepth);
            sampleWeight += weight;
        }
    }
    float shadowFloor = QualityOptions.x >= 0.5f ? 0.13f : 0.38f;
    float filteredShadow = lerp(shadowFloor, 1.0f, visibility / max(sampleWeight, 0.001f));
    float edgeFade = smoothstep(filterExtent, filterExtent + ShadowOptions.x * 6.0f, edgeDistance);
    return lerp(1.0f, filteredShadow, edgeFade);
}

float3 ReflectedSky(float3 direction)
{
    return SkyColor(direction);
}

ScenePixelOutput BuildSceneOutput(
    float3 color,
    float alpha,
    float3 normal,
    float waterMask,
    float3 emissiveRadiance,
    float emissionStrength)
{
    ScenePixelOutput output;
    output.Color = float4(EncodeSceneColor(color), alpha);
    output.Surface = float4(normalize(normal) * 0.5f + 0.5f, saturate(waterMask));
    output.Material = float4(max(emissiveRadiance, 0.0f), saturate(emissionStrength));
    return output;
}

int ReflectionPlaneIndex(float height)
{
    [unroll] for(int index = 0; index < 4; index++)
        if(index < (int)ReflectionOptions.z && abs(height - ReflectionPlaneHeights[index]) < 0.10f) return index;
    return -1;
}

float4 SamplePlanarReflection(float3 worldPosition, float3 normal)
{
    int planeIndex = ReflectionPlaneIndex(worldPosition.y);
    if(planeIndex < 0) return 0.0f;
    float4 reflectedPosition;
    if(planeIndex == 0) reflectedPosition = mul(float4(worldPosition, 1.0f), ReflectionViewProjection);
    else if(planeIndex == 1) reflectedPosition = mul(float4(worldPosition, 1.0f), ReflectionViewProjection1);
    else if(planeIndex == 2) reflectedPosition = mul(float4(worldPosition, 1.0f), ReflectionViewProjection2);
    else reflectedPosition = mul(float4(worldPosition, 1.0f), ReflectionViewProjection3);
    if(reflectedPosition.w <= 0.0001f) return 0.0f;
    float3 projected = reflectedPosition.xyz / reflectedPosition.w;
    float2 uv = float2(projected.x * 0.5f + 0.5f, 0.5f - projected.y * 0.5f);
    uv += normal.xz * 0.012f;
    if(any(uv <= 0.002f) || any(uv >= 0.998f)) return 0.0f;
    float edgeFade = smoothstep(0.005f, 0.055f, min(min(uv.x, uv.y), min(1.0f - uv.x, 1.0f - uv.y)));
    uint reflectionWidth, reflectionHeight, reflectionLayers;
    PlanarReflection.GetDimensions(reflectionWidth, reflectionHeight, reflectionLayers);
    float2 reflectionTexel = 1.0f / float2(reflectionWidth, reflectionHeight);
    float3 reflection = PlanarReflection.SampleLevel(SceneSampler, float3(uv, planeIndex), 0.0f).rgb * 0.52f;
    reflection += PlanarReflection.SampleLevel(SceneSampler, float3(uv + float2(reflectionTexel.x, 0.0f), planeIndex), 0.0f).rgb * 0.12f;
    reflection += PlanarReflection.SampleLevel(SceneSampler, float3(uv - float2(reflectionTexel.x, 0.0f), planeIndex), 0.0f).rgb * 0.12f;
    reflection += PlanarReflection.SampleLevel(SceneSampler, float3(uv + float2(0.0f, reflectionTexel.y), planeIndex), 0.0f).rgb * 0.12f;
    reflection += PlanarReflection.SampleLevel(SceneSampler, float3(uv - float2(0.0f, reflectionTexel.y), planeIndex), 0.0f).rgb * 0.12f;
    return float4(reflection, edgeFade);
}

void ClipUnneededReflection(float2 pixelPosition)
{
    if(ReflectionOptions.y > 0.5f && ReflectionCoverageOptions.x > 0.5f)
    {
        int2 tile = clamp(int2(pixelPosition / ReflectionCoverageOptions.y), int2(0, 0), int2(ReflectionCoverageOptions.zw) - 1);
        clip(ReflectionCoverageMask.Load(int3(tile, 0)) - 0.5f);
    }
}

int RefractionBackgroundIndex(float height)
{
    if(ReflectionOptions.y > 0.5f) return -1;
    [loop] for(int index = 0; index < min(16, (int)PlanarRefractionOptions.x); index++)
        if(abs(height - PlanarRefractionHeights[index / 4][index % 4]) < 0.002f) return index;
    return -1;
}

float3 SampleRefractionBackground(float2 uv, float height)
{
    int index = RefractionBackgroundIndex(height);
    if(index >= 0) return RefractionBackgrounds.SampleLevel(SceneSampler, float3(uv, index), 0.0f).rgb;
    return OpaqueSceneColor.SampleLevel(SceneSampler, uv, 0.0f).rgb;
}

float ObserverBodyShadow(float3 worldPosition)
{
    [branch] if(ObserverBodyShadowOptions.x < 0.5f) return 1.0f;
    float4 clipPosition = mul(float4(worldPosition, 1.0f), ObserverBodySunViewProjection);
    float3 projected = clipPosition.xyz / max(abs(clipPosition.w), 0.00001f);
    float2 uv = float2(projected.x * 0.5f + 0.5f, 0.5f - projected.y * 0.5f);
    [branch] if(projected.z <= 0.0f || projected.z >= 1.0f || any(uv < 0.0f) || any(uv > 1.0f)) return 1.0f;
    float visibility = 0.0f;
    [unroll] for(int y = -1; y <= 1; y++)
        [unroll] for(int x = -1; x <= 1; x++)
            visibility += ObserverBodyShadowMap.SampleCmpLevelZero(SunShadowSampler,
                uv + float2(x, y) * ObserverBodyShadowOptions.y,
                projected.z - ObserverBodyShadowOptions.z);
    return visibility / 9.0f;
}

ScenePixelOutput ShadeVoxel(PixelInput input)
{
    ClipUnneededReflection(input.Position.xy);
    if(PlanarRefractionOptions.z > 0.5f) clip(PlanarRefractionOptions.y - input.WorldPosition.y - 0.002f);
    float4 texel = SampleBlockTexture(input.TextureCoordinate, input.TextureRegion);
    float alpha = texel.a * input.Color.a;
    clip(alpha - MaterialOptions.x);
    if(ReflectionOptions.y > 0.5f) clip(input.WorldPosition.y - ReflectionOptions.x - 0.035f);

    float3 normal = normalize(input.Normal);
    float waterMask = step(0.5f, input.MaterialFlags);
    float waterSurfaceMask = waterMask * step(0.55f, normal.y);
    if(QualityOptions.x >= 0.5f && waterSurfaceMask > 0.5f)
    {
        float time = EnvironmentOptions.x;
        float longWaveX = sin(input.WorldPosition.x * 0.115f + input.WorldPosition.z * 0.071f + time * 0.72f);
        float longWaveZ = cos(input.WorldPosition.z * 0.123f - input.WorldPosition.x * 0.064f + time * 0.61f);
        float capillaryX = sin(input.WorldPosition.x * 0.43f - input.WorldPosition.z * 0.27f + time * 1.43f);
        float capillaryZ = cos(input.WorldPosition.z * 0.39f + input.WorldPosition.x * 0.24f + time * 1.21f);
        normal = normalize(normal + float3(
            longWaveX * 0.022f + capillaryX * 0.008f,
            0.0f,
            longWaveZ * 0.022f + capillaryZ * 0.008f));
    }
    float3 albedo = texel.rgb * SrgbToLinear(saturate(input.Color.rgb));
    float skyLight = pow(saturate(input.Lighting.x), 1.30f);
    float blockLight = pow(saturate(input.Lighting.y), 1.10f);
    float ao = lerp(1.0f, max(input.Lighting.z, 0.55f), saturate(input.Shade));
    if(QualityOptions.x >= 0.5f) ao = pow(ao, 1.48f);
    float faceLight = lerp(1.0f, MinecraftFaceLight(normal), saturate(input.Shade));
    float ambientVisibility = lerp(0.38f, 1.0f, skyLight);

    float3 towardSun = normalize(-SunDirectionAndStrength.xyz);
    float3 ambient = AmbientColorAndStrength.rgb * AmbientColorAndStrength.w * faceLight * ao * ambientVisibility;
    float sunDiffuse = saturate(dot(normal, towardSun));
    // Sampling the exact receiver position lets large coplanar voxel faces read their own shadow-map depth.
    // Offset in world space before projection so the bias stays stable across face slopes and camera distance.
    float receiverNormalOffset = QualityOptions.x >= 0.5f ? 0.065f : 0.035f;
    float4 receiverSunPosition = mul(float4(input.WorldPosition + normal * receiverNormalOffset, 1.0f), SunViewProjection);
    float shadowVisibility = SunShadow(receiverSunPosition, normal);
    shadowVisibility = min(shadowVisibility, ObserverBodyShadow(input.WorldPosition + normal * 0.003f));
    // Propagated skylight remains authoritative outside the local shadow volume and inside closed rooms.
    float directSkyVisibility = skyLight;
    float3 directSun = SunlightColor.rgb * SunDirectionAndStrength.w * sunDiffuse * directSkyVisibility * shadowVisibility;

    float moonDiffuse = saturate(dot(normal, -MoonDirectionAndStrength.xyz));
    float3 moon = MoonlightColor.rgb * MoonDirectionAndStrength.w * moonDiffuse * skyLight;
    float3 localLight = float3(1.0f, 0.54f, 0.22f) * blockLight * 0.72f * ao;
    float3 groundBounce = 0.0f;
    float3 minimumLight = float3(0.018f, 0.022f, 0.032f);
    if(QualityOptions.x >= 0.5f)
    {
        float daylight = saturate(ShadowOptions.w);
        float nightFactor = 1.0f - daylight;
        float skyHemisphere = saturate(normal.y * 0.5f + 0.5f);
        float sunElevation = saturate(towardSun.y);
        float lowSunCompensation = lerp(1.78f, 1.0f, smoothstep(0.12f, 0.78f, sunElevation));
        ambient = AmbientColorAndStrength.rgb * AmbientColorAndStrength.w * ambientVisibility * ao *
                  lerp(0.46f, 1.0f, skyHemisphere) * lerp(0.28f, 0.76f, daylight);
        groundBounce = SrgbToLinear(float3(0.30f, 0.22f, 0.16f)) * ShadowOptions.w * ao *
                       (1.0f - skyHemisphere) * 0.32f;
        directSun *= 3.55f * lowSunCompensation;
        moon *= 1.42f;
        // Preserve Minecraft's propagated BlockLight field, but keep it local. A sub-linear curve made even weak
        // distant light brighten the entire night scene; this steep curve retains the source halo and real falloff.
        float localLightFalloff = pow(blockLight, 2.65f);
        localLight = float3(1.0f, 0.43f, 0.12f) * localLightFalloff *
                     lerp(0.72f, 2.85f, nightFactor) * pow(ao, 1.72f);
        minimumLight = lerp(float3(0.004f, 0.006f, 0.012f), float3(0.012f, 0.016f, 0.026f), daylight);
    }
    float3 irradiance = max(minimumLight, ambient + groundBounce + directSun + moon + localLight);
    float sourceEmission = saturate(input.Lighting.w);
    float nightFactor = 1.0f - saturate(ShadowOptions.w);
    float emissionGain = QualityOptions.x >= 0.5f
        ? lerp(0.56f, 2.72f, pow(nightFactor, 0.82f))
        : 0.92f;
    float3 emission = albedo * sourceEmission * emissionGain;
    float3 specular = 0.0f;
    if(QualityOptions.x >= 0.5f && SunDirectionAndStrength.w > 0.01f && waterSurfaceMask < 0.5f)
    {
        float3 viewDirection = normalize(CameraPosition.xyz - input.WorldPosition);
        float3 lightDirection = normalize(-SunDirectionAndStrength.xyz);
        float3 halfDirection = normalize(viewDirection + lightDirection);
        float textureVariation = saturate(length(fwidth(texel.rgb)) * 1.7f);
        float smoothness = 1.0f - textureVariation;
        float specularPower = lerp(14.0f, 72.0f, smoothness);
        float fresnel = pow(1.0f - saturate(dot(normal, viewDirection)), 5.0f);
        float specularStrength = lerp(0.018f, 0.085f, smoothness) + fresnel * 0.035f;
        specular = SunlightColor.rgb * pow(saturate(dot(normal, halfDirection)), specularPower) *
                   specularStrength * SunDirectionAndStrength.w * lerp(0.58f, 1.0f, skyLight) * shadowVisibility;
    }
    float3 lit = albedo * irradiance + emission + specular;
    if(QualityOptions.x >= 0.5f && waterSurfaceMask > 0.5f)
    {
        float3 viewDirection = normalize(CameraPosition.xyz - input.WorldPosition);
        float3 reflectionDirection = reflect(-viewDirection, normal);
        float viewFacing = saturate(dot(normal, viewDirection));
        float fresnel = 0.022f + 0.978f * pow(1.0f - viewFacing, 5.0f);
        float opticalPath = rcp(max(viewFacing, 0.12f));
        float waterThickness = opticalPath;
        float3 refractedBackground = lit;
        if(MaterialOptions.y > 0.5f)
        {
            float2 screenUv = saturate(input.Position.xy / max(ViewportOptions.xy, float2(1.0f, 1.0f)));
            float opaqueDepth = OpaqueSceneDepth.SampleLevel(SceneSampler, screenUv, 0.0f);
            if(opaqueDepth < 0.99999f)
            {
                float2 ndc = float2(screenUv.x * 2.0f - 1.0f, 1.0f - screenUv.y * 2.0f);
                float4 opaqueWorld = mul(float4(ndc, opaqueDepth, 1.0f), InverseViewProjection);
                float3 opaquePosition = opaqueWorld.xyz / max(abs(opaqueWorld.w), 0.00001f);
                waterThickness = clamp(length(opaquePosition - input.WorldPosition), 0.05f, 32.0f);
            }
            else
            {
                waterThickness = 18.0f * opticalPath;
            }
            float distortion = lerp(0.0015f, 0.010f, saturate(waterThickness / 12.0f));
            float2 refractedUv = saturate(screenUv + normal.xz * distortion);
            refractedBackground = SampleRefractionBackground(refractedUv, input.WorldPosition.y);
        }
        float3 absorption = exp(-float3(0.16f, 0.055f, 0.025f) * waterThickness);
        float3 waterScatter = SrgbToLinear(float3(0.035f, 0.31f, 0.46f)) * (0.72f + ShadowOptions.w * 0.46f);
        lit = refractedBackground * absorption + waterScatter * (1.0f - absorption);
        float3 reflection = ReflectedSky(reflectionDirection);
        float4 planarReflection = SamplePlanarReflection(input.WorldPosition, normal);
        reflection = lerp(reflection, planarReflection.rgb, planarReflection.a);
        float reflectedSun = saturate(dot(reflectionDirection, towardSun));
        float sunSparkle = pow(reflectedSun, 220.0f) * 1.45f + pow(reflectedSun, 36.0f) * 0.10f;
        float reflectionStrength = saturate(0.035f + fresnel * 0.965f);
        lit = lerp(lit, reflection, reflectionStrength);
        lit += SunlightColor.rgb * sunSparkle * SunDirectionAndStrength.w * shadowVisibility;
        float absorptionOpacity = 1.0f - exp(-0.18f * waterThickness);
        float opticalAlpha = lerp(0.18f, 0.62f, absorptionOpacity);
        alpha = MaterialOptions.y > 0.5f ? 1.0f : max(alpha * 0.42f, opticalAlpha);
    }
    else if(QualityOptions.x >= 0.5f && waterMask > 0.5f) alpha = min(alpha, 0.62f);
    return BuildSceneOutput(
        ApplyDistanceFog(lit, input.WorldPosition),
        alpha,
        normal,
        waterSurfaceMask,
        emission,
        sourceEmission);
}

ScenePixelOutput PSMain(PixelInput input)
{
    return ShadeVoxel(input);
}

ScenePixelOutput GroundPS(PixelInput input)
{
    // Chunk mode is a light, two-sided working plane with emphasized 16x16 boundaries.
    if(ViewportOptions.z < 0.5f)
    {
        float2 cell = frac(input.WorldPosition.xz / 16.0f);
        float edgeDistance = min(min(cell.x, 1.0f - cell.x), min(cell.y, 1.0f - cell.y)) * 16.0f;
        float boundary = 1.0f - smoothstep(0.035f, 0.15f, edgeDistance);
        float3 baseColor = SrgbToLinear(lerp(float3(0.31f, 0.36f, 0.43f), float3(0.52f, 0.60f, 0.70f), boundary));
        float alpha = lerp(0.09f, 0.58f, boundary);
        return BuildSceneOutput(ApplyDistanceFog(baseColor, input.WorldPosition), alpha, input.Normal, 0.0f, 0.0f, 0.0f);
    }
    ScenePixelOutput shaded = ShadeVoxel(input);
    // Minecraft never lets the superflat grass reference disappear into the night sky. Keep its original
    // texture and biome tint visible while still allowing daylight, weather and shadows to brighten it.
    float3 texel = SampleBlockTexture(input.TextureCoordinate, input.TextureRegion).rgb;
    float3 albedo = texel * SrgbToLinear(saturate(input.Color.rgb));
    float3 visibilityFloor = EncodeSceneColor(albedo * 0.24f);
    shaded.Color.rgb = max(shaded.Color.rgb, visibilityFloor);
    return shaded;
}

ScreenPixelInput SkyVS(uint vertexId : SV_VertexID)
{
    // Match the counter-clockwise front-face policy used by voxel geometry.
    float2 uv = float2(vertexId & 2, (vertexId << 1) & 2);
    ScreenPixelInput output;
    // Sky is drawn after opaque geometry at the far plane. Other fullscreen passes disable depth.
    output.Position = float4(uv.x * 2.0f - 1.0f, 1.0f - uv.y * 2.0f, 1.0f, 1.0f);
    output.ScreenUv = uv;
    return output;
}

RainPixelInput RainVS(uint vertexId : SV_VertexID)
{
    uint dropIndex = vertexId >> 1u;
    float endpoint = (float)(vertexId & 1u);
    float drop = (float)dropIndex;
    float xRandom = Hash21(float2(drop, 11.0f));
    float yRandom = Hash21(float2(drop, 37.0f));
    float zRandom = Hash21(float2(drop, 71.0f));
    float lengthRandom = Hash21(float2(drop, 103.0f));
    float opacityRandom = Hash21(float2(drop, 149.0f));

    float2 horizontal = (float2(xRandom, zRandom) - 0.5f) * RainOptions.w * 2.0f;
    float cycle = frac(yRandom + EnvironmentOptions.x * RainOptions.x / RainOptions.y);
    float top = RainOptions.z - cycle * RainOptions.y;
    float streakLength = lerp(2.2f, 5.0f, lengthRandom);
    float3 worldPosition = CameraPosition.xyz + float3(horizontal.x, top - endpoint * streakLength, horizontal.y);

    RainPixelInput output;
    output.Position = mul(float4(worldPosition, 1.0f), ViewProjection);
    output.WorldPosition = worldPosition;
    float radialFade = 1.0f - smoothstep(RainOptions.w * 0.72f, RainOptions.w, length(horizontal));
    output.Opacity = radialFade * lerp(0.72f, 1.0f, opacityRandom);
    return output;
}

float3 WorldRay(float2 screenUv)
{
    float2 ndc = float2(screenUv.x * 2.0f - 1.0f, 1.0f - screenUv.y * 2.0f);
    return normalize(
        CameraForward.xyz +
        CameraRightAndHorizontalScale.xyz * (ndc.x * CameraRightAndHorizontalScale.w) +
        CameraUpAndVerticalScale.xyz * (ndc.y * CameraUpAndVerticalScale.w));
}

float SquareDisc(float3 ray, float3 direction, float halfSize)
{
    float facing = dot(ray, direction);
    float3 reference = abs(direction.y) > 0.94f ? float3(0.0f, 0.0f, 1.0f) : float3(0.0f, 1.0f, 0.0f);
    float3 right = normalize(cross(reference, direction));
    float3 up = normalize(cross(direction, right));
    float2 offset = float2(dot(ray, right), dot(ray, up)) / max(facing, 0.001f);
    return step(0.0f, facing) * step(max(abs(offset.x), abs(offset.y)), halfSize);
}

float StarField(float3 ray)
{
    const float Pi = 3.14159265f;
    float2 sphere = float2(atan2(ray.z, ray.x) / (2.0f * Pi) + 0.5f, asin(clamp(ray.y, -1.0f, 1.0f)) / Pi + 0.5f);
    float2 grid = sphere * float2(420.0f, 210.0f);
    float2 cell = floor(grid);
    float brightness = step(0.992f, Hash21(cell));
    float starPoint = 1.0f - smoothstep(0.04f, 0.18f, length(frac(grid) - 0.5f));
    return brightness * starPoint * smoothstep(-0.08f, 0.20f, ray.y);
}

float CloudCell(float2 cell)
{
    float coarse = Hash21(floor(cell / 6.0f));
    float medium = Hash21(floor((cell + 1.75f) / 3.0f));
    float detail = Hash21(cell);
    return step(0.60f, coarse * 0.56f + medium * 0.30f + detail * 0.14f);
}

float FilteredCloudCells(float2 cloudPosition)
{
    float2 centered = cloudPosition - 0.5f;
    float2 cell = floor(centered);
    float2 local = frac(centered);
    float2 footprint = abs(ddx(cloudPosition)) + abs(ddy(cloudPosition));
    float2 halfFilter = clamp(footprint * 0.5f, 0.001f, 0.49f);
    float2 blend = smoothstep(0.5f - halfFilter, 0.5f + halfFilter, local);

    float lower = lerp(CloudCell(cell), CloudCell(cell + float2(1.0f, 0.0f)), blend.x);
    float upper = lerp(CloudCell(cell + float2(0.0f, 1.0f)), CloudCell(cell + 1.0f), blend.x);
    return lerp(lower, upper, blend.y);
}

struct CloudRayHit
{
    float Coverage;
    float FaceLight;
};

CloudRayHit TraceCloudLayer(float3 ray)
{
    const float CloudBottom = 128.0f;
    const float CloudTop = 132.0f;
    const float CloudCellSize = 10.0f;
    const float MaximumCloudTravel = 1200.0f;

    CloudRayHit miss;
    miss.Coverage = 0.0f;
    miss.FaceLight = 1.0f;
    if(EnvironmentOptions.z < 0.5f || abs(ray.y) < 0.015f) return miss;

    float bottomTravel = (CloudBottom - CameraPosition.y) / ray.y;
    float topTravel = (CloudTop - CameraPosition.y) / ray.y;
    float entryTravel = max(min(bottomTravel, topTravel), 0.0f);
    float exitTravel = max(bottomTravel, topTravel);
    if(exitTravel <= entryTravel || entryTravel > MaximumCloudTravel) return miss;

    float2 motion = float2(EnvironmentOptions.x * 0.85f, 0.0f);
    float2 entryCloudPosition =
        (CameraPosition.xz + ray.xz * entryTravel + motion) / CloudCellSize;
    float2 centeredPosition = entryCloudPosition - 0.5f;
    float2 cell = floor(centeredPosition);
    float horizonVisibility = smoothstep(0.015f, 0.10f, abs(ray.y));

    if(CloudCell(cell) > 0.5f)
    {
        CloudRayHit horizontalFace;
        float opticalCoverage = 1.0f - exp(-(exitTravel - entryTravel) * 0.24f);
        horizontalFace.Coverage = FilteredCloudCells(entryCloudPosition) * opticalCoverage * horizonVisibility *
                                  (1.0f - smoothstep(700.0f, MaximumCloudTravel, entryTravel));
        // A camera below the slab sees its darker underside; a camera above it sees the bright top.
        horizontalFace.FaceLight = CameraPosition.y < CloudBottom
            ? 0.58f
            : CameraPosition.y > CloudTop ? 1.0f : 0.72f;
        return horizontalFace;
    }

    // Walk exact X/Z cell boundaries inside the four-block cloud slab. Four crossings are enough for
    // ordinary view angles, while the near-horizon fade keeps this bounded work from becoming a costly
    // full ray march. Entering a filled neighboring cell is a real cuboid side rather than another sheet.
    float2 cellVelocity = ray.xz / CloudCellSize;
    float2 stepDirection = float2(cellVelocity.x >= 0.0f ? 1.0f : -1.0f,
                                  cellVelocity.y >= 0.0f ? 1.0f : -1.0f);
    float2 boundaryTravel = float2(1.0e20f, 1.0e20f);
    float2 boundaryStride = float2(1.0e20f, 1.0e20f);
    if(abs(cellVelocity.x) > 0.000001f)
    {
        float nextBoundaryX = cell.x + (stepDirection.x > 0.0f ? 1.0f : 0.0f);
        boundaryTravel.x = (nextBoundaryX - centeredPosition.x) / cellVelocity.x;
        boundaryStride.x = abs(1.0f / cellVelocity.x);
    }
    if(abs(cellVelocity.y) > 0.000001f)
    {
        float nextBoundaryZ = cell.y + (stepDirection.y > 0.0f ? 1.0f : 0.0f);
        boundaryTravel.y = (nextBoundaryZ - centeredPosition.y) / cellVelocity.y;
        boundaryStride.y = abs(1.0f / cellVelocity.y);
    }

    float slabTravel = exitTravel - entryTravel;
    [unroll]
    for(int crossing = 0; crossing < 4; crossing++)
    {
        bool crossedX = boundaryTravel.x <= boundaryTravel.y;
        float relativeTravel = crossedX ? boundaryTravel.x : boundaryTravel.y;
        if(relativeTravel > slabTravel || entryTravel + relativeTravel > MaximumCloudTravel) break;

        if(crossedX)
        {
            cell.x += stepDirection.x;
            boundaryTravel.x += boundaryStride.x;
        }
        else
        {
            cell.y += stepDirection.y;
            boundaryTravel.y += boundaryStride.y;
        }

        if(CloudCell(cell) > 0.5f)
        {
            CloudRayHit sideFace;
            float opticalCoverage = 1.0f - exp(-max(slabTravel - relativeTravel, 0.0f) * 0.24f);
            sideFace.Coverage = opticalCoverage * horizonVisibility *
                (1.0f - smoothstep(700.0f, MaximumCloudTravel, entryTravel + relativeTravel));
            // Match Minecraft's directional face contrast: X faces are darker than Z faces.
            sideFace.FaceLight = crossedX ? 0.66f : 0.82f;
            return sideFace;
        }
    }

    return miss;
}

float3 SkyColor(float3 ray)
{
    float daylight = saturate(ShadowOptions.w);
    float vertical = smoothstep(-0.34f, 0.72f, ray.y);
    float3 color = lerp(SkyHorizonColor.rgb * 0.56f, SkyZenithColor.rgb, vertical);
    if(QualityOptions.x >= 0.5f && EnvironmentOptions.z > 0.5f && ray.y > 0.0f)
    {
        float3 painted = SampleStaticSky(ray);
        float3 dayTint = lerp(float3(1.0f, 0.79f, 0.67f), float3(1.0f, 1.0f, 1.0f), daylight);
        float3 nightSky = painted * float3(0.024f, 0.036f, 0.070f);
        float3 cloudSky = lerp(nightSky, painted * dayTint, daylight);
        if(EnvironmentOptions.w > 0.5f)
        {
            float luminance = dot(cloudSky, float3(0.2126f, 0.7152f, 0.0722f));
            cloudSky = lerp(cloudSky, luminance * float3(0.65f, 0.72f, 0.82f), 0.72f) * 0.62f;
        }
        // Keep the original editor background below the horizon. Extending the panorama's
        // horizon row downward creates vertical streaks and a blue, water-like working plane.
        color = lerp(color, cloudSky, smoothstep(0.0f, 0.08f, ray.y));
    }
    float3 sunDirection = normalize(-SunDirectionAndStrength.xyz);
    float3 moonDirection = normalize(-MoonDirectionAndStrength.xyz);
    float transmission = StaticCloudTransmission(ray);
    float sun = SquareDisc(ray, sunDirection, 0.040f);
    float moon = SquareDisc(ray, moonDirection, 0.034f);
    color = lerp(color, SunlightColor.rgb, sun * saturate(SunDirectionAndStrength.w * 1.7f) * transmission);
    if(QualityOptions.x >= 0.5f)
    {
        float sunFacing = saturate(dot(ray, sunDirection));
        color += SunlightColor.rgb * (pow(sunFacing, 28.0f) * 0.18f + pow(sunFacing, 220.0f) * 1.4f) *
            saturate(SunDirectionAndStrength.w) * transmission;
    }
    color = lerp(color, MoonlightColor.rgb, moon * ViewportOptions.w * transmission);
    color += StarField(ray) * ViewportOptions.w * transmission * SrgbToLinear(float3(0.72f, 0.80f, 1.0f));
    if(QualityOptions.x < 0.5f)
    {
        CloudRayHit cloud = TraceCloudLayer(ray);
        float3 cloudColor = EnvironmentOptions.w > 0.5f
            ? SrgbToLinear(float3(0.34f, 0.37f, 0.41f))
            : lerp(SrgbToLinear(float3(0.42f, 0.46f, 0.55f)), SrgbToLinear(float3(0.96f, 0.97f, 1.0f)), ShadowOptions.w);
        cloudColor *= cloud.FaceLight;
        float cloudOpacity = EnvironmentOptions.w > 0.5f ? 0.94f : 0.78f;
        color = lerp(color, cloudColor, cloud.Coverage * cloudOpacity);
    }
    return color;
}

float4 SkyPS(ScreenPixelInput input) : SV_TARGET
{
    ClipUnneededReflection(input.Position.xy);
    return float4(EncodeSceneColor(SkyColor(WorldRay(input.ScreenUv))), 1.0f);
}
float4 RainPS(RainPixelInput input) : SV_TARGET
{
    if(EnvironmentOptions.w < 0.5f) discard;
    float distanceToCamera = length(input.WorldPosition - CameraPosition.xyz);
    float fog = 1.0f - exp(-pow(distanceToCamera * FogColorAndDensity.w, 2.0f));
    float3 rainColor = lerp(SrgbToLinear(float3(0.561f, 0.718f, 0.808f)), FogColorAndDensity.rgb, 0.42f);
    rainColor = lerp(rainColor, FogColorAndDensity.rgb, fog * 0.72f);
    float atmosphericFade = lerp(1.0f, 0.42f, saturate(fog));
    return float4(
        EncodeSceneColor(rainColor),
        input.Opacity * 0.58f * atmosphericFade);
}

float3 BloomContribution(float3 color)
{
    float brightness = max(color.r, max(color.g, color.b));
    return max(color - 0.72f, 0.0f) * smoothstep(0.72f, 1.55f, brightness);
}

float3 ReconstructWorldPosition(float2 uv, float depth)
{
    float2 ndc = float2(uv.x * 2.0f - 1.0f, 1.0f - uv.y * 2.0f);
    float4 world = mul(float4(ndc, depth, 1.0f), InverseViewProjection);
    return world.xyz / max(abs(world.w), 0.00001f);
}

float LoadSceneDepth(float2 uv)
{
    float2 viewportSize = max(ViewportOptions.xy, float2(1.0f, 1.0f));
    int2 pixel = clamp(int2(saturate(uv) * viewportSize), int2(0, 0), int2(viewportSize) - 1);
    return SceneDepth.Load(int3(pixel, 0));
}

bool ProjectWorldPosition(float3 worldPosition, out float2 uv, out float depth)
{
    float4 clip = mul(float4(worldPosition, 1.0f), ViewProjection);
    if(clip.w <= 0.0001f)
    {
        uv = 0.0f;
        depth = 1.0f;
        return false;
    }
    float3 projected = clip.xyz / clip.w;
    uv = float2(projected.x * 0.5f + 0.5f, 0.5f - projected.y * 0.5f);
    depth = projected.z;
    return depth > 0.0f && depth < 1.0f && all(uv > 0.001f) && all(uv < 0.999f);
}

float4 ScreenSpaceReflection(float3 worldPosition, float3 normal)
{
    float3 incident = normalize(worldPosition - CameraPosition.xyz);
    float3 reflectionDirection = normalize(reflect(incident, normal));
    float3 rayOrigin = worldPosition + normal * 0.10f + reflectionDirection * 0.18f;
    [unroll]
    for(int stepIndex = 0; stepIndex < 22; stepIndex++)
    {
        float stepValue = (float)(stepIndex + 1);
        float travel = 0.18f + stepValue * 0.62f;
        float3 rayPosition = rayOrigin + reflectionDirection * travel;
        float2 rayUv;
        float rayDepth;
        if(!ProjectWorldPosition(rayPosition, rayUv, rayDepth)) break;

        float sceneDepth = LoadSceneDepth(rayUv);
        if(sceneDepth >= 0.99999f || sceneDepth >= rayDepth - 0.00003f) continue;
        float sampledWater = SceneSurface.SampleLevel(SceneSampler, rayUv, 0.0f).w;
        if(sampledWater > 0.20f) continue;

        float3 sceneWorld = ReconstructWorldPosition(rayUv, sceneDepth);
        float rayDistance = length(rayPosition - CameraPosition.xyz);
        float sceneDistance = length(sceneWorld - CameraPosition.xyz);
        float depthGap = rayDistance - sceneDistance;
        float thickness = 0.34f + travel * 0.055f;
        if(depthGap > 0.02f && depthGap < thickness)
        {
            float edgeFade = saturate(1.0f - max(abs(rayUv.x - 0.5f), abs(rayUv.y - 0.5f)) * 2.0f);
            float travelFade = 1.0f - stepValue / 25.0f;
            return float4(SceneColor.SampleLevel(SceneSampler, rayUv, 0.0f).rgb, edgeFade * travelFade);
        }
    }
    return 0.0f;
}

float AmbientOcclusionSample(float2 uv, float3 worldPosition, float3 normal, float2 pixelOffset)
{
    float2 viewportSize = max(ViewportOptions.xy, float2(1.0f, 1.0f));
    float2 sampleUv = saturate(uv + pixelOffset / viewportSize);
    int2 samplePixel = clamp(int2(sampleUv * viewportSize), int2(0, 0), int2(viewportSize) - 1);
    float sampleDepth = SceneDepth.Load(int3(samplePixel, 0));
    if(sampleDepth >= 0.99999f) return 0.0f;

    float3 sampleWorld = ReconstructWorldPosition(sampleUv, sampleDepth);
    float3 delta = sampleWorld - worldPosition;
    float distanceToSample = length(delta);
    if(distanceToSample < 0.035f || distanceToSample > 9.0f) return 0.0f;

    float hemisphere = smoothstep(0.035f, 0.52f, dot(normal, delta / distanceToSample));
    float rangeFade = 1.0f - smoothstep(1.4f, 9.0f, distanceToSample);
    return hemisphere * rangeFade;
}

float ScreenSpaceAmbientOcclusion(float2 uv)
{
    float depth = LoadSceneDepth(uv);
    if(depth >= 0.99999f) return 1.0f;

    float3 worldPosition = ReconstructWorldPosition(uv, depth);
    float3 normal = normalize(SceneSurface.SampleLevel(SceneSampler, uv, 0.0f).xyz * 2.0f - 1.0f);
    float radius = lerp(10.0f, 4.0f, saturate(depth));
    float occlusion = 0.0f;
    occlusion += AmbientOcclusionSample(uv, worldPosition, normal, float2( 1.0f,  0.0f) * radius);
    occlusion += AmbientOcclusionSample(uv, worldPosition, normal, float2(-1.0f,  0.0f) * radius);
    occlusion += AmbientOcclusionSample(uv, worldPosition, normal, float2( 0.0f,  1.0f) * radius);
    occlusion += AmbientOcclusionSample(uv, worldPosition, normal, float2( 0.0f, -1.0f) * radius);
    occlusion += AmbientOcclusionSample(uv, worldPosition, normal, float2( 0.72f,  0.72f) * radius * 1.45f);
    occlusion += AmbientOcclusionSample(uv, worldPosition, normal, float2(-0.72f,  0.72f) * radius * 1.45f);
    occlusion += AmbientOcclusionSample(uv, worldPosition, normal, float2( 0.72f, -0.72f) * radius * 1.45f);
    occlusion += AmbientOcclusionSample(uv, worldPosition, normal, float2(-0.72f, -0.72f) * radius * 1.45f);
    return 1.0f - saturate(occlusion * 0.20f) * 0.62f;
}

float EmissionHaloReach(float2 uv, float ringRadius)
{
    float depth = LoadSceneDepth(uv);
    if(depth >= 0.99999f) return 0.0f;
    float3 worldPosition = ReconstructWorldPosition(uv, depth);
    float forwardDistance = max(abs(dot(worldPosition - CameraPosition.xyz, CameraForward.xyz)), 0.25f);
    float projectedBlockPixels = ViewportOptions.y /
        max(2.0f * forwardDistance * CameraUpAndVerticalScale.w, 0.001f);
    float haloRadius = clamp(projectedBlockPixels * 0.22f + 1.75f, 3.0f, 18.0f);
    return 1.0f - smoothstep(haloRadius * 0.72f, haloRadius * 1.08f, ringRadius);
}

float3 SampleEmissionSource(float2 uv, float ringRadius)
{
    float2 sourceUv = saturate(uv);
    float4 source = SceneMaterial.SampleLevel(SceneSampler, sourceUv, 0.0f);
    // The existing smoothstep is exactly zero here. Avoid depth reconstruction for each of the
    // 32 bloom taps on non-emissive foliage, terrain and water without changing any contributing tap.
    [branch] if(source.a <= 0.08f) return 0.0f;
    float peak = max(source.r, max(source.g, source.b));
    float compression = rcp(1.0f + max(peak - 1.15f, 0.0f) * 0.72f);
    return source.rgb * compression * smoothstep(0.08f, 0.62f, source.a) *
           EmissionHaloReach(sourceUv, ringRadius);
}

float3 SampleEmissionRing(float2 uv, float2 texel, float radius, float2 basis)
{
    float2 tangent = float2(-basis.y, basis.x);
    float2 diagonalA = (basis + tangent) * 0.70710678f;
    float2 diagonalB = (basis - tangent) * 0.70710678f;
    float2 basisOffset = basis * texel * radius;
    float2 tangentOffset = tangent * texel * radius;
    float2 diagonalAOffset = diagonalA * texel * radius;
    float2 diagonalBOffset = diagonalB * texel * radius;
    float3 sum = SampleEmissionSource(uv + basisOffset, radius);
    sum += SampleEmissionSource(uv - basisOffset, radius);
    sum += SampleEmissionSource(uv + tangentOffset, radius);
    sum += SampleEmissionSource(uv - tangentOffset, radius);
    sum += SampleEmissionSource(uv + diagonalAOffset, radius);
    sum += SampleEmissionSource(uv - diagonalAOffset, radius);
    sum += SampleEmissionSource(uv + diagonalBOffset, radius);
    sum += SampleEmissionSource(uv - diagonalBOffset, radius);
    return sum * 0.125f;
}

float3 EmissiveBloom(float2 uv, float2 texel)
{
    // Every ring is within 17 source pixels. Include bilinear footprints and rounding; a 36px
    // neighborhood touches at most two 64px tiles on either axis. Max reduction never averages
    // away a tiny emitter, so a zero bound proves all 32 original taps contribute zero.
    uint coverageWidth, coverageHeight;
    EmissionCoverage.GetDimensions(coverageWidth, coverageHeight);
    int2 lastTile = int2(coverageWidth, coverageHeight) - 1;
    int2 first = clamp(int2(floor((uv * ViewportOptions.xy - 18.0f) / 64.0f)), 0, lastTile);
    int2 last = clamp(int2(floor((uv * ViewportOptions.xy + 18.0f) / 64.0f)), 0, lastTile);
    float maximumEmission = max(max(EmissionCoverage.Load(int3(first, 0)).y,
                                   EmissionCoverage.Load(int3(last.x, first.y, 0)).y),
                               max(EmissionCoverage.Load(int3(first.x, last.y, 0)).y,
                                   EmissionCoverage.Load(int3(last, 0)).y));
    [branch] if(maximumEmission <= 0.08f) return 0.0f;
    // Rotating successive rings avoids visible spokes. Each sampled emitter also limits large rings according
    // to its projected block size, so close lights receive a soft halo while distant lights stay compact.
    float3 innerGlow = SampleEmissionRing(uv, texel, 2.0f, float2(1.0f, 0.0f));
    float3 nearGlow = SampleEmissionRing(uv, texel, 5.5f, float2(0.9238795f, 0.3826834f));
    float3 middleGlow = SampleEmissionRing(uv, texel, 10.5f, float2(0.9807853f, 0.1950903f));
    float3 outerGlow = SampleEmissionRing(uv, texel, 17.0f, float2(0.8314696f, 0.5555702f));
    return innerGlow * 0.38f + nearGlow * 0.30f + middleGlow * 0.21f + outerGlow * 0.11f;
}

float2 SunOcclusionPS(ScreenPixelInput input) : SV_TARGET
{
    if(SunOpticsOptions.y <= 0.0f || SunDirectionAndStrength.w <= 0.001f || SunDirectionAndStrength.y >= 0.0f) return 0.0f;
    float3 direction = normalize(-SunDirectionAndStrength.xyz);
    float cloudTransmission = StaticCloudTransmission(direction);
    if(SunOpticsOptions.x <= 0.0f) return float2(0.0f, cloudTransmission);
    float3 reference = abs(direction.y) > 0.94f ? float3(0, 0, 1) : float3(0, 1, 0);
    float3 right = normalize(cross(reference, direction));
    float3 up = normalize(cross(direction, right));
    float visibility = 0.0f;
    // Match SkyPS's square solar footprint, including its tangent-plane orientation. A moving edge
    // crosses 144 independent depth samples instead of toggling one center ray on and off.
    [loop] for(int y = 0; y < 12; y++)
    [loop] for(int x = 0; x < 12; x++)
    {
        float2 offset = ((float2(x, y) + 0.5f) / 12.0f * 2.0f - 1.0f) * 0.040f;
        float3 ray = direction + right * offset.x + up * offset.y;
        float facing = dot(ray, CameraForward.xyz);
        if(facing <= 0.0001f) continue;
        float2 uv = float2(0.5f + dot(ray, CameraRightAndHorizontalScale.xyz) / (2.0f * facing * CameraRightAndHorizontalScale.w),
                          0.5f - dot(ray, CameraUpAndVerticalScale.xyz) / (2.0f * facing * CameraUpAndVerticalScale.w));
        if(any(uv < 0.0f) || any(uv >= 1.0f)) continue;
        int2 pixel = min(int2(uv * ViewportOptions.xy), int2(ViewportOptions.xy) - 1);
        visibility += step(0.99999f, SceneDepth.Load(int3(pixel, 0)));
    }
    return float2(visibility / 144.0f * SunOpticsOptions.x * cloudTransmission, cloudTransmission);
}

float3 LensSunlight(float2 uv, float visibility)
{
    if(visibility <= 0.0001f) return 0.0f;
    float aspect = ViewportOptions.x / ViewportOptions.y;
    float2 delta = (uv - SunOpticsProjection.xy) * float2(aspect, 1.0f);
    float distanceToSun = length(delta);
    float angle = atan2(delta.y, delta.x);
    float halo = 0.17f * exp(-distanceToSun * 10.0f);
    halo += 0.040f * exp(-pow((distanceToSun - 0.17f) / 0.037f, 2.0f));
    float diffraction = pow(abs(cos(angle * 4.0f)), 48.0f) * exp(-distanceToSun * 13.0f) * 0.08f;
    float3 ghosts = 0.0f;
    [unroll] for(int index = 0; index < 3; index++)
    {
        float t = 0.55f + index * 0.43f;
        float2 center = lerp(SunOpticsProjection.xy, 1.0f - SunOpticsProjection.xy, t);
        float radius = 0.035f + index * 0.020f;
        float r = length((uv - center) * float2(aspect, 1.0f));
        float ring = exp(-pow((r - radius) / (radius * 0.27f), 2.0f));
        float fill = exp(-r * r / (radius * radius * 0.55f));
        float3 tint = index == 0 ? float3(0.36f, 0.68f, 1.0f) : index == 1 ? float3(1.0f, 0.45f, 0.20f) : float3(0.28f, 0.72f, 0.49f);
        ghosts += tint * (ring * 0.035f + fill * 0.018f);
    }
    return (SunlightColor.rgb * (halo + diffraction) + ghosts) * visibility * SunDirectionAndStrength.w;
}

// Returns a constant visibility only if conservative depth bounds prove every original tap
// has that value. Mixed depth, shadow edges, borders and missing summaries use the full marcher.
float ClassifySunShaft(float3 firstPosition, float3 lastPosition)
{
    uint width, height, levels;
    SunShadowRanges.GetDimensions(0, width, height, levels);
    [branch] if(width == 0 || height == 0) return -1.0f;
    float4 firstClip = mul(float4(firstPosition, 1.0f), SunViewProjection);
    float4 lastClip = mul(float4(lastPosition, 1.0f), SunViewProjection);
    if(firstClip.w <= 0.0f || lastClip.w <= 0.0f) return -1.0f;
    float3 firstProjected = firstClip.xyz / firstClip.w;
    float3 lastProjected = lastClip.xyz / lastClip.w;
    float minimumDepth = min(firstProjected.z, lastProjected.z);
    float maximumDepth = max(firstProjected.z, lastProjected.z);
    if(minimumDepth <= 0.00001f || maximumDepth >= 0.99999f) return -1.0f;
    float2 firstUv = float2(firstProjected.x * 0.5f + 0.5f, 0.5f - firstProjected.y * 0.5f);
    float2 lastUv = float2(lastProjected.x * 0.5f + 0.5f, 0.5f - lastProjected.y * 0.5f);
    int2 shadowSize = int2(width, height) * 16;
    // SampleCmpLevelZero filters neighboring depth comparisons; include its entire footprint
    // plus a texel of numerical margin. Border sampling is deliberately left to the original path.
    int2 firstPixel = int2(floor(min(firstUv, lastUv) * shadowSize - 0.5f)) - 1;
    int2 lastPixel = int2(ceil(max(firstUv, lastUv) * shadowSize - 0.5f)) + 1;
    if(any(firstPixel < 0) || any(lastPixel >= shadowSize)) return -1.0f;
    int2 firstTile = firstPixel / 16;
    int2 lastTile = lastPixel / 16;
    int2 span = lastTile - firstTile + 1;
    int level = min((int)ceil(log2((float)max(span.x, span.y))), (int)levels - 1);
    firstTile = firstTile >> level;
    lastTile = lastTile >> level;
    float2 a = SunShadowRanges.Load(int3(firstTile, level));
    float2 b = SunShadowRanges.Load(int3(lastTile.x, firstTile.y, level));
    float2 c = SunShadowRanges.Load(int3(firstTile.x, lastTile.y, level));
    float2 d = SunShadowRanges.Load(int3(lastTile, level));
    float nearestOccluder = min(min(a.x, b.x), min(c.x, d.x));
    float farthestOccluder = max(max(a.y, b.y), max(c.y, d.y));
    if(nearestOccluder >= maximumDepth - ShadowOptions.y + 0.00001f) return 1.0f;
    if(farthestOccluder < minimumDepth - ShadowOptions.y - 0.00001f) return 0.0f;
    return -1.0f;
}

float3 SunShafts(float2 uv, float depth, float visibility)
{
    [branch] if(visibility <= 0.0001f || ShadowOptions.z < 0.5f) return 0.0f;
    float3 endPosition = ReconstructWorldPosition(uv, min(depth, 0.99998f));
    float3 delta = endPosition - CameraPosition.xyz;
    float rayLength = min(length(delta), 96.0f);
    float3 ray = normalize(delta);
    float facing = pow(saturate(dot(ray, normalize(-SunDirectionAndStrength.xyz))), 7.0f);
    [branch] if(facing < 0.002f || rayLength < 0.05f) return 0.0f;
    float illumination = 0.0f;
    float constantSunlight = ClassifySunShaft(CameraPosition.xyz + ray * (rayLength * 0.5f / 24.0f),
                                            CameraPosition.xyz + ray * (rayLength * 23.5f / 24.0f));
    [branch] if(constantSunlight == 0.0f) return 0.0f;
    [branch] if(constantSunlight == 1.0f && ObserverBodyShadowOptions.x < 0.5f)
    {
        // The same 24 integration weights form a geometric series. Accumulate the weights
        // directly when every shadow comparison is proven 1; keep the original sample count.
        float attenuationStep = exp(-rayLength * 0.018f / 24.0f);
        float attenuation = sqrt(attenuationStep);
        [unroll] for(int weightIndex = 0; weightIndex < 24; weightIndex++)
        {
            illumination += attenuation;
            attenuation *= attenuationStep;
        }
    }
    else
    {
        // Fixed world-space integration ends at the first visible surface. Each step tests the actual
        // sun shadow map; a building blocks the light volume instead of merely painting screen rays.
        [loop] for(int index = 0; index < 24; index++)
        {
            float distanceAlongRay = rayLength * (index + 0.5f) / 24.0f;
            float3 samplePosition = CameraPosition.xyz + ray * distanceAlongRay;
            float4 lightPosition = mul(float4(samplePosition, 1.0f), SunViewProjection);
            float3 projected = lightPosition.xyz / lightPosition.w;
            float2 shadowUv = float2(projected.x * 0.5f + 0.5f, 0.5f - projected.y * 0.5f);
            if(any(shadowUv < 0.0f) || any(shadowUv > 1.0f) || projected.z <= 0.0f || projected.z >= 1.0f) continue;
            float lit = 1.0f;
            [branch] if(constantSunlight < 0.0f)
                lit = SunShadowMap.SampleCmpLevelZero(SunShadowSampler, shadowUv, projected.z - ShadowOptions.y);
            lit = min(lit, ObserverBodyShadow(samplePosition));
            illumination += lit * exp(-distanceAlongRay * 0.018f);
        }
    }
    return SunlightColor.rgb * illumination / 24.0f * (1.0f - exp(-rayLength * 0.018f)) * facing * 0.20f * visibility * SunDirectionAndStrength.w;
}

// Compute reductions never read back to the CPU. All threads, including padded edge threads,
// participate in every barrier; min/max summaries preserve narrow light and occluder features.
void ReduceEffectRange(uint lane, uint2 tile)
{
    GroupMemoryBarrierWithGroupSync();
    [unroll] for(uint stride = 128; stride > 0; stride >>= 1)
    {
        if(lane < stride)
        {
            float2 other = EffectRangeShared[lane + stride];
            EffectRangeShared[lane] = float2(min(EffectRangeShared[lane].x, other.x),
                                             max(EffectRangeShared[lane].y, other.y));
        }
        GroupMemoryBarrierWithGroupSync();
    }
    if(lane == 0) EffectRangeOutput[tile] = EffectRangeShared[0];
}

[numthreads(16, 16, 1)]
void EmissionCoverageCS(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID, uint lane : SV_GroupIndex)
{
    uint width, height;
    SceneMaterial.GetDimensions(width, height);
    float maximumEmission = 0.0f;
    [unroll] for(uint y = 0; y < 4; y++)
    [unroll] for(uint x = 0; x < 4; x++)
    {
        uint2 pixel = min(group.xy * 64 + local.xy + uint2(x, y) * 16, uint2(width, height) - 1);
        maximumEmission = max(maximumEmission, SceneMaterial.Load(int3(pixel, 0)).a);
    }
    EffectRangeShared[lane] = float2(0.0f, maximumEmission);
    ReduceEffectRange(lane, group.xy);
}

[numthreads(16, 16, 1)]
void ShadowRangeCS(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID, uint lane : SV_GroupIndex)
{
    float depth = SunShadowMap.Load(int3(group.xy * 16 + local.xy, 0));
    EffectRangeShared[lane] = float2(depth, depth);
    ReduceEffectRange(lane, group.xy);
}

[numthreads(8, 8, 1)]
void RangeReductionCS(uint3 pixel : SV_DispatchThreadID)
{
    uint width, height;
    EffectRangeOutput.GetDimensions(width, height);
    if(pixel.x >= width || pixel.y >= height) return;
    int2 source = int2(pixel.xy) * 2;
    float2 a = SunShadowRanges.Load(int3(source, 0));
    float2 b = SunShadowRanges.Load(int3(source + int2(1, 0), 0));
    float2 c = SunShadowRanges.Load(int3(source + int2(0, 1), 0));
    float2 d = SunShadowRanges.Load(int3(source + int2(1, 1), 0));
    EffectRangeOutput[pixel.xy] = float2(min(min(a.x, b.x), min(c.x, d.x)),
                                        max(max(a.y, b.y), max(c.y, d.y)));
}

float4 PostProcessPS(ScreenPixelInput input) : SV_TARGET
{
    float2 viewportSize = max(ViewportOptions.xy, float2(1.0f, 1.0f));
    float2 texel = 1.0f / viewportSize;
    float2 uv = input.ScreenUv;
    float3 center = SceneColor.Sample(SceneSampler, uv).rgb;
    float3 nearLeft = SceneColor.Sample(SceneSampler, uv + float2(-1.5f, 0.0f) * texel).rgb;
    float3 nearRight = SceneColor.Sample(SceneSampler, uv + float2(1.5f, 0.0f) * texel).rgb;
    float3 nearUp = SceneColor.Sample(SceneSampler, uv + float2(0.0f, -1.5f) * texel).rgb;
    float3 nearDown = SceneColor.Sample(SceneSampler, uv + float2(0.0f, 1.5f) * texel).rgb;
    float3 farNorthWest = SceneColor.Sample(SceneSampler, uv + float2(-4.0f, -4.0f) * texel).rgb;
    float3 farNorthEast = SceneColor.Sample(SceneSampler, uv + float2(4.0f, -4.0f) * texel).rgb;
    float3 farSouthWest = SceneColor.Sample(SceneSampler, uv + float2(-4.0f, 4.0f) * texel).rgb;
    float3 farSouthEast = SceneColor.Sample(SceneSampler, uv + float2(4.0f, 4.0f) * texel).rgb;

    float3 localAverage = (nearLeft + nearRight + nearUp + nearDown) * 0.25f;
    float3 wideAverage = (farNorthWest + farNorthEast + farSouthWest + farSouthEast) * 0.25f;
    float3 bloom = BloomContribution(center) * 0.32f + BloomContribution(localAverage) * 0.40f +
                   BloomContribution(wideAverage) * 0.28f;
    float3 emissiveGlow = EmissiveBloom(uv, texel);
    float4 surface = SceneSurface.SampleLevel(SceneSampler, uv, 0.0f);
    float waterMask = surface.w;
    float depth = LoadSceneDepth(uv);
    float ambientOcclusion = ScreenSpaceAmbientOcclusion(uv);
    float3 worldPosition = 0.0f;
    float3 normal = float3(0.0f, 1.0f, 0.0f);
    if(depth < 0.99999f)
    {
        worldPosition = ReconstructWorldPosition(uv, depth);
        normal = normalize(surface.xyz * 2.0f - 1.0f);
    }
    float daylight = saturate(ShadowOptions.w);
    float nightFactor = 1.0f - daylight;
    float sourceEmission = SceneMaterial.SampleLevel(SceneSampler, uv, 0.0f).a;
    float emissiveBloomStrength = lerp(0.085f, 0.92f, pow(nightFactor, 0.78f));
    float sourceBloomSuppression = lerp(1.0f, 0.14f, smoothstep(0.08f, 0.72f, sourceEmission));
    float3 color = center * lerp(ambientOcclusion, 1.0f, saturate(waterMask)) +
                   bloom * 0.24f + emissiveGlow * emissiveBloomStrength * sourceBloomSuppression +
                   (center - localAverage) * 0.065f;
    if(waterMask > 0.12f && depth < 0.99999f && ReflectionPlaneIndex(worldPosition.y) < 0)
    {
        float4 reflection = ScreenSpaceReflection(worldPosition, normal);
        float3 viewDirection = normalize(CameraPosition.xyz - worldPosition);
        float fresnel = 0.04f + 0.96f * pow(1.0f - saturate(dot(normal, viewDirection)), 5.0f);
        float reflectionBlend = saturate(waterMask) * reflection.a * lerp(0.06f, 0.48f, fresnel);
        color = lerp(color, reflection.rgb, reflectionBlend);
    }
    float2 solarVisibility = SolarVisibility.Load(int3(0, 0, 0));
    // Lens ghosts require direct disc visibility; illuminated world-space haze may remain visible
    // beside an occluder. Each volume step independently tests the building/body shadow maps.
    color += LensSunlight(uv, solarVisibility.x) + SunShafts(uv, depth, solarVisibility.y);
    color *= QualityOptions.y * lerp(0.50f, 1.0f, daylight);
    color = AcesToneMap(max(color, 0.0f));

    float luminance = dot(color, float3(0.2126f, 0.7152f, 0.0722f));
    color = lerp(float3(luminance, luminance, luminance), color, QualityOptions.z);
    float2 centered = uv - 0.5f;
    centered.x *= viewportSize.x / viewportSize.y;
    float vignette = 1.0f - smoothstep(0.38f, 0.92f, length(centered)) * QualityOptions.w;
    return float4(LinearToSrgb(color * vignette), 1.0f);
}
