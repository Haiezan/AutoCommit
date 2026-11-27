# AutoCommit v1.0.4

## 本版变化

- 调整 C# 与 VS Code 的模型提示，使直接生成和分批总结共享变更证据约束，减少重复提示占用请求预算。
- 运行日志补充请求阶段、耗时、输入 Token 和缓存 Token 信息；Visual Studio 与 VS Code 显示缓存输入用量，Visual Studio 同时显示缓存输入占比。
- Visual Studio 运行详情增加生成上下文、阶段、耗时和结果信息，并支持打开日志目录；设置和变更视图在内容过期时提示并要求重新读取。
- 三个平台的扩展版本统一为 1.0.4。

## 安装与验证

按编辑器选择 VS2019、VS2022（17.14）或 VS Code 安装包。三个安装包的 SHA-256 校验值见 [SHA256SUMS.txt](https://github.com/Haiezan/AutoCommit/releases/download/v1.0.4/SHA256SUMS.txt)，GitHub Release 也提供全部附件。

Visual Studio Release 重建通过（0 警告、0 错误），37 项 .NET 测试和 22 项 Node 测试通过，VS Code VSIX 打包成功。尚未完成 Visual Studio / VS Code 实机 GUI 验收、真实模型服务和 TFVC 服务器联调。
