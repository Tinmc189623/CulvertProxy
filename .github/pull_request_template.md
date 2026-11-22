## 变更内容

用一两句话说明这个 PR 解决了什么问题，以及用户会看到什么变化。

## 类型

- [ ] 修复
- [ ] 新功能
- [ ] 性能优化
- [ ] 文档 / 构建 / CI
- [ ] 重构

## 验证

- [ ] `pwsh -NoProfile -File .\build.ps1`
- [ ] `dotnet test .\tests\Nexsteaduser.CulvertProxy.Tests\Nexsteaduser.CulvertProxy.Tests.csproj -c Release`
- [ ] 涉及原生 Hook / 注入时，执行 `build.ps1 -Smoke` 或完整的 `SmokeTest --release-test`
- [ ] 涉及界面时，附上深色和浅色主题截图
- [ ] 涉及协议、路由或 DNS 时，补充对应单元 / 集成测试

## 风险与回滚

说明可能的兼容性影响、失败关闭行为或配置迁移。没有风险时写「无」。

## 检查清单

- [ ] 没有提交密码、令牌、真实用户数据或本机配置文件
- [ ] 新增代码包含函数级注释，没有占位实现
- [ ] 文档和 README 已同步更新
- [ ] 品牌写作使用完整的 Nexsteaduser，版权声明保持正确
- [ ] 提交内容以 Apache-2.0 许可证授权
