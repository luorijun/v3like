using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using monogame.Terrain;

namespace monogame.Planet.Gen;

internal static class TerrainLodAssetBuilder {
    public static TerrainLodAsset Build(
        in TerrainLodBuildSettings settings,
        IReferenceElevationSource elevationSource) {
        settings.Validate();
        ArgumentNullException.ThrowIfNull(elevationSource);

        var faceElevations = SampleAndQuantize(settings, elevationSource);
        var nodes = new TerrainLodNode[TerrainLodNodeIndex.GetNodeCount(settings.MaximumLod)];

        for (var faceIndex = 0; faceIndex < CubeSphereGrid.FaceCount; faceIndex++) {
            var face = (TerrainCubeFace)faceIndex;
            var positions = CreateReferencePositions(settings, face, faceElevations[faceIndex]);
            BuildFaceNodes(settings, face, positions, nodes);
        }

        var occluderRadius = CalculateOccluderRadius(settings.MaximumLod, nodes);
        BuildHorizonPoints(occluderRadius, nodes);
        TerrainLodAssetIntegrity.ValidateNodeHierarchy(settings.MaximumLod, nodes, occluderRadius);

        return new TerrainLodAsset(
            settings,
            faceElevations,
            nodes,
            occluderRadius,
            TerrainLodAssetIntegrity.CalculateReferenceGeometryHash(settings, faceElevations)
        );
    }

    private static short[][] SampleAndQuantize(
        in TerrainLodBuildSettings settings,
        IReferenceElevationSource elevationSource) {
        var resolution = settings.FinestResolution;
        var intervals = settings.FinestIntervals;
        var sampleCount = checked(resolution * resolution);
        var faces = new short[CubeSphereGrid.FaceCount][];
        var sharedBoundaryElevations = new Dictionary<CubeSurfaceKey, short>(intervals * 12 + 8);

        for (var faceIndex = 0; faceIndex < CubeSphereGrid.FaceCount; faceIndex++) {
            var face = (TerrainCubeFace)faceIndex;
            var elevations = new short[sampleCount];
            faces[faceIndex] = elevations;

            for (var y = 0; y < resolution; y++) {
                for (var x = 0; x < resolution; x++) {
                    var isBoundary = x == 0 || y == 0 || x == intervals || y == intervals;
                    if (isBoundary) {
                        var key = CubeSphereGrid.GetSurfaceKey(face, x, y, intervals);
                        if (!sharedBoundaryElevations.TryGetValue(key, out var quantizedElevation)) {
                            var direction = Vector3.Normalize(new Vector3(key.X, key.Y, key.Z));
                            quantizedElevation = SampleAndQuantize(settings, elevationSource, direction);
                            sharedBoundaryElevations.Add(key, quantizedElevation);
                        }

                        elevations[y * resolution + x] = quantizedElevation;
                        continue;
                    }

                    var interiorDirection = CubeSphereGrid.GetDirection(face, x, y, intervals);
                    elevations[y * resolution + x] = SampleAndQuantize(settings, elevationSource, interiorDirection);
                }
            }
        }

        return faces;
    }

    private static short SampleAndQuantize(
        in TerrainLodBuildSettings settings,
        IReferenceElevationSource elevationSource,
        in Vector3 direction) {
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
        in TerrainLodBuildSettings settings,
        TerrainCubeFace face,
        short[] quantizedElevations) {
        var resolution = settings.FinestResolution;
        var intervals = settings.FinestIntervals;
        var positions = new Vector3[quantizedElevations.Length];

        for (var y = 0; y < resolution; y++) {
            for (var x = 0; x < resolution; x++) {
                var direction = CubeSphereGrid.GetDirection(face, x, y, intervals);
                var elevation = quantizedElevations[y * resolution + x] * settings.ElevationQuantizationStep;
                var radius = settings.ReferenceRadius + elevation;
                if (!float.IsFinite(radius) || radius <= 0.0f) {
                    throw new InvalidOperationException("A quantized reference elevation produces a non-positive planet radius.");
                }

                positions[y * resolution + x] = direction * radius;
            }
        }

        return positions;
    }

    private static void BuildFaceNodes(
        in TerrainLodBuildSettings settings,
        TerrainCubeFace face,
        Vector3[] positions,
        TerrainLodNode[] nodes) {
        for (var level = settings.MaximumLod; level >= 0; level--) {
            var chunksPerAxis = 1 << level;
            for (var y = 0; y < chunksPerAxis; y++) {
                for (var x = 0; x < chunksPerAxis; x++) {
                    var id = new TerrainLodNodeId(face, level, x, y);
                    nodes[TerrainLodNodeIndex.GetIndex(id, settings.MaximumLod)] = BuildNode(settings, id, positions, nodes);
                }
            }
        }
    }

    private static TerrainLodNode BuildNode(
        in TerrainLodBuildSettings settings,
        in TerrainLodNodeId id,
        Vector3[] positions,
        TerrainLodNode[] nodes) {
        var cellsPerChunk = settings.ChunkResolution - 1;
        var scale = 1 << (settings.MaximumLod - id.Level);
        var startX = checked(id.X * cellsPerChunk * scale);
        var startY = checked(id.Y * cellsPerChunk * scale);
        var endX = checked(startX + cellsPerChunk * scale);
        var endY = checked(startY + cellsPerChunk * scale);
        var centerDirection = GetNodeCenterDirection(id);

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
        if (id.Level < settings.MaximumLod) {
            for (var childY = 0; childY < 2; childY++) {
                for (var childX = 0; childX < 2; childX++) {
                    var childId = new TerrainLodNodeId(
                        id.Face,
                        id.Level + 1,
                        id.X * 2 + childX,
                        id.Y * 2 + childY
                    );
                    var child = nodes[TerrainLodNodeIndex.GetIndex(childId, settings.MaximumLod)];
                    minimumRadius = Math.Min(minimumRadius, child.MinimumRadius);
                    childError = Math.Max(childError, child.GeometricError);
                }
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

        var directError = id.Level == settings.MaximumLod
            ? 0.0
            : CalculateDirectError(settings, startX, startY, scale, positions);
        var geometricError = Math.Max(directError, childError);

        return new TerrainLodNode(
            id,
            centerDirection,
            EncodeUpper(angularRadius),
            EncodeLower(minimumRadius),
            EncodeUpper(maximumRadius),
            new BoundingSphere(sphereCenter, EncodeUpper(sphereRadius)),
            HorizonPointRadius: 0.0f,
            EncodeUpper(geometricError)
        );
    }

    private static Vector3 GetNodeCenterDirection(in TerrainLodNodeId id) {
        var chunksPerAxis = 1 << id.Level;
        var chunkSize = 2.0f / chunksPerAxis;
        var u = -1.0f + (id.X + 0.5f) * chunkSize;
        var v = -1.0f + (id.Y + 0.5f) * chunkSize;
        return CubeSphereGrid.GetDirection(id.Face, u, v);
    }

    private static double CalculateCurrentMinimumRadius(
        in TerrainLodBuildSettings settings,
        int startX,
        int startY,
        int scale,
        Vector3[] positions) {
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
        in TerrainLodBuildSettings settings,
        int startX,
        int startY,
        int scale,
        Vector3[] positions) {
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
                } else {
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
        out double fraction) {
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

    private static float CalculateOccluderRadius(int maximumLod, TerrainLodNode[] nodes) {
        var occluderRadius = float.PositiveInfinity;
        for (var faceIndex = 0; faceIndex < CubeSphereGrid.FaceCount; faceIndex++) {
            var rootId = new TerrainLodNodeId((TerrainCubeFace)faceIndex, 0, 0, 0);
            var root = nodes[TerrainLodNodeIndex.GetIndex(rootId, maximumLod)];
            occluderRadius = MathF.Min(occluderRadius, root.MinimumRadius);
        }

        if (!float.IsFinite(occluderRadius) || occluderRadius <= 0.0f) {
            throw new InvalidOperationException("The terrain does not contain a positive spherical occluder.");
        }

        return occluderRadius;
    }

    private static void BuildHorizonPoints(float occluderRadius, TerrainLodNode[] nodes) {
        for (var index = 0; index < nodes.Length; index++) {
            var node = nodes[index];
            var ratio = Math.Clamp(occluderRadius / node.MaximumRadius, 0.0f, 1.0f);
            var horizonAngle = (double)node.AngularRadius + Math.Acos(ratio);
            var horizonPointRadius = horizonAngle >= Math.PI * 0.5
                ? 0.0f
                : EncodeUpper(occluderRadius / Math.Cos(horizonAngle));
            nodes[index] = node with { HorizonPointRadius = horizonPointRadius };
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
        in DoubleVector3 c) {
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
}
