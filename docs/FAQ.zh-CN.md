# 常见问题（FAQ）

## 1) 为什么需要弹出 Chrome？能不能不登录？

小米云笔记页面需要登录后才能访问笔记列表。程序第一次运行会打开 Chrome，让你登录一次；之后会复用登录态（Cookie/本地存储等）。

## 2) 登录态存在哪里？如何清除“记忆登录”？

默认目录：`%LOCALAPPDATA%\\xiaomiNoteExporter\\chrome_user_data`

想清除登录态：关闭所有 Chrome 后，删除上面这个目录即可（下次运行会要求重新登录）。

## 3) 导出会不会把我的笔记上传到哪里？

不会。导出内容只写入你指定的本地目录。

## 4) 导出到一半卡住了怎么办？

建议按顺序尝试：

1. 先观察导出目录里的 `progress.txt` 是否仍在更新。
2. 如果长时间不更新，重新运行程序即可继续（`--split` 会自动断点续传）。
3. 如果提示 Chrome/profile 被占用，先关闭所有 Chrome，再重试。

## 5) 启动时报 `DevToolsActivePort file doesn't exist` / `Chrome failed to start`

这通常与 Chrome Profile 被占用或上次异常退出后残留锁文件有关：

1. 关闭所有 Chrome 窗口（任务管理器里也确认没有 `chrome.exe`）。
2. 重新运行程序；程序会尝试清理锁文件，必要时自动修复 profile。
3. 仍失败：给程序指定一个全新 profile 目录（避免老 profile 损坏）：

```
setx XNE_PROFILE_DIR "D:\xne_profile"
```

关闭终端重新打开后再运行。

## 6) 如何确认导出“全部齐了且不重复”？

使用校验模式（把 `--expected` 改成你网页端看到的总数）：

```
.\xiaomiNoteExporter.exe --verify "D:\导出结果\mi-notes\exported_notes_yyyy-MM-dd_HH-mm-ss" --expected 1056
```

