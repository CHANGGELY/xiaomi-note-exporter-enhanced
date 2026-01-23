# 小米云笔记导出器（增强版）

一次登录、断点续传、稳定导出：把小米云端笔记导出为本地 Markdown（可选下载图片 / 转 JSON / 导出校验）。

English: `README.en.md` · 常见问题：`docs/FAQ.zh-CN.md`

> 免责声明：本项目与小米公司无任何关联；请确保你的使用符合当地法律法规与小米服务条款。

## 特色（相比原版）

- 一次登录：使用持久化 Chrome Profile，首次登录后后续运行会复用登录态（不需要反复登录）。
- 断点续传：`--split` 模式下会自动接续已有 `exported_notes_*` 目录，避免从头导出。
- 去重更稳：多重指纹（元素 ID / 文本哈希 / DOM 哈希）降低重绘/滚动导致的漏导与重复。
- 更可靠的滚动加载：加入“加载中 spinner”判断与等待，减少误判到底部导致卡住。
- 可验证：`--verify` / `--expected` 校验导出数量与重复情况。
- 进度可视化：控制台进度条 + `progress.txt`（新手不用写命令也能看进度）。

## 快速开始（小白版）

### 你需要准备什么

- Windows 10/11
- 已安装 Google Chrome（最新版即可）
- 已安装 `.NET 8 Runtime`：https://dotnet.microsoft.com/download/dotnet/8.0/runtime

### 最简单的用法（推荐）

1. 下载 Release 压缩包（包含 `xiaomiNoteExporter.exe`）。
2. 解压到一个空文件夹（例如 `D:\xiaomi-note-exporter\`）。
3. 双击运行 `xiaomiNoteExporter.exe`。
4. 第一次运行会弹出 Chrome 窗口：登录你的小米账号。
5. 登录成功后程序会自动开始导出；完成后会提示你导出文件/目录位置。

后续再次运行时，会自动复用上一次的登录状态（除非你手动清除了登录数据）。

## CLI 用法（复制粘贴即可）

下面示例假设你在 PowerShell 里执行，并且 `xiaomiNoteExporter.exe` 就在当前目录。

### 1) 导出为“分文件夹 + 每条一份”并保存到指定目录

```
.\xiaomiNoteExporter.exe --domain i.mi.com --split --output "D:\导出结果\mi-notes"
```

导出后会生成：`D:\导出结果\mi-notes\exported_notes_yyyy-MM-dd_HH-mm-ss\`

### 2) 校验导出是否齐全（推荐）

```
.\xiaomiNoteExporter.exe --verify "D:\导出结果\mi-notes\exported_notes_yyyy-MM-dd_HH-mm-ss" --expected 1056
```

`--expected` 填你在网页端“全部笔记”的数量（以你看到的为准）。

### 3) 不下载图片（更快）

```
.\xiaomiNoteExporter.exe --domain i.mi.com --split --disable-images
```

## 进度查看（不用命令）

导出目录里会有一个 `progress.txt`，用记事本打开即可看到：更新时间 / 已完成数量 / 状态。

## 隐私与安全

- 导出的笔记与图片仅保存在你电脑本地；本项目不会上传你的内容。
- 不要把导出目录（`exported_notes_*`、`images*`、`manifest*`）提交到 GitHub。
- 登录态默认存放在：`%LOCALAPPDATA%\xiaomiNoteExporter\chrome_user_data`  
  如果你想“清除记忆登录”，删除该目录即可（下次运行会要求重新登录）。

## 常见问题

### Chrome 启动崩溃 / Profile 被占用（DevToolsActivePort）

1. 先关闭所有 Chrome 窗口（包括后台进程）。
2. 重新运行程序；程序会尝试清理残留锁文件并在必要时自动修复 profile。
3. 仍失败时，可临时指定一个全新 profile：`setx XNE_PROFILE_DIR "D:\xne_profile"`（重开终端后生效）。

## License

本项目遵循 `GPL-3.0`，详见 `LICENSE.txt`。

## 致谢

本项目基于开源项目 `nogiszd/xiaomi-note-exporter` 的思路与代码演进而来，感谢原作者与贡献者。
