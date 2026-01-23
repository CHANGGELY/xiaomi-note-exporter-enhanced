# 贡献指南（CONTRIBUTING）

感谢你愿意改进这个项目！

## 开发环境

- Windows 10/11
- .NET SDK 8.x
- Google Chrome（用于本地运行/调试）

## 本地构建

在仓库根目录执行：

```
dotnet build
```

## 本地运行（开发版）

```
dotnet run --project .\xiaomiNoteExporter\xiaomiNoteExporter.csproj -- --domain i.mi.com --split
```

## 提交前检查

- 确保没有把任何导出内容提交到仓库：`exported_notes_*` / `images_*` / `manifest*` / `progress.txt` 等都不应出现在 PR 中。
- 尽量保持改动聚焦（一个 PR 解决一个问题）。

## PR 说明建议

- 问题背景（复现步骤）
- 改动内容（为什么这样改）
- 如何验证（你如何确认修复有效）

