# SourceBuilder 模板

本例展示实例目标、显式 `this` 参数和静态 helper 的布局。目标契约为自定义类型：

```csharp
namespace Example
{
    public class Calculator
    {
        public int Offset;
        public int Add(int value) => Offset + value;
    }
}
```

本目录只提供 replacement 源码和配置，不包含注入后的目标程序集或 IFix runtime。
仅编译上述类不会产生工具所需的 IDMAP 和 wrapper；目标工程需先完成 InjectFix 注入。
将同版本元数据及依赖放入仓库根目录的 `inputs/DummyDll/` 后，
按 [SourceBuilder 工作流](../../IFixPatch.SourceBuilder/README.md#快速开始) 执行。

使用其他目标时，修改 `profile.json` 的输入路径，从 `catalog.json` 取得目标签名，
并同步修改 `patch.json` 和 `PatchImpl.cs`。配置路径相对各自 JSON 文件解析。

无需目标程序集的可运行示例见 [roundtrip](../roundtrip/README.md)。
