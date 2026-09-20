#version 300 es

precision highp float;
precision highp int;
precision highp sampler2D;
precision highp sampler2DArray;

uniform sampler2DArray xOverlays;
uniform sampler2DArray xAlphas;
uniform float xAmbient;
uniform float uAlpha;
// Per terrain-atlas layer TexTiling (Region TerrainTex.tex_tiling); matches client CopyAndTile repeats.
uniform float uTerrainTiling[36];

// Grid uniforms
uniform bool uShowLandblockGrid;  // Enable/disable landblock grid
uniform bool uShowCellGrid;       // Enable/disable cell grid
uniform vec3 uLandblockGridColor; // Color for landblock grid lines (RGB)
uniform vec3 uCellGridColor;      // Color for cell grid lines (RGB)
uniform float uGridLineWidth;     // Base width of grid lines in pixels
uniform float uGridOpacity;       // Opacity of grid lines (0.0 - 1.0)
uniform float uCameraDistance;    // Distance from camera to terrain
uniform float uScreenHeight;      // Screen height in pixels for scaling

// Slope highlight uniforms
uniform bool uShowSlopeHighlight;    // Enable/disable slope highlighting
uniform float uSlopeThreshold;      // Slope angle threshold in radians
uniform vec3 uSlopeHighlightColor;  // Color for slope highlight (RGB)
uniform float uSlopeHighlightOpacity; // Opacity of slope highlight

// Brush preview uniforms
uniform bool uBrushActive;          // Enable/disable brush preview
uniform vec2 uBrushCenter;          // Brush center in world XY coordinates
uniform float uBrushRadius;         // Brush radius in world units
uniform float uBrushFalloff;        // 0 = hard disk, 1 = soft fade

// Texture preview uniforms
uniform bool uPreviewActive;        // Enable/disable texture preview
uniform float uPreviewTexIndex;     // Atlas layer index for preview texture

// Fog uniforms
uniform bool uFogEnabled;
uniform vec3 uFogColor;
uniform float uFogStart;
uniform float uFogEnd;
uniform vec2 uCameraPos2D;          // Camera XY world position for fog distance

// Grid uses the actual half-FOV tangent so line thickness matches the projection
uniform float uTanHalfFov;

in vec3 vTexUV;
in vec4 vOverlay0;
in vec4 vOverlay1;
in vec4 vOverlay2;
in vec4 vRoad0;
in vec4 vRoad1;
in float vLightingFactor;
in vec2 vWorldPos;
in vec3 vNormal;

out vec4 FragColor;

vec2 terrainLayerUv(vec2 cornerUv, float layerIndex) {
    int index = clamp(int(layerIndex + 0.5), 0, 35);
    float tiling = uTerrainTiling[index];
    if (tiling <= 0.0) {
        tiling = 1.0;
    }

    // Terrain albedo/detail layers honor Region TerrainTex.TexTiling; only the alpha blend masks stay
    // un-tiled per cell.
    return cornerUv * tiling;
}

vec4 combineOverlays(vec3 pTexUV, vec4 pOverlay0, vec4 pOverlay1, vec4 pOverlay2) {
    vec2 uvb = pTexUV.xy;

    // Sequential blending matching the AC client's TexMerge::FillTempTexBuffer.
    // Each overlay is composited onto the running result:
    //   result = alpha * existing + (1 - alpha) * overlay
    // where alpha comes from the alpha map (high alpha = keep base, low alpha = show overlay).
    // We track total coverage so the caller can blend against the base texture.
    float totalAlpha = 1.0;
    vec3 accum = vec3(0.0);
    bool hasAny = false;

    if (pOverlay0.z >= 0.0) {
        vec2 uv0 = terrainLayerUv(uvb, pOverlay0.z);
        vec3 tex0 = texture(xOverlays, vec3(uv0, pOverlay0.z)).rgb;
        float a0 = 1.0;
        if (pOverlay0.w >= 0.0) {
            // Blend mask is a per-cell rotated 0..1 mask (client TexMerge): sample un-tiled at the
            // overlay's rotated corner UV, NOT the tiled albedo UV. Tiling the mask = blocky checkerboard.
            a0 = texture(xAlphas, vec3(pOverlay0.xy, pOverlay0.w)).a;
        }
        accum = a0 * accum + (1.0 - a0) * tex0;
        totalAlpha *= a0;
        hasAny = true;

        if (pOverlay1.z >= 0.0) {
            vec2 uv1 = terrainLayerUv(uvb, pOverlay1.z);
            vec3 tex1 = texture(xOverlays, vec3(uv1, pOverlay1.z)).rgb;
            float a1 = 1.0;
            if (pOverlay1.w >= 0.0) {
                a1 = texture(xAlphas, vec3(pOverlay1.xy, pOverlay1.w)).a;
            }
            accum = a1 * accum + (1.0 - a1) * tex1;
            totalAlpha *= a1;

            if (pOverlay2.z >= 0.0) {
                vec2 uv2 = terrainLayerUv(uvb, pOverlay2.z);
                vec3 tex2 = texture(xOverlays, vec3(uv2, pOverlay2.z)).rgb;
                float a2 = 1.0;
                if (pOverlay2.w >= 0.0) {
                    a2 = texture(xAlphas, vec3(pOverlay2.xy, pOverlay2.w)).a;
                }
                accum = a2 * accum + (1.0 - a2) * tex2;
                totalAlpha *= a2;
            }
        }
    }

    float coverage = 1.0 - totalAlpha;
    if (!hasAny || coverage < 0.001) return vec4(0.0);
    return vec4(accum / coverage, coverage);
}

vec4 combineRoad(vec3 pTexUV, vec4 pRoad0, vec4 pRoad1) {
    float h0 = pRoad0.z < 0.0 ? 0.0 : 1.0;
    float h1 = pRoad1.z < 0.0 ? 0.0 : 1.0;
    vec2 uvb = pTexUV.xy;
    vec4 result = vec4(0.0);
    if (h0 > 0.0) {
        vec2 roadUv = terrainLayerUv(uvb, pRoad0.z);
        result = texture(xOverlays, vec3(roadUv, pRoad0.z));
        if (pRoad0.w >= 0.0) {
            // Road blend mask: per-cell rotated 0..1 mask, sampled un-tiled (see overlay note above).
            vec4 roadAlpha0 = texture(xAlphas, vec3(pRoad0.xy, pRoad0.w));
            result.a = 1.0 - roadAlpha0.a;
            if (h1 > 0.0 && pRoad1.w >= 0.0) {
                vec4 roadAlpha1 = texture(xAlphas, vec3(pRoad1.xy, pRoad1.w));
                result.a = 1.0 - (roadAlpha0.a * roadAlpha1.a);
            }
        }
    }
    return result;
}

float saturate(float value) {
    return clamp(value, 0.0, 1.0);
}

vec3 saturate(vec3 value) {
    return clamp(value, 0.0, 1.0);
}

vec3 calculateGrid(vec2 worldPos, vec3 terrainColor) {
    // Early out if both grids are disabled
    if (!uShowLandblockGrid && !uShowCellGrid) {
        return vec3(0.0);
    }
    
    float lw = 192.0; // Landblock width
    float cw = 24.0;  // Cell width
    float glowWidthFactor = 1.5; // Glow extends wider than the line
    float glowIntensity = 0.5;   // Adjusted glow intensity
    float landblockLineWidthFactor = 2.0; // Double the thickness for landblock lines

    // Calculate pixel size in world units
    float worldUnitsPerPixel = uCameraDistance * uTanHalfFov * 2.0 / uScreenHeight;
    float scaledLineWidth = uGridLineWidth * worldUnitsPerPixel;
    float scaledGlowWidth = scaledLineWidth * glowWidthFactor;
    float scaledLandblockGlowWidth = scaledGlowWidth * landblockLineWidthFactor; // Thicker glow for landblock lines

    // Determine if cell grid is visible
    bool showCellGrid = (cw / 2.0 > worldUnitsPerPixel);
    bool showLandblockGrid = (lw / 2.0 > worldUnitsPerPixel);

    // Use normal line width for landblock lines if cell grid is not visible
    float scaledLandblockLineWidth = showCellGrid ? scaledLineWidth * landblockLineWidthFactor : scaledLineWidth;

    if (!showLandblockGrid && !showCellGrid) {
        return vec3(0.0);
    }

    // Boost contrast for grid by adjusting inversion
    vec3 invertedColor = vec3(1.0) - terrainColor;
    float brightness = dot(invertedColor, vec3(0.299, 0.587, 0.114)); // Luminance
    if (brightness > 0.4 && brightness < 0.6) { // If color is near gray
        invertedColor = normalize(invertedColor) * 0.8; // Boost saturation
    }
    
    // Calculate distances to nearest grid boundaries
    vec2 landblockGrid = mod(worldPos, lw);
    vec2 cellGrid = mod(worldPos, cw);
    
    // Find distance to nearest boundary
    vec2 landblockDist = min(landblockGrid, lw - landblockGrid);
    vec2 cellDist = min(cellGrid, cw - cellGrid);
    
    // Create lines at boundaries using smoothstep for anti-aliasing
    float landblockLineX = uShowLandblockGrid ? 1.0 - smoothstep(0.0, scaledLandblockLineWidth, landblockDist.x) : 0.0;
    float landblockLineY = uShowLandblockGrid ? 1.0 - smoothstep(0.0, scaledLandblockLineWidth, landblockDist.y) : 0.0;
    float landblockLine = max(landblockLineX, landblockLineY);
    
    // Cell lines
    float cellLineX = uShowCellGrid ? 1.0 - smoothstep(0.0, scaledLineWidth, cellDist.x) : 0.0;
    float cellLineY = uShowCellGrid ? 1.0 - smoothstep(0.0, scaledLineWidth, cellDist.y) : 0.0;
    float cellLine = max(cellLineX, cellLineY);
    
    // Glow effect for landblock lines
    float landblockGlowX = uShowLandblockGrid ? 1.0 - smoothstep(0.0, scaledLandblockGlowWidth, landblockDist.x) : 0.0;
    float landblockGlowY = uShowLandblockGrid ? 1.0 - smoothstep(0.0, scaledLandblockGlowWidth, landblockDist.y) : 0.0;
    float landblockGlow = max(landblockGlowX, landblockGlowY);
    
    // Glow effect for cell lines
    float cellGlowX = uShowCellGrid ? 1.0 - smoothstep(0.0, scaledGlowWidth, cellDist.x) : 0.0;
    float cellGlowY = uShowCellGrid ? 1.0 - smoothstep(0.0, scaledGlowWidth, cellDist.y) : 0.0;
    float cellGlow = max(cellGlowX, cellGlowY);
    
    // Combine grid colors - landblock grid has priority
    vec3 gridColor = vec3(0.0);
    vec3 glowColor = vec3(1.0); // White glow
    
    if (showLandblockGrid && landblockLine > 0.0) {
        gridColor = uLandblockGridColor * landblockLine;
        gridColor += invertedColor * landblockGlow * (1.0 - landblockLine) * glowIntensity;
    } else if (showCellGrid && cellLine > 0.0) {
        gridColor = uCellGridColor * cellLine;
        gridColor += invertedColor * cellGlow * (1.0 - cellLine) * glowIntensity;
    } else {
        // Faint glow for areas outside main lines
        if (showLandblockGrid) {
            gridColor += uLandblockGridColor * landblockGlow * glowIntensity * 0.5;
        }
        if (showCellGrid) {
            gridColor += uCellGridColor * cellGlow * glowIntensity * 0.5;
        }
    }
    
    return gridColor * uGridOpacity;
}

vec3 calculateSlopeHighlight(vec3 normal) {
    if (!uShowSlopeHighlight) return vec3(0.0);

    // Calculate slope angle from the normal's Z component (dot with up vector)
    // normal.Z == 1.0 means flat, normal.Z == 0.0 means vertical
    float cosAngle = abs(normalize(normal).z);
    float slopeAngle = acos(clamp(cosAngle, 0.0, 1.0));

    // Smooth transition around the threshold
    float transitionWidth = 0.05; // ~3 degrees of smooth blending
    float highlight = smoothstep(uSlopeThreshold - transitionWidth, uSlopeThreshold + transitionWidth, slopeAngle);

    return uSlopeHighlightColor * highlight * uSlopeHighlightOpacity;
}

vec3 calculateBrushPreview(vec2 worldPos) {
    if (!uBrushActive) return vec3(0.0);

    float dist = distance(worldPos, uBrushCenter);
    if (dist > uBrushRadius + 2.0) return vec3(0.0); // Early out

    vec3 brushColor = vec3(0.3, 0.6, 1.0); // Soft blue tint

    float inner = uBrushRadius * (1.0 - clamp(uBrushFalloff, 0.0, 1.0));
    float fillAlpha = 0.0;
    if (dist < uBrushRadius) {
        float t = 0.0;
        if (uBrushRadius - inner > 0.001)
            t = clamp((dist - inner) / (uBrushRadius - inner), 0.0, 1.0);
        fillAlpha = 0.16 * (1.0 - t);
    }

    // Outer ring at the brush boundary
    float ringWidth = max(1.5, uBrushRadius * 0.03);
    float ringDist = abs(dist - uBrushRadius);
    float ringAlpha = (1.0 - smoothstep(0.0, ringWidth, ringDist)) * 0.85;

    // Inner falloff ring so strength falloff is visible
    float innerRing = 0.0;
    if (uBrushFalloff > 0.05) {
        float innerDist = abs(dist - inner);
        innerRing = (1.0 - smoothstep(0.0, ringWidth, innerDist)) * 0.35;
    }

    float totalAlpha = max(fillAlpha, max(ringAlpha, innerRing));
    return brushColor * totalAlpha;
}

void main() {
    // Texture preview: substitute base texture inside brush area
    vec3 texUV = vTexUV;
    bool isInPreview = false;
    if (uPreviewActive && uBrushActive) {
        float dist = distance(vWorldPos, uBrushCenter);
        if (dist < uBrushRadius) {
            texUV = vec3(vTexUV.xy, uPreviewTexIndex);
            isInPreview = true;
        }
    }

    vec2 baseUv = terrainLayerUv(texUV.xy, texUV.z);
    vec4 baseColor = texture(xOverlays, vec3(baseUv, texUV.z));
    vec4 combinedOverlays = vec4(0.0);
    vec4 combinedRoad = vec4(0.0);
    
    // Inside preview area, suppress overlays so the preview texture shows cleanly
    if (!isInPreview) {
        if (vOverlay0.z >= 0.0)
            combinedOverlays = combineOverlays(vTexUV, vOverlay0, vOverlay1, vOverlay2);
        if (vRoad0.z >= 0.0)
            combinedRoad = combineRoad(vTexUV, vRoad0, vRoad1);
    }
    
    vec3 baseMasked = vec3(saturate(baseColor.rgb * ((1.0 - combinedOverlays.a) * (1.0 - combinedRoad.a))));
    vec3 overlaysMasked = vec3(saturate(combinedOverlays.rgb * (combinedOverlays.a * (1.0 - combinedRoad.a))));
    vec3 roadMasked = combinedRoad.rgb * combinedRoad.a;
    
    // Calculate base terrain color
    vec3 terrainColor = baseMasked + overlaysMasked + roadMasked;
    
    // Calculate world position for this fragment
    vec2 worldPos = vWorldPos;
    
    // Calculate grid contribution, passing terrainColor
    vec3 gridColor = calculateGrid(worldPos, terrainColor);
    
    // Blend grid with terrain
    vec3 finalColor = mix(terrainColor, gridColor, length(gridColor));

    // Apply slope highlighting
    vec3 slopeColor = calculateSlopeHighlight(vNormal);
    finalColor = mix(finalColor, slopeColor, length(slopeColor));

    // Apply brush preview
    vec3 brushColor = calculateBrushPreview(worldPos);
    finalColor = finalColor + brushColor;
    
    vec3 litColor = finalColor * (saturate(vLightingFactor) + xAmbient);

    if (uFogEnabled) {
        float fogDist = length(vWorldPos - uCameraPos2D);
        float fogFactor = clamp((fogDist - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
        litColor = mix(litColor, uFogColor, fogFactor);
    }

    FragColor = vec4(litColor, uAlpha);
}
