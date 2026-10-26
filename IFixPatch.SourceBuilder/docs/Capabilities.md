# 能力边界

上游依据为 [InjectFix 固定版本](https://github.com/Tencent/InjectFix/tree/5ba42eb8f63fe37134a3c78690f97d8425e13588)
的 `Source/VSProj/Src/Tools/CodeTranslator.cs`、`GenerateConfigure.cs` 及 Core 实现。
使用其他上游版本时应重新核对限制。

## 支持

- 普通同步 replacement 方法：局部变量、分支和异常表。
- 目标与外部程序集成员的引用重绑定。
- 补丁内部 helper：静态、非泛型、有方法体且不是构造函数。通过 `process_cfg` 第 2 段登记，
  由 `getMethodId` 翻译为 `CallType.Internal`，不占 fix list 和 IDMAP slot。
- 闭合泛型类型上的成员，以及作为 extern 引用的泛型方法实例。
  `writeMethod` 会保存方法泛型实参；调用可用性仍需核对目标 AOT 实现。
- 多文件、`partial` 和跨静态类型的 helper。

## 不支持的形态

| 形态 | 原因或替代方式 |
| --- | --- |
| 泛型 patch 入口、泛型 helper | 上游 `addInterpretMethod` / `getMethodId` 拒绝解释泛型方法；尚未实现单态化 |
| 被移植 IL 中的开放泛型形参 | 缺少可用于替换形参的具体类型 |
| 字段类型内嵌声明类型形参，如 `T[]`、`List<T>` | 上游字段序列化未传泛型上下文；改用可用的属性或非泛型访问器 |
| 实现程序集自有字段、类型和编译器生成闭包 | 尚未实现相关搬运；编译期内联常量可用 `const` |
| 使用 RVA 字段的数组初始化 | 上游不支持 `ldtoken <field>`；可改为显式逐元素赋值 |
| `calli` / `CallSite`、数组类型上的成员 | 当前引用映射不支持 |
| patch 实现直接调用另一个 patch 实现入口 | 应通过目标程序集的方法引用调用 |
| 调用被替换方法的原 Native 实现 | 当前工具没有保留原实现的调用通道 |

闭合泛型字段的裸 `T` 可由上游替换，但嵌套形参不支持；方法签名会传声明类型作为上下文。
上游支持部分新增引用类型的 `AnonymousStorey` 表示，SourceBuilder 尚未实现移植。
新增值类型也不在当前能力范围内。

数组初始化是否使用 RVA 字段取决于编译器生成的 IL；`string[]` 等逐元素写入形式不受该限制。

## 规模限额

`PatchSizeAnalyzer` 根据两项编码或运行时约束计算用量：

1. `StackSpace` 将 locals 和 maxstack 各编码为 16 位。计数超过 65535 时判 `fail`。
2. IFix VM 使用每线程共用求值栈。工具按最深有界调用链累加各帧的
   `实参 + 局部变量 + 运算栈`，以固定上游的 10240 槽位为基准。
   使用其他目标运行时时应核对其 `MAX_EVALUATION_STACK_SIZE`；当前工具不自动提取该值。

| `manifest.limits` 字段 | 默认值 | 含义 |
| --- | --- | --- |
| `warnEvaluationStackPercent` | 25 | 有界链占共用求值栈的百分比，达到即 warn |
| `failEvaluationStackPercent` | 80 | 同上，达到即 fail，为 extern 回调再入保留余量 |
| `warnInstructionCount` | 3000 | 单方法指令数超过该值时 warn |
| `warnHelperCount` | 128 | helper 数超过该值时 warn |

helper 数和指令数是工具的警告阈值，不是 VM 硬限额。
递归 helper 无法静态定界，工具对相应入口警告，并继续检查其他有界入口。
`build` 遇 `fail` 返回 2，`generate` 拒绝继续生成。
离线 `verify` 不检查运行时栈深度。

## 泛型引用证据

`GenericInstanceProbe` 扫描目标程序集的基类、接口、字段、方法签名、属性和事件：

- 找到实例时，报告 `proven: true` 及元数据出处。这里的 `proven` 仅表示找到元数据引用，
  不保证所有成员的 AOT 实现、反射解析和目标调用均可用。
- 未找到时，报告 `proven: false` 并给出警告。DummyDll 没有原始方法体，
  实例可能只出现在方法体中，不能据此判定不存在。

泛型方法实例不做这项自动扫描。类型与方法的 AOT 可用性都需要另行核对，
参见 [Unity 的 AOT 限制](https://docs.unity3d.com/2023.2/Documentation/Manual/ScriptingRestrictions.html)。

## 元数据壳处理

clean shell 移除既有类型、方法和字段上的 `CompilerGeneratedAttribute`，
避免 DummyDll getter 或状态机空体被误翻译为 VM 内部方法。
compile reference 放宽访问权限；同名 event 与后备字段冲突时仅放宽 event accessor，避免 C# 绑定歧义。
