# 状态与新增成员模板

使用上一层 [目标契约](../README.md) 和 [profile](../profile.json)。
本模板为 `Example.Calculator` 增加 VM 旁表字段和属性，并使用补丁自有静态计数。

```bash
dotnet run -c Release --project IFixPatch.SourceBuilder -- build .work/prepared/prepared-profile.json samples/sourcebuilder/state/patch.json -o .work/state
dotnet run -c Release --project IFixPatch.SourceBuilder -- generate .work/prepared/prepared-profile.json .work/state/build-report.json -o .work/state/patch.bytes
dotnet run -c Release --project IFixPatch.SourceBuilder -- verify .work/prepared/prepared-profile.json .work/state/patch.bytes --build-report .work/state/build-report.json
```

`typeBindings` 将实现程序集的扩展类型绑定到已有目标类型；源码用经 object 的显式转换访问新成员。
移植后该转换引用真实目标类型，新增字段由 IFix 旁表保存，不修改目标原生对象布局。
扩展类型只能声明新增成员；同名成员冲突会拒绝构建。现有构造函数仅在构建壳中用于提取新增字段的初始化表达式，不被替换。

自有类型、静态字段和自动属性由类型图移植。闭包或迭代器还要求目标预先提供对应接口/委托桥接，工具不会生成新的客户端桥接器。
保留 `patch.bytes.generation.json`，它关联补丁和构建报告的 SHA-256，并记录实际生成的方法数量。
