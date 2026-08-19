using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using monogame.Planet.Gen;

namespace monogame.Planet;

internal readonly record struct TerrainLodSelectionSettings(
    int MinimumLod,
    float SplitThreshold
) {
    public void Validate(int maximumLod) {
        if (MinimumLod < 0 || MinimumLod > maximumLod) {
            throw new ArgumentOutOfRangeException(nameof(MinimumLod));
        }

        if (!float.IsFinite(SplitThreshold) || SplitThreshold <= 0.0f) {
            throw new ArgumentOutOfRangeException(nameof(SplitThreshold));
        }
    }
}

internal readonly record struct TerrainLodView(
    Vector3 CameraPosition,
    float VerticalFieldOfView,
    int ViewportHeight,
    Matrix ViewProjection
) {
    public void Validate() {
        if (!IsFinite(CameraPosition) || CameraPosition.LengthSquared() <= 0.0f) {
            throw new ArgumentOutOfRangeException(nameof(CameraPosition));
        }

        if (!float.IsFinite(VerticalFieldOfView)
            || VerticalFieldOfView <= 0.0f
            || VerticalFieldOfView >= MathF.PI) {
            throw new ArgumentOutOfRangeException(nameof(VerticalFieldOfView));
        }

        if (ViewportHeight <= 0) {
            throw new ArgumentOutOfRangeException(nameof(ViewportHeight));
        }

        if (!IsFinite(ViewProjection)) {
            throw new ArgumentOutOfRangeException(nameof(ViewProjection));
        }
    }

    private static bool IsFinite(in Vector3 value) {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static bool IsFinite(in Matrix value) {
        return float.IsFinite(value.M11) && float.IsFinite(value.M12)
            && float.IsFinite(value.M13) && float.IsFinite(value.M14)
            && float.IsFinite(value.M21) && float.IsFinite(value.M22)
            && float.IsFinite(value.M23) && float.IsFinite(value.M24)
            && float.IsFinite(value.M31) && float.IsFinite(value.M32)
            && float.IsFinite(value.M33) && float.IsFinite(value.M34)
            && float.IsFinite(value.M41) && float.IsFinite(value.M42)
            && float.IsFinite(value.M43) && float.IsFinite(value.M44);
    }
}

internal sealed class TerrainLodSelectionResult {
    private readonly TerrainLodNodeId[] _selectedLeafNodeIds;

    internal TerrainLodSelectionResult(TerrainLodNodeId[] selectedLeafNodeIds) {
        _selectedLeafNodeIds = selectedLeafNodeIds;
    }

    public ReadOnlySpan<TerrainLodNodeId> SelectedLeafNodeIds => _selectedLeafNodeIds;
}

internal sealed class TerrainLodSelector {
    private const double RelativeDistanceFloor = 1e-7;

    private readonly TerrainLodAsset _asset;
    private readonly TerrainLodSelectionSettings _settings;
    private readonly double _distanceFloor;

    public TerrainLodSelector(
        TerrainLodAsset asset,
        in TerrainLodSelectionSettings settings) {
        ArgumentNullException.ThrowIfNull(asset);
        settings.Validate(asset.Settings.MaximumLod);
        _asset = asset;
        _settings = settings;
        _distanceFloor = Math.Max(asset.Settings.ReferenceRadius * RelativeDistanceFloor, double.Epsilon);
    }

    public TerrainLodSelectionResult Select(in TerrainLodView view) {
        view.Validate();
        var cameraLengthSquared = LengthSquared(view.CameraPosition);
        var cameraLength = Math.Sqrt(cameraLengthSquared);
        var focalLength = view.ViewportHeight * 0.5
            / Math.Tan(view.VerticalFieldOfView * 0.5);
        var context = new SelectionContext(
            view.CameraPosition,
            cameraLength,
            cameraLengthSquared,
            focalLength,
            new BoundingFrustum(view.ViewProjection),
            new List<TerrainLodNodeId>()
        );

        for (var faceIndex = 0; faceIndex < CubeSphereGrid.FaceCount; faceIndex++) {
            SelectNode(
                new TerrainLodNodeId((TerrainCubeFace)faceIndex, 0, 0, 0),
                context
            );
        }

        return new TerrainLodSelectionResult(context.SelectedLeaves.ToArray());
    }

    private void SelectNode(in TerrainLodNodeId id, SelectionContext context) {
        var node = _asset.GetNode(id);
        if (IsFullyBehindHorizon(node, context)
            || context.Frustum.Contains(node.BoundingSphere) == ContainmentType.Disjoint) {
            return;
        }

        if (id.Level == _asset.Settings.MaximumLod) {
            context.SelectedLeaves.Add(id);
            return;
        }

        if (id.Level < _settings.MinimumLod) {
            SelectChildren(id, context);
            return;
        }

        var distance = CalculateDistanceToNode(node, context);
        var screenSpaceError = node.GeometricError * context.FocalLength / distance;
        if (screenSpaceError <= _settings.SplitThreshold) {
            context.SelectedLeaves.Add(id);
            return;
        }

        SelectChildren(id, context);
    }

    private void SelectChildren(in TerrainLodNodeId parent, SelectionContext context) {
        var childLevel = parent.Level + 1;
        var childX = parent.X * 2;
        var childY = parent.Y * 2;
        SelectNode(new TerrainLodNodeId(parent.Face, childLevel, childX, childY), context);
        SelectNode(new TerrainLodNodeId(parent.Face, childLevel, childX + 1, childY), context);
        SelectNode(new TerrainLodNodeId(parent.Face, childLevel, childX, childY + 1), context);
        SelectNode(new TerrainLodNodeId(parent.Face, childLevel, childX + 1, childY + 1), context);
    }

    private bool IsFullyBehindHorizon(in TerrainLodNode node, SelectionContext context) {
        var horizonPointRadius = node.HorizonPointRadius;
        if (horizonPointRadius <= 0.0f) {
            return false;
        }

        var occluderRadius = _asset.OccluderRadius;
        var occluderRadiusSquared = (double)occluderRadius * occluderRadius;
        if (context.CameraLengthSquared <= occluderRadiusSquared) {
            return false;
        }

        var pointX = node.CenterDirection.X * (double)horizonPointRadius;
        var pointY = node.CenterDirection.Y * (double)horizonPointRadius;
        var pointZ = node.CenterDirection.Z * (double)horizonPointRadius;
        var vectorX = pointX - context.CameraPosition.X;
        var vectorY = pointY - context.CameraPosition.Y;
        var vectorZ = pointZ - context.CameraPosition.Z;
        var vectorLengthSquared = vectorX * vectorX + vectorY * vectorY + vectorZ * vectorZ;
        if (vectorLengthSquared <= 0.0) {
            return false;
        }

        var projection = -(
            context.CameraPosition.X * vectorX
            + context.CameraPosition.Y * vectorY
            + context.CameraPosition.Z * vectorZ
        );
        var cameraHorizonSquared = context.CameraLengthSquared - occluderRadiusSquared;
        return projection > 0.0
            && projection < vectorLengthSquared
            && projection * projection > cameraHorizonSquared * vectorLengthSquared;
    }

    private double CalculateDistanceToNode(in TerrainLodNode node, SelectionContext context) {
        var centerLength = Math.Sqrt(LengthSquared(node.CenterDirection));
        var cosineTheta = (
            context.CameraPosition.X * node.CenterDirection.X
            + context.CameraPosition.Y * node.CenterDirection.Y
            + context.CameraPosition.Z * node.CenterDirection.Z
        ) / (context.CameraLength * centerLength);
        var theta = Math.Acos(Math.Clamp(cosineTheta, -1.0, 1.0));
        var beta = Math.Max(0.0, theta - node.AngularRadius);
        var cosineBeta = Math.Cos(beta);
        var radius = Math.Clamp(
            context.CameraLength * cosineBeta,
            node.MinimumRadius,
            node.MaximumRadius
        );
        var distanceSquared = context.CameraLengthSquared
            + radius * radius
            - 2.0 * context.CameraLength * radius * cosineBeta;
        return Math.Max(Math.Sqrt(Math.Max(0.0, distanceSquared)), _distanceFloor);
    }

    private static double LengthSquared(in Vector3 value) {
        return (double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z;
    }

    private sealed class SelectionContext {
        public SelectionContext(
            Vector3 cameraPosition,
            double cameraLength,
            double cameraLengthSquared,
            double focalLength,
            BoundingFrustum frustum,
            List<TerrainLodNodeId> selectedLeaves) {
            CameraPosition = cameraPosition;
            CameraLength = cameraLength;
            CameraLengthSquared = cameraLengthSquared;
            FocalLength = focalLength;
            Frustum = frustum;
            SelectedLeaves = selectedLeaves;
        }

        public Vector3 CameraPosition { get; }

        public double CameraLength { get; }

        public double CameraLengthSquared { get; }

        public double FocalLength { get; }

        public BoundingFrustum Frustum { get; }

        public List<TerrainLodNodeId> SelectedLeaves { get; }
    }
}
