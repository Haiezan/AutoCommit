AutoCommit for VS Code 0.1.0，开发者 Jason Jia。

适用于 VS Code 桌面版。通过“扩展：从 VSIX 安装”选择 AutoCommit-VSCode-0.1.0.vsix，或运行：
code --install-extension AutoCommit-VSCode-0.1.0.vsix

支持 Git 暂存内容读取、OpenAI 兼容模型生成、共享 .auto 配置与规范、VS Code SecretStorage 凭据和原生提交框回填。不会自动暂存、提交或推送。不包含 TFVC 和 VS Code 网页版支持。

验证：4 项 Node 自动化测试通过，VS Code 专用 VSIX 打包成功。真实模型调用、GUI 操作与多仓库交互尚未完成实机验收。仓库中的 Visual Studio VSIX 不适用于 VS Code。
