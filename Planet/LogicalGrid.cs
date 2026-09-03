using System;
using Microsoft.Xna.Framework;

namespace monogame.Planet;

internal sealed class LogicalGrid {
    internal const int Frequency = 358;
    internal const int TileCount = 10 * Frequency * Frequency + 2;
    internal const int IndexTextureResolution = 256;

    private const int BaseVertexCount = 12;
    private const int BaseEdgeCount = 30;
    private const int BaseFaceCount = 20;
    private const int EdgeInteriorCount = Frequency - 1;
    private const int FaceInteriorCount = (Frequency - 1) * (Frequency - 2) / 2;
    private const int FaceInteriorStart = BaseVertexCount + BaseEdgeCount * EdgeInteriorCount;
    private const int MaximumNeighborCount = 6;
    private const uint TileCodeMask = (1u << 21) - 1u;

    private static readonly int[,] s_faces = {
        { 0, 11, 5 }, { 0, 5, 1 }, { 0, 1, 7 }, { 0, 7, 10 }, { 0, 10, 11 },
        { 1, 5, 9 }, { 5, 11, 4 }, { 11, 10, 2 }, { 10, 7, 6 }, { 7, 1, 8 },
        { 3, 9, 4 }, { 3, 4, 2 }, { 3, 2, 6 }, { 3, 6, 8 }, { 3, 8, 9 },
        { 4, 9, 5 }, { 2, 4, 11 }, { 6, 2, 10 }, { 8, 6, 7 }, { 9, 8, 1 },
    };

    private readonly Vector3[] _baseVertices;
    private readonly FaceProjection[] _faceProjections;
    private readonly int[] _edgeIds;
    private readonly Vector3[] _tileCenters;
    private readonly int[] _neighbors;
    private readonly byte[] _neighborCounts;

    internal LogicalGrid() {
        _baseVertices = CreateBaseVertices();
        _edgeIds = CreateEdgeIds();
        _faceProjections = CreateFaceProjections();
        _tileCenters = CreateTileCenters();
        (_neighbors, _neighborCounts) = CreateNeighbors();
        ValidateTopology();
    }

    internal Color[] CreateIndexColors(
        in Matrix faceOrientation,
        in Vector2 chunkPosition,
        float chunkSize
    ) {
        var colors = new Color[IndexTextureResolution * IndexTextureResolution];
        var step = chunkSize / IndexTextureResolution;
        var previousRowFirstTileId = -1;

        for (var y = 0; y < IndexTextureResolution; y++) {
            var pointY = chunkPosition.Y + (y + 0.5f) * step;
            var tileId = previousRowFirstTileId;
            for (var x = 0; x < IndexTextureResolution; x++) {
                var point = new Vector2(
                    chunkPosition.X + (x + 0.5f) * step,
                    pointY
                );
                var direction = Utils.Mesh.GetSphereDirection(point, faceOrientation);
                tileId = tileId < 0
                    ? LocateFromProjection(direction)
                    : LocateFromSeed(direction, tileId);
                if (x == 0) {
                    previousRowFirstTileId = tileId;
                }

                colors[y * IndexTextureResolution + x] = CreateIndexColor(tileId);
            }
        }

        return colors;
    }

    private int LocateFromProjection(in Vector3 direction) {
        var faceIndex = 0;
        var maximumDot = float.NegativeInfinity;
        for (var index = 0; index < _faceProjections.Length; index++) {
            var dot = Vector3.Dot(direction, _faceProjections[index].Normal);
            if (dot > maximumDot) {
                maximumDot = dot;
                faceIndex = index;
            }
        }

        ref readonly var projection = ref _faceProjections[faceIndex];
        var intersection = direction * (
            projection.PlaneDistance / Vector3.Dot(projection.Normal, direction)
        );
        var offset = intersection - projection.A;
        var dot20 = Vector3.Dot(offset, projection.AB);
        var dot21 = Vector3.Dot(offset, projection.AC);
        var b = (projection.Dot11 * dot20 - projection.Dot01 * dot21)
            * projection.InverseDenominator;
        var c = (projection.Dot00 * dot21 - projection.Dot01 * dot20)
            * projection.InverseDenominator;
        var a = 1.0f - b - c;

        a = MathF.Max(0.0f, a);
        b = MathF.Max(0.0f, b);
        c = MathF.Max(0.0f, c);
        var inverseSum = 1.0f / (a + b + c);
        a *= inverseSum;
        b *= inverseSum;
        c *= inverseSum;

        var i = (int)MathF.Round(a * Frequency);
        var j = (int)MathF.Round(b * Frequency);
        var k = (int)MathF.Round(c * Frequency);
        var differenceI = MathF.Abs(i - a * Frequency);
        var differenceJ = MathF.Abs(j - b * Frequency);
        var differenceK = MathF.Abs(k - c * Frequency);
        if (differenceI >= differenceJ && differenceI >= differenceK) {
            i = Frequency - j - k;
        }
        else if (differenceJ >= differenceK) {
            j = Frequency - i - k;
        }
        else {
            k = Frequency - i - j;
        }

        return LocateFromSeed(direction, GetTileId(faceIndex, i, j, k));
    }

    private int LocateFromSeed(in Vector3 direction, int seedTileId) {
        var current = seedTileId;
        var currentDot = Vector3.Dot(direction, _tileCenters[current]);

        while (true) {
            var best = current;
            var bestDot = currentDot;
            var offset = current * MaximumNeighborCount;
            for (var index = 0; index < _neighborCounts[current]; index++) {
                var candidate = _neighbors[offset + index];
                var candidateDot = Vector3.Dot(direction, _tileCenters[candidate]);
                if (candidateDot > bestDot
                    || (candidateDot == bestDot && candidate < best)) {
                    best = candidate;
                    bestDot = candidateDot;
                }
            }

            if (best == current) {
                return current;
            }

            current = best;
            currentDot = bestDot;
        }
    }

    private Vector3[] CreateTileCenters() {
        var centers = new Vector3[TileCount];
        Array.Copy(_baseVertices, centers, BaseVertexCount);

        for (var first = 0; first < BaseVertexCount; first++) {
            for (var second = first + 1; second < BaseVertexCount; second++) {
                var edgeId = GetEdgeId(first, second);
                if (edgeId < 0) {
                    continue;
                }

                for (var t = 1; t < Frequency; t++) {
                    var point = _baseVertices[first] * (Frequency - t)
                        + _baseVertices[second] * t;
                    centers[GetEdgeTileId(first, second, Frequency - t, t)] =
                        Vector3.Normalize(point);
                }
            }
        }

        for (var faceIndex = 0; faceIndex < BaseFaceCount; faceIndex++) {
            var a = _baseVertices[s_faces[faceIndex, 0]];
            var b = _baseVertices[s_faces[faceIndex, 1]];
            var c = _baseVertices[s_faces[faceIndex, 2]];
            for (var i = 1; i <= Frequency - 2; i++) {
                for (var j = 1; j <= Frequency - i - 1; j++) {
                    var k = Frequency - i - j;
                    var point = a * i + b * j + c * k;
                    centers[GetFaceInteriorTileId(faceIndex, i, j)] =
                        Vector3.Normalize(point);
                }
            }
        }

        return centers;
    }

    private (int[] Neighbors, byte[] Counts) CreateNeighbors() {
        var neighbors = new int[TileCount * MaximumNeighborCount];
        var counts = new byte[TileCount];
        ReadOnlySpan<(int I, int J, int K)> directions = [
            (1, -1, 0), (1, 0, -1), (0, 1, -1),
            (-1, 1, 0), (-1, 0, 1), (0, -1, 1),
        ];

        for (var faceIndex = 0; faceIndex < BaseFaceCount; faceIndex++) {
            for (var i = 0; i <= Frequency; i++) {
                for (var j = 0; j <= Frequency - i; j++) {
                    var k = Frequency - i - j;
                    var tileId = GetTileId(faceIndex, i, j, k);
                    foreach (var direction in directions) {
                        var neighborI = i + direction.I;
                        var neighborJ = j + direction.J;
                        var neighborK = k + direction.K;
                        if (neighborI < 0 || neighborJ < 0 || neighborK < 0) {
                            continue;
                        }

                        AddNeighbor(
                            neighbors,
                            counts,
                            tileId,
                            GetTileId(faceIndex, neighborI, neighborJ, neighborK)
                        );
                    }
                }
            }
        }

        return (neighbors, counts);
    }

    private void AddNeighbor(int[] neighbors, byte[] counts, int tileId, int neighborId) {
        var offset = tileId * MaximumNeighborCount;
        for (var index = 0; index < counts[tileId]; index++) {
            if (neighbors[offset + index] == neighborId) {
                return;
            }
        }

        if (counts[tileId] == MaximumNeighborCount) {
            throw new InvalidOperationException("A logical-grid tile has more than six neighbors.");
        }

        neighbors[offset + counts[tileId]++] = neighborId;
    }

    private void ValidateTopology() {
        var pentagonCount = 0;
        for (var tileId = 0; tileId < TileCount; tileId++) {
            switch (_neighborCounts[tileId]) {
                case 5:
                    pentagonCount++;
                    break;
                case 6:
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Logical-grid tile {tileId} has {_neighborCounts[tileId]} neighbors."
                    );
            }
        }

        if (pentagonCount != BaseVertexCount) {
            throw new InvalidOperationException(
                $"The logical grid has {pentagonCount} pentagons instead of {BaseVertexCount}."
            );
        }
    }

    private FaceProjection[] CreateFaceProjections() {
        var projections = new FaceProjection[BaseFaceCount];
        for (var faceIndex = 0; faceIndex < BaseFaceCount; faceIndex++) {
            var a = _baseVertices[s_faces[faceIndex, 0]];
            var b = _baseVertices[s_faces[faceIndex, 1]];
            var c = _baseVertices[s_faces[faceIndex, 2]];
            var ab = b - a;
            var ac = c - a;
            var normal = Vector3.Normalize(Vector3.Cross(ab, ac));
            if (Vector3.Dot(normal, a) <= 0.0f) {
                throw new InvalidOperationException(
                    $"Logical-grid base face {faceIndex} is not counter-clockwise."
                );
            }

            var dot00 = Vector3.Dot(ab, ab);
            var dot01 = Vector3.Dot(ab, ac);
            var dot11 = Vector3.Dot(ac, ac);
            projections[faceIndex] = new FaceProjection(
                a,
                ab,
                ac,
                normal,
                Vector3.Dot(normal, a),
                dot00,
                dot01,
                dot11,
                1.0f / (dot00 * dot11 - dot01 * dot01)
            );
        }

        return projections;
    }

    private int[] CreateEdgeIds() {
        var connected = new bool[BaseVertexCount * BaseVertexCount];
        for (var faceIndex = 0; faceIndex < BaseFaceCount; faceIndex++) {
            MarkConnected(s_faces[faceIndex, 0], s_faces[faceIndex, 1]);
            MarkConnected(s_faces[faceIndex, 1], s_faces[faceIndex, 2]);
            MarkConnected(s_faces[faceIndex, 2], s_faces[faceIndex, 0]);
        }

        var edgeIds = new int[BaseVertexCount * BaseVertexCount];
        Array.Fill(edgeIds, -1);
        var edgeId = 0;
        for (var first = 0; first < BaseVertexCount; first++) {
            for (var second = first + 1; second < BaseVertexCount; second++) {
                if (!connected[first * BaseVertexCount + second]) {
                    continue;
                }

                edgeIds[first * BaseVertexCount + second] = edgeId;
                edgeIds[second * BaseVertexCount + first] = edgeId;
                edgeId++;
            }
        }

        if (edgeId != BaseEdgeCount) {
            throw new InvalidOperationException(
                $"The logical-grid base has {edgeId} edges instead of {BaseEdgeCount}."
            );
        }

        return edgeIds;

        void MarkConnected(int first, int second) {
            connected[first * BaseVertexCount + second] = true;
            connected[second * BaseVertexCount + first] = true;
        }
    }

    private int GetTileId(int faceIndex, int i, int j, int k) {
        var a = s_faces[faceIndex, 0];
        var b = s_faces[faceIndex, 1];
        var c = s_faces[faceIndex, 2];

        if (i == Frequency) {
            return a;
        }
        if (j == Frequency) {
            return b;
        }
        if (k == Frequency) {
            return c;
        }
        if (k == 0) {
            return GetEdgeTileId(a, b, i, j);
        }
        if (j == 0) {
            return GetEdgeTileId(a, c, i, k);
        }
        if (i == 0) {
            return GetEdgeTileId(b, c, j, k);
        }

        return GetFaceInteriorTileId(faceIndex, i, j);
    }

    private int GetEdgeTileId(int first, int second, int firstWeight, int secondWeight) {
        var edgeId = GetEdgeId(first, second);
        var t = first < second ? secondWeight : firstWeight;
        return BaseVertexCount + edgeId * EdgeInteriorCount + t - 1;
    }

    private int GetEdgeId(int first, int second) {
        return _edgeIds[first * BaseVertexCount + second];
    }

    private static int GetFaceInteriorTileId(int faceIndex, int i, int j) {
        var rank = (i - 1) * (Frequency - 1) - (i - 1) * i / 2 + j - 1;
        return FaceInteriorStart + faceIndex * FaceInteriorCount + rank;
    }

    private static Color CreateIndexColor(int tileId) {
        var code = HashTileId((uint)tileId);
        var red = (byte)(((code & 0x7Fu) << 1) | ((code >> 20) & 1u));
        var green = (byte)((((code >> 7) & 0x7Fu) << 1) | ((code >> 6) & 1u));
        var blue = (byte)((((code >> 14) & 0x7Fu) << 1) | ((code >> 13) & 1u));
        return new Color(red, green, blue, byte.MaxValue);
    }

    private static uint HashTileId(uint tileId) {
        if (tileId >= TileCodeMask) {
            throw new ArgumentOutOfRangeException(nameof(tileId));
        }

        var value = tileId + 1u;
        value ^= value >> 10;
        value = (uint)(((ulong)value * 0x0B352Du) & TileCodeMask);
        value ^= value >> 9;
        value = (uint)(((ulong)value * 0x0CA68Bu) & TileCodeMask);
        value ^= value >> 10;
        return value & TileCodeMask;
    }

    private static Vector3[] CreateBaseVertices() {
        var phi = (1.0f + MathF.Sqrt(5.0f)) * 0.5f;
        var vertices = new[] {
            new Vector3(-1.0f, phi, 0.0f), new Vector3(1.0f, phi, 0.0f),
            new Vector3(-1.0f, -phi, 0.0f), new Vector3(1.0f, -phi, 0.0f),
            new Vector3(0.0f, -1.0f, phi), new Vector3(0.0f, 1.0f, phi),
            new Vector3(0.0f, -1.0f, -phi), new Vector3(0.0f, 1.0f, -phi),
            new Vector3(phi, 0.0f, -1.0f), new Vector3(phi, 0.0f, 1.0f),
            new Vector3(-phi, 0.0f, -1.0f), new Vector3(-phi, 0.0f, 1.0f),
        };
        for (var index = 0; index < vertices.Length; index++) {
            vertices[index] = Vector3.Normalize(vertices[index]);
        }

        return vertices;
    }

    private readonly record struct FaceProjection(
        Vector3 A,
        Vector3 AB,
        Vector3 AC,
        Vector3 Normal,
        float PlaneDistance,
        float Dot00,
        float Dot01,
        float Dot11,
        float InverseDenominator
    );
}
