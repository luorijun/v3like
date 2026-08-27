using System;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using monogame.Terrain;
using monogame.Utils;

namespace monogame.Planet;

internal static class AssetBuilder {
    internal static SphereData Build(in AssetSettings settings, IReferenceElevationSource elevationSource) {
        ArgumentNullException.ThrowIfNull(elevationSource);
        return BuildSphere(settings, elevationSource);
    }

    private static SphereData BuildSphere(in AssetSettings settings, IReferenceElevationSource elevationSource) {
        var maximumElevation = settings.MaximumElevation;
        var maximumRadius = 1.0f + maximumElevation;
        var minRadiusFactors = CalcMinRadiusFactors(settings.ChunkResolution, settings.MaximumLod);
        var occluderRadius = minRadiusFactors[0];
        var sphereParams = new SphereParams(
            maximumRadius,
            occluderRadius,
            MathF.Acos(occluderRadius / maximumRadius),
            settings.ChunkResolution,
            settings.MaximumLod,
            settings.FinestResolution,
            settings.ChunkCount,
            settings.MaximumElevation / short.MaxValue,
            2.0f / settings.FinestIntervals
        );

        var faceElevations = new short[SphereData.FaceCount][];
        var faceChunks = new ChunkData[SphereData.FaceCount][];

        for (var faceIndex = 0; faceIndex < SphereData.FaceCount; faceIndex++) {
            var result = BuildFace(sphereParams, (FaceId)faceIndex, elevationSource, minRadiusFactors);
            faceElevations[faceIndex] = result.Elevations;
            faceChunks[faceIndex] = result.Chunks;
        }

        return Asset.CreateData(settings, occluderRadius, faceElevations, faceChunks);
    }

    private static float[] CalcMinRadiusFactors(int chunkResolution, int maximumLod) {
        var factors = new float[maximumLod + 1];
        var cellsPerChunk = chunkResolution - 1;
        for (var level = 0; level <= maximumLod; level++) {
            var intervals = checked(cellsPerChunk * (1 << level));
            var angleStep = MathF.PI * 0.5f / intervals;
            var tangent = MathF.Tan(MathF.PI * 0.25f - angleStep);
            var tangentSquared = tangent * tangent;
            factors[level] = MathF.Sqrt(
                (tangentSquared + 2.0f * tangent + 3.0f)
                / (2.0f * tangentSquared + 4.0f)
            );
        }

        return factors;
    }

    private static FaceResult BuildFace(SphereParams sphere, FaceId face, IReferenceElevationSource elevationSource, float[] minRadiusFactors) {
        var orientation = Face.GetOrientation(face);
        var elevations = CalcFaceElevations(sphere, orientation, elevationSource);
        var faceParams = new FaceParams(elevations, orientation);

        var chunks = new ChunkData[sphere.ChunkCount];
        var workspaceResolution = checked((sphere.ChunkResolution - 1) * 2 + 1);
        var workspaceSampleCount = checked(workspaceResolution * workspaceResolution);

        for (var level = sphere.MaximumLod; level >= 0; level--) {
            var chunksPerAxis = 1 << level;
            Parallel.For(
                0, chunksPerAxis * chunksPerAxis,
                () => new Vector3[workspaceSampleCount],
                (index, _, positionWorkspace) => {
                    var x = index % chunksPerAxis;
                    var y = index / chunksPerAxis;
                    var id = Chunk.GetId(level, x, y);
                    chunks[checked((int)id)] = BuildChunk(
                        sphere,
                        faceParams,
                        level,
                        x,
                        y,
                        id,
                        chunks,
                        minRadiusFactors,
                        positionWorkspace
                    );
                    return positionWorkspace;
                },
                static _ => { }
            );
        }

        return new FaceResult(elevations, chunks);
    }

    private static short[] CalcFaceElevations(in SphereParams sphere, in Matrix orientation, IReferenceElevationSource elevationSource) {
        var resolution = sphere.FinestResolution;
        var gridStep = sphere.GridStep;

        var elevations = new short[resolution * resolution];
        for (var y = 0; y < resolution; y++) {
            for (var x = 0; x < resolution; x++) {
                var index = y * resolution + x;
                var point = new Vector2(-1.0f + x * gridStep, -1.0f + y * gridStep);
                var direction = Mesh.GetSphereDirection(point, orientation);
                var elevation = elevationSource.GetElevation(direction);
                var quantizedElevation = (short)MathF.Round(elevation * short.MaxValue);
                elevations[index] = quantizedElevation;
            }
        }

        return elevations;
    }

    private static ChunkData BuildChunk(
        in SphereParams sphere,
        in FaceParams face,
        int level,
        int chunkX,
        int chunkY,
        uint id,
        ChunkData[] chunks,
        float[] minRadiusFactors,
        Vector3[] positionWorkspace
    ) {
        var maxRadius = sphere.MaximumRadius;
        var minRadius = minRadiusFactors[level];
        var (
            centerDirection,
            angularRadius,
            boundingSphere
        ) = CalcChunkBounds(face.Orientation, level, chunkX, chunkY, minRadius, maxRadius);

        var horizonAngle = angularRadius + sphere.HorizonAngleOffset;
        var horizonPointRadius = horizonAngle >= MathF.PI * 0.5f
            ? 0.0f
            : sphere.OccluderRadius / MathF.Cos(horizonAngle);

        var cellsPerChunk = sphere.ChunkResolution - 1;
        var geometricError = 0.0f;
        if (level < sphere.MaximumLod) {
            var scale = 1 << (sphere.MaximumLod - level);
            var startX = checked(chunkX * cellsPerChunk * scale);
            var startY = checked(chunkY * cellsPerChunk * scale);
            var tileResolution = checked(cellsPerChunk * 2 + 1);
            var sampleScale = scale / 2;
            var elevations = face.Elevations;
            var orientation = face.Orientation;
            var gridStep = sphere.GridStep;

            for (var localY = 0; localY < tileResolution; localY++) {
                var y = checked(startY + localY * sampleScale);
                var rowOffset = y * sphere.FinestResolution;
                for (var localX = 0; localX < tileResolution; localX++) {
                    var x = checked(startX + localX * sampleScale);
                    var point = new Vector2(-1.0f + x * gridStep, -1.0f + y * gridStep);
                    var direction = Mesh.GetSphereDirection(point, orientation);
                    var elevation = elevations[rowOffset + x] * sphere.ElevationStep;
                    var radius = 1.0f + elevation;
                    var index = localY * tileResolution + localX;
                    positionWorkspace[index] = direction * radius;
                }
            }

            geometricError = CalcLocalTransitionError(tileResolution, positionWorkspace);
            for (var index = 0; index < 4; index++) {
                var childId = Chunk.GetChildId(id, (ChunkQuadrant)index);
                var child = chunks[checked((int)childId)];
                geometricError = MathF.Max(geometricError, child.GeometricError);
            }
        }

        return new ChunkData(
            centerDirection,
            angularRadius,
            minRadius,
            maxRadius,
            boundingSphere,
            horizonPointRadius,
            geometricError
        );
    }

    private static (
        Vector3 CenterDirection,
        float AngularRadius,
        BoundingSphere BoundingSphere
    ) CalcChunkBounds(in Matrix orientation, int level, int x, int y, float minRadius, float maxRadius) {
        var chunksPerAxis = 1 << level;
        var chunkSize = 2.0f / chunksPerAxis;
        var minimumU = -1.0f + x * chunkSize;
        var minimumV = -1.0f + y * chunkSize;
        var maximumU = minimumU + chunkSize;
        var maximumV = minimumV + chunkSize;
        var center = Mesh.GetSphereDirection(
            new Vector2(minimumU + chunkSize * 0.5f, minimumV + chunkSize * 0.5f),
            Matrix.Identity
        );
        var maximumChordSquared = 0.0f;

        maximumChordSquared = MathF.Max(maximumChordSquared, GetCornerChordSquared(minimumU, minimumV));
        maximumChordSquared = MathF.Max(maximumChordSquared, GetCornerChordSquared(maximumU, minimumV));
        maximumChordSquared = MathF.Max(maximumChordSquared, GetCornerChordSquared(minimumU, maximumV));
        maximumChordSquared = MathF.Max(maximumChordSquared, GetCornerChordSquared(maximumU, maximumV));
        var centerDirection = Vector3.TransformNormal(center, orientation);
        var radialHalfExtent = (maxRadius - minRadius) * 0.5f;
        var midRadius = minRadius + radialHalfExtent;
        var sphereRadius = MathF.Sqrt(
            radialHalfExtent * radialHalfExtent
            + maxRadius * midRadius * maximumChordSquared
        );

        return (
            centerDirection,
            2.0f * MathF.Asin(MathF.Sqrt(maximumChordSquared) * 0.5f),
            new BoundingSphere(centerDirection * midRadius, sphereRadius)
        );

        float GetCornerChordSquared(float u, float v) {
            var direction = Mesh.GetSphereDirection(new Vector2(u, v), Matrix.Identity);
            return Vector3.DistanceSquared(center, direction);
        }
    }

    private static float CalcLocalTransitionError(int tileResolution, Vector3[] positions) {
        var cellsPerChunk = tileResolution / 2;
        var maximumErrorSquared = 0.0f;

        for (var y = 0; y <= cellsPerChunk; y++) {
            var rowOffset = y * 2 * tileResolution;
            for (var x = 0; x < cellsPerChunk; x++) {
                var left = rowOffset + x * 2;
                AccumulateMidpointError(left + 1, left, left + 2);
            }
        }

        for (var y = 0; y < cellsPerChunk; y++) {
            var upperRowOffset = y * 2 * tileResolution;
            var lowerRowOffset = upperRowOffset + tileResolution * 2;
            for (var x = 0; x <= cellsPerChunk; x++) {
                var upper = upperRowOffset + x * 2;
                AccumulateMidpointError(
                    upper + tileResolution,
                    upper,
                    lowerRowOffset + x * 2
                );
            }
        }

        for (var y = 0; y < cellsPerChunk; y++) {
            var upperRowOffset = y * 2 * tileResolution;
            var lowerRowOffset = upperRowOffset + tileResolution * 2;
            for (var x = 0; x < cellsPerChunk; x++) {
                var upperLeft = upperRowOffset + x * 2;
                AccumulateMidpointError(
                    upperLeft + tileResolution + 1,
                    upperLeft + 2,
                    lowerRowOffset + x * 2
                );
            }
        }

        return MathF.Sqrt(maximumErrorSquared);

        void AccumulateMidpointError(int childIndex, int firstParentIndex, int secondParentIndex) {
            var childPosition = positions[childIndex];
            var firstParentPosition = positions[firstParentIndex];
            var secondParentPosition = positions[secondParentIndex];
            var parentPosition = (firstParentPosition + secondParentPosition) * 0.5f;
            maximumErrorSquared = MathF.Max(
                maximumErrorSquared,
                Vector3.DistanceSquared(childPosition, parentPosition)
            );
        }
    }

    private readonly record struct SphereParams(
        float MaximumRadius,
        float OccluderRadius,
        float HorizonAngleOffset,
        int ChunkResolution,
        int MaximumLod,
        int FinestResolution,
        int ChunkCount,
        float ElevationStep,
        float GridStep
    );

    private readonly record struct FaceParams(
        short[] Elevations,
        Matrix Orientation
    );

    private readonly record struct FaceResult(
        short[] Elevations,
        ChunkData[] Chunks
    );

}
