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

The InjectFix adapter compiles upstream source files and copies the Cecil DLL from that checkout.
Its bundled Cecil version is separate from the Mono.Cecil 0.11.6 NuGet dependency used by this project.
Retain these notices and the `licenses/` directory when distributing the adapter, tools or packages.

ICSharpCode.Decompiler package metadata additionally states: Copyright 2011-2026 AlphaSierraPapa.
System.CommandLine package metadata additionally states: © Microsoft Corporation. All rights reserved.
The linked license texts preserve the notices shipped by their upstream repositories.

Self-contained .NET publications also include runtime components. Retain the `LICENSE.txt` and
`ThirdPartyNotices.txt` provided by the runtime, along with the project notices above.
