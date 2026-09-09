namespace monogame.Planet;

internal struct SelectionCounters {
    internal int VisitedNodes;
    internal int HorizonRejected;
    internal int FrustumRejected;
}

// Current selection state; per-frame timings and invocation counts live in the profiler.
internal readonly record struct SelectionMetrics(
    int TargetLod,
    int VisitedNodes,
    int ActiveChunks,
    int HorizonRejected,
    int FrustumRejected
);
