# AutoCommit 项目配置

`.auto/model.json` 保存模型地址、模型名称、HTTP 开关和单次输入字符预算。`MaxInputCharacters` 默认为 48,000，超出时会分批汇总。`.auto/commit-rules.md` 保存项目的提交规范，优先于扩展内置规范。

`EnableThinking` 可显式控制支持此参数的模型。设为 `false` 关闭思考，设为 `true` 开启，省略时采用服务端默认值。本仓库的百炼 `qwen4-max` 配置关闭思考以缩短生成时间。

API Key 在扩展中设置并保存在本机。Visual Studio 使用 Windows DPAPI，VS Code 使用 SecretStorage。切换项目或模型服务地址时，插件会按新的项目和地址查找凭据。

Git 从仓库根目录读取 `.auto`，TFVC 从所选范围目录读取 `.auto`。Visual Studio 的 TFVC 路径要求 Team Explorer 打开「挂起的更改」，并读取所选映射目录内的「包含的更改」。平台和提交步骤见[运行逻辑与流程图](../docs/runtime.md)。
