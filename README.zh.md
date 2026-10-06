# windows-context-menu-cleanup

彻底清除 Windows 资源管理器右键菜单里的第三方软件项（百度网盘、迅雷、搜狗输入法、
360 等）——**带验证、带回滚、不假成功**。

[English](./README.md) · [更新记录](./CHANGELOG.md) · MIT

**当前版本：v1.0.0 —— 仅 PowerShell 版。** 编译版可执行文件
（`CtxMenuCleaner.exe`）正在 `src/` 下开发，**不属于本版本**，详见
[后续规划](#后续规划)。

---

## 1. 这个项目要解决的问题

你删了注册表键，`reg` 明确提示 **「操作成功完成」**，但右键菜单**纹丝不动**。
这不是缓存问题，而是下面三个真实原因之一，本项目三个都处理了。

### 1.1 键名被空格填充（最主要的原因）

从真实机器上抓到的：

```
HKLM\SOFTWARE\Classes\*\shellex\ContextMenuHandlers
    键名: [    sgshellext]        length=14
    字符码: 0020 0020 0020 0020 0073 0067 ...
            ^^^^ 四个前导空格
```

扩展的可见名字是 `sgshellext`，但注册表键实际叫 `"    sgshellext"`。所以下面这两条
命令都会**报成功但一个字节都没删**：

```powershell
Remove-Item 'HKLM:\SOFTWARE\Classes\*\shellex\ContextMenuHandlers\sgshellext' -Force
reg delete "HKLM\SOFTWARE\Classes\*\shellex\ContextMenuHandlers\sgshellext" /f
```

OneDrive 用了同样的手法（`" FileSyncEx"`，一个前导空格），这就是网上那些
"删除 OneDrive 右键菜单"的注册表改法经常看似生效、实则毫无作用的原因。

**本项目永远不用固定键名去操作这些键**，而是枚举容器 + 子串匹配。

### 1.2 软件注册了两套机制，你只删了一套

| 形态 | 位置 | 删除后的效果 |
|---|---|---|
| 静态动词 | `...\shell\<动词>` 加一个 `command` 子键 | 删掉该动词 |
| COM 扩展 | `...\shellex\ContextMenuHandlers\<名称>`，默认值是 CLSID | **通常真正画出菜单项的是它** |

百度网盘**两套都用了**（静态动词 `YunShellExplorerCommand` **加上** COM 扩展
`YunShellExt`）。只删静态动词没有任何可见效果。

### 1.3 你只删了一套注册表视图

32 位和 64 位注册表视图是**两套独立存储**，只删一套，另一套继续生效。在受影响的
机器上实测：删掉 64 位的项之后，同名键在 32 位视图里依然 `PRESENT`。

更麻烦的是，Windows PowerShell 5.1 **无法在同一个进程里读取两套视图**
（`RegistryKey.OpenBaseKey` 在 5.1 里不存在），所以本项目必须另外重启一个 32 位
PowerShell 来覆盖另一套视图。

---

## 2. 主要功能

### 三个工具

| 工具 | 作用 |
|---|---|
| **`manage-context-menu.ps1`** | 列出**两套视图**里的全部右键菜单组件，按归属的 DLL/exe 分组、编号，并标注「第三方」或「Windows 内置」。你按编号挑选，它会删掉该组件的**全部注册项**。 |
| **`remove-vendor-context-menu.ps1`** | 按 `vendors.json` 里的厂商特征做可重复、可脚本化的删除，支持追加自己的特征。 |
| **`enumerate-context-menu.ps1`** | 只读审计：打印**真实键名**、带空格键名的字符码，以及每个 handler 背后的 COM 服务 DLL。 |

### 关键行为保证

- **枚举而非固定键名** —— 绕过空格填充陷阱。
- **一次运行覆盖两套视图** —— 64 位调用会自动重启 32 位 PowerShell 并合并结果，
  你不必再记 `/reg:32`。
- **每次删除前导出 `.reg` 备份** —— 用 `reg.exe export`（写 UTF-16LE，正是
  `reg import` 需要的格式），回滚只需一条命令。
- **删除后逐项验证** —— 每次删除后重新读取该键确认已消失。失败会如实报告为
  `FAILED (exit N): ...` 或 `STILL PRESENT`，**绝不吞掉错误**。
- **默认安全** —— Windows 内置项被标注且保守处理：凡不能明确归因于第三方的
  （裸模块名如 `@shell32.dll,-8506`、通用动词如 `open`/`runas`、未解析的 CLSID）
  都归为内置。`manage-context-menu.ps1` 默认不打印它们，必须用 `-SelectWin` 才会碰。
- **无第三方依赖** —— 只需 Windows 自带的 PowerShell 5.1 和 `reg.exe`。
- **脚本源码纯 ASCII** —— PowerShell 5.1 会用 ANSI 代码页读取无 BOM 的 UTF-8 脚本，
  导致字面非 ASCII 文本（包括路径里的中文）被破坏。本项目脚本保持源码 ASCII，
  中文特征用 `\uXXXX` 转义或码点在运行时构造。

### 它不做什么

- **不卸载**软件、不删软件文件、不改文件关联。只删右键菜单的**注册项**。
- **不能阻止**软件在下次更新时重新注册。见 [让删除「不复活」](#让删除不复活)。
- **不覆盖**资源管理器自身的工具栏、"发送到"目录、Internet Explorer 的 `MenuExt` 项。

---

## 3. 安装方法

无需安装器，除 Windows 本身外无任何依赖。

**环境要求**

- Windows 10 或 11 —— 实测于 Windows 10 22H2（build 19045）
- Windows PowerShell 5.1（系统自带）—— **已实测**
- PowerShell 7+ —— 从代码看应当可用，但**本项目未实测**
- 删除需要管理员权限；`-DryRun`、`-ListOnly`、`enumerate-context-menu.ps1` 不需要

**获取文件**

```powershell
git clone https://github.com/<you>/windows-context-menu-cleanup.git
cd windows-context-menu-cleanup
```

或从 Releases 页下载 ZIP 解压到任意目录。若遇到执行策略报错，用
`-ExecutionPolicy Bypass` 运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\validate.ps1
```

**动手删任何东西之前，先做一次自检**（只读，不需要管理员）：

```powershell
.\scripts\validate.ps1
```

它会检查 JSON 加载、`\uXXXX` 解码、脚本在你这个 PowerShell 版本下能否解析、
源码是否纯 ASCII，然后跑一次 dry-run。预期输出以 `ALL CHECKS PASSED` 结尾。

---

## 4. 使用方法

### 手动挑选（推荐）

```powershell
# 1. 先看清单（不需要管理员，不改变任何东西）
.\scripts\manage-context-menu.ps1 -ListOnly

# 2. 按编号挑选（需管理员 PowerShell）
.\scripts\manage-context-menu.ps1
```

`-ListOnly` 默认只打印第三方组件。Windows 内置项**也会被编号**，所以你可以故意
指定它，但除非用 `-Grid`（会列出全部）或 `-SelectWin`，它们不会显示出来。

**`manage-context-menu.ps1` 参数**

| 参数 | 含义 |
|---|---|
| `-ListOnly` | 打印编号清单后退出；不删除；不需要管理员 |
| `-Grid` | 用可搜索的勾选窗口挑选，而不是输入编号 |
| `-Select <int[]>` | 直接删除这些编号，例如 `-Select 3,7` |
| `-SelectWin` | 选中所有 Windows 内置项（高级；见下方警告） |
| `-NoRestart` | 之后不重启 explorer |
| `-NoBackup` | 跳过 `.reg` 备份（不推荐） |
| `-ExportMyVendors <路径>` | 把它找到的第三方键名导出为 JSON，可喂给 `-VendorsFile`。这是**起点而非成品**：真实键名可能是裸 CLSID 或动词 id，用前需要筛选。 |
| `-BackupDir <路径>` | 备份目录；默认 `.\context-menu-backup-<时间戳>` |

### 按特征批量删除

```powershell
# 1. 先看内置特征 + vendors.json 命中了什么
.\scripts\remove-vendor-context-menu.ps1 -DryRun

# 2. 执行删除（需管理员）
.\scripts\remove-vendor-context-menu.ps1
```

**`remove-vendor-context-menu.ps1` 参数**

| 参数 | 含义 |
|---|---|
| `-DryRun` | 只列出命中项，不删除，不需要管理员 |
| `-Pattern <string[]>` | 在已有特征之上追加你自己的键名子串 |
| `-VendorsFile <路径>` | 使用别的厂商配置（默认 `scripts\vendors.json`） |
| `-BackupDir <路径>` | 备份目录 |
| `-NoPause` | 结束时不等回车（供脚本调用） |

配置在 [`scripts/vendors.json`](./scripts/vendors.json) —— 7 条厂商、25 个键名特征。
中文关键词请写成 `\uXXXX` 转义；脚本会自行解码，因为 PowerShell 5.1 的
`ConvertFrom-Json` 不会解码。

### 只做审计

```powershell
.\scripts\enumerate-context-menu.ps1
```

只读。打印每条注册项的真实键名、为带空格的键名标注字符码、把每个 CLSID 解析成
DLL，并显示静态动词的命令行。它读取的是 **64 位**注册表视图。

### 关于重启 explorer

脚本在删除成功后会重启 explorer，让菜单立即刷新。**你的任务栏和桌面图标会消失
一下再回来**——这是正常的，不是崩溃。用 `-NoRestart` 可跳过，之后自己重启。

### `-SelectWin` 警告

`-SelectWin` 会删除 Windows 自己的 shell 扩展，包括"打开方式""发送到""固定到
开始屏幕"、共享等项。**这可能弄坏资源管理器。** 它只为高级排障而存在，请优先用
`-Select` 指定具体编号。

---

## 5. 使用示例

### 示例 A —— 看看你的菜单里有什么（安全，不需要管理员）

```powershell
PS> .\scripts\manage-context-menu.ps1 -ListOnly

=== Windows built-ins (NOT selected; only remove these if you know why) ===
...
=== Third-party add-ons ===
  [ 47] shellext.dll   (3 registration(s))
        from: C:\Program Files\Windows Defender
        Directory               COM handler   64-bit HKLM
        Drive                   COM handler   64-bit HKLM
        *                       COM handler   64-bit HKLM
  [ 50] FileSyncShell64.dll   (4 registration(s))
        from: C:\Program Files\Microsoft OneDrive\26.168.0830.0006
        Directory               COM handler   64-bit HKLM   [padded key name]
        lnkfile                 COM handler   64-bit HKLM   [padded key name]
        ...
  [ 60] QQMusic.exe  [QQMusic.2.Add]   (2 registration(s))
        runs: "C:\Program Files\QQMusic\QQMusic.exe" /add "%1"
        Directory               static verb  64-bit HKLM
        Directory               static verb  32-bit HKLM
```

注意 `[padded key name]` —— 这就是反删除手法被识别并标注出来的样子。

### 示例 B —— 交互式删除一个组件

```powershell
PS> .\scripts\manage-context-menu.ps1
...
Enter the numbers of the components to DELETE, separated by spaces or commas.
  e.g.   1 4 7   or   2,3        (empty = cancel)
Selection: 3

About to remove 4 registry key(s) from 1 component(s).
Type YES to confirm: YES

Deleting [OneDrive]  HKLM\Directory\shellex\ContextMenuHandlers\ FileSyncEx
        backup -> .\context-menu-backup-<timestamp>\HKLM_Directory_shellex_ContextMenuHandlers__FileSyncEx.reg
        -> DELETED
...
Deleted 4, failed 0.
Backups in .\context-menu-backup-<timestamp>
Restarting Explorer...
Explorer is running (pid 12345).
```

### 示例 C —— 脚本化、无交互

```powershell
# 管理员 shell
.\scripts\manage-context-menu.ps1 -Select 3,7 -NoRestart
```

### 示例 D —— 按特征删除已知软件

```powershell
PS> .\scripts\remove-vendor-context-menu.ps1 -DryRun
Loaded 24 patterns from ...\scripts\vendors.json

MATCH  HKLM /reg:64 *\shellex\ContextMenuHandlers
       key name : [    sgshellext]  length=14   <== PADDED NAME
       charcodes: 0020 0020 0020 0020 0073 0067 0073 0068 0065 006C 006C 0065 0078 0074
...
=== 64-bit view: matched 8, deleted 0, failed 0 (dry run) ===
```

然后去掉 `-DryRun` 并以管理员运行，每条命中项会变成 `-> DELETED` 或
`-> FAILED (exit N): ...`。

### 示例 E —— 添加你自己的软件

```powershell
# 1. 用审计工具找到键名
.\scripts\enumerate-context-menu.ps1 | Select-String -Pattern 'PADDED' -Context 3,0

# 2. 把子串加进 scripts/vendors.json
#    { "vendor": "某软件", "patterns": ["SomeAppShellExt"] }

# 3. 先 dry-run，再删除
.\scripts\remove-vendor-context-menu.ps1 -DryRun
.\scripts\remove-vendor-context-menu.ps1
```

### 示例 F —— 回滚

```powershell
reg import ".\context-menu-backup-<timestamp>\HKLM_Directory_shellex_ContextMenuHandlers__FileSyncEx.reg"
# 然后重启 explorer
Stop-Process -Name explorer -Force; Start-Sleep 2; Start-Process explorer.exe
```

### 让删除「不复活」

这类软件在下次启动或升级时通常会把键写回来。按推荐程度排序：

1. **在软件自己的设置里关掉集成开关。** 最干净，且能扛住升级。
   （搜狗：属性设置 → 高级 → 右键菜单；百度网盘、迅雷也有类似选项。）
2. **给具体键设置 Deny 写入权限**，让它写不回来 —— 用 `regini` 或改 ACL。
   更霸道；若安装程序重建该键并带来全新 ACL，需要重做。
3. **软件升级后重跑本脚本。**

---

## 作为 DSH 技能使用

本仓库同时是一个合法的 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)
技能。把整个目录复制到 `$DSH_HOME/skills/`（通常是 `~/.dsh/skills/`），
保持 `SKILL.md` 在根目录即可：

```powershell
Copy-Item . "$env:USERPROFILE\.dsh\skills\windows-context-menu-cleanup" -Recurse
```

## 故障排查

完整清单见 [`references/troubleshooting.md`](./references/troubleshooting.md)。
代价最高的几条：

| 现象 | 原因 |
|---|---|
| `MethodNotFound: ... does not contain a method named OpenBaseKey` | 你在用 PowerShell 5.1，该 API 是 .NET Core / PS 7 专属。用 `$PSVersionTable.PSEdition` 确认。 |
| `Access is denied` | 键在 `HKLM` 下，而进程没有提权。 |
| 64 位程序菜单项没了、32 位还在（或反之） | 另一套注册表视图没处理。 |
| `New-Item` 建出了一个名字被拼接的怪键 | `*` 这个类键对 PowerShell 注册表提供程序来说是通配符。读取用 `-LiteralPath`，删除用 `reg.exe`。 |
| 桌面和任务栏消失了 | 你杀掉了 explorer，`Start-Process explorer.exe` 即可恢复。 |
| 中文变成乱码 | PS 5.1 控制台按 ANSI 代码页解码子进程输出。脚本源码保持纯 ASCII。 |

实测确认的厂商注册点（含空格填充后的**真实键名**）记录在
[`references/field-notes.md`](./references/field-notes.md)。

## 后续规划

- **v1.x** —— 即本版本交付的 PowerShell 工具。
- **v2（开发中，未发布）** —— `CtxMenuCleaner.exe`，用 Windows 自带的 C# 编译器编译的
  原生程序。它通过 `RegistryKey.OpenBaseKey` 在**单个进程**内读取两套注册表视图
  （PowerShell 5.1 做不到），从而彻底摆脱对 PowerShell 版本的依赖。源码在 `src/`，
  二进制不提交。其删除路径在 `HKCU\SOFTWARE\Classes`（两套视图共享同一份存储）下
  仍有一个未解决的 access-denied bug。

## 来源与致谢

这里记录的每一个坑，都是在真实机器上清掉百度网盘、迅雷、搜狗输入法时**实际踩到的**
—— 包括第一次「报成功但零字节删除」、脚本因 `OpenBaseKey` 这个错误在第 90 行直接崩掉、
以及杀掉 explorer 后留下一片空桌面。写这份排查笔记就是为了让你不必再踩一遍。

## 许可证

MIT —— 见 [LICENSE](./LICENSE)。

> **注册表风险提示。** 本项目会修改 Windows 注册表：删除第三方软件注册的右键菜单项，
> 并在删除前为每个键导出 `.reg` 备份。注册表修改存在固有风险——运行前请先审阅脚本与
> 备份产物，并先在非关键机器上测试。作者不对使用造成的损害承担责任。

---

## 测试环境（实测标注）

本版本的功能验证全部在下面这台机器上完成。凡标注"未实测"的项目，请勿视作可用。

| 项目 | 值 |
|---|---|
| **验证日期** | 2026-10-06 |
| **操作系统** | Windows 10 专业版 22H2 |
| **内部版本** | 10.0.19045.6466（`CurrentBuild 19045`，`UBR 6466`，`ReleaseId 2009`） |
| **系统架构** | 64 位 |
| **CPU** | 12th Gen Intel Core i5-12600KF（10 核 16 线程） |
| **内存** | 31.8 GB |
| **系统语言 / 区域** | 中文（简体，中国）· `zh-CN` |
| **ANSI 代码页** | 936（GBK）—— 这正是脚本必须保持纯 ASCII 的原因 |
| **Windows PowerShell** | 5.1.19041.6456（`PSEdition = Desktop`）**← 全部测试在此完成** |
| **CLR** | 4.0.30319.42000 |
| **.NET Framework** | 4.8.09037 |
| **C# 编译器** | `csc.exe` 4.8.9232.0（仅 v2 的 exe 用到） |
| **reg.exe** | `C:\Windows\system32\reg.exe` |
| **PowerShell 7+** | **未安装、未测试** |
| **时区** | China Standard Time（UTC+08:00） |

### 在这台机器上实际验证过的项目

- 5 个脚本在 PowerShell 5.1 下语法解析通过，源码 **0 个非 ASCII 字节**
- `validate.ps1` 自检全部通过（含 `\u641c\u72d7` → `641C 72D7` 解码断言）
- 带 4 个前导空格的键名能被枚举、匹配并**真实删除**（用 HKCU 测试键验证）
- 中文键名 `百度网盘` 能被正确匹配（用测试键验证后清理）
- 两套注册表视图均被覆盖，32 位视图由子进程合并
- `reg.exe export` 生成 356 字节的 UTF-16LE 备份，可被 `reg import` 使用
- 全部 13 个文档化参数逐个真实调用，均可解析
- `-DryRun` 经确认不产生任何删除
- 原始目标（搜狗 8 项 + 百度 8 项，共 16 个键）清理后残留为 **0**

### 明确未实测的项目

- **PowerShell 7+（`pwsh`）** —— 按已知差异设计过，但从未实际执行。
- **Windows 11**，或除 19045.6466 之外的任何版本。
- **非中文系统区域** —— 纯 ASCII 源码的设计应当使其安全，但未经检验。
