# SpdPool

Windows 上的**文件级存储池**：把多块独立 NTFS 盘合并成一个盘符（如 `S:`），新文件按剩余空间自动选盘，文件完整落在单块盘上；另带一块 SSD 做热数据分层加速。

底层使用 [WinFsp](https://github.com/winfsp/winfsp)（Windows File System Proxy）用户态文件系统，不修改系统内核、不依赖 Storage Spaces。

## 为什么

Windows 原生没有"简单拼接"（JBOD）的现代方案：

- Storage Spaces 的 Simple 必须条带化（列数固定，小盘写满后大空间无法分配）
- 跨区卷只支持 MBR 动态磁盘，>2 TB 的盘无法参与
- GPT 动态磁盘不支持跨区卷

SpdPool 用文件级方案绕开这些限制：

- 每块盘保持独立 NTFS，单盘故障只丢该盘上的文件
- 文件完整落单盘，不条带、无列数约束
- 容量按剩余空间比例自动分配（mfs 策略）
- SSD 作为热数据层：新文件优先落 SSD，水位超过 85% 后后台把最冷的文件迁到 HDD

## 特性

- ✅ 多盘合并为单盘符（`m1 + m2 + m3 → S:`）
- ✅ 文件级放置（新文件按剩余空间选盘，SSD 优先）
- ✅ 目录跨成员合并（同名目录自动合并枚举）
- ✅ SSD 热数据分层（85%/75% 水位，冷文件后台迁移）
- ✅ 无中心数据库（文件归属由所在成员决定）
- ✅ Windows 服务部署（WinSW），重启自动挂载
- ✅ Avalonia 管理界面（容量、使用率、SSD 标记、下次分配目标）
- ❌ 无冗余（不做镜像/奇偶校验）
- ❌ 不支持 VSS 卷影副本（备份请走文件级复制）

## 架构

```
应用 / SMB / eCloud / 备份软件
              │
              ▼
        S:  (WinFsp 用户态文件系统)
              │
     ┌────────┼────────┐
     ▼        ▼        ▼
   m1 3TB    m2 500GB  m3 240GB SSD
   NTFS      NTFS      NTFS
   C:\SpdPool\mnt\m1 .. \m3   (卷 GUID 作为后端路径)
```

- **目录合并**：`ReadDirectory` 合并所有成员同名目录的条目并去重
- **文件路由**：`Create` 按 mfs 选成员，文件完整写入该成员
- **跨成员操作**：重命名/删除按成员转发；跨成员重命名 v1 拒绝
- **崩溃一致性**：单文件操作由底层 NTFS 保证；迁移先复制后删除

## 项目结构

```
spdpool/
├─ src/
│  ├─ SpdPool.Core/       成员探测、容量、mfs 选盘
│  ├─ SpdPool.Fs/         WinFsp 文件系统宿主（UnionFileSystem、Migrator）
│  ├─ SpdPool.Cli/        spdpool probe / status
│  └─ SpdPool.UI/         Avalonia 11 管理界面
├─ docs/adr-0001-winfsp-go-no-go.md
└─ tools/                 （构建脚本）
```

## 构建

要求：.NET 8 SDK、Windows 10/11 或 Server 2016+、WinFsp 2026 v2.2 已安装。

```powershell
# 还原与构建
& "D:\dotnet-sdk\dotnet.exe" build src\SpdPool.Cli -c Release
& "D:\dotnet-sdk\dotnet.exe" build src\SpdPool.Fs -c Release
& "D:\dotnet-sdk\dotnet.exe" build src\SpdPool.UI -c Release

# 自包含发布
& "D:\dotnet-sdk\dotnet.exe" publish src\SpdPool.Fs -c Release -r win-x64 --self-contained true -o out\fs
& "D:\dotnet-sdk\dotnet.exe" publish src\SpdPool.UI -c Release -r win-x64 --self-contained true -o out\ui
```

## 部署

1. 安装 WinFsp：`msiexec /i winfsp-2.2.26323.msi /qn ADDLOCAL=ALL`
2. 把成员盘格式化为 GPT + NTFS，挂载到 `C:\SpdPool\mnt\m1..3`（无盘符）
3. `spdpool probe` 确认成员在线
4. 启动文件系统：`SpdPool.Fs.exe -m S: --service`
5. 注册为服务：用 WinSW 包装（参考 `deploy/SpdPoolService.xml`）

> **注意**：WinFsp 不能把目录挂载点当作后端根路径（根目录 `Create` 返回 `STATUS_OBJECT_PATH_NOT_FOUND`），本项目自动解析为卷 GUID 路径（`\\?\Volume{...}\`）。

## 已知限制

- 无冗余：任何一块成员盘故障，该盘上的文件丢失（其余文件可读）
- 不支持 VSS：备份软件请使用文件级复制（GoodSync 等）
- 跨成员重命名被拒绝（`STATUS_NOT_SAME_DEVICE`）
- 小文件多时 4 KB 对齐的元数据开销较高

## 许可

GPL-3.0-or-later（与 WinFsp 的 GPLv3 + FLOSS 例外兼容）。
