# 第三方组件与版权声明（Third-Party Notices）

> 这份文件回答一个问题：**WARDEN 里有没有别人的代码？声明在哪？** ✓

## 一句话结论

**WARDEN 自身源码没有任何第三方库依赖** ✓
但**自包含构建的几个工具里打包了 .NET 运行时** ✗
那是微软的东西 ✓ 必须按它的许可随附声明 ✓

---

## 一、WARDEN 自己的代码

* 许可证：**AGPL-3.0-or-later** ✓ 全文见 [LICENSE](LICENSE)
* 作者：Firefly0619sec
* 你可以查看、修改、再分发 ✓（改动按 AGPL 开源即可 ✓ 做成在线服务同样要提供源码 ✓）

## 二、被【打包进安装包】的第三方组件

| 组件 | 许可证 | 声明原文 |
| --- | --- | --- |
| **.NET 运行时**（设置 / 日志查看 / 帮助 / 更新 / 卸载 / 在线安装器 ✓） | **MIT** | [dotnet-LICENSE.txt](docs/第三方声明/dotnet-LICENSE.txt) |
| .NET 运行时内含的第三方组件 | 各自许可（与 MIT 兼容 ✓） | [dotnet-ThirdPartyNotices.txt](docs/第三方声明/dotnet-ThirdPartyNotices.txt) |

说明：

* **WARDEN.exe 本体是 Native AOT** ✓ 不依赖运行时 ✓
  只有上面那几个工具是自包含的 ✓ 各自内嵌一份运行时 ✓ 要声明的就是它 ✓
* 这两份文本是【原样复制】微软随 .NET 分发的声明文件 ✓ 未作修改 ✓
* 分发来源：`%ProgramFiles%\dotnet\` 下的 `LICENSE.txt` 与 `ThirdPartyNotices.txt` ✓

## 三、只用于开发、没有被打包进去的

| 工具 | 用途 | 随包分发 |
| --- | --- | --- |
| **OpenCC** | 生成繁体中文语言包 ✓ | ✗ **不打包** ✓ 生成结果是纯文本 ✓ |

## 四、明确没有引入的东西

* ✗ 没有 NuGet 第三方包（`PackageReference` 数量 = 0 ✓）
* ✗ 没有统计 / 广告 / 崩溃上报 SDK
* ✗ 没有第三方图标或字体（图标自行绘制 ✓ 界面用系统字体 ✓）
* ✗ 没有从别处抄来的代码片段（将来若有，会在此逐条列出出处与许可 ✓）

## 五、Windows 平台接口

大量调用 Win32 API（窗口枚举、Shell_NotifyIcon、SetupAPI 等 ✓）
这些是**操作系统接口** ✗ 不涉及第三方代码分发，无需额外声明 ✓

## 六、如果你是再分发者

1. 随包带上 `LICENSE`（AGPL-3.0 全文 ✓）—— 硬要求 ✗
2. 随包带上本文件与 `docs/第三方声明/` ✓（MIT 要求随分发保留声明 ✓）
3. 你改过的 WARDEN 源码也要按 AGPL 提供 ✓

## 七、发现问题怎么办

如果你认为这里有遗漏或声明不当 ✓ 请在仓库提 issue ✓ 我们会立即补正 ✓
（宁可多列，不可漏列 ✓）

---

*最后更新：2026-10-11　　对应版本：0.2.4*

---

## 八、运行时内含组件的逐项审计（2026-10-11 复核 ✓）

审计方法：把真正随包的产物解出来，逐个文件比对来源 ✓
（自包含工具是**单文件**发布 ✗ 运行时被内嵌 ✓ 所以按组件名审计 ✓）

| 组件 | 出现在 | 许可证 | 声明在哪 |
| --- | --- | --- | --- |
| .NET 运行时核心（coreclr / hostfxr / BCL ✓） | 各工具 exe 内嵌 | MIT | `dotnet-LICENSE.txt` ✓ |
| **ICU**（全球化数据 ✓） | 设置/日志/帮助等工具 | Unicode License v3 | `dotnet-ThirdPartyNotices.txt`（49 处 ✓） |
| **Brotli**（压缩 ✓） | 同上 | MIT | `dotnet-ThirdPartyNotices.txt` ✓ |
| **msquic**（HTTP/3 用的 QUIC 栈 ✓） | 部分工具 | **MIT** | ★ 微软声明里**没点名** ✓ → 本条即为它的声明 ✓ |
| **zlib-ng**（`System.IO.Compression.Native` 用的 ✓） | 部分工具 | **zlib License** | ★ 同上 ✓ → 本条即为它的声明 ✓ |
| `createdump` / `mscordaccore`（诊断 ✓） | 部分工具 | MIT | `dotnet-LICENSE.txt` ✓ |
| `Microsoft.DiaSymReader.Native` | 部分工具 | MIT | `dotnet-LICENSE.txt` ✓ |

**没有出现**的（所以不用声明 ✓）：OpenSSL ✗（Windows 用 Schannel ✓）、
libdeflate ✗、任何非微软的第三方 DLL ✗

许可证要点（便于你复核 ✓）：

* **msquic** —— MIT License ✓ 版权 Microsoft Corporation ✓
  源码：<https://github.com/microsoft/msquic>
* **zlib-ng** —— zlib License ✓ 允许自由使用与再分发 ✓
  源码：<https://github.com/zlib-ng/zlib-ng>

★ 审计同时确认：**源码树里没有任何第三方二进制** ✓ **零 NuGet 依赖** ✓
  唯一"打包进来的别人家的东西"就是上面这份表 ✓ 别的都没有 ✗

---

## 九、msquic / zlib-ng 的许可证正文（审计要求补齐 ✓）

前面第八节说"本条即为它的声明"不够 ✗ 这里把许可正文与版权行补上 ✓

### msquic —— MIT License

`
MIT License

Copyright (c) Microsoft Corporation.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
`

来源：<https://github.com/microsoft/msquic>

### zlib-ng —— zlib License

`
(C) 2016 Intel Corporation
(C) 2014-2024 zlib-ng contributors

This software is provided 'as-is', without any express or implied warranty.
In no event will the authors be held liable for any damages arising from the
use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it freely,
subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not claim
   that you wrote the original software. If you use this software in a
   product, an acknowledgment in the product documentation would be
   appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.
`

来源：<https://github.com/zlib-ng/zlib-ng>
