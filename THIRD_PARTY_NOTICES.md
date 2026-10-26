# Third-party notices

IFixPatchTools is licensed under MIT. Third-party components retain their own copyrights and licenses.
The project license does not cover user-supplied assemblies, metadata or patches.

| Component | Version / revision | License text |
| --- | --- | --- |
| [InjectFix](https://github.com/Tencent/InjectFix/tree/5ba42eb8f63fe37134a3c78690f97d8425e13588) | `5ba42eb8f63fe37134a3c78690f97d8425e13588` | [MIT and bundled Cecil notice](licenses/InjectFix.txt) |
| [Mono.Cecil](https://github.com/jbevain/cecil/tree/0.11.6) | 0.11.6 | [MIT](licenses/Mono.Cecil.txt) |
| [ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy/tree/1377eb6e7351b21112858c8c1df39848f40181ec) | 10.1.1.8388 | [MIT](licenses/ICSharpCode.Decompiler.txt) |
| Humanizer source included in ICSharpCode.Decompiler | as bundled in the revision above | [MIT](licenses/Humanizer.txt) |
| [System.CommandLine](https://github.com/dotnet/command-line-api/tree/209b724a3c843253d3071e8348c353b297b0b8b5) | 2.0.0-beta4.22272.1 | [MIT](licenses/System.CommandLine.txt) |
| [Microsoft.NETFramework.ReferenceAssemblies.net471](https://www.nuget.org/packages/Microsoft.NETFramework.ReferenceAssemblies.net471/1.0.3) | 1.0.3, build only | [MIT](licenses/NETFramework.ReferenceAssemblies.txt) |

Test-only dependencies:

| Component | Version | License text |
| --- | --- | --- |
| [Microsoft.NET.Test.Sdk, Microsoft.TestPlatform.ObjectModel, Microsoft.TestPlatform.TestHost, Microsoft.CodeCoverage](https://github.com/microsoft/vstest/tree/490850ae3fdc1b470e3804ceab4f6a41cf89ae51) | 17.14.1 | [MIT](licenses/Microsoft.TestPlatform.txt) |
| [xUnit.net, including assert, core and extensibility packages](https://github.com/xunit/xunit/tree/9712244020d385955d33136b3fe3e87de43539cd) | 2.9.3 | [Upstream notices](licenses/xUnit.txt), [Apache-2.0](licenses/Apache-2.0.txt) |
| [xUnit.net Visual Studio runner](https://github.com/xunit/visualstudio.xunit/tree/50e68bbb8b9ddd4b1bbb95d062b62010caf99909) | 3.1.4 | [Upstream notices](licenses/xUnit.VisualStudio.txt), [Apache-2.0](licenses/Apache-2.0.txt) |
| [xunit.abstractions](https://www.nuget.org/packages/xunit.abstractions/2.0.3) | 2.0.3 | [xUnit.net notices](licenses/xUnit.txt), [Apache-2.0](licenses/Apache-2.0.txt) |
| [xunit.analyzers](https://github.com/xunit/xunit.analyzers/tree/2620a4ddabcba16ceb64ea9ad19c566252015fe8) | 1.18.0 | [Upstream notices](licenses/xUnit.Analyzers.txt), [Apache-2.0](licenses/Apache-2.0.txt) |
| [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json/tree/0a2e291c0d9c0c7675d445703e51750363a549ef) | 13.0.3 | [MIT](licenses/Newtonsoft.Json.txt) |

The xUnit.net packages retain Copyright (c) .NET Foundation and Contributors.
Their upstream notice files also preserve notices for bundled MIT components.
These test packages are not referenced by the production projects.
The `xunit.abstractions` package metadata links to the xUnit.net license; its assembly
copyright identifies the .NET Foundation. Newtonsoft.Json package metadata additionally
states: Copyright © James Newton-King 2008; its bundled license retains the 2007 notice.

The InjectFix adapter compiles upstream source files and copies the Cecil DLL from that checkout.
Its bundled Cecil version is separate from the Mono.Cecil 0.11.6 NuGet dependency used by this project.
Retain these notices and the `licenses/` directory when distributing the adapter, tools or packages.

ICSharpCode.Decompiler package metadata additionally states: Copyright 2011-2026 AlphaSierraPapa.
System.CommandLine package metadata additionally states: © Microsoft Corporation. All rights reserved.
The linked license texts preserve the notices shipped by their upstream repositories.

Self-contained .NET publications also include runtime components. Retain the `LICENSE.txt` and
`ThirdPartyNotices.txt` provided by the runtime, along with the project notices above.
