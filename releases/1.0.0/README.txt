AutoCommit 1.0.0
提交说明生成与审核工具
开发者：Jason Jia

AutoCommit 根据所选代码变更、项目提交规范和你配置的 OpenAI 兼容模型服务生成提交说明。生成结果供开发者审核；用户明确确认后，Visual Studio 可执行本地 Git 提交或 TFVC 签入，VS Code 将结果写入原生 Git 提交框。

支持平台
- Visual Studio 2019（16.x，x86）：Git 与 TFVC，安装 AutoCommit-VS2019-1.0.0.vsix。
- Visual Studio 2022 17.14（x64）：Git 与 TFVC，安装 AutoCommit-VS2022-1.0.0.vsix。
- VS Code 桌面版：Git，安装 AutoCommit-VSCode-1.0.0.vsix。
三个 VSIX 面向不同宿主，不能互相安装。VS Code 扩展最低要求 VS Code 1.96.0。

功能介绍
1. 读取准确的提交范围。Git 使用暂存区，因此支持部分暂存。VS Code 在暂存区为空但存在变更时，询问后可暂存全部变更。Visual Studio 的 TFVC 范围是所选映射目录与 Team Explorer「包含的更改」的交集，并递归包含子目录。
2. 按项目规范生成。支持自定义模型地址、模型名、HTTP 许可、单次请求字符预算、请求超时和可选的 EnableThinking。Git 从仓库根目录读取 .auto；TFVC 从所选范围目录读取。项目模型设置和 .auto/commit-rules.md 优先于内置默认值。
3. 分批处理大差异。预算超限时按差异提取逐文件事实，给事实附上来源编号，按最终请求容量合并相邻摘要，再生成完整说明。请求超时会按配置重试；较大批次可缩小并继续处理。无法完成时提供部分摘要，并指出尚未确认覆盖的文件；尚无模型事实时提供本地摘要。
4. 保护草稿和用户提交。生成前后会校验所选变更快照；Visual Studio 在本地提交或 TFVC 签入前再次校验。取消、范围过期或用户拒绝替换时保留旧稿。VS Code 将文本填入原生提交框，用户仍通过 VS Code SCM 执行提交。
5. 配置与排查。API Key 分别使用 Windows DPAPI（Visual Studio）和 VS Code SecretStorage 保存在本机。连接测试只发送固定文本；VS Code 的示例生成也使用固定示例差异。可在输出面板或 Visual Studio 模型设置中查看日志、阶段、输入大小、耗时、超时与回退信息。

快速使用
- 安装匹配宿主的 VSIX 并重启编辑器。
- 配置 OpenAI 兼容服务地址、已部署模型名和 API Key。HTTP 服务需要显式启用。项目已有 .auto/model.json 时，以项目文件字段为准。
- Git 先暂存本次要提交的内容。TFVC 先在 Team Explorer 将准备签入的文件移入「包含的更改」。
- 读取变更，生成并审核说明，再按宿主的提交流程操作。

安全与边界
- 点击生成会把所选变更和项目提交规范发送到配置的模型服务；请确认服务适合接收仓库代码。当前没有自动敏感信息扫描。
- 扩展不会自动推送。TFVC 签入仍接受服务器冲突检查、必填说明和团队策略；不能绕过团队门控。
- Visual Studio / VS Code GUI 实机验收和真实 TFVC 服务端联调未纳入本次自动化发布验证。请在目标环境中验证安装、模型连接、提交范围以及团队策略行为。
- VS Code 打包器提示仓库没有 LICENSE 文件；扩展 VSIX 仍成功生成。

验证结果
- Visual Studio Release 重建：0 警告、0 错误。
- .NET 自动化测试：33 通过，0 失败。
- VS Code Node 测试：19 通过，0 失败。
- 三个 VSIX 内部版本均核对为 1.0.0；SHA-256 见 SHA256SUMS.txt。

GitHub 发布页：https://github.com/Haiezan/AutoCommit/releases/tag/v1.0.0
