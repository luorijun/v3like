using Microsoft.Xna.Framework;

namespace monogame.Terrain;

/// <summary>
/// Supplies normalized terrain elevation for an already normalized planet direction.
/// </summary>
internal interface IReferenceElevationSource {
    /// <summary>
    /// Returns elevation in [0, 1], where 0 is the base surface and 1 is the maximum elevation.
    /// </summary>
    float GetElevation(in Vector3 unitDirection);
}
