# ADR-0001: WinFsp 可行性验证（M0）— GO

- 日期：2026-11-21
- 状态：**GO**（继续 WinFsp 文件级路线）
- 决策人：王鸿韬

## 背景

NAS（Datacenter-Wang，Windows Server 2022）需要一个把 3TB HDD + 500GB HDD + 240GB SSD 合并为单盘符 S: 的文件级存储池。块级 WinSpd 方案因无法保证「文件完整落单盘」被否决，改用 WinFsp 用户态文件系统。

## 验证环境

| 项 | 本机 | NAS |
|---|---|---|
| OS | Windows 10 Pro for Workstations 19045 | Windows Server 2022 |
| WinFsp | 2026 v2.2（winfsp-2.2.26323.msi） | 同左 |
| .NET | 8.0.425（D:\dotnet-sdk） | 自包含 win-x64 发布 |
| 绑定 | winfsp.net 2.2.26323 NuGet | 随发布目录（winfsp-msil.dll） |

## 验证结果

| 验证项 | 结果 | 备注 |
|---|---|---|
| WinFsp MSI 静默安装 | ✅ | `msiexec /i ... /qn ADDLOCAL=ALL`，exit 0 |
| WinFsp.Launcher 服务 | ✅ | 安装后自动 Running |
| .NET 8 + winfsp.net 编译 | ✅ | 仅需修正 FileStream 重载差异 |
| 挂载盘符（T:） | ✅ | 本机与 NAS 均成功 |
| 文件创建/写入/读取 | ✅ | 后端目录出现真实文件，内容一致 |
| 目录创建/子目录写入 | ✅ | |
| 64 MB 大文件 + SHA-256 | ✅ | 哈希一致 |
| 重命名 / 删除 | ✅ | |
| 目录枚举 | ✅ | |
| 顺序写性能（256 MB） | ✅ | NAS 上 313 MB/s（后端 D: SSD） |
| 顺序读性能（256 MB） | ✅ | NAS 上 833 MB/s（含缓存效应） |
| VSS 卷影副本 | ❌ | `vssadmin` 找不到卷——WinFsp 挂载盘不支持 VSS |
| 服务方式运行 | 待 M6 | 当前仅控制台模式验证 |
| 重启后自动挂载 | 待 M6 | 需 WinSW + 盘符持久化 |

## 关键实现修正（相对官方 passthrough-dotnet 示例）

1. 官方示例为 .NET Framework 4.5.2；.NET 8 下 `FileStream` 的 `FileSystemRights` 重载不可用，改用 `FileAccess + FileOptions`。
2. 对不存在的路径，`GetSecurityByName`/`Open` 必须**直接返回 `STATUS_OBJECT_NAME_NOT_FOUND`**，不能抛异常——异常转换 fallback 会变成 `0xC00000E9 (UNEXPECTED_IO_ERROR)`，导致写文件失败。
3. `CanDelete` 不应真正删除文件（官方示例只是标记删除），后续 M2 修正。

## 结论

**GO**：WinFsp 2026 v2.2 在 Windows 10 与 Server 2022 上均可正常工作，.NET 8 绑定可用，性能满足 NAS 场景。

## 已知限制与应对

1. **不支持 VSS**：备份必须走文件级复制（GoodSync、eCloud 本身都是文件级，兼容）。
2. **无冗余**：单盘故障只丢该盘文件，其余文件仍可读——这是文件级方案相对块级方案的优点。
3. **WinFsp 许可**：GPLv3 + 商业许可双轨；个人使用无影响，若分发需按 GPLv3 开源或购买商业许可。

## 下一步（M1）

1. 在 NAS 上准备三块成员盘：GPT + NTFS，挂载到 `C:\SpdPool\mnt\m1..3`（无盘符）
2. 实现 `MemberRegistry`（成员探测、健康、容量）
3. 实现 `SpdPool.Cli probe/init`
4. 用 union 骨架把 S: 挂载起来（先单成员 passthrough）

## 验证产物

- 本机与 NAS 上的挂载/读写测试脚本
- NAS 上 `D:\spdpool-m0\`（阶段产物，可保留供 M1 使用）
- 本 ADR
