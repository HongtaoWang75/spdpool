# Changelog

## v1.0.0 (2026-11-22)

首个可用版本：WinFsp 文件级存储池，在 Windows Server 2022 上以服务方式运行，合并 3 块 NTFS 成员盘为一个 S: 盘符。

### 功能

- 文件级放置：新文件按剩余空间百分比选择成员（mfs），文件完整落单盘
- 目录跨成员合并枚举
- SSD 热数据分层：低于 85% 自动触发、手动「均衡」按钮；SSD 剩余低于 15% 时新文件回退 HDD
- 实时状态：读写速率、IO 队列深度、迁移任务（`C:\ProgramData\SpdPool\stats.json`）
- Avalonia 管理界面：磁盘卡片（型号/健康度/使用率）、300ms 刷新、HarmonyOS Sans 字体
- 成员命名为 HDD0 / HDD1 / SSD0（挂载点 `C:\SpdPool\mnt\HDD0...`）
- Windows 服务部署（WinSW + SCM 恢复策略，任何退出 5 秒内重启）
- 心跳日志 `heartbeat.log`、崩溃日志 `crash.log`

### 修复（开发过程中的真实负载迭代）

- 目录枚举返回 `.` 导致递归删除 `dir\dir` 死循环（0x80004005）
- marker 找不到时从头枚举导致死循环
- 目录删除同步化、只作用于所属成员
- SMB/UNC 访问返回 `error 267`：条目 FileId 为 0、卷序列号 0000-0000
- Open 失败时 FileStream 句柄泄漏
- 服务 `Wait()` 意外返回导致静默退出（改为循环等待）
- 高并发读写每次分配 byte[]（改用 ArrayPool）
- SSD 被无水位写入灌到 94%（增加 15% 剩余保护）

### 已知限制

- 无冗余：任一成员盘故障，该盘上的文件不可读（其余文件正常）
- 不支持 VSS 卷影副本（备份使用文件级复制，如 GoodSync）
- 跨成员重命名返回 `NOT_SAME_DEVICE`
- 温度显示已移除（三块盘均不上报 Windows 温度 IOCTL）
- 单个超大文件（约 50 GB）通过资源管理器 SMB 复制可能报 `0x8007045D`，建议用 GoodSync
