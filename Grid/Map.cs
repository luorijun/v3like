using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SurfaceKind = monogame.Asset.SurfaceKind;

namespace monogame.Grid;

internal enum MapMode { Raw = 0, Surface = 1, Terrain = 2 }

// Owns tile topology, interaction and display data. Shared assets belong to GameManager.
internal sealed class Map : IDisposable {
    internal const int Frequency = 358;
    internal const int TileCount = 10 * Frequency * Frequency + 2;

    private const int BaseVertexCount = 12;
    private const int BaseEdgeCount = 30;
    private const int BaseFaceCount = 20;
    private const int EdgeInteriorCount = Frequency - 1;
    private const int FaceInteriorCount = (Frequency - 1) * (Frequency - 2) / 2;
    private const int FaceInteriorStart = BaseVertexCount + BaseEdgeCount * EdgeInteriorCount;
    private const int MaximumNeighborCount = 6;
    private const uint TileCodeMask = (1u << 21) - 1u;

    private static readonly (int Height, Color Color)[] s_seaColors = [
        (-8000, new Color(16, 42, 67)),
        (-4000, new Color(30, 82, 120)),
        (-1000, new Color(50, 127, 163)),
        (0, new Color(120, 185, 199)),
    ];

    private static readonly (int Height, Color Color)[] s_landColors = [
        (0, new Color(83, 117, 76)),
        (500, new Color(129, 147, 92)),
        (1500, new Color(176, 161, 109)),
        (3000, new Color(146, 119, 93)),
        (5000, new Color(213, 209, 200)),
        (8000, new Color(240, 239, 233)),
    ];

    private static readonly int[,] s_faces = {
        { 0, 11, 5 }, { 0, 5, 1 }, { 0, 1, 7 }, { 0, 7, 10 }, { 0, 10, 11 },
        { 1, 5, 9 }, { 5, 11, 4 }, { 11, 10, 2 }, { 10, 7, 6 }, { 7, 1, 8 },
        { 3, 9, 4 }, { 3, 4, 2 }, { 3, 2, 6 }, { 3, 6, 8 }, { 3, 8, 9 },
        { 4, 9, 5 }, { 2, 4, 11 }, { 6, 2, 10 }, { 8, 6, 7 }, { 9, 8, 1 },
    };

    // Fixed base geometry and numbering; initialized once and never mutated.
    private static readonly Vector3[] s_baseVertices = CreateBaseVertices();
    private static readonly int[] s_edgeIds = CreateEdgeIds();

    private readonly FaceProjection[] _faceProjections;
    private readonly Vector3[] _tileCenters;
    private readonly int[] _neighbors;
    private readonly byte[] _neighborCounts;

    private readonly Texture2D _gridTiles;
    private readonly Texture2D _gridSeeds;
    private readonly Texture2D _gridFaces;
    private readonly Texture2D _tileDisplayColors;

    private readonly EffectParameter _gridTilesParameter;
    private readonly EffectParameter _gridSeedsParameter;
    private readonly EffectParameter _gridFacesParameter;
    private readonly EffectParameter _tileDisplayColorsParameter;
    private readonly EffectParameter _gridDataWidthParameter;
    private readonly EffectParameter _gridFrequencyParameter;
    private readonly EffectParameter _tileSelectedParameter;
    private readonly EffectParameter _borderEdgesParameter;
    private readonly EffectParameter _borderCountParameter;
    private readonly Vector4[] _borderEdges = new Vector4[MaximumNeighborCount];
    private float _borderWidth;
    private int _borderCount;

    internal MapMode Mode { get; private set; }
    internal int? TileSelected { get; private set; }

    // Fraction of each edge's spherical distance to the tile center.
    internal float BorderWidth {
        get => _borderWidth;
        set {
            if (!float.IsFinite(value) || value < 0 || value > 1) {
                throw new ArgumentOutOfRangeException(nameof(value), "BorderWidth must be between 0 and 1.");
            }
            if (_borderWidth == value) return;
            _borderWidth = value;
            UpdateBorder();
        }
    }

    internal Map(in MapConfig config) {
        if (!Enum.IsDefined(config.Mode)) {
            throw new ArgumentOutOfRangeException(nameof(config), "Unknown map mode.");
        }
        Mode = config.Mode;
        BorderWidth = config.BorderWidth;
        var graphicsDevice = GameManager.GraphicsDevice;
        var effect = GameManager.SurfaceEffect;
        _gridTilesParameter = GetRequiredParameter(effect, "GridTiles");
        _gridSeedsParameter = GetRequiredParameter(effect, "GridSeeds");
        _gridFacesParameter = GetRequiredParameter(effect, "GridFaces");
        _tileDisplayColorsParameter = GetRequiredParameter(effect, "TileDisplayColors");
        _gridDataWidthParameter = GetRequiredParameter(effect, "GridDataWidth");
        _gridFrequencyParameter = GetRequiredParameter(effect, "GridFrequency");
        _tileSelectedParameter = GetRequiredParameter(effect, "TileSelected");
        _borderEdgesParameter = GetRequiredParameter(effect, "BorderEdges");
        _borderCountParameter = GetRequiredParameter(effect, "BorderCount");

        _faceProjections = CreateFaceProjections();
        _tileCenters = CreateTileCenters();
        (_neighbors, _neighborCounts) = CreateNeighbors();
        ValidateTopology();
        var gridData = CreateGpuData();
        try {
            _gridTiles = CreateGridTexture(graphicsDevice, gridData.Width, gridData.Tiles, SurfaceFormat.Vector4);
            _gridSeeds = CreateGridTexture(graphicsDevice, gridData.Width, gridData.Seeds, SurfaceFormat.Single);
            _gridFaces = CreateGridTexture(graphicsDevice, 5, gridData.Faces, SurfaceFormat.Vector4);
            _tileDisplayColors = new Texture2D(graphicsDevice, gridData.Width,
                (TileCount + gridData.Width - 1) / gridData.Width, false, SurfaceFormat.Color);
            UpdateDisplayColors(0, CreateDisplayColors(Mode));
        }
        catch {
            _tileDisplayColors?.Dispose();
            _gridFaces?.Dispose();
            _gridSeeds?.Dispose();
            _gridTiles?.Dispose();
            throw;
        }
    }

    internal void Update(in View view) {
        if (InputManager.IsClick(Keys.D0, InputFocus.Scene)) SetMode(MapMode.Raw);
        if (InputManager.IsClick(Keys.D1, InputFocus.Scene)) SetMode(MapMode.Surface);
        if (InputManager.IsClick(Keys.D2, InputFocus.Scene)) SetMode(MapMode.Terrain);

        if (!InputManager.IsClick(MouseButton.Left, InputFocus.Scene)) return;

        var position = InputManager.MousePosition;
        if (!view.Viewport.Bounds.Contains(position)) return;

        // Picks the ideal unit sphere; the rendered LOD surface is an approximation.
        var ray = view.CreateRay(position);
        var distance = ray.Intersects(new BoundingSphere(Vector3.Zero, 1.0f));
        TileSelected = distance.HasValue
            ? LocateFromProjection(Vector3.Normalize(ray.Position + ray.Direction * distance.Value))
            : null;
        UpdateBorder();
    }

    private void UpdateBorder() {
        _borderCount = 0;
        if (TileSelected is not int tile || _borderWidth == 0) return;

        var center = GetCenter(tile);
        foreach (var neighbor in GetNeighbors(tile)) {
            var normal = Vector3.Normalize(center - GetCenter(neighbor));
            // asin(dot(direction, normal)) is the signed angular distance to
            // the great-circle edge. Precompute its threshold for all pixels.
            var centerSine = (double)center.X * normal.X
                + (double)center.Y * normal.Y + (double)center.Z * normal.Z;
            var threshold = (float)Math.Sin(_borderWidth * Math.Asin(Math.Clamp(centerSine, 0.0, 1.0)));
            _borderEdges[_borderCount++] = new Vector4(normal, threshold);
        }
    }

    private void SetMode(MapMode mode) {
        if (Mode == mode) return;
        UpdateDisplayColors(0, CreateDisplayColors(mode));
        Mode = mode;
    }

    // Bind existing resources and selection before drawing; no texture upload here.
    internal void Bind() {
        _gridTilesParameter.SetValue(_gridTiles);
        _gridSeedsParameter.SetValue(_gridSeeds);
        _gridFacesParameter.SetValue(_gridFaces);
        _tileDisplayColorsParameter.SetValue(_tileDisplayColors);
        _gridDataWidthParameter.SetValue(_gridTiles.Width);
        _gridFrequencyParameter.SetValue(Frequency);
        _tileSelectedParameter.SetValue(TileSelected ?? -1);
        _borderEdgesParameter.SetValue(_borderEdges);
        _borderCountParameter.SetValue(_borderCount);
    }

    // Call on the graphics thread before drawing.
    internal void UpdateDisplayColors(int firstTileId, Color[] colors) {
        ArgumentNullException.ThrowIfNull(colors);
        if (firstTileId < 0 || firstTileId > TileCount
            || colors.Length > TileCount - firstTileId) {
            throw new ArgumentOutOfRangeException(nameof(firstTileId));
        }

        var width = _tileDisplayColors.Width;
        var offset = 0;
        while (offset < colors.Length) {
            var tile = firstTileId + offset;
            var x = tile % width;
            var remaining = colors.Length - offset;
            var rows = x == 0 ? remaining / width : 0;
            var region = rows > 0
                ? new Rectangle(0, tile / width, width, rows)
                : new Rectangle(x, tile / width, Math.Min(width - x, remaining), 1);
            var count = region.Width * region.Height;
            _tileDisplayColors.SetData(0, region, colors, offset, count);
            offset += count;
        }
    }

    public void Dispose() {
        _tileDisplayColors.Dispose();
        _gridFaces.Dispose();
        _gridSeeds.Dispose();
        _gridTiles.Dispose();
    }

    // GPU tables use the same centers, adjacency and tile IDs as the CPU locator.
    // Three RGBA texels per tile: center/count, then its six neighbor IDs.
    private GpuGridData CreateGpuData() {
        const int width = 2048;
        var tiles = new Vector4[((TileCount * 3 + width - 1) / width) * width];
        for (var tile = 0; tile < TileCount; tile++) {
            var neighbors = GetNeighbors(tile);
            tiles[tile * 3] = new Vector4(GetCenter(tile), neighbors.Length);
            tiles[tile * 3 + 1] = new Vector4(
                neighbors[0], neighbors[1], neighbors[2], 0);
            tiles[tile * 3 + 2] = new Vector4(
                neighbors[3], neighbors[4], neighbors.Length == 6 ? neighbors[5] : 0, 0);
        }

        var samplesPerFace = (Frequency + 1) * (Frequency + 2) / 2;
        var seeds = new float[((FaceProjections.Length * samplesPerFace + width - 1) / width) * width];
        var next = 0;
        for (var face = 0; face < FaceProjections.Length; face++) {
            for (var i = 0; i <= Frequency; i++) {
                for (var j = 0; j <= Frequency - i; j++) {
                    seeds[next++] = GetTileId(face, i, j, Frequency - i - j);
                }
            }
        }

        var faces = new Vector4[FaceProjections.Length * 5];
        for (var face = 0; face < FaceProjections.Length; face++) {
            var projection = FaceProjections[face];
            faces[face * 5] = new Vector4(projection.Normal, projection.PlaneDistance);
            faces[face * 5 + 1] = new Vector4(projection.A, 0);
            faces[face * 5 + 2] = new Vector4(projection.AB, 0);
            faces[face * 5 + 3] = new Vector4(projection.AC, 0);
            faces[face * 5 + 4] = new Vector4(projection.Dot00, projection.Dot01,
                projection.Dot11, projection.InverseDenominator);
        }

        return new GpuGridData(width, tiles, seeds, faces);
    }

    private static Texture2D CreateGridTexture<T>(
        GraphicsDevice device, int width, T[] data, SurfaceFormat format
    ) where T : struct {
        var texture = new Texture2D(device, width, data.Length / width, false, format);
        try {
            texture.SetData(data);
            return texture;
        }
        catch {
            texture.Dispose();
            throw;
        }
    }

    private static Color[] CreateDisplayColors(MapMode mode) {
        var colors = new Color[TileCount];
        if (mode == MapMode.Surface) {
            var surface = GameManager.Surface;
            for (var tile = 0; tile < colors.Length; tile++) {
                colors[tile] = surface.GetKind(tile) switch {
                    SurfaceKind.Ocean => new Color(30, 82, 120),
                    SurfaceKind.Land => new Color(83, 117, 76),
                    SurfaceKind.Lake => new Color(70, 170, 190),
                    _ => throw new InvalidOperationException("Unknown surface category."),
                };
            }
            return colors;
        }
        if (mode == MapMode.Terrain) {
            var terrain = GameManager.Terrain;
            for (var tile = 0; tile < colors.Length; tile++) {
                colors[tile] = GetTerrainColor(terrain.GetHeight(tile));
            }
            return colors;
        }
        for (var tile = 0; tile < colors.Length; tile++) {
            colors[tile] = CreateIndexColor(tile);
        }
        return colors;
    }

    private static Color GetTerrainColor(short height) {
        var stops = height < 0 ? s_seaColors : s_landColors;
        if (height <= stops[0].Height) return stops[0].Color;
        for (var index = 1; index < stops.Length; index++) {
            var upper = stops[index];
            if (height > upper.Height) continue;
            var lower = stops[index - 1];
            var amount = (float)(height - lower.Height) / (upper.Height - lower.Height);
            return Color.Lerp(lower.Color, upper.Color, amount);
        }
        return stops[^1].Color;
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

    private static EffectParameter GetRequiredParameter(Effect effect, string name) {
        return effect.Parameters[name]
            ?? throw new InvalidOperationException(
                $"The surface effect is missing its {name} parameter."
            );
    }

    private readonly record struct GpuGridData(
        int Width,
        Vector4[] Tiles,
        float[] Seeds,
        Vector4[] Faces
    );
    internal Vector3 GetCenter(int tileId) => _tileCenters[tileId];

    internal ReadOnlySpan<int> GetNeighbors(int tileId) =>
        _neighbors.AsSpan(tileId * MaximumNeighborCount, _neighborCounts[tileId]);

    private ReadOnlySpan<FaceProjection> FaceProjections => _faceProjections;

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

        while (true) {
            var best = current;
            var offset = current * MaximumNeighborCount;
            for (var index = 0; index < _neighborCounts[current]; index++) {
                var candidate = _neighbors[offset + index];
                var candidateCenter = _tileCenters[candidate];
                var bestCenter = _tileCenters[best];
                var dotDifference =
                    direction.X * ((double)candidateCenter.X - bestCenter.X) +
                    direction.Y * ((double)candidateCenter.Y - bestCenter.Y) +
                    direction.Z * ((double)candidateCenter.Z - bestCenter.Z);
                if (dotDifference > 0.0 || (dotDifference == 0.0 && candidate < best)) {
                    best = candidate;
                }
            }

            if (best == current) {
                return current;
            }

            current = best;
        }
    }

    internal static Vector3[] CreateTileCenters() {
        var centers = new Vector3[TileCount];
        Array.Copy(s_baseVertices, centers, BaseVertexCount);

        for (var first = 0; first < BaseVertexCount; first++) {
            for (var second = first + 1; second < BaseVertexCount; second++) {
                var edgeId = GetEdgeId(first, second);
                if (edgeId < 0) {
                    continue;
                }

                for (var t = 1; t < Frequency; t++) {
                    var point = s_baseVertices[first] * (Frequency - t)
                        + s_baseVertices[second] * t;
                    centers[GetEdgeTileId(first, second, Frequency - t, t)] =
                        Vector3.Normalize(point);
                }
            }
        }

        for (var faceIndex = 0; faceIndex < BaseFaceCount; faceIndex++) {
            var a = s_baseVertices[s_faces[faceIndex, 0]];
            var b = s_baseVertices[s_faces[faceIndex, 1]];
            var c = s_baseVertices[s_faces[faceIndex, 2]];
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

    private static (int[] Neighbors, byte[] Counts) CreateNeighbors() {
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

    private static void AddNeighbor(int[] neighbors, byte[] counts, int tileId, int neighborId) {
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

    private static FaceProjection[] CreateFaceProjections() {
        var projections = new FaceProjection[BaseFaceCount];
        for (var faceIndex = 0; faceIndex < BaseFaceCount; faceIndex++) {
            var a = s_baseVertices[s_faces[faceIndex, 0]];
            var b = s_baseVertices[s_faces[faceIndex, 1]];
            var c = s_baseVertices[s_faces[faceIndex, 2]];
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

    private static int[] CreateEdgeIds() {
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

    private static int GetTileId(int faceIndex, int i, int j, int k) {
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

    private static int GetEdgeTileId(int first, int second, int firstWeight, int secondWeight) {
        var edgeId = GetEdgeId(first, second);
        var t = first < second ? secondWeight : firstWeight;
        return BaseVertexCount + edgeId * EdgeInteriorCount + t - 1;
    }

    private static int GetEdgeId(int first, int second) {
        return s_edgeIds[first * BaseVertexCount + second];
    }

    private static int GetFaceInteriorTileId(int faceIndex, int i, int j) {
        var rank = (i - 1) * (Frequency - 1) - (i - 1) * i / 2 + j - 1;
        return FaceInteriorStart + faceIndex * FaceInteriorCount + rank;
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

internal readonly record struct MapConfig(
    MapMode Mode,
    float BorderWidth
);
