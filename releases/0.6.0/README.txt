AutoCommit 0.6.0 VS2019 Release
新增 TFVC 读取及签入、独立模型和仓库状态。
先在 VS2019 Team Explorer 连接 TFS 并映射工作区，确认更改已经挂起。
选择项目或手工指定目录后读取，生成并审核说明。
TFVC 签入将所选目录递归范围内全部挂起更改写入服务器，不使用 Team Explorer 的勾选列表。
策略、必填说明、工单或门控要求无法满足时，请复制说明到 Team Explorer 签入；不会自动覆盖策略。
需要 VS2019 自带的 Team Explorer TFVC 客户端组件。
14 项自动化测试通过，Release 构建零警告。未连接真实 TFS，也未完成 VS 实机加载和签入验收，请先使用测试项目。安装包未签名。
