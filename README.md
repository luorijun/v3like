## todo

- 球体渲染
- 地形变形
- 网格系统
- 射线查找
- 选中高亮

## 静态资源生成

Planet 资产由仓库内固定配方生成，不提交到 Git。首次运行或生成算法变更后执行：

```powershell
dotnet run --project tools/AssetBuilder -- planet
```

命令会生成并校验 `Content/sphere.asset`，随后主项目会在构建时将其复制到输出目录。
