# 项目发布约定

用户要求发布新版本时，从仓库根目录运行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish-Release.ps1 -Version X.Y.Z`。该命令会统一更新并构建 VS2019、VS2022、VS Code 三个扩展，将安装包、说明和 SHA-256 校验值集中保存到 `releases/X.Y.Z`，并更新 `releases/LATEST.md`。

不要只复制单个平台的构建产物来代表完整发布。先检查脚本执行结果和三个安装包，再报告发布完成。发布流程详情见 `README.md` 的“本地批量发布”。
