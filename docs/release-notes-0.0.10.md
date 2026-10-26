# 0.0.10

## Bug fixes

- Reuse an already downloaded installer after validating its size and SHA-256, including after restarting the app, instead of downloading it again.
- Keep update downloads in the installation folder's Updates directory and include the version in the filename. Show the version that is ready to install.
- Reclaim the protected temporary installer copy after setup finishes or is canceled, without removing the reusable download or personal settings.

## Updating

Versions 0.0.9 and earlier keep their existing download location until this version is installed. A canceled first upgrade continues to use the old helper's cleanup behavior.

---

# 0.0.10 中文更新日志

## 错误修复

- 校验已有安装包的大小和 SHA-256 后直接复用，重新打开应用也无需再次下载。
- 更新下载保存在安装目录内的 Updates 文件夹，文件名包含版本号，同时显示待安装版本。
- 安装完成或取消后回收受保护的临时安装副本，保留可复用的下载包和个人设置。

## 更新方式

0.0.9 及更早版本在安装此版本前，仍使用原下载位置。首次升级若在替换助手前取消，临时副本回收仍沿用旧助手的行为。
