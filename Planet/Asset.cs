using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using monogame.Terrain;
using monogame.Utils;

namespace monogame.Planet;

internal sealed class SphereData {
    internal const int FaceCount = 6;

    internal SphereData(
        float referenceRadius,
        int chunkResolution,
        int maximumLod,
        float occluderRadius,
        FaceData[] faces
    ) {
        ReferenceRadius = referenceRadius;
        ChunkResolution = chunkResolution;
        MaximumLod = maximumLod;
        OccluderRadius = occluderRadius;
        Faces = faces;
    }

    public float ReferenceRadius { get; }

    public int ChunkResolution { get; }

    public int MaximumLod { get; }

    public float OccluderRadius { get; }

    internal FaceData[] Faces { get; }

    internal int FinestIntervals => checked((ChunkResolution - 1) * (1 << MaximumLod));

    internal int FinestResolution => checked(FinestIntervals + 1);
}

internal sealed class FaceData {
    private readonly short[] _elevations;
    private readonly float _elevationQuantizationStep;

    internal FaceData(
        int resolution,
        float elevationQuantizationStep,
        short[] elevations,
        ChunkData[] chunks
    ) {
        Resolution = resolution;
        _elevationQuantizationStep = elevationQuantizationStep;
        _elevations = elevations;
        Chunks = chunks;
    }

    public int Resolution { get; }

    internal ReadOnlySpan<short> QuantizedElevations => _elevations;

    internal ChunkData[] Chunks { get; }

    internal float GetElevationUnchecked(int x, int y) {
        return _elevations[y * Resolution + x] * _elevationQuantizationStep;
    }
}

internal readonly record struct ChunkData(
    Vector3 CenterDirection,
    float AngularRadius,
    float MinimumRadius,
    float MaximumRadius,
    BoundingSphere BoundingSphere,
    float HorizonPointRadius,
    float GeometricError
);

internal static class Asset {
    private const uint Magic = 0x444F4C54; // TLOD
    private const int Version = 1;
    private const int SectionCount = 3;
    private const int FixedHeaderSize = 48;
    private const int SectionEntrySize = 20;
    private const int HeaderSize = FixedHeaderSize + SectionCount * SectionEntrySize;
    private const int ChunkSize = 48;
    private const int ReferenceGeometryHashSize = 32;
    private const int ProjectionVersion = 1;
    private const int TriangleTopologyVersion = 1;
    // Increment whenever the meaning or calculation of derived chunk metadata changes.
    private const int ChunkMetadataVersion = 1;

    public static SphereData Read(Stream source) {
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

    public static void Write(
        Stream destination,
        float referenceRadius,
        int chunkResolution,
        int maximumLod,
        float elevationQuantizationStep,
        IReferenceElevationSource elevationSource
    ) {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(elevationSource);
        if (!destination.CanWrite) {
            throw new ArgumentException("The destination stream is not writable.", nameof(destination));
        }

        ValidateGeometry(
            referenceRadius,
            chunkResolution,
            maximumLod,
            elevationQuantizationStep
        );
        var settings = new Geometry(
            referenceRadius,
            chunkResolution,
            maximumLod,
            elevationQuantizationStep
        );
        var data = Build(settings, elevationSource);
        Write(destination, settings, data);
    }

    private static void Write(Stream destination, in Geometry settings, SphereData data) {
        using var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true);
        var heightSectionLength = checked(
            (long)SphereData.FaceCount
            * settings.FinestResolution
            * settings.FinestResolution
            * sizeof(short)
        );
        var chunkSectionLength = checked(
            (long)SphereData.FaceCount * GetChunkCount(settings.MaximumLod) * ChunkSize
        );
        var heightSectionOffset = (long)HeaderSize;
        var chunkSectionOffset = checked(heightSectionOffset + heightSectionLength);
        var checksumSectionOffset = checked(chunkSectionOffset + chunkSectionLength);

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(HeaderSize);
        writer.Write(SectionCount);
        writer.Write(settings.ReferenceRadius);
        writer.Write(settings.ChunkResolution);
        writer.Write(settings.MaximumLod);
        writer.Write(settings.ElevationQuantizationStep);
        writer.Write(settings.FinestResolution);
        writer.Write(SphereData.FaceCount);
        writer.Write(checked(SphereData.FaceCount * GetChunkCount(settings.MaximumLod)));
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

    private static SphereData Build(
        in Geometry settings,
        IReferenceElevationSource elevationSource
    ) {
        var faceElevations = SampleAndQuantize(settings, elevationSource);
        var faceChunks = new ChunkData[SphereData.FaceCount][];

        for (var faceIndex = 0; faceIndex < SphereData.FaceCount; faceIndex++) {
            var face = (FaceId)faceIndex;
            var positions = CreateReferencePositions(settings, face, faceElevations[faceIndex]);
            var chunks = new ChunkData[GetChunkCount(settings.MaximumLod)];
            BuildFaceChunks(settings, face, positions, chunks);
            faceChunks[faceIndex] = chunks;
        }

        var occluderRadius = CalculateOccluderRadius(faceChunks);
        BuildHorizonPoints(occluderRadius, faceChunks);
        return CreateSphereData(settings, occluderRadius, faceElevations, faceChunks);
    }

    private static short[][] SampleAndQuantize(
        in Geometry settings,
        IReferenceElevationSource elevationSource
    ) {
        var resolution = settings.FinestResolution;
        var intervals = settings.FinestIntervals;
        var sampleCount = checked(resolution * resolution);
        var faces = new short[SphereData.FaceCount][];
        var sharedBoundaryElevations = new Dictionary<SurfaceKey, short>(intervals * 12 + 8);

        for (var faceIndex = 0; faceIndex < SphereData.FaceCount; faceIndex++) {
            var face = (FaceId)faceIndex;
            var orientation = Face.GetOrientation(face);
            var directions = Mesh.CreateGrid(
                resolution,
                new Vector2(-1.0f, -1.0f),
                2.0f,
                (_, _, point) => Mesh.GetSphereDirection(point, orientation)
            );
            var elevations = new short[sampleCount];
            faces[faceIndex] = elevations;

            for (var y = 0; y < resolution; y++) {
                for (var x = 0; x < resolution; x++) {
                    var sampleIndex = y * resolution + x;
                    var isBoundary = x == 0 || y == 0 || x == intervals || y == intervals;
                    if (isBoundary) {
                        var key = GetSurfaceKey(face, x, y, intervals);
                        if (!sharedBoundaryElevations.TryGetValue(key, out var quantizedElevation)) {
                            quantizedElevation = SampleAndQuantize(
                                settings,
                                elevationSource,
                                directions[sampleIndex]
                            );
                            sharedBoundaryElevations.Add(key, quantizedElevation);
                        }

                        elevations[sampleIndex] = quantizedElevation;
                        continue;
                    }

                    elevations[sampleIndex] = SampleAndQuantize(
                        settings,
                        elevationSource,
                        directions[sampleIndex]
                    );
                }
            }
        }

        return faces;
    }

    private static short SampleAndQuantize(
        in Geometry settings,
        IReferenceElevationSource elevationSource,
        in Vector3 direction
    ) {
        var elevation = elevationSource.GetElevation(direction);
        if (!float.IsFinite(elevation)) {
            throw new InvalidOperationException("The reference elevation source returned a non-finite value.");
        }

        var scaled = elevation / settings.ElevationQuantizationStep;
        var rounded = MathF.Round(scaled, MidpointRounding.ToEven);
        if (rounded < short.MinValue || rounded > short.MaxValue) {
            throw new InvalidOperationException("The reference elevation cannot be represented by the configured quantization step.");
        }

        return (short)rounded;
    }

    private static Vector3[] CreateReferencePositions(
        in Geometry settings,
        FaceId face,
        short[] quantizedElevations
    ) {
        var resolution = settings.FinestResolution;
        var referenceRadius = settings.ReferenceRadius;
        var elevationQuantizationStep = settings.ElevationQuantizationStep;
        var orientation = Face.GetOrientation(face);
        return Mesh.CreateGrid(
            resolution,
            new Vector2(-1.0f, -1.0f),
            2.0f,
            (x, y, point) => {
                var direction = Mesh.GetSphereDirection(point, orientation);
                var sampleIndex = y * resolution + x;
                var elevation = quantizedElevations[sampleIndex] * elevationQuantizationStep;
                var radius = referenceRadius + elevation;
                if (!float.IsFinite(radius) || radius <= 0.0f) {
                    throw new InvalidOperationException("A quantized reference elevation produces a non-positive planet radius.");
                }

                return direction * radius;
            }
        );
    }

    private static void BuildFaceChunks(
        in Geometry settings,
        FaceId face,
        Vector3[] positions,
        ChunkData[] chunks
    ) {
        for (var level = settings.MaximumLod; level >= 0; level--) {
            var chunksPerAxis = 1 << level;
            for (var y = 0; y < chunksPerAxis; y++) {
                for (var x = 0; x < chunksPerAxis; x++) {
                    var id = Chunk.GetId(level, x, y);
                    chunks[checked((int)id)] = BuildChunk(
                        settings,
                        face,
                        level,
                        x,
                        y,
                        id,
                        positions,
                        chunks
                    );
                }
            }
        }
    }

    private static ChunkData BuildChunk(
        in Geometry settings,
        FaceId face,
        int level,
        int chunkX,
        int chunkY,
        uint id,
        Vector3[] positions,
        ChunkData[] chunks
    ) {
        var cellsPerChunk = settings.ChunkResolution - 1;
        var scale = 1 << (settings.MaximumLod - level);
        var startX = checked(chunkX * cellsPerChunk * scale);
        var startY = checked(chunkY * cellsPerChunk * scale);
        var endX = checked(startX + cellsPerChunk * scale);
        var endY = checked(startY + cellsPerChunk * scale);
        var centerDirection = GetChunkCenterDirection(face, level, chunkX, chunkY);

        var angularRadius = 0.0;
        var maximumRadius = 0.0;
        for (var y = startY; y <= endY; y++) {
            for (var x = startX; x <= endX; x++) {
                var position = positions[y * settings.FinestResolution + x];
                var position64 = DoubleVector3.From(position);
                var radius = position64.Length();
                maximumRadius = Math.Max(maximumRadius, radius);
                angularRadius = Math.Max(angularRadius, Angle(centerDirection, position64, radius));
            }
        }

        var minimumRadius = CalculateCurrentMinimumRadius(settings, startX, startY, scale, positions);
        var childError = 0.0;
        if (level < settings.MaximumLod) {
            for (var index = 0; index < 4; index++) {
                var childId = Chunk.GetChildId(id, (ChunkQuadrant)index);
                var child = chunks[checked((int)childId)];
                minimumRadius = Math.Min(minimumRadius, child.MinimumRadius);
                childError = Math.Max(childError, child.GeometricError);
            }
        }

        var sphereCenterRadius = (float)((minimumRadius + maximumRadius) * 0.5);
        var sphereCenter = centerDirection * sphereCenterRadius;
        var sphereRadius = 0.0;
        var sphereCenter64 = DoubleVector3.From(sphereCenter);
        for (var y = startY; y <= endY; y++) {
            for (var x = startX; x <= endX; x++) {
                var position = DoubleVector3.From(positions[y * settings.FinestResolution + x]);
                sphereRadius = Math.Max(sphereRadius, (position - sphereCenter64).Length());
            }
        }

        var directError = level == settings.MaximumLod
            ? 0.0
            : CalculateDirectError(settings, startX, startY, scale, positions);
        var geometricError = Math.Max(directError, childError);

        return new ChunkData(
            centerDirection,
            EncodeUpper(angularRadius),
            EncodeLower(minimumRadius),
            EncodeUpper(maximumRadius),
            new BoundingSphere(sphereCenter, EncodeUpper(sphereRadius)),
            HorizonPointRadius: 0.0f,
            EncodeUpper(geometricError)
        );
    }

    private static Vector3 GetChunkCenterDirection(FaceId face, int level, int x, int y) {
        var chunksPerAxis = 1 << level;
        var chunkSize = 2.0f / chunksPerAxis;
        var u = -1.0f + (x + 0.5f) * chunkSize;
        var v = -1.0f + (y + 0.5f) * chunkSize;
        return Mesh.GetSphereDirection(new Vector2(u, v), Face.GetOrientation(face));
    }

    private static double CalculateCurrentMinimumRadius(
        in Geometry settings,
        int startX,
        int startY,
        int scale,
        Vector3[] positions
    ) {
        var minimumRadiusSquared = double.PositiveInfinity;
        var cellsPerChunk = settings.ChunkResolution - 1;

        for (var localY = 0; localY < cellsPerChunk; localY++) {
            var y = startY + localY * scale;
            for (var localX = 0; localX < cellsPerChunk; localX++) {
                var x = startX + localX * scale;
                var upperLeft = GetPosition(settings.FinestResolution, positions, x, y);
                var upperRight = GetPosition(settings.FinestResolution, positions, x + scale, y);
                var lowerLeft = GetPosition(settings.FinestResolution, positions, x, y + scale);
                var lowerRight = GetPosition(settings.FinestResolution, positions, x + scale, y + scale);

                minimumRadiusSquared = Math.Min(
                    minimumRadiusSquared,
                    DistanceSquaredToTriangle(upperLeft, upperRight, lowerLeft)
                );
                minimumRadiusSquared = Math.Min(
                    minimumRadiusSquared,
                    DistanceSquaredToTriangle(upperRight, lowerRight, lowerLeft)
                );
            }
        }

        return Math.Sqrt(minimumRadiusSquared);
    }

    private static double CalculateDirectError(
        in Geometry settings,
        int startX,
        int startY,
        int scale,
        Vector3[] positions
    ) {
        var maximumError = 0.0;
        var nodeExtent = (settings.ChunkResolution - 1) * scale;

        for (var offsetY = 0; offsetY <= nodeExtent; offsetY++) {
            GetCellCoordinate(offsetY, scale, settings.ChunkResolution, out var cellY, out var fractionY);
            for (var offsetX = 0; offsetX <= nodeExtent; offsetX++) {
                GetCellCoordinate(offsetX, scale, settings.ChunkResolution, out var cellX, out var fractionX);

                var x = startX + cellX * scale;
                var y = startY + cellY * scale;
                var upperLeft = GetPosition(settings.FinestResolution, positions, x, y);
                var upperRight = GetPosition(settings.FinestResolution, positions, x + scale, y);
                var lowerLeft = GetPosition(settings.FinestResolution, positions, x, y + scale);
                var lowerRight = GetPosition(settings.FinestResolution, positions, x + scale, y + scale);

                DoubleVector3 currentPosition;
                if (fractionX + fractionY <= 1.0) {
                    currentPosition = upperLeft
                        + (upperRight - upperLeft) * fractionX
                        + (lowerLeft - upperLeft) * fractionY;
                }
                else {
                    currentPosition = upperRight * (1.0 - fractionY)
                        + lowerRight * (fractionX + fractionY - 1.0)
                        + lowerLeft * (1.0 - fractionX);
                }

                var referencePosition = GetPosition(
                    settings.FinestResolution,
                    positions,
                    startX + offsetX,
                    startY + offsetY
                );
                maximumError = Math.Max(maximumError, (referencePosition - currentPosition).Length());
            }
        }

        return maximumError;
    }

    private static void GetCellCoordinate(
        int offset,
        int scale,
        int chunkResolution,
        out int cell,
        out double fraction
    ) {
        var cellsPerChunk = chunkResolution - 1;
        if (offset == cellsPerChunk * scale) {
            cell = cellsPerChunk - 1;
            fraction = 1.0;
            return;
        }

        cell = offset / scale;
        fraction = (offset - cell * scale) / (double)scale;
    }

    private static DoubleVector3 GetPosition(int resolution, Vector3[] positions, int x, int y) {
        return DoubleVector3.From(positions[y * resolution + x]);
    }

    private static float CalculateOccluderRadius(ChunkData[][] faces) {
        var occluderRadius = float.PositiveInfinity;
        foreach (var chunks in faces) {
            occluderRadius = MathF.Min(occluderRadius, chunks[0].MinimumRadius);
        }

        if (!float.IsFinite(occluderRadius) || occluderRadius <= 0.0f) {
            throw new InvalidOperationException("The terrain does not contain a positive spherical occluder.");
        }

        return occluderRadius;
    }

    private static void BuildHorizonPoints(float occluderRadius, ChunkData[][] faces) {
        foreach (var chunks in faces) {
            for (var index = 0; index < chunks.Length; index++) {
                var chunk = chunks[index];
                var ratio = Math.Clamp(occluderRadius / chunk.MaximumRadius, 0.0f, 1.0f);
                var horizonAngle = (double)chunk.AngularRadius + Math.Acos(ratio);
                var horizonPointRadius = horizonAngle >= Math.PI * 0.5
                    ? 0.0f
                    : EncodeUpper(occluderRadius / Math.Cos(horizonAngle));
                chunks[index] = chunk with { HorizonPointRadius = horizonPointRadius };
            }
        }
    }

    private static double Angle(in Vector3 centerDirection, in DoubleVector3 position, double radius) {
        var center = DoubleVector3.From(centerDirection);
        var cosine = DoubleVector3.Dot(center, position) / (center.Length() * radius);
        return Math.Acos(Math.Clamp(cosine, -1.0, 1.0));
    }

    private static double DistanceSquaredToTriangle(
        in DoubleVector3 a,
        in DoubleVector3 b,
        in DoubleVector3 c
    ) {
        var edge0 = b - a;
        var edge1 = c - a;
        var normal = DoubleVector3.Cross(edge0, edge1);
        var normalLengthSquared = normal.LengthSquared();

        if (normalLengthSquared > 0.0) {
            var projected = normal * (DoubleVector3.Dot(normal, a) / normalLengthSquared);
            var fromA = projected - a;
            var dot00 = DoubleVector3.Dot(edge0, edge0);
            var dot01 = DoubleVector3.Dot(edge0, edge1);
            var dot11 = DoubleVector3.Dot(edge1, edge1);
            var dot20 = DoubleVector3.Dot(fromA, edge0);
            var dot21 = DoubleVector3.Dot(fromA, edge1);
            var denominator = dot00 * dot11 - dot01 * dot01;

            if (denominator > 0.0) {
                var alongEdge0 = (dot11 * dot20 - dot01 * dot21) / denominator;
                var alongEdge1 = (dot00 * dot21 - dot01 * dot20) / denominator;
                if (alongEdge0 >= 0.0 && alongEdge1 >= 0.0 && alongEdge0 + alongEdge1 <= 1.0) {
                    return projected.LengthSquared();
                }
            }
        }

        return Math.Min(
            DistanceSquaredToSegment(a, b),
            Math.Min(DistanceSquaredToSegment(b, c), DistanceSquaredToSegment(c, a))
        );
    }

    private static double DistanceSquaredToSegment(in DoubleVector3 a, in DoubleVector3 b) {
        var edge = b - a;
        var lengthSquared = edge.LengthSquared();
        if (lengthSquared == 0.0) {
            return a.LengthSquared();
        }

        var amount = Math.Clamp(-DoubleVector3.Dot(a, edge) / lengthSquared, 0.0, 1.0);
        return (a + edge * amount).LengthSquared();
    }

    private static float EncodeUpper(double value) {
        var encoded = (float)value;
        return encoded < value ? MathF.BitIncrement(encoded) : encoded;
    }

    private static float EncodeLower(double value) {
        var encoded = (float)value;
        return encoded > value ? MathF.BitDecrement(encoded) : encoded;
    }

    private static SurfaceKey GetSurfaceKey(FaceId face, int x, int y, int intervals) {
        var u = checked(x * 2 - intervals);
        var v = checked(y * 2 - intervals);
        return face switch {
            FaceId.PositiveX => new SurfaceKey(intervals, v, -u),
            FaceId.NegativeX => new SurfaceKey(-intervals, v, u),
            FaceId.PositiveY => new SurfaceKey(u, intervals, -v),
            FaceId.NegativeY => new SurfaceKey(u, -intervals, v),
            FaceId.PositiveZ => new SurfaceKey(u, v, intervals),
            FaceId.NegativeZ => new SurfaceKey(-u, v, -intervals),
            _ => throw new ArgumentOutOfRangeException(nameof(face)),
        };
    }

    private readonly record struct SurfaceKey(int X, int Y, int Z);

    private readonly record struct DoubleVector3(double X, double Y, double Z) {
        public static DoubleVector3 From(in Vector3 value) => new(value.X, value.Y, value.Z);

        public double LengthSquared() => X * X + Y * Y + Z * Z;

        public double Length() => Math.Sqrt(LengthSquared());

        public static double Dot(in DoubleVector3 left, in DoubleVector3 right) {
            return left.X * right.X + left.Y * right.Y + left.Z * right.Z;
        }

        public static DoubleVector3 Cross(in DoubleVector3 left, in DoubleVector3 right) {
            return new DoubleVector3(
                left.Y * right.Z - left.Z * right.Y,
                left.Z * right.X - left.X * right.Z,
                left.X * right.Y - left.Y * right.X
            );
        }

        public static DoubleVector3 operator +(DoubleVector3 left, DoubleVector3 right) {
            return new DoubleVector3(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
        }

        public static DoubleVector3 operator -(DoubleVector3 left, DoubleVector3 right) {
            return new DoubleVector3(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
        }

        public static DoubleVector3 operator *(DoubleVector3 value, double scale) {
            return new DoubleVector3(value.X * scale, value.Y * scale, value.Z * scale);
        }
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

        Geometry settings;
        try {
            var referenceRadius = reader.ReadSingle();
            var chunkResolution = reader.ReadInt32();
            var maximumLod = reader.ReadInt32();
            var elevationQuantizationStep = reader.ReadSingle();
            ValidateGeometry(referenceRadius, chunkResolution, maximumLod, elevationQuantizationStep);
            settings = new Geometry(
                referenceRadius,
                chunkResolution,
                maximumLod,
                elevationQuantizationStep
            );
        }
        catch (ArgumentOutOfRangeException exception) {
            throw new InvalidDataException("The terrain LOD asset contains invalid build settings.", exception);
        }

        var storedFinestResolution = reader.ReadInt32();
        var storedFaceCount = reader.ReadInt32();
        var storedChunkCount = reader.ReadInt32();
        var occluderRadius = reader.ReadSingle();
        var expectedChunkCount = checked(SphereData.FaceCount * GetChunkCount(settings.MaximumLod));
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
            settings.MaximumLod,
            sections[Section.ChunkMetadata]
        );
        var storedHash = ReadHash(source, assetStart, sections[Section.Checksums]);
        var calculatedHash = CalculateReferenceGeometryHash(settings, faceElevations);
        if (!CryptographicOperations.FixedTimeEquals(storedHash, calculatedHash)) {
            throw new InvalidDataException("The terrain LOD reference geometry checksum does not match its contents.");
        }

        var data = CreateSphereData(settings, occluderRadius, faceElevations, chunks);
        ValidateLoadedData(data);
        source.Position = checked(assetStart + GetAssetLength(sections.Values));
        return data;
    }

    private static SphereData CreateSphereData(
        in Geometry settings,
        float occluderRadius,
        short[][] elevations,
        ChunkData[][] chunks
    ) {
        var faces = new FaceData[SphereData.FaceCount];
        for (var index = 0; index < faces.Length; index++) {
            faces[index] = new FaceData(
                settings.FinestResolution,
                settings.ElevationQuantizationStep,
                elevations[index],
                chunks[index]
            );
        }

        return new SphereData(
            settings.ReferenceRadius,
            settings.ChunkResolution,
            settings.MaximumLod,
            occluderRadius,
            faces
        );
    }

    private static void ValidateLoadedData(SphereData data) {
        if (data.Faces.Length != SphereData.FaceCount) {
            throw new InvalidDataException("The terrain LOD asset must contain six faces.");
        }

        if (!float.IsFinite(data.OccluderRadius) || data.OccluderRadius <= 0.0f) {
            throw new InvalidDataException("The terrain LOD asset has an invalid occluder radius.");
        }

        var expectedElevations = checked(data.FinestResolution * data.FinestResolution);
        var expectedChunks = GetChunkCount(data.MaximumLod);
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
        in Geometry settings,
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
        in Geometry settings,
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
        int maximumLod,
        in SectionDescriptor section
    ) {
        source.Position = checked(assetStart + section.Offset);
        var chunkCount = GetChunkCount(maximumLod);
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
        in Geometry settings,
        SphereData data
    ) {
        using var hash = CreateReferenceGeometryHash(settings);
        foreach (var face in data.Faces) {
            hash.AppendData(MemoryMarshal.AsBytes(face.QuantizedElevations));
        }

        return hash.GetHashAndReset();
    }

    private static byte[] CalculateReferenceGeometryHash(
        in Geometry settings,
        short[][] faceElevations
    ) {
        using var hash = CreateReferenceGeometryHash(settings);
        foreach (var elevations in faceElevations) {
            hash.AppendData(MemoryMarshal.AsBytes(elevations.AsSpan()));
        }

        return hash.GetHashAndReset();
    }

    private static IncrementalHash CreateReferenceGeometryHash(in Geometry settings) {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[28];
        BinaryPrimitives.WriteInt32LittleEndian(header[0..4], ProjectionVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..8], TriangleTopologyVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..12], ChunkMetadataVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..16], BitConverter.SingleToInt32Bits(settings.ReferenceRadius));
        BinaryPrimitives.WriteInt32LittleEndian(header[16..20], settings.ChunkResolution);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..24], settings.MaximumLod);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..28], BitConverter.SingleToInt32Bits(settings.ElevationQuantizationStep));
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

    private static int GetChunkCount(int maximumLod) {
        var count = 0L;
        var chunksAtLevel = 1L;
        for (var level = 0; level <= maximumLod; level++) {
            count += chunksAtLevel;
            chunksAtLevel *= 4;
        }

        return checked((int)count);
    }

    private static void ValidateGeometry(
        float referenceRadius,
        int chunkResolution,
        int maximumLod,
        float elevationQuantizationStep
    ) {
        if (!float.IsFinite(referenceRadius) || referenceRadius <= 0.0f
            || chunkResolution < 2
            || maximumLod is < 0 or > Chunk.MaximumLevel
            || !float.IsFinite(elevationQuantizationStep)
            || elevationQuantizationStep <= 0.0f) {
            throw new ArgumentOutOfRangeException(nameof(referenceRadius));
        }

        _ = checked((chunkResolution - 1) * (1 << maximumLod) + 1);
        _ = GetChunkCount(maximumLod);
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

    private readonly record struct Geometry(
        float ReferenceRadius,
        int ChunkResolution,
        int MaximumLod,
        float ElevationQuantizationStep
    ) {
        public int FinestIntervals => checked((ChunkResolution - 1) * (1 << MaximumLod));

        public int FinestResolution => checked(FinestIntervals + 1);
    }
}
