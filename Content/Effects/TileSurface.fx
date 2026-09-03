float4x4 WorldViewProjection;
float4x4 FaceOrientation;
float2 ChunkPosition;
float ChunkSize;
texture TileIndexTexture;
float3 SurfaceColor;

sampler TileIndexSampler = sampler_state
{
    Texture = <TileIndexTexture>;
    MinFilter = POINT;
    MagFilter = POINT;
    MipFilter = NONE;
    AddressU = Clamp;
    AddressV = Clamp;
};

struct VertexShaderInput
{
    float4 Position : POSITION0;
    float2 TextureCoordinate : TEXCOORD0;
};

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float2 TextureCoordinate : TEXCOORD0;
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
    output.TextureCoordinate = input.TextureCoordinate;
    return output;
}

float4 TilePixelShaderFunction(VertexShaderOutput input) : COLOR0
{
    return tex2D(TileIndexSampler, input.TextureCoordinate);
}

float4 SolidPixelShaderFunction(VertexShaderOutput input) : COLOR0
{
    return float4(SurfaceColor, 1.0);
}

technique TileSurface
{
    pass Pass0
    {
        VertexShader = compile vs_4_0_level_9_1 VertexShaderFunction();
        PixelShader = compile ps_4_0_level_9_1 TilePixelShaderFunction();
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
