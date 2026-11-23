## 本版变化

- Visual Studio 与 VS Code 支持按模型服务地址共享 API Key。单个项目可以使用专用 Key，也可以关闭认证或恢复使用共享 Key。
- Visual Studio 的 TFVC 变更预览分别显示当前目录内与工作区包含的变更数量，读取失败时提示具体阶段。

## 安装与验证

按编辑器选择 VS2019、VS2022（17.14）或 VSCode 安装包。三个安装包的 SHA-256 校验值见附件 SHA256SUMS.txt。

33 项 .NET 测试、21 项 Node 测试及 Windows DPAPI 凭据检查均已通过。模型请求使用模拟测试，编辑器 GUI 验收与真实 TFVC 服务器联调尚未执行。
