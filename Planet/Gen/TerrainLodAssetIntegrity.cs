using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;

namespace monogame.Planet.Gen;

internal static class TerrainLodAssetIntegrity {
    private const int ProjectionVersion = 1;
    private const int TriangleTopologyVersion = 1;

    public static byte[] CalculateReferenceGeometryHash(
        in TerrainLodBuildSettings settings,
        short[][] faceElevations) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(header[0..4], ProjectionVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..8], TriangleTopologyVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..12], BitConverter.SingleToInt32Bits(settings.ReferenceRadius));
        BinaryPrimitives.WriteInt32LittleEndian(header[12..16], settings.ChunkResolution);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..20], settings.MaximumLod);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..24], BitConverter.SingleToInt32Bits(settings.ElevationQuantizationStep));
        hash.AppendData(header);

        foreach (var face in faceElevations) {
            hash.AppendData(MemoryMarshal.AsBytes(face.AsSpan()));
        }

        return hash.GetHashAndReset();
    }

    public static void ValidateNodeHierarchy(
        int maximumLod,
        TerrainLodNode[] nodes,
        float occluderRadius) {
        foreach (var node in nodes) {
            if (!IsFinite(node.CenterDirection)
                || !float.IsFinite(node.AngularRadius)
                || !float.IsFinite(node.MinimumRadius)
                || !float.IsFinite(node.MaximumRadius)
                || !IsFinite(node.BoundingSphere.Center)
                || !float.IsFinite(node.BoundingSphere.Radius)
                || !float.IsFinite(node.HorizonPointRadius)
                || !float.IsFinite(node.GeometricError)) {
                throw new InvalidDataException("The terrain LOD asset contains non-finite node metadata.");
            }

            if (node.AngularRadius < 0.0f
                || node.MinimumRadius <= 0.0f
                || node.MinimumRadius > node.MaximumRadius
                || node.BoundingSphere.Radius < 0.0f
                || node.HorizonPointRadius < 0.0f
                || node.GeometricError < 0.0f
                || occluderRadius > node.MinimumRadius) {
                throw new InvalidDataException("The terrain LOD asset contains invalid node bounds.");
            }

            if (node.Id.Level == maximumLod) {
                if (node.GeometricError != 0.0f) {
                    throw new InvalidDataException("A highest-LOD node has non-zero geometric error.");
                }

                continue;
            }

            for (var childY = 0; childY < 2; childY++) {
                for (var childX = 0; childX < 2; childX++) {
                    var childId = new TerrainLodNodeId(
                        node.Id.Face,
                        node.Id.Level + 1,
                        node.Id.X * 2 + childX,
                        node.Id.Y * 2 + childY
                    );
                    var child = nodes[TerrainLodNodeIndex.GetIndex(childId, maximumLod)];
                    if (node.GeometricError < child.GeometricError) {
                        throw new InvalidDataException("A parent node has less geometric error than one of its children.");
                    }
                }
            }
        }
    }

    private static bool IsFinite(in Vector3 value) {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}
