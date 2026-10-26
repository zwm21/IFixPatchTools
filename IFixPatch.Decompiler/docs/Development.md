# 实现说明

## 二进制与元数据

补丁按 .NET `BinaryWriter` 的顺序布局读写：小端整数、7-bit 长度字符串，无节目录。
类型、方法、字段和字符串通过侧表索引引用；类型及成员名字用于运行时反射定位。

接口匿名类的 slot 表长度由接口声明方法数决定，需要 `DummyDll` 或 `dump.cs` 元数据。
`DummyDllSource` 保留程序集和类型身份，并为 Cecil IL 构建提供方法定义；
`DumpCsIndex` 适合解析和反汇编，但短名可能有歧义。

## IL 转译与签名推断

- 先声明全部修复方法和内部方法桩，再填充方法体，以支持方法间的前向引用。
- 内部方法元数来自调用指令的参数数、`Ldarg` 下标及委托的 `Invoke` 契约。
- 参数、局部变量、返回值和闭包字段通过类型栈模拟及不动点迭代推断。
- 泛型发射保留 MemberRef 定义的开放签名；推断侧用声明类型和方法实参具体化形参。
- 合并类型时保留具体信息，对整数族、浮点族和引用类型公共基类分别处理。
- 负数字段操作数按实例字段与 VM 静态字段分流；新增字段使用旁表访问器表示。
- `StackBalanceCheck` 根据 Cecil 的 `StackBehaviour` 检查栈深度与返回匹配。

`SeedAnonStoreys` 默认关闭。接口契约可能降低方法体推断的类型精度，
该选项仅保留接口槽播种；object 虚方法槽映射未启用。

## 信息边界

- vtable 槽位目标需要目标运行时布局，Cecil 声明顺序不能直接替代。
- 没有调用、委托或其他引用现场的内部方法，其签名可能无法恢复。
- 没有读写现场的闭包字段，其类型可能只能保留为 `object`。
- `magic` 从输入读取，解析器不验证它是否适合目标运行时。
- opcode literal 缺失时需要补充映射来源；未知指令的转译应检查诊断。

完整解析、零尾随字节、字节往返和栈检查分别验证布局、编码与部分 IL 约束，
不能单独证明业务语义等价。目标版本的验证记录应同时说明输入、工具版本和实际执行路径。

## 相关实现

| 文件 | 职责 |
| --- | --- |
| `IFixPatch.Core/PatchReader.cs`、`PatchWriter.cs` | 补丁读写 |
| `IFixPatch.Core/OpCodeMap.cs` | opcode 映射提取 |
| `IFixPatch.Core/DummyDllSource.cs`、`DumpCsIndex.cs` | 元数据来源 |
| `ILTranspiler.cs`、`IFixPatch.Core/InternalSignatures.cs` | IL 发射、签名与类型推断 |
| `PatchDecompiler.cs` | 合成程序集并调用 C# 反编译器 |
| `IFixPatch.Core/StackBalanceCheck.cs` | 栈检查 |
