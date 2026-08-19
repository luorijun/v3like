# 地形四叉树 LOD 实施方案

本文记录 Cube-sphere 地形细分方案的当前确定结论。本文只规定可见性剔除和理想叶节点选择；裂缝处理、资源流式加载与纹理 LOD 不在本阶段范围内。

## 固定约束

- 地球采用 Cube-sphere 四叉树和真实高度比例。
- `ChunkResolution = 17`：每条边 17 个顶点，每个 Chunk 为 `16 × 16` 个网格单元。
- `MinimumLod = 0`，不强制预细分。
- `MaximumLod = 7`，达到后无条件停止细分。
- 所有 Chunk 使用相同分辨率、三角形拓扑和对角线方向。
- 最高 LOD 三角网格是游戏可表达的几何参考面（`ReferenceMesh`）。
- LOD 由几何屏幕误差驱动，不使用 Chunk 屏幕直径、相机高度分段或固定距离表。
- 不用 Chunk 数、三角形数或 Draw Call 预算截断正常细分。

最高参考网格在每个 Cube Face 边上的区间数为：

```text
(ChunkResolution - 1) × 2^MaximumLod = 16 × 128 = 2048
```

## 最高 LOD 参考面

全球高程数据是项目内最精确的地形源数据。几何管线对其进行确定性的过滤和 Cube-sphere 重采样，生成最高 LOD 顶点，再以固定拓扑连接为 `ReferenceMesh`。

- 超过最高 LOD 空间分辨率的细节不要求由几何表达，但仍可用于生成法线、坡度、阴影和地形纹理。
- 重采样必须抗混叠；不能仅从原始高程中任意抽点。
- Cube Face 共边顶点必须由同一地理位置得到完全一致的高程。
- 高程数据清理、缺失值和异常尖峰处理发生在误差构建之前。

以下内容共同构成离线资产版本，任一变化都必须重建 LOD 元数据：

```text
ChunkResolution
MaximumLod
TriangleTopology
CubeSphereProjection
ElevationDataset
ElevationResamplingRule
```

## 离线几何误差

对每个非最高 LOD 节点，遍历其范围内的全部最高 LOD 顶点。使用相同 Cube Face 参数位置，找到当前 LOD 对应三角形并进行重心插值：

```text
DirectError(node) = max over finest vertices:
    length(ReferencePosition - CurrentTrianglePosition)

GeometricError(node) = max(
    DirectError(node),
    GeometricError(node.Children)
)
```

- 比较完整三维欧氏距离，而不是仅比较高程差或到三角形的最近距离。
- 误差使用最高 LOD 参考面直接计算，并强制父误差不小于子误差。
- 固定、嵌套的参数网格和三角形拓扑保证：最高 LOD 顶点上的最大误差足以界定分片线性参考面，不需要继续采样最高 LOD 三角形内部。
- 离线计算使用 `double`；运行时误差元数据存为 `float`。
- 最高 LOD 的 `GeometricError` 按定义为零。

每个四叉树节点至少保存：

```text
CenterDirection   节点方向范围的中心单位向量
AngularRadius     相对 CenterDirection 的最大球心夹角
MinimumRadius     节点参考三角面到球心的最小距离
MaximumRadius     节点参考三角面的最大顶点半径
BoundingSphere    包含节点全部参考三角面的紧包围球
GeometricError    当前节点相对 ReferenceMesh 的最大误差
```

包围数据由节点覆盖的最高 LOD 参考三角网格离线生成，并必须保守包含全部参考几何。三角形弦面内部可能比其顶点更接近球心，因此 `MinimumRadius` 必须计算三角面到球心的最小距离，不能只取顶点最小半径。`MaximumRadius` 可由顶点最大半径得到；包含全部顶点的凸包包围球也会包含三角面。

## 运行时视图数据

LOD 选择至少需要：

```text
CameraPosition
VerticalFieldOfView
ViewportHeight
FrustumPlanes
PreviousSelectionState
```

以像素为单位的投影焦距为：

```text
FocalLength =
    ViewportHeight × 0.5 / tan(VerticalFieldOfView × 0.5)
```

## 可见性剔除

运行时依次执行地平线剔除和视锥剔除。测试必须保守：只有确认整个节点不可见时才能剔除；相交节点继续参与 SSE 判断。

### 地平线剔除

使用完全位于参考地形内部的遮挡球：

```text
OccluderRadius = ReferenceMesh 全部三角面到球心的全局最小距离
```

设：

```text
D        = 相机到球心距离
Rmax     = node.MaximumRadius
CameraDirection = normalize(CameraPosition)
theta    = CameraDirection 与 node.CenterDirection 的夹角
alpha    = node.AngularRadius
```

当相机位于遮挡球外时：

```text
VisibleAngle =
    acos(OccluderRadius / D)
  + acos(OccluderRadius / Rmax)

theta - alpha > VisibleAngle
    => 整个节点位于地平线后，Cull
```

第二项允许节点高处越过基础球面地平线。若相机不在遮挡球外，则禁用地平线剔除。

### 视锥剔除

使用 `BoundingSphere` 对六个视锥平面做测试：

```text
完全位于任意视锥平面外 => Cull
相交或位于视锥内       => 继续
```

## Chunk 距离下界

SSE 不使用当前实现中宽松的“相机到包围球中心距离减半径”。使用节点角度范围与径向范围构成的球面扇区，计算保守且更紧的距离下界。

设 `theta`、`alpha`、`D` 同上：

```text
beta = max(0, theta - alpha)

radius = clamp(
    D × cos(beta),
    node.MinimumRadius,
    node.MaximumRadius
)

DistanceToChunk = sqrt(
    D² + radius² - 2 × D × radius × cos(beta)
)
```

该球面扇区包含实际节点，因此所得距离不会高于真实最近距离。距离接近零时使用一个仅用于数值稳定的正数下限，并继续细分直到满足阈值或到达最高 LOD。

## 几何 SSE 与细分

```text
ScreenSpaceError =
    node.GeometricError × FocalLength / DistanceToChunk
```

暂定质量参数：

```text
SplitThreshold = 2.0 px
MergeThreshold = 1.2 px
```

阈值规则：

```text
当前节点未细分：
    SSE > 2.0 px  => 细分
    SSE <= 2.0 px => 保留当前节点

当前节点已细分：
    SSE < 1.2 px  => 合并
    SSE >= 1.2 px => 保持细分
```

恰好等于阈值时保持当前状态。滞回只允许暂时保持比要求更精细的节点，不允许未达到最高 LOD 的叶节点超过拆分阈值。

阈值是运行时质量参数，不属于离线资产版本。后续可以根据实测调整而无需重建几何误差。

## 叶节点选择流程

```text
Select(node, view):
    if FullyBehindHorizon(node, view):
        return Culled

    if FullyOutsideFrustum(node, view):
        return Culled

    if node.Level == MaximumLod:
        return SelectedLeaf

    distance = ConservativeDistanceToNode(node, view)
    sse = node.GeometricError * view.FocalLength / distance

    if node was not split:
        if sse > SplitThreshold:
            return Select(node.Children)
        return SelectedLeaf

    if sse < MergeThreshold:
        return SelectedLeaf

    return Select(node.Children)
```

四个子节点独立执行可见性和 SSE 判断。选择结果不受遍历顺序影响，也不受全局资源预算影响。

最高 LOD 本身就是 `ReferenceMesh`，其 `GeometricError` 恒为零；到达最高 LOD 后无需继续计算 SSE。

运行时至少记录：

```text
SelectedChunkCount
CulledByHorizonCount
CulledByFrustumCount
MaximumSelectedSse
SelectedChunksByLod
```

## 验收条件

- 稳定状态下，所有通过可见性测试且 `Level < MaximumLod` 的选中叶节点满足 `SSE <= 2.0 px`。
- 最高 LOD 节点的 `GeometricError` 为零，并无相对 `ReferenceMesh` 的残余误差。
- 相同参考资产、相机和上一帧选择状态产生确定的选择结果。
- 离线包围数据包含节点范围内的全部最高 LOD 参考三角面；最小半径不得只由顶点最小值代替。
- 父节点 `GeometricError` 不小于任一子节点误差，最高 LOD 误差为零。
- 地平线和视锥测试不得错误剔除任何参考几何可见的节点；允许保守保留不可见节点。
- 调整 SSE 阈值不需要重建离线资产。
- 节点数量异常时检查剔除、距离、误差或高程数据；不得以硬 Chunk/三角形上限替代质量规则。

## 本阶段不包含

- 相邻 LOD 平衡、裂缝、裙边、拼接索引和几何 morph。
- CPU/GPU 网格创建、异步加载、缓存、释放和每帧工作量限制。
- 纹理、法线和其他地形派生数据的 LOD。
- 固定 Chunk 数、三角形数或 Draw Call 预算。
