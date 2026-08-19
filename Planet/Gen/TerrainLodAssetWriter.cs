using System;
using System.IO;
using System.Runtime.InteropServices;

namespace monogame.Planet.Gen;

internal static class TerrainLodAssetWriter {
    public static void Write(Stream destination, TerrainLodAsset asset) {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(asset);
        if (!destination.CanWrite) {
            throw new ArgumentException("The destination stream is not writable.", nameof(destination));
        }

        using var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true);
        var settings = asset.Settings;
        var heightSectionLength = checked(
            (long)CubeSphereGrid.FaceCount
            * settings.FinestResolution
            * settings.FinestResolution
            * sizeof(short)
        );
        var nodeSectionLength = checked((long)asset.Nodes.Length * TerrainLodAssetFormat.NodeSize);
        var checksumSectionLength = asset.ReferenceGeometryHash.Length;
        var headerSize = TerrainLodAssetFormat.HeaderSize;
        var heightSectionOffset = (long)headerSize;
        var nodeSectionOffset = checked(heightSectionOffset + heightSectionLength);
        var checksumSectionOffset = checked(nodeSectionOffset + nodeSectionLength);

        writer.Write(TerrainLodAssetFormat.Magic);
        writer.Write(TerrainLodAssetFormat.Version);
        writer.Write(headerSize);
        writer.Write(TerrainLodAssetFormat.SectionCount);
        writer.Write(settings.ReferenceRadius);
        writer.Write(settings.ChunkResolution);
        writer.Write(settings.MaximumLod);
        writer.Write(settings.ElevationQuantizationStep);
        writer.Write(settings.FinestResolution);
        writer.Write(CubeSphereGrid.FaceCount);
        writer.Write(asset.Nodes.Length);
        writer.Write(asset.OccluderRadius);
        WriteSection(writer, TerrainLodAssetSection.ReferenceHeights, heightSectionOffset, heightSectionLength);
        WriteSection(writer, TerrainLodAssetSection.LodNodeMetadata, nodeSectionOffset, nodeSectionLength);
        WriteSection(writer, TerrainLodAssetSection.Checksums, checksumSectionOffset, checksumSectionLength);

        for (var faceIndex = 0; faceIndex < CubeSphereGrid.FaceCount; faceIndex++) {
            var elevations = asset.GetQuantizedElevations((TerrainCubeFace)faceIndex);
            if (BitConverter.IsLittleEndian) {
                writer.Flush();
                destination.Write(MemoryMarshal.AsBytes(elevations));
            } else {
                foreach (var elevation in elevations) {
                    writer.Write(elevation);
                }
            }
        }

        foreach (var node in asset.Nodes) {
            WriteVector3(writer, node.CenterDirection);
            writer.Write(node.AngularRadius);
            writer.Write(node.MinimumRadius);
            writer.Write(node.MaximumRadius);
            WriteVector3(writer, node.BoundingSphere.Center);
            writer.Write(node.BoundingSphere.Radius);
            writer.Write(node.HorizonPointRadius);
            writer.Write(node.GeometricError);
        }

        writer.Write(asset.ReferenceGeometryHash);
    }

    private static void WriteSection(
        BinaryWriter writer,
        TerrainLodAssetSection section,
        long offset,
        long length) {
        writer.Write((int)section);
        writer.Write(offset);
        writer.Write(length);
    }

    private static void WriteVector3(BinaryWriter writer, Microsoft.Xna.Framework.Vector3 value) {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

}
