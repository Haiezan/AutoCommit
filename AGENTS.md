# AutoCommit 项目指南

本指南适用于整个仓库。AutoCommit 根据所选变更和项目规范生成提交说明。安装和使用见 [README](README.md)，运行流程与快照校验见[运行逻辑](docs/runtime.md)。

## 代码入口

| 路径 | 职责 |
| --- | --- |
| [`src/AutoCommit.Vsix`](src/AutoCommit.Vsix) | Visual Studio 2022 17.14 的 VSIX 项目及两代 Visual Studio 共用的 WPF、仓库识别和 TFVC 代码。工具窗口逻辑在 `CommitControl.cs`，TFVC 读取和签入在 `TfvcProvider.cs`。 |
| [`src/AutoCommit.Vsix.VS2019`](src/AutoCommit.Vsix.VS2019) | Visual Studio 2019 的 VSIX 清单及项目文件。项目链接 `AutoCommit.Vsix` 的 `.cs` 和 `.vsct` 文件。两个 VSIX 都以 .NET Framework 4.7.2 为目标。 |
| [`src/AutoCommit.Core`](src/AutoCommit.Core) | C# 共用逻辑。`Changes.cs` 处理 Git 快照与本地提交，`Generation.cs` 和 `BatchSummarizer.cs` 处理模型请求和分批合并，`DefaultRules.cs` 提供默认规范。 |
| [`src/AutoCommit.Vscode`](src/AutoCommit.Vscode) | 独立的 VS Code 扩展，仅支持 Git。`extension.js` 注册命令并回填原生提交框，`lib/core.js` 读取暂存区并请求模型，`lib/batch.js` 负责分批。 |
| [`tests/AutoCommit.Tests`](tests/AutoCommit.Tests) 与 [`src/AutoCommit.Vscode/tests`](src/AutoCommit.Vscode/tests) | .NET xUnit 和 Node 测试，涵盖实际 Git 暂存区及模拟模型请求。 |

修改跨平台行为时，核对 C# 和 JavaScript 两套实现。若任务只涉及一个平台，按对应入口修改。

## 保持的行为

- **Git 范围**以暂存区为准，包含部分暂存的情形。Visual Studio 和 VS Code 在暂存区为空且有变更时，可在用户确认后暂存全部变更，再生成说明。Visual Studio 的本地提交使用用户确认的快照，并保留 Git hooks 和签名设置。VS Code 将说明写入原生提交框，由用户提交。
- **TFVC 范围**是当前 Team Explorer 工作区中「包含的更改」与所选映射目录的交集，递归包括子目录。读取、生成和签入使用同一范围。获取包含项失败时显示错误。签入前核对清单和完整内容指纹，再由 TFVC SDK 评估冲突、必填说明及团队策略。
- **快照和草稿**在生成前后校验，Visual Studio 提交或签入前再次校验。取消、快照过期或用户拒绝替换时保留旧稿。模型请求失败或必要内容放不进预算时，提供标有「本地摘要」的候选稿，替换现有说明需用户确认。
- **分批生成**使用完整可用差异，界面预览可以更短。`MaxInputCharacters` 是单次请求的字符预算。超限后分段提取事实，逐层合并，最后结合项目规范生成说明。修改预算或捕获流程时，注意转义后的请求长度、取消和临时文件清理。
- **项目配置与凭据**分别管理。Git 从仓库根目录读取 `.auto`，TFVC 从所选范围目录读取。项目规范优先于内置规范。Visual Studio 的项目凭据使用 DPAPI，VS Code 使用 SecretStorage。模型连接测试只发送固定文本，HTTP 需要显式允许，模型请求禁用重定向。
- **默认规范**有两份内置副本，即 [`DefaultRules.cs`](src/AutoCommit.Core/DefaultRules.cs) 与 [`default-rules.md`](src/AutoCommit.Vscode/resources/default-rules.md)。修改内置规范时保持两份内容一致。本仓库实际使用 [`.auto/commit-rules.md`](.auto/commit-rules.md)，业务需要时同步更新。其它项目已有的规则文件优先。

## 构建与验证

Visual Studio 解决方案在 Windows 使用 .NET 8 或更新版本构建。仓库环境如果已在 `%LOCALAPPDATA%\AutoCommit\dotnet` 装有 SDK，可以用该目录的 `dotnet.exe` 执行这些命令。

```powershell
dotnet build AutoCommit.sln -c Release
dotnet test tests/AutoCommit.Tests/AutoCommit.Tests.csproj -c Release
```

从 `src/AutoCommit.Vscode` 运行 Node 测试。打包前用 `npm.cmd ci` 安装锁定的依赖。

```powershell
npm.cmd test
npm.cmd run package
```

生成或变更读取逻辑的修改优先运行对应测试。核对说明文档的相对链接，并预览变更后的 Mermaid 图。报告结果时区分模拟模型验证、编辑器 GUI 验收和真实 TFVC 服务器联调。

## 项目发布约定

用户要求发布新版本时，从仓库根目录运行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish-Release.ps1 -Version X.Y.Z`。该命令会统一更新并构建 VS2019、VS2022、VS Code 三个扩展，将安装包、说明和 SHA-256 校验值集中保存到 `releases/X.Y.Z`，并更新 `releases/LATEST.md`。

不要只复制单个平台的构建产物来代表完整发布。先检查脚本执行结果和三个安装包，再报告发布完成。发布流程详情见 `README.md` 的“本地批量发布”。

用户要求发布到 GitHub Releases 时，使用指向对应源码的版本标签，上传三个 VSIX、说明和 `SHA256SUMS.txt`。核对公开发布页的附件及校验值后报告结果。
