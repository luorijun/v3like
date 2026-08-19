using System;
using Microsoft.Xna.Framework;

namespace monogame.Terrain;

internal sealed class FlatReferenceElevationSource : IReferenceElevationSource {
    private readonly float _elevation;

    public FlatReferenceElevationSource(float elevation = 0.0f) {
        _elevation = elevation;
    }

    public float GetElevation(in Vector3 unitDirection) {
        return _elevation;
    }
}

internal sealed class MultiScaleReferenceElevationSource : IReferenceElevationSource {
    private readonly float _maximumAbsoluteElevation;

    public MultiScaleReferenceElevationSource(float maximumAbsoluteElevation = 0.0015f) {
        _maximumAbsoluteElevation = MathF.Abs(maximumAbsoluteElevation);
    }

    public float GetElevation(in Vector3 unitDirection) {
        var broad = MathF.Sin(unitDirection.X * 2.3f + unitDirection.Z * 1.7f)
            * MathF.Cos(unitDirection.Y * 2.9f - unitDirection.X * 0.8f);
        var medium = MathF.Sin((unitDirection.X + unitDirection.Y - unitDirection.Z) * 7.0f);
        var fine = MathF.Cos(unitDirection.X * 19.0f - unitDirection.Y * 13.0f + unitDirection.Z * 17.0f);
        var normalizedElevation = broad * 0.6f + medium * 0.27f + fine * 0.13f;
        return MathHelper.Clamp(normalizedElevation, -1.0f, 1.0f) * _maximumAbsoluteElevation;
    }
}

internal sealed class BoundaryExtremaReferenceElevationSource : IReferenceElevationSource {
    private static readonly Vector3 EdgePeakDirection = Vector3.Normalize(new Vector3(1.0f, 1.0f, 0.0f));
    private static readonly Vector3 CornerPeakDirection = Vector3.Normalize(new Vector3(1.0f, 1.0f, 1.0f));
    private static readonly Vector3 CornerDepressionDirection = Vector3.Normalize(new Vector3(-1.0f, 1.0f, 1.0f));

    private readonly float _maximumElevation;
    private readonly float _minimumElevation;

    public BoundaryExtremaReferenceElevationSource(
        float maximumElevation = 0.0015f,
        float minimumElevation = -0.0015f) {
        _maximumElevation = maximumElevation;
        _minimumElevation = minimumElevation;
    }

    public float GetElevation(in Vector3 unitDirection) {
        var edgePeak = SmoothCap(unitDirection, EdgePeakDirection, 0.985f);
        var cornerPeak = SmoothCap(unitDirection, CornerPeakDirection, 0.992f);
        var cornerDepression = SmoothCap(unitDirection, CornerDepressionDirection, 0.988f);
        return MathF.Max(edgePeak, cornerPeak) * _maximumElevation
            + cornerDepression * _minimumElevation;
    }

    private static float SmoothCap(in Vector3 direction, in Vector3 center, float minimumDot) {
        var amount = MathHelper.Clamp(
            (Vector3.Dot(direction, center) - minimumDot) / (1.0f - minimumDot),
            0.0f,
            1.0f
        );
        return amount * amount * (3.0f - 2.0f * amount);
    }
}
