# AutoCommit for VS Code

开发者：Jason Jia。此扩展面向 VS Code 桌面版的 Git 仓库。

1. 在源代码管理视图暂存要提交的文件。
2. 在设置中填写 `AutoCommit: Base Url`、`AutoCommit: Model`；本机 HTTP 服务还需启用 `AutoCommit: Allow Http`。若仓库已有 `.auto/model.json`，请直接填写该文件中的 `Model` 等字段；项目文件优先于 VS Code 设置，包括 `Model` 为空时。
3. 运行“AutoCommit: 设置当前仓库 API Key”（无认证服务可以留空），再运行“测试模型连接”。Key 由 VS Code 的 SecretStorage 保存，不写入项目。
4. 运行“AutoCommit: 生成提交说明”或点击源代码管理视图中的 AutoCommit 命令。审核发送范围后，生成结果会写入该仓库的原生提交框。可在其中修改，并使用 VS Code 的 Git 提交操作。

扩展仅读取 Git 暂存区，生成前后检查快照；不自动暂存、提交或推送。生成后若调整暂存内容，请重新生成说明，再使用 VS Code 提交。生成会把暂存差异和规范发送到配置的模型服务。`.auto/commit-rules.md` 与 Visual Studio 插件共用；若不存在，使用内置规范。VS Code 使用自己的凭据存储，首次使用需重新填写 API Key。

开发验证：在本目录运行 `npm test`，用 `npm run package` 打包 VS Code 专用 VSIX。

正式本地发布请从仓库根目录运行 `scripts/Publish-Release.ps1 -Version X.Y.Z`，该命令会同时生成 Visual Studio 2019、Visual Studio 2022 和 VS Code 的安装包，并集中保存到 `releases/X.Y.Z`。
