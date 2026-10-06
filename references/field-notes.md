# Field notes: confirmed vendor registrations

Registration points confirmed by direct registry inspection on a live Windows 10 22H2
(19045) machine, in **both** the 32-bit and 64-bit registry views.

Key names are shown exactly as stored, including padding spaces.

## Sogou Input Method (`sgshellext`, `sgshellext2`)

CLSIDs seen: `{85212cfd-77ed-4add-8e24-a0a39e3dbfc3}` (sgshellext),
`{7BCE96FA-77AF-4288-9E16-2388A50EC807}` (sgshellext2), both resolving to
`...\SogouInput\Components\biz_center\<version>\biz_shellext64.dll` (64-bit) and
`biz_shellext.dll` (32-bit).

| Class key | Real key name | Notes |
|---|---|---|
| `*` | `    sgshellext` (4 leading spaces) | |
| `Directory` | `    sgshellext` | |
| `Directory\Background` | `    sgshellext2` | different CLSID from the others |
| `Drive` | `    sgshellext` | |
| `lnkfile` | `    sgshellext` | |

Sogou also registers Internet Explorer `MenuExt` entries
(`HKCU\...\Microsoft\Internet Explorer\MenuExt`), which are unrelated to the Explorer
context menu and were not touched.

## Baidu Netdisk (`YunShellExt`, `YunShellExplorerCommand`)

Baidu registers **two independent mechanisms**. Removing only one has no visible
effect.

**COM handler** — `{6D85624F-305A-491d-8848-C1927AA0D790}` resolving to
`%APPDATA%\baidu\BaiduNetdisk\YunShellExtV164.dll` (64-bit) /
`YunShellExtV1.dll` (32-bit):

| Class key | Real key name |
|---|---|
| `*` | `YunShellExt` (not padded) |
| `Directory` | `YunShellExt` |
| `lnkfile` | `YunShellExt` |

**Static verb** — `ExplorerCommandHandler` = `{499C8928-90D7-411D-8DBD-E35B92529123}`,
resolving to `%APPDATA%\baidu\BaiduNetdisk\sysres\YunShellCommand64.dll`, with the
default value `baidunetdisk` and an empty `NeverDefault` value:

| Class key | Real key name |
|---|---|
| `*` | `YunShellExplorerCommand` |
| `Directory` | `YunShellExplorerCommand` |

## Thunder / Xunlei (`UploadToThunderPan`)

Static verb, under **HKCU** (so no elevation needed), with
`Icon = <ThunderInstallDir>\Program\Thunder.exe,0` and
`command = Thunder.exe -ShellUpload:"%1" -StartType:Explorer`:

| Class key | Real key name |
|---|---|
| `*` | `UploadToThunderPan` |

## Microsoft OneDrive (`FileSyncEx`) — documented, NOT removed

Also uses leading-space padding, which is why a naive "remove OneDrive from the
context menu" registry edit so often appears to work and does nothing.

| Class key | Real key name | DLL |
|---|---|---|
| `*` | `" FileSyncEx"` (1 leading space) | `FileSyncShell64.dll` |
| `Directory` | `" FileSyncEx"` | |
| `Directory\Background` | `" FileSyncEx"` | |
| `lnkfile` | `" FileSyncEx"` | |

## A note on detection

The original detection used the bare substring `Thunder`, which also matches
Windows' own built-in `EnhancedStorageShell`. Use vendor-specific key names instead:
`UploadToThunderPan`, `sgshellext`, `YunShellExt`.
