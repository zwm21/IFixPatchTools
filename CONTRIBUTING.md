# 贡献说明

## 构建与验证

安装 .NET 10 SDK，在仓库根目录执行 README 中的 Release 构建命令。
涉及二进制读写、方法体移植或校验的修改，应运行合成输入回归：

```bash
dotnet run -c Release --project tests/IFixPatchTools.Smoke
```

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
