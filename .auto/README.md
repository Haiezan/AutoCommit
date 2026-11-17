# AutoCommit 项目配置

model.json 保存模型地址和名称；commit-rules.md 保存提交规范。API Key 在插件设置中填写并在本机加密保存，不写入项目文件。更换服务地址后需重新填写 Key。

当前源码支持 Git（包含 TFS/Azure DevOps 托管的 Git）和 TFVC。TFVC 需要 VS2019 Team Explorer 客户端及已映射的工作区；签入会直接写入服务器，真实服务联调尚未完成。仓库中 VS2022 的现有发布包为 0.5.0，仅支持 Git。
