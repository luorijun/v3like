using Microsoft.Xna.Framework;

namespace monogame.Terrain;

/// <summary>
/// Supplies reference-surface elevation for an already normalized planet direction.
/// </summary>
internal interface IReferenceElevationSource {
    /// <summary>
    /// Returns elevation in the same length unit as the reference planet radius.
    /// </summary>
    float GetElevation(in Vector3 unitDirection);
}
