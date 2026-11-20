# AutoCommit for VS Code

开发者：Jason Jia。此扩展面向 VS Code 桌面版的 Git 仓库。

1. 如需只提交一部分改动，先在源代码管理视图暂存这部分内容。
2. 在设置中填写 `AutoCommit: Base Url`、`AutoCommit: Model`；本机 HTTP 服务还需启用 `AutoCommit: Allow Http`。若仓库已有 `.auto/model.json`，请直接填写该文件中的 `Model` 等字段；项目文件优先于 VS Code 设置，包括 `Model` 为空时。
3. 运行“AutoCommit: 设置当前仓库 API Key”（无认证服务可以留空），再运行“测试模型连接”。Key 由 VS Code 的 SecretStorage 保存，不写入项目。
4. 运行「AutoCommit: 生成提交说明」或点击源代码管理视图中的 AutoCommit 命令。已有暂存内容时直接生成。暂存区为空且有变更时，会询问是否暂存全部变更并生成。
5. 如需单独验证生成，可运行「AutoCommit: 用示例变更测试生成」。该命令仅将固定示例差异发送给模型服务，不读取仓库内容；结果显示在通知中。

确认暂存全部变更后，Git 会暂存修改、删除和未被忽略的新增文件。生成会把暂存差异和规范发送到配置的模型服务，并在生成前后检查快照。结果写入原生提交框，替换已有说明需确认。请在提交框审核结果，再使用 VS Code 的 Git 提交操作。`.auto/commit-rules.md` 与 Visual Studio 插件共用，文件缺失时使用内置规范。VS Code 使用自己的凭据存储，首次使用需重新填写 API Key。

`MaxInputCharacters` 默认每次请求 48,000 字符。超出预算时，扩展会分批提取变更事实，再逐层合并并结合提交规范生成说明。每次请求默认等待 240 秒，可在项目 .auto/model.json 中通过 `RequestTimeoutSeconds` 设置为 1 至 300 秒。小差异直接生成、摘要合并和最终生成超时时，插件会重试当前请求一次，较大的差异请求超时后会缩小批次并继续汇总。可在 `.auto/model.json` 为支持的模型设置 `"EnableThinking": false` 关闭思考。通知会显示阶段和超时信息，用户可以取消。模型仍无法生成说明时，可以使用根据暂存文件清单生成的本地摘要。已成功提取的事实会写入部分摘要。取消或快照变化时保留原提交框内容。

运行生成或连接测试时会自动打开 AutoCommit 输出通道。也可以按 F1 执行「AutoCommit: 查看运行日志」。日志显示当前加载的扩展版本，并按运行编号记录快照校验、模型请求耗时、输入字符数、超时和回退原因。示例生成同样写入该通道。

开发验证：在本目录运行 `npm test`，用 `npm run package` 打包 VS Code 专用 VSIX。

正式本地发布请从仓库根目录运行 `scripts/Publish-Release.ps1 -Version X.Y.Z`，该命令会同时生成 Visual Studio 2019、Visual Studio 2022 和 VS Code 的安装包，并集中保存到 `releases/X.Y.Z`。
