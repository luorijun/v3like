using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;

namespace monogame.Planet;

internal static class AssetCodec {
    private const uint Magic = 0x444F4C54; // TLOD
    private const int Version = 3;
    private const int SectionCount = 3;
    private const int FixedHeaderSize = 44;
    private const int SectionEntrySize = 20;
    private const int HeaderSize = FixedHeaderSize + SectionCount * SectionEntrySize;
    private const int ChunkSize = 48;
    private const int ReferenceGeometryHashSize = 32;
    private const int ProjectionVersion = 2;
    private const int TriangleTopologyVersion = 1;
    // Increment whenever the meaning or calculation of derived chunk metadata changes.
    private const int ChunkMetadataVersion = 3;

    internal static SphereData Read(Stream source) {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek) {
            throw new ArgumentException("The terrain LOD asset stream must be readable and seekable.", nameof(source));
        }

        var assetStart = source.Position;
        try {
            return ReadCore(source, assetStart);
        }
        catch (InvalidDataException) {
            throw;
        }
        catch (Exception exception) when (exception is EndOfStreamException
            or OverflowException
            or ArgumentOutOfRangeException) {
            throw new InvalidDataException("The terrain LOD asset is truncated or malformed.", exception);
        }
    }

    internal static void Write(
        Stream destination,
        in AssetSettings settings,
        SphereData data
    ) {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(data);
        if (!destination.CanWrite) {
            throw new ArgumentException("The destination stream is not writable.", nameof(destination));
        }
        using var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true);
        var heightSectionLength = checked(
            (long)SphereData.FaceCount
            * settings.FinestResolution
            * settings.FinestResolution
            * sizeof(short)
        );
        var chunkSectionLength = checked(
            (long)SphereData.FaceCount * settings.ChunkCount * ChunkSize
        );
        var heightSectionOffset = (long)HeaderSize;
        var chunkSectionOffset = checked(heightSectionOffset + heightSectionLength);
        var checksumSectionOffset = checked(chunkSectionOffset + chunkSectionLength);

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(HeaderSize);
        writer.Write(SectionCount);
        writer.Write(settings.ChunkResolution);
        writer.Write(settings.MaximumLod);
        writer.Write(settings.MaximumElevation);
        writer.Write(settings.FinestResolution);
        writer.Write(SphereData.FaceCount);
        writer.Write(checked(SphereData.FaceCount * settings.ChunkCount));
        writer.Write(data.OccluderRadius);
        WriteSection(writer, Section.ReferenceHeights, heightSectionOffset, heightSectionLength);
        WriteSection(writer, Section.ChunkMetadata, chunkSectionOffset, chunkSectionLength);
        WriteSection(writer, Section.Checksums, checksumSectionOffset, ReferenceGeometryHashSize);

        foreach (var face in data.Faces) {
            var elevations = face.QuantizedElevations;
            if (BitConverter.IsLittleEndian) {
                writer.Flush();
                destination.Write(MemoryMarshal.AsBytes(elevations));
            }
            else {
                foreach (var elevation in elevations) {
                    writer.Write(elevation);
                }
            }
        }

        foreach (var face in data.Faces) {
            foreach (var chunk in face.Chunks) {
                WriteVector3(writer, chunk.CenterDirection);
                writer.Write(chunk.AngularRadius);
                writer.Write(chunk.MinimumRadius);
                writer.Write(chunk.MaximumRadius);
                WriteVector3(writer, chunk.BoundingSphere.Center);
                writer.Write(chunk.BoundingSphere.Radius);
                writer.Write(chunk.HorizonPointRadius);
                writer.Write(chunk.GeometricError);
            }
        }

        writer.Write(CalculateReferenceGeometryHash(settings, data));
    }

    private static SphereData ReadCore(Stream source, long assetStart) {
        using var reader = new BinaryReader(source, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic) {
            throw new InvalidDataException("The stream is not a terrain LOD asset.");
        }

        if (reader.ReadInt32() != Version) {
            throw new InvalidDataException("The terrain LOD asset version is not supported.");
        }

        var headerSize = reader.ReadInt32();
        var sectionCount = reader.ReadInt32();
        if (headerSize != HeaderSize || sectionCount != SectionCount) {
            throw new InvalidDataException("The terrain LOD asset header layout is invalid.");
        }

        AssetSettings settings;
        try {
            var chunkResolution = reader.ReadInt32();
            var maximumLod = reader.ReadInt32();
            var maximumElevation = reader.ReadSingle();
            settings = new AssetSettings(
                chunkResolution,
                maximumLod,
                maximumElevation
            );
        }
        catch (ArgumentOutOfRangeException exception) {
            throw new InvalidDataException("The terrain LOD asset contains invalid build settings.", exception);
        }

        var storedFinestResolution = reader.ReadInt32();
        var storedFaceCount = reader.ReadInt32();
        var storedChunkCount = reader.ReadInt32();
        var occluderRadius = reader.ReadSingle();
        var expectedChunkCount = checked(SphereData.FaceCount * settings.ChunkCount);
        if (storedFinestResolution != settings.FinestResolution
            || storedFaceCount != SphereData.FaceCount
            || storedChunkCount != expectedChunkCount
            || !float.IsFinite(occluderRadius)
            || occluderRadius <= 0.0f) {
            throw new InvalidDataException("The terrain LOD asset header values are inconsistent.");
        }

        var sections = ReadSectionTable(reader, sectionCount);
        ValidateSections(source, assetStart, headerSize, settings, storedChunkCount, sections);

        var faceElevations = ReadElevations(
            source,
            assetStart,
            settings,
            sections[Section.ReferenceHeights]
        );
        var chunks = ReadChunks(
            reader,
            source,
            assetStart,
            settings.ChunkCount,
            sections[Section.ChunkMetadata]
        );
        var storedHash = ReadHash(source, assetStart, sections[Section.Checksums]);
        var calculatedHash = CalculateReferenceGeometryHash(settings, faceElevations);
        if (!CryptographicOperations.FixedTimeEquals(storedHash, calculatedHash)) {
            throw new InvalidDataException("The terrain LOD reference geometry checksum does not match its contents.");
        }

        var data = Asset.CreateData(settings, occluderRadius, faceElevations, chunks);
        ValidateLoadedData(settings, data);
        source.Position = checked(assetStart + GetAssetLength(sections.Values));
        return data;
    }

    private static void ValidateLoadedData(
        in AssetSettings settings,
        SphereData data
    ) {
        if (data.Faces.Length != SphereData.FaceCount) {
            throw new InvalidDataException("The terrain LOD asset must contain six faces.");
        }

        if (!float.IsFinite(data.OccluderRadius) || data.OccluderRadius <= 0.0f) {
            throw new InvalidDataException("The terrain LOD asset has an invalid occluder radius.");
        }

        var expectedElevations = checked(data.FinestResolution * data.FinestResolution);
        var expectedChunks = settings.ChunkCount;
        for (var faceIndex = 0; faceIndex < SphereData.FaceCount; faceIndex++) {
            var face = data.Faces[faceIndex];
            if (face is null
                || face.Resolution != data.FinestResolution
                || face.QuantizedElevations.Length != expectedElevations) {
                throw new InvalidDataException("A terrain LOD asset face has an invalid elevation count.");
            }

            if (face.Chunks is null || face.Chunks.Length != expectedChunks) {
                throw new InvalidDataException("A terrain LOD asset face has an invalid chunk count.");
            }

            ValidateLoadedChunks(data.MaximumLod, data.OccluderRadius, face.Chunks);
        }
    }

    private static void ValidateLoadedChunks(
        int maximumLod,
        float occluderRadius,
        ChunkData[] chunks
    ) {
        var firstId = 0u;
        var chunksAtLevel = 1u;
        for (var level = 0; level <= maximumLod; level++) {
            var endId = checked(firstId + chunksAtLevel);
            for (var id = firstId; id < endId; id++) {
                var chunk = chunks[checked((int)id)];
                if (!IsFinite(chunk.CenterDirection)
                    || !float.IsFinite(chunk.AngularRadius)
                    || !float.IsFinite(chunk.MinimumRadius)
                    || !float.IsFinite(chunk.MaximumRadius)
                    || !IsFinite(chunk.BoundingSphere.Center)
                    || !float.IsFinite(chunk.BoundingSphere.Radius)
                    || !float.IsFinite(chunk.HorizonPointRadius)
                    || !float.IsFinite(chunk.GeometricError)) {
                    throw new InvalidDataException("The terrain LOD asset contains non-finite chunk metadata.");
                }

                if (chunk.AngularRadius < 0.0f
                    || chunk.MinimumRadius <= 0.0f
                    || chunk.MinimumRadius > chunk.MaximumRadius
                    || chunk.BoundingSphere.Radius < 0.0f
                    || chunk.HorizonPointRadius < 0.0f
                    || chunk.GeometricError < 0.0f
                    || occluderRadius > chunk.MinimumRadius) {
                    throw new InvalidDataException("The terrain LOD asset contains invalid chunk bounds.");
                }

                if (level == maximumLod) {
                    if (chunk.GeometricError != 0.0f) {
                        throw new InvalidDataException("A highest-LOD chunk has non-zero geometric error.");
                    }

                    continue;
                }

                for (var childIndex = 0; childIndex < 4; childIndex++) {
                    var childId = Chunk.GetChildId(id, (ChunkQuadrant)childIndex);
                    var child = chunks[checked((int)childId)];
                    if (chunk.GeometricError < child.GeometricError) {
                        throw new InvalidDataException("A parent chunk has less geometric error than one of its children.");
                    }
                }
            }

            firstId = endId;
            chunksAtLevel = checked(chunksAtLevel * 4);
        }
    }

    private static bool IsFinite(in Vector3 value) {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static Dictionary<Section, SectionDescriptor> ReadSectionTable(
        BinaryReader reader,
        int sectionCount
    ) {
        var sections = new Dictionary<Section, SectionDescriptor>(sectionCount);
        for (var index = 0; index < sectionCount; index++) {
            var section = (Section)reader.ReadInt32();
            var offset = reader.ReadInt64();
            var length = reader.ReadInt64();
            if (!Enum.IsDefined(section)
                || !sections.TryAdd(section, new SectionDescriptor(section, offset, length))) {
                throw new InvalidDataException("The terrain LOD asset contains an unknown or duplicate section.");
            }
        }

        return sections;
    }

    private static void ValidateSections(
        Stream source,
        long assetStart,
        int headerSize,
        in AssetSettings settings,
        int chunkCount,
        Dictionary<Section, SectionDescriptor> sections
    ) {
        if (sections.Count != SectionCount
            || !sections.TryGetValue(Section.ReferenceHeights, out var heights)
            || !sections.TryGetValue(Section.ChunkMetadata, out var chunks)
            || !sections.TryGetValue(Section.Checksums, out var checksums)) {
            throw new InvalidDataException("The terrain LOD asset is missing a required section.");
        }

        var expectedHeightLength = checked(
            (long)SphereData.FaceCount
            * settings.FinestResolution
            * settings.FinestResolution
            * sizeof(short)
        );
        var expectedChunkLength = checked((long)chunkCount * ChunkSize);
        if (heights.Length != expectedHeightLength
            || chunks.Length != expectedChunkLength
            || checksums.Length != ReferenceGeometryHashSize) {
            throw new InvalidDataException("A terrain LOD asset section has an invalid length.");
        }

        var ordered = new[] { heights, chunks, checksums };
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
        in AssetSettings settings,
        in SectionDescriptor section
    ) {
        source.Position = checked(assetStart + section.Offset);
        var samplesPerFace = checked(settings.FinestResolution * settings.FinestResolution);
        var faces = new short[SphereData.FaceCount][];
        for (var faceIndex = 0; faceIndex < faces.Length; faceIndex++) {
            var elevations = new short[samplesPerFace];
            faces[faceIndex] = elevations;
            if (BitConverter.IsLittleEndian) {
                source.ReadExactly(MemoryMarshal.AsBytes(elevations.AsSpan()));
            }
            else {
                using var reader = new BinaryReader(source, System.Text.Encoding.UTF8, leaveOpen: true);
                for (var index = 0; index < elevations.Length; index++) {
                    elevations[index] = reader.ReadInt16();
                }
            }
        }

        return faces;
    }

    private static ChunkData[][] ReadChunks(
        BinaryReader reader,
        Stream source,
        long assetStart,
        int chunkCount,
        in SectionDescriptor section
    ) {
        source.Position = checked(assetStart + section.Offset);
        var faces = new ChunkData[SphereData.FaceCount][];
        for (var faceIndex = 0; faceIndex < faces.Length; faceIndex++) {
            var chunks = new ChunkData[chunkCount];
            faces[faceIndex] = chunks;
            for (var index = 0; index < chunks.Length; index++) {
                var centerDirection = ReadVector3(reader);
                var angularRadius = reader.ReadSingle();
                var minimumRadius = reader.ReadSingle();
                var maximumRadius = reader.ReadSingle();
                var sphereCenter = ReadVector3(reader);
                var sphereRadius = reader.ReadSingle();
                var horizonPointRadius = reader.ReadSingle();
                var geometricError = reader.ReadSingle();
                chunks[index] = new ChunkData(
                    centerDirection,
                    angularRadius,
                    minimumRadius,
                    maximumRadius,
                    new BoundingSphere(sphereCenter, sphereRadius),
                    horizonPointRadius,
                    geometricError
                );
            }
        }

        return faces;
    }

    private static byte[] ReadHash(
        Stream source,
        long assetStart,
        in SectionDescriptor section
    ) {
        source.Position = checked(assetStart + section.Offset);
        var hash = new byte[ReferenceGeometryHashSize];
        source.ReadExactly(hash);
        return hash;
    }

    private static byte[] CalculateReferenceGeometryHash(
        in AssetSettings settings,
        SphereData data
    ) {
        using var hash = CreateReferenceGeometryHash(settings);
        foreach (var face in data.Faces) {
            hash.AppendData(MemoryMarshal.AsBytes(face.QuantizedElevations));
        }

        return hash.GetHashAndReset();
    }

    private static byte[] CalculateReferenceGeometryHash(
        in AssetSettings settings,
        short[][] faceElevations
    ) {
        using var hash = CreateReferenceGeometryHash(settings);
        foreach (var elevations in faceElevations) {
            hash.AppendData(MemoryMarshal.AsBytes(elevations.AsSpan()));
        }

        return hash.GetHashAndReset();
    }

    private static IncrementalHash CreateReferenceGeometryHash(in AssetSettings settings) {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(header[0..4], ProjectionVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..8], TriangleTopologyVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..12], ChunkMetadataVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..16], settings.ChunkResolution);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..20], settings.MaximumLod);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..24], BitConverter.SingleToInt32Bits(settings.MaximumElevation));
        hash.AppendData(header);
        return hash;
    }

    private static long GetAssetLength(IEnumerable<SectionDescriptor> sections) {
        var length = (long)HeaderSize;
        foreach (var section in sections) {
            length = Math.Max(length, checked(section.Offset + section.Length));
        }

        return length;
    }

    private static void WriteSection(
        BinaryWriter writer,
        Section section,
        long offset,
        long length
    ) {
        writer.Write((int)section);
        writer.Write(offset);
        writer.Write(length);
    }

    private static Vector3 ReadVector3(BinaryReader reader) {
        return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value) {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private enum Section {
        ReferenceHeights = 1,
        ChunkMetadata = 2,
        Checksums = 3,
    }

    private readonly record struct SectionDescriptor(
        Section Section,
        long Offset,
        long Length
    );
}
