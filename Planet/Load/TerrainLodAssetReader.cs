using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using monogame.Planet.Gen;

namespace monogame.Planet.Load;

internal static class TerrainLodAssetReader {
    public static TerrainLodAsset Read(Stream source) {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek) {
            throw new ArgumentException("The terrain LOD asset stream must be readable and seekable.", nameof(source));
        }

        var assetStart = source.Position;
        try {
            return ReadCore(source, assetStart);
        } catch (InvalidDataException) {
            throw;
        } catch (Exception exception) when (exception is EndOfStreamException
            or OverflowException
            or ArgumentOutOfRangeException) {
            throw new InvalidDataException("The terrain LOD asset is truncated or malformed.", exception);
        }
    }

    private static TerrainLodAsset ReadCore(Stream source, long assetStart) {
        using var reader = new BinaryReader(source, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != TerrainLodAssetFormat.Magic) {
            throw new InvalidDataException("The stream is not a terrain LOD asset.");
        }

        if (reader.ReadInt32() != TerrainLodAssetFormat.Version) {
            throw new InvalidDataException("The terrain LOD asset version is not supported.");
        }

        var headerSize = reader.ReadInt32();
        var sectionCount = reader.ReadInt32();
        if (headerSize != TerrainLodAssetFormat.HeaderSize
            || sectionCount != TerrainLodAssetFormat.SectionCount) {
            throw new InvalidDataException("The terrain LOD asset header layout is invalid.");
        }

        var settings = new TerrainLodBuildSettings(
            reader.ReadSingle(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadSingle()
        );
        try {
            settings.Validate();
        } catch (ArgumentOutOfRangeException exception) {
            throw new InvalidDataException("The terrain LOD asset contains invalid build settings.", exception);
        }

        var storedFinestResolution = reader.ReadInt32();
        var storedFaceCount = reader.ReadInt32();
        var storedNodeCount = reader.ReadInt32();
        var occluderRadius = reader.ReadSingle();
        var expectedNodeCount = TerrainLodNodeIndex.GetNodeCount(settings.MaximumLod);
        if (storedFinestResolution != settings.FinestResolution
            || storedFaceCount != CubeSphereGrid.FaceCount
            || storedNodeCount != expectedNodeCount
            || !float.IsFinite(occluderRadius)
            || occluderRadius <= 0.0f) {
            throw new InvalidDataException("The terrain LOD asset header values are inconsistent.");
        }

        var sections = ReadSectionTable(reader, sectionCount);
        ValidateSections(source, assetStart, headerSize, settings, storedNodeCount, sections);

        var faceElevations = ReadElevations(
            source,
            assetStart,
            settings,
            sections[TerrainLodAssetSection.ReferenceHeights]
        );
        var nodes = ReadNodes(
            reader,
            source,
            assetStart,
            settings.MaximumLod,
            storedNodeCount,
            sections[TerrainLodAssetSection.LodNodeMetadata]
        );
        var storedHash = ReadHash(
            source,
            assetStart,
            sections[TerrainLodAssetSection.Checksums]
        );
        var calculatedHash = TerrainLodAssetIntegrity.CalculateReferenceGeometryHash(settings, faceElevations);
        if (!CryptographicOperations.FixedTimeEquals(storedHash, calculatedHash)) {
            throw new InvalidDataException("The terrain LOD reference geometry checksum does not match its contents.");
        }

        TerrainLodAssetIntegrity.ValidateNodeHierarchy(settings.MaximumLod, nodes, occluderRadius);
        source.Position = checked(assetStart + GetAssetLength(sections.Values));
        return new TerrainLodAsset(settings, faceElevations, nodes, occluderRadius, storedHash);
    }

    private static Dictionary<TerrainLodAssetSection, TerrainLodAssetSectionDescriptor> ReadSectionTable(
        BinaryReader reader,
        int sectionCount) {
        var sections = new Dictionary<TerrainLodAssetSection, TerrainLodAssetSectionDescriptor>(sectionCount);
        for (var index = 0; index < sectionCount; index++) {
            var section = (TerrainLodAssetSection)reader.ReadInt32();
            var offset = reader.ReadInt64();
            var length = reader.ReadInt64();
            if (!Enum.IsDefined(section)
                || !sections.TryAdd(section, new TerrainLodAssetSectionDescriptor(section, offset, length))) {
                throw new InvalidDataException("The terrain LOD asset contains an unknown or duplicate section.");
            }
        }

        return sections;
    }

    private static void ValidateSections(
        Stream source,
        long assetStart,
        int headerSize,
        in TerrainLodBuildSettings settings,
        int nodeCount,
        Dictionary<TerrainLodAssetSection, TerrainLodAssetSectionDescriptor> sections) {
        if (sections.Count != TerrainLodAssetFormat.SectionCount
            || !sections.TryGetValue(TerrainLodAssetSection.ReferenceHeights, out var heights)
            || !sections.TryGetValue(TerrainLodAssetSection.LodNodeMetadata, out var nodes)
            || !sections.TryGetValue(TerrainLodAssetSection.Checksums, out var checksums)) {
            throw new InvalidDataException("The terrain LOD asset is missing a required section.");
        }

        var expectedHeightLength = checked(
            (long)CubeSphereGrid.FaceCount
            * settings.FinestResolution
            * settings.FinestResolution
            * sizeof(short)
        );
        var expectedNodeLength = checked((long)nodeCount * TerrainLodAssetFormat.NodeSize);
        if (heights.Length != expectedHeightLength
            || nodes.Length != expectedNodeLength
            || checksums.Length != TerrainLodAssetFormat.ReferenceGeometryHashSize) {
            throw new InvalidDataException("A terrain LOD asset section has an invalid length.");
        }

        var ordered = new[] { heights, nodes, checksums };
        Array.Sort(ordered, static (left, right) => left.Offset.CompareTo(right.Offset));
        var previousEnd = (long)headerSize;
        var availableLength = checked(source.Length - assetStart);
        foreach (var section in ordered) {
            var sectionEnd = checked(section.Offset + section.Length);
            if (section.Offset < previousEnd || sectionEnd > availableLength) {
                throw new InvalidDataException("Terrain LOD asset sections overlap or exceed the stream.");
            }

            previousEnd = sectionEnd;
        }
    }

    private static short[][] ReadElevations(
        Stream source,
        long assetStart,
        in TerrainLodBuildSettings settings,
        in TerrainLodAssetSectionDescriptor section) {
        source.Position = checked(assetStart + section.Offset);
        var samplesPerFace = checked(settings.FinestResolution * settings.FinestResolution);
        var faces = new short[CubeSphereGrid.FaceCount][];
        for (var faceIndex = 0; faceIndex < faces.Length; faceIndex++) {
            var elevations = new short[samplesPerFace];
            faces[faceIndex] = elevations;
            if (BitConverter.IsLittleEndian) {
                source.ReadExactly(MemoryMarshal.AsBytes(elevations.AsSpan()));
            } else {
                using var reader = new BinaryReader(source, System.Text.Encoding.UTF8, leaveOpen: true);
                for (var index = 0; index < elevations.Length; index++) {
                    elevations[index] = reader.ReadInt16();
                }
            }
        }

        return faces;
    }

    private static TerrainLodNode[] ReadNodes(
        BinaryReader reader,
        Stream source,
        long assetStart,
        int maximumLod,
        int nodeCount,
        in TerrainLodAssetSectionDescriptor section) {
        source.Position = checked(assetStart + section.Offset);
        var nodes = new TerrainLodNode[nodeCount];
        for (var index = 0; index < nodes.Length; index++) {
            var id = TerrainLodNodeIndex.GetId(index, maximumLod);
            var centerDirection = ReadVector3(reader);
            var angularRadius = reader.ReadSingle();
            var minimumRadius = reader.ReadSingle();
            var maximumRadius = reader.ReadSingle();
            var sphereCenter = ReadVector3(reader);
            var sphereRadius = reader.ReadSingle();
            var horizonPointRadius = reader.ReadSingle();
            var geometricError = reader.ReadSingle();
            nodes[index] = new TerrainLodNode(
                id,
                centerDirection,
                angularRadius,
                minimumRadius,
                maximumRadius,
                new BoundingSphere(sphereCenter, sphereRadius),
                horizonPointRadius,
                geometricError
            );
        }

        return nodes;
    }

    private static byte[] ReadHash(
        Stream source,
        long assetStart,
        in TerrainLodAssetSectionDescriptor section) {
        source.Position = checked(assetStart + section.Offset);
        var hash = new byte[TerrainLodAssetFormat.ReferenceGeometryHashSize];
        source.ReadExactly(hash);
        return hash;
    }

    private static long GetAssetLength(IEnumerable<TerrainLodAssetSectionDescriptor> sections) {
        var length = (long)TerrainLodAssetFormat.HeaderSize;
        foreach (var section in sections) {
            length = Math.Max(length, checked(section.Offset + section.Length));
        }

        return length;
    }

    private static Vector3 ReadVector3(BinaryReader reader) {
        return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}
