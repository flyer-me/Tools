# HS2 Missing Mods Downloader / HS2 缺失模组补全工具

基于 [ModResolver](https://github.com/XrmSeries/ModResolver/releases) 扫描报告，从 BetterRepack CDN 下载缺失的 `.zipmod` 模组文件。

---

## 快速开始 / Quick Start

### 1. 安装 / Setup

运行 **`setup.bat`** — 自动下载 ModResolver 便携版并安装本工具。

或手动操作：
- 从 <https://github.com/XrmSeries/ModResolver/releases> 下载 `ModResolver.V*.Portable.rar`
- 解压到任意目录
- 将 `dist/` 下的 `.exe` 和 `.bat` 复制到 ModResolver 目录

### 2. 扫描 / Scan

运行 `ModResolver.exe` → 点击 **Scan** → 等待完成 → 关闭

### 3. 下载 / Download

双击 **`download_missing_mods.bat`** → 选择下载模式：

| 选项 | 说明 |
|---|---|
| `[1] Essential` | 仅角色卡必需的服装/发型/身体模组 |
| `[2] All` | 全部（含工作室道具和地图） |
| `[3] Dry-run` | 预览模式，不实际下载 |
| `[4] Custom` | 自定义参数 |

或命令行：
```bash
download_missing_mods.exe                  # 默认：必需模组
download_missing_mods.exe --all            # 全部模组
download_missing_mods.exe --aria2c         # aria2c 加速
download_missing_mods.exe --dry-run        # 仅预览
download_missing_mods.exe --help           # 完整帮助
```

---

## 分类说明 / Category Groups

| 分类 | 默认 | 说明 |
|---|---|---|
| Sideloader Modpack | ✓ 下载 | 服装、发型、配饰、身体 |
| Bleeding Edge | ✓ 下载 | 实验性角色模组 |
| Exclusive HS2 / AIS | ✓ 下载 | HS2/AIS 专属角色模组 |
| Studio | — | 工作室道具、特效 |
| Maps | — | 工作室/游戏地图 |

使用 `--all` 下载全部类别。

---

## 功能 / Features

- **断点续传** — 中断后重跑自动跳过已下载文件
- **优先级排序** — 被最多角色卡引用的模组优先下载
- **aria2c 加速** — 自动检测，`--aria2c` 启用（需 `winget install aria2.aria2`）
- **0 依赖** — exe 独立运行，无需安装 Python
- **限速保护** — 自适应退避重试，避免被封 IP

---

## 开发 / Development

```
hs2-missing-mods-downloader/
├── src/
│   └── download_missing_mods.py   # 源码 (Python 3.8+, stdlib only)
├── dist/
│   ├── download_missing_mods.exe  # 构建产物，不随仓库分发
│   └── download_missing_mods.bat  # 启动器 (优先 exe，回退 py)
├── setup.bat                      # 一键安装脚本
├── build_exe.bat                  # 从源码重新构建 exe
└── README.md
```

**重新构建 exe**：安装 Python + `pip install pyinstaller`，运行 `build_exe.bat`。

---

## 风险提示 / Risk Notice

- `setup.bat` 会从 GitHub 下载 ModResolver 便携包并解压到当前目录。来源是第三方仓库，版本与内容不由本仓库控制。
- 工具会把缺失的 `.zipmod` 从 BetterRepack CDN 直接写入 ModResolver 所在目录；已存在的文件不会被删除或覆盖。
- 下载内容不做签名校验。`.zipmod` 是可被游戏加载的压缩包，请只在可信网络环境下使用。
- 不修改注册表、不要求管理员权限。

---

## 许可 / License

MIT — 自由使用、修改、分发。
