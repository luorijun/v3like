float4x4 WorldViewProjection;
float4x4 FaceOrientation;
float2 ChunkPosition;
float ChunkSize;
float3 SurfaceColor;
int GridFrequency;
int GridDataWidth;
int TileSelected;
float4 BorderEdges[6];
int BorderCount;
Texture2D GridTiles;
Texture2D GridSeeds;
Texture2D GridFaces;
Texture2D TileDisplayColors;

struct VertexShaderInput
{
    float4 Position : POSITION0;
};

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float2 FacePoint : TEXCOORD0;
};

VertexShaderOutput VertexShaderFunction(VertexShaderInput input)
{
    VertexShaderOutput output;
    float2 facePoint = ChunkPosition + input.Position.xy * ChunkSize;
    float3 faceDirection = normalize(float3(
        tan(facePoint.x * 0.78539816339),
        1.0,
        -tan(facePoint.y * 0.78539816339)
    ));
    float3 spherePosition = mul(float4(faceDirection, 0.0), FaceOrientation).xyz;
    output.Position = mul(float4(spherePosition, 1.0), WorldViewProjection);
    output.FacePoint = facePoint;
    return output;
}

float4 SolidPixelShaderFunction(VertexShaderOutput input) : COLOR0
{
    return float4(SurfaceColor, 1.0);
}

float4 LoadTileData(int index)
{
    return GridTiles.Load(int3((uint)index % (uint)GridDataWidth, (uint)index / (uint)GridDataWidth, 0));
}

int LocateGpuTile(float3 direction)
{
    int face = 0;
    float maximumDot = dot(direction, GridFaces.Load(int3(0, 0, 0)).xyz);
    [unroll] for (int index = 1; index < 20; index++)
    {
        float score = dot(direction, GridFaces.Load(int3(0, index, 0)).xyz);
        if (score > maximumDot) { maximumDot = score; face = index; }
    }

    float4 plane = GridFaces.Load(int3(0, face, 0));
    float3 offset = direction * (plane.w / dot(plane.xyz, direction))
        - GridFaces.Load(int3(1, face, 0)).xyz;
    float dot20 = dot(offset, GridFaces.Load(int3(2, face, 0)).xyz);
    float dot21 = dot(offset, GridFaces.Load(int3(3, face, 0)).xyz);
    float4 coefficients = GridFaces.Load(int3(4, face, 0));
    float b = (coefficients.z * dot20 - coefficients.y * dot21) * coefficients.w;
    float c = (coefficients.x * dot21 - coefficients.y * dot20) * coefficients.w;
    float3 weights = max(float3(1 - b - c, b, c), 0);
    weights *= GridFrequency / (weights.x + weights.y + weights.z);
    int3 rounded = (int3)round(weights);
    float3 differences = abs(rounded - weights);
    if (differences.x >= differences.y && differences.x >= differences.z)
        rounded.x = GridFrequency - rounded.y - rounded.z;
    else if (differences.y >= differences.z)
        rounded.y = GridFrequency - rounded.x - rounded.z;
    else
        rounded.z = GridFrequency - rounded.x - rounded.y;

    int seedIndex = face * (((GridFrequency + 1) * (GridFrequency + 2)) >> 1)
        + rounded.x * (GridFrequency + 1) - ((rounded.x * (rounded.x - 1)) >> 1) + rounded.y;
    int current = (int)GridSeeds.Load(int3((uint)seedIndex % (uint)GridDataWidth, (uint)seedIndex / (uint)GridDataWidth, 0)).x;

    // On a convex polyhedral adjacency graph, ascent of this linear objective
    // reaches a global maximum in exact arithmetic (ties use decreasing tile ID).
    // Eight iterations is a workload budget, not a proven convergence bound.
    // Expose budget exhaustion rather than silently returning an approximate tile.
    [loop] for (int iteration = 0; iteration < 8; iteration++)
    {
        float4 center = LoadTileData(current * 3);
        int3 neighborsA = (int3)LoadTileData(current * 3 + 1).xyz;
        int3 neighborsB = (int3)LoadTileData(current * 3 + 2).xyz;
        int best = current;
        float3 bestCenter = center.xyz;
        [loop] for (int neighbor = 0; neighbor < (int)center.w; neighbor++)
        {
            int candidate = neighbor < 3 ? neighborsA[min(neighbor, 2)] : neighborsB[max(neighbor - 3, 0)];
            float3 candidateCenter = LoadTileData(candidate * 3).xyz;
            // Compare center differences, avoiding subtraction of two dots near 1.
            precise float3 products = direction * (candidateCenter - bestCenter);
            precise float difference = (products.x + products.y) + products.z;
            if (difference > 0 || (difference == 0 && candidate < best))
            {
                best = candidate;
                bestCenter = candidateCenter;
            }
        }
        if (best == current) return current;
        current = best;
    }
    return -1;
}

float4 TilePixelShaderFunction(VertexShaderOutput input) : COLOR0
{
    float2 facePoint = input.FacePoint;
    float2 tangents = tan(facePoint * 0.78539816339);
    tangents.x = abs(facePoint.x) == 1 ? facePoint.x : tangents.x;
    tangents.y = abs(facePoint.y) == 1 ? facePoint.y : tangents.y;
    // Positive direction scaling cancels in projection and preserves tile comparisons.
    float3 direction = mul(float4(tangents.x, 1, -tangents.y, 0), FaceOrientation).xyz;
    int tile = LocateGpuTile(direction);
    if (tile < 0) return float4(1, 0, 1, 1);
    float4 color = TileDisplayColors.Load(int3((uint)tile % (uint)GridDataWidth, (uint)tile / (uint)GridDataWidth, 0));
    if (tile == TileSelected)
    {
        color.rgb = lerp(color.rgb, float3(1, 1, 1), 0.16);
        float3 unitDirection = normalize(direction);
        float border = 0;
        [loop] for (int edge = 0; edge < BorderCount; edge++)
        {
            // Sine-distance ratio approximates the angular ratio for these small tiles.
            float ratio = dot(unitDirection, BorderEdges[edge].xyz) / BorderEdges[edge].w;
            border = max(border, 1 - smoothstep(0, 1, ratio));
        }
        color.rgb = lerp(color.rgb, float3(1, 1, 1), border);
    }
    return color;
}

technique TileSurface
{
    pass Pass0
    {
        VertexShader = compile vs_4_0 VertexShaderFunction();
        PixelShader = compile ps_4_0 TilePixelShaderFunction();
    }
}

technique SolidColor
{
    pass Pass0
    {
        VertexShader = compile vs_4_0_level_9_1 VertexShaderFunction();
        PixelShader = compile ps_4_0_level_9_1 SolidPixelShaderFunction();
    }
}
