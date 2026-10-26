# 贡献说明

## 构建与验证

安装 .NET 10 SDK，在仓库根目录执行 README 中的 Release 构建命令。
各程序集分别维护单元测试：

| 测试项目 | 主要范围 |
| --- | --- |
| [Core](IFixPatch.Core/tests/IFixPatch.Core.Tests.csproj) | 二进制与 IR/IL 往返、校验、位模式、元数据解析 |
| [Decompiler](IFixPatch.Decompiler/tests/IFixPatch.Decompiler.Tests.csproj) | 合成程序集执行、VM 虚派发、静态状态、旁表诊断 |
| [Recompiler](IFixPatch.Recompiler/tests/IFixPatch.Recompiler.Tests.csproj) | 命令退出码、输入诊断、装配与原输出保护 |
| [SourceBuilder.Core](IFixPatch.SourceBuilder.Core/tests/IFixPatch.SourceBuilder.Core.Tests.csproj) | 注入布局、目标身份、移植、prepared 哈希与 manifest |
| [SourceBuilder](IFixPatch.SourceBuilder/tests/IFixPatch.SourceBuilder.Tests.csproj) | 命令诊断、离线验证、生成溯源与失败输出保护 |

```bash
dotnet test IFixPatchTools.slnx -c Release

# 单独运行一个程序集的测试。
dotnet test IFixPatch.Core/tests/IFixPatch.Core.Tests.csproj -c Release
```

解决方案包含五个常规程序集及其测试；需要上游源码的 InjectFix 适配器仍按需构建。
测试使用临时自建输入，不依赖上游 checkout 或现有生成产物。生产项目排除其 `tests/` 下的源码和资源，
测试框架只供测试项目引用。测试项目的依赖版本统一放在 `Directory.Build.targets`。

涉及二进制读写或校验的修改，可按 [roundtrip 示例](samples/roundtrip/README.md) 检查 IR 与 IL 往返。
涉及源码构建或方法体移植的修改，应使用自建目标验证生成结果及所需运行行为。
新增单元测试时，按工具或核心库分别组织，覆盖输入边界与可观察行为。
测试文件和主类同名，以功能或行为命名，避免 `Branch`、`Final`、`Audit` 等过程性名称。
测试方法使用 PascalCase，说明输入条件和预期行为；测试夹具使用 `Fixture` 后缀，静态样例工厂使用 `TestData` 后缀。
断言依据格式约定、CLR/VM 语义或独立期望结果；不能把现有实现的返回值直接当作正确要求。
未覆盖分支应逐项区分可观察的工具行为、前置校验已阻断的输入和构造上不可达的路径，并保留依据。
不支持的运行时功能仍应验证拒绝或降级诊断；不以纯 CLR 执行或进程协议桩冒充目标 VM 验证。

需要目标客户端的数据或行为时，说明所用版本、输入来源、离线结果和客户端验证结果。
示例与测试使用自建输入；提交前检查新增文件及生成产物中是否包含凭据、私有路径或第三方目标数据。
文档使用仓库根目录可执行的命令，注释说明当前机制、约束及原因。

## 提交信息

项目提交信息必须遵循 [Conventional Commits 1.0.0](https://www.conventionalcommits.org/zh-hans/v1.0.0/)：

```text
<type>[optional scope][!]: <description>

[optional body]

[optional footer(s)]
```

- 新功能使用 `feat`，缺陷修复使用 `fix`。
- 其他常用类型：`docs`、`refactor`、`perf`、`test`、`build`、`ci`、`chore`、`style`、`revert`。
- scope 可使用 `core`、`decompiler`、`recompiler`、`sourcebuilder` 等受影响的模块名。
- description 简洁说明实际变更；有正文或页脚时，以空行分隔。
- 不兼容变更使用 `!` 或 `BREAKING CHANGE: <说明>` 页脚，并说明迁移方式。

示例：

```text
fix(sourcebuilder): reject empty patch method bodies
docs: document standalone setup
build(decompiler): enable dotnet tool packaging
```

本规范用于后续提交及压缩合并后的提交信息。
