namespace monogame.Planet.Gen;

internal static class TerrainLodAssetFormat {
    public const uint Magic = 0x444F4C54; // TLOD
    public const int Version = 1;
    public const int SectionCount = 3;
    public const int FixedHeaderSize = 48;
    public const int SectionEntrySize = 20;
    public const int HeaderSize = FixedHeaderSize + SectionCount * SectionEntrySize;
    public const int NodeSize = 48;
    public const int ReferenceGeometryHashSize = 32;
}

internal enum TerrainLodAssetSection {
    ReferenceHeights = 1,
    LodNodeMetadata = 2,
    Checksums = 3,
}

internal readonly record struct TerrainLodAssetSectionDescriptor(
    TerrainLodAssetSection Section,
    long Offset,
    long Length
);
