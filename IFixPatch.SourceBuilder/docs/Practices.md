# 编写实践

命令从仓库根目录执行；能力边界见 [Capabilities.md](Capabilities.md)。

## 查询目标签名和字段

`prepare` 输出的 `.work/prepared/catalog.json` 包含方法的 Cecil 完整签名和 IDMAP slot：

```bash
python3 - .work/prepared/catalog.json <<'PYTHON'
import json, sys
with open(sys.argv[1], encoding="utf-8") as stream:
    catalog = json.load(stream)
for method in catalog["methods"]:
    if "Example.Calculator::Add" in method["signature"]:
        print(method["signature"])
PYTHON
```

字段信息单独保存在 `field-catalog.json`，包含声明类型、名称、类型、可见性、静态与只读标志、
后备字段及对应属性。可见性来自未改动的输入程序集；编译引用会放宽访问权限。
自动属性后备字段按元数据名字 `<Prop>k__BackingField` 识别。

## 核对目标 API

每个 API 的可用性都应针对具体版本记录证据：

| 证据 | 能说明什么 |
| --- | --- |
| 元数据中找到类型或签名 | 存在元数据引用 |
| 找到同版本 Native 实现或 AOT 实例 | 具备进一步核对调用的依据 |
| 补丁离线生成并通过 `verify` | 通过工具当前的结构检查 |
| 客户端实际执行相应路径 | 该版本、该调用场景已通过运行验证 |

样例源码中出现过某 API 不等于已运行验证；扫描没有找到也不等于不可用。
泛型类型与方法还需核对 AOT 实例，见 [泛型引用证据](Capabilities.md#泛型引用证据)。
格式化数值时应显式考虑区域设置和分隔符；使用 `InvariantCulture` 等 API 前，
同样需要检查它在目标运行时是否可用。

## 组织源码

`sources` 可使用通配并跨静态类型调用 helper。`**/*.cs` 会包含递归目录下的全部 `.cs`，
补丁目录中不要混入 `obj/`、`bin/` 或不参与编译的文件。
源码按稳定顺序展开，helper 按调用关系收集；改动文件组织后仍应重新检查补丁结果。

`generate` 会核对构建报告中记录的每个源文件的 SHA-256。
源码改变后必须重新 `build`，否则会收到 `Patch source changed after build` 错误。

## 规模与运行验证

查看 `build-report.json` 的 `size` 和 `diagnostics`。
递归调用不能静态定界，工具会给出警告；阈值及其含义见 [规模限额](Capabilities.md#规模限额)。
先排查意外生成的 helper、递归或过大的方法，再决定是否调整警告阈值。

每个新增业务入口、API 组合或复杂 IL 都应在目标客户端执行相应路径。
记录目标版本、输入及产物 SHA-256、离线结果和客户端观察结果，避免将单个入口的成功推广到整个补丁。
