# dsh-chemdraw

把 ChemDraw 的 `.cdx` 文件接进 DeepSeek Harness：读结构、读立体几何、读标注，并且能**安全地写回**。共 12 个工具。

```
ChemDraw（本机安装并已激活，隐藏窗口）
        │  COM 自动化
        ▼
ChemBridge.exe   ──  C# 桥接：CDX ⇄ CDXML ⇄ SDF，几何计算、校验、备份
        │  ChemScript .NET（化学引擎：SMILES / InChI / IUPAC 名 / 3D）
        ▼
dsh-chemdraw（index.js）──  12 个工具
```

> English: [README.en.md](README.en.md)

## 环境要求

| 项目 | 说明 |
|---|---|
| Windows | 桥接是 .NET Framework 控制台程序 |
| ChemDraw，已安装并激活 | 每次调用会启动一个隐藏的 ChemDraw 进程（冷启动约 1 秒） |
| `CambridgeSoft.ChemScript.dll` | 随 ChemDraw 提供，**本仓库不分发**——请从你自己的安装里复制到 `bin/` 下 |
| `CHEMDRAW_HOME` 环境变量 | 指向 ChemDraw 安装目录（该目录下应有 `ChemScript` 和 `ChemDraw` 两个子目录） |
| .NET Framework 4.x + `csc.exe` | 只有想重新编译桥接时才需要 |

装好插件后，把你的 `CambridgeSoft.ChemScript.dll` 放进 `bin/`，并设置：

```powershell
setx CHEMDRAW_HOME "C:\Program Files\Revvity\ChemDraw"   # 换成你的实际安装路径
```

运行期缓存（CDXML、整页 PNG、每结构缩略图）会写到 `var/`，该目录已在 `.gitignore` 里。

重新编译桥接（用系统自带的 `csc.exe`，不需要联网）：

```powershell
powershell -ExecutionPolicy Bypass -File bridge/build.ps1
```

## 工具

| 工具 | 作用 | 写文件 |
|---|---|---|
| `chemdraw_inspect` | 结构清单（SMILES/InChI/分子式/MW/IUPAC 名）+ 2D 几何 + 立体校验 + 标注 + 反应箭头 + 缩略图 | 否 |
| `chemdraw_find` | 按 label / id / 分子式 / SMILES 片段查结构，返回 id 与缩略图（用于消歧同名 label） | 否 |
| `chemdraw_render` | 整页 PNG + 每结构裁剪图 | 否 |
| `chemdraw_3d` | MM2 构象 + 平面性 RMSD / 最大扭转 / 分子跨度 | 否 |
| `chemdraw_add` | 按 SMILES 加结构（自动避让布局、可带标注） | 是（默认 dry-run） |
| `chemdraw_delete` | 按 id 或 label 删结构，连同它自己的标注；同名 label 用 `nth` 选第几个 | 是（默认 dry-run） |
| `chemdraw_replace` | 原位替换结构，并重算 Chemical Formula / Molecular Weight 标注 | 是（默认 dry-run） |
| `chemdraw_annotate` | 加文字标注（不会把 "B1" 误读成硼） | 是（默认 dry-run） |
| `chemdraw_move` | 移动结构及其标注 | 是（默认 dry-run） |
| `chemdraw_add_arrow` | 加反应箭头 | 是（默认 dry-run） |
| `chemdraw_edit` | 批量事务：add / delete / replace / annotate / move / arrow 一次提交 | 是（默认 dry-run） |
| `chemdraw_doctor` | 检查安装、授权、引擎自检 | 否 |

写回后每个变更都会带 `id_after_save`（按坐标回填的存盘 id）和 `id_match`（`position` / `inchi` / `inchi_ambiguous`）。ChemDraw 存盘会重排片段，所以**后续操作请重新 inspect，或直接用 `label` 定位**。

## 写入安全模型

1. **默认 dry-run**：`dry_run: false` 才落盘。
2. **先验证再落盘**：把改动写成临时 `.cdx`，重新读回来逐结构比对 InChI 多重集；不等就放弃，原文件不动。
3. **自动备份**：`xxx.cdx.bak.<时间戳>`，只保留最近 3 个。
4. **原子替换**：同目录临时文件 + `File.Replace`。
5. **不碰别人的文字**：只更新"属于该结构"的属性标注（按位置配对、一对一）。

## 已知限制

1. **写入范围**：DSH 的沙箱默认只允许写会话工作区。改工作区以外的 `.cdx` 会被拒绝——把文件复制进工作区，或用全权限会话。
2. **id 会变**：任何一次写回后 ChemDraw 会重排片段，`chemdraw_inspect` 的编号可能改变；写完请重新 inspect，按 `label` 删除更稳。
3. **没有 InChIKey**：ChemScript 不提供，也不自己实现哈希（宁可缺，也不给错的）。检索时用 SMILES/InChI（PubChem 两者都接受）。
4. **缩写基团**：图里无法识别的缩写会被当成元素，`formula_unexpected` 警告会点出来，需要人工确认。
5. **属性标注重算有边界**：只改 `Chemical Formula:` / `Molecular Weight:` 后面的值；用户手写的化合物名称那一行不会被改写。
6. **ChemDraw 必须已安装并激活**：读取要启动一个隐藏的 ChemDraw 进程；若授权失效可能弹模态框，工具会超时报错。

## 安装

```sh
dsh plugin add github:CGH999/dsh-chemdraw
```

或在 **插件 → 添加插件** 里填仓库地址。

## 许可

MIT。ChemDraw 与 ChemScript 是 CambridgeSoft / Revvity 的商业产品，**不包含在本仓库中**。
