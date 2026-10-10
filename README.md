# 插件市场索引分支（`plugin`）

本分支是 **Class Daily Land · .NET 版** 插件市场的唯一数据源。
客户端「设置 → 插件 → 插件市场」里的列表，就是从这里读的。

> 与 Python 版的关系：Python 版市场挂在 `Class-Daily-Land` 仓库的 `plugins` 分支，
> 分发的是 `main.py`；本分支分发的是 **.NET 程序集**（`main.dll`）。两者互不通用，
> 因此 .NET 版单独用本仓库的本分支，不再读取旧的官方 / Gitee 源。

---

## 一、分支结构

```
plugin/
├── list.json                     # 聚合索引（自动生成，勿手改）
├── plugins/
│   ├── .gitkeep
│   ├── README.md
│   └── class.<作者>.<插件名>.cblplugin   # 插件包本体（你上传的就是这个）
├── tools/
│   └── build-index.mjs           # 索引生成脚本（本地 / CI 共用）
└── .github/workflows/
    └── build-index.yml           # 推包后自动重建 list.json
```

---

## 二、发布一个插件（三步）

### 1. 打包

插件本体是 .NET 程序集，目录形态与运行时一致：

```
MyClock/
├── plugin.json     # 清单
└── main.dll        # 入口程序集（文件名任意，由 plugin.json 的 entry 指定）
```

`plugin.json` 字段：

| 字段 | 必填 | 说明 |
|---|---|---|
| `name` | 否 | 展示名，缺省时用包名末段 |
| `entry` | **是** | 入口程序集文件名，如 `main.dll`；必须真实存在于包内 |
| `type` | 否 | 实现 `IPlugin` 的完整类型名，留空则宿主自动扫描 |
| `version` | **是** | 语义化版本 `x.y.z`（可带 `-beta.1` 之类的预发布后缀） |
| `api_version` | 否 | 插件 API 版本，缺省 1 |
| `requires` | 否 | 依赖的第三方包名数组 |
| `author` | 否 | 作者 |
| `description` | 否 | 一句话说明 |

把 `MyClock/` 里的内容（**注意：不是外层目录**，`plugin.json` 要在压缩包根）压成 zip，
改后缀为 `.cblplugin`。文件名即插件 id，规范是 `class.<作者>.<插件名>`，例如
`class.hexwisp72.weather.cblplugin`。

### 2. 上传

把 `.cblplugin` 放进 `plugins/`，提交推送即可：

```bash
git add plugins/class.hexwisp72.weather.cblplugin
git commit -m "feat: 新增天气插件"
git push
```

### 3. 自动生成索引

推送后 `.github/workflows/build-index.yml` 会自动跑 `tools/build-index.mjs`，
扫描全部插件包、算出 sha256 / 体积 / 元数据，重建 `list.json` 并提交回本分支。
**你不需要手动改 `list.json`。**

> 想本地先看一眼？在仓库根执行 `node tools/build-index.mjs` 即可。
> 脚本零依赖，Node 18+ 直接跑。

---

## 三、索引格式（`list.json`）

```jsonc
{
  "schema": 1,
  "repo": "LCHXXXX-1/Class-Daily-Land-by-net",
  "branch": "plugin",
  "count": 1,
  "plugins": [
    {
      "package_name": "class.hexwisp72.weather",   // 唯一 id = 文件名
      "name": "weather",                            // 短名
      "display_name": "天气",                        // 展示名
      "author_id": "hexwisp72",
      "author_name": "hexwisp72",
      "version": "1.0.0",
      "api_version": 1,
      "description": "副岛动画天气…",
      "entry": "main.dll",
      "requires": [],
      "file": "plugins/class.hexwisp72.weather.cblplugin",
      "size": 7319,
      "sha256": "318e5ebb…",                        // 包体哈希，客户端下载后校验
      "url": "https://github.com/LCHXXXX-1/Class-Daily-Land-by-net/raw/plugin/plugins/class.hexwisp72.weather.cblplugin",
      "uploaded_at": "2026-09-27T08:00:00.000Z"
    }
  ]
}
```

客户端只用 `plugins[]` 里的这几个字段：`package_name`、`display_name`（缺失时退回
`name`）、`author_name`、`version`、`description`、`file`、`sha256`、`url`。
其余（`api_version`、`requires`、`size`、`uploaded_at`）是给未来扩展和人工排查用的。

**校验口径**（`tools/build-index.mjs` 与客户端一致）：

- `package_name`：字母数字开头，只含 `字母 数字 . _ -`
- `version`：必须是 SemVer `x.y.z`
- `sha256`：64 位十六进制
- `url`：必须是 `https://`
- 任一不合格 → 索引生成**整体失败**（CI 变红），坏包不会进入用户看到的列表

---

## 四、客户端如何消费（安装 / 更新链路）

### 拉索引

客户端依次尝试三个镜像，任一成功即用：

1. `https://cdn.jsdelivr.net/gh/LCHXXXX-1/Class-Daily-Land-by-net@plugin/list.json` ← 首选
2. `https://raw.githubusercontent.com/...`
3. `https://github.com/.../raw/plugin/list.json`

> 为什么 jsDelivr 排第一？国内直连 `raw.githubusercontent.com` 常年不通（实测 20s 超时），
> 排第一会每次白等超时；jsDelivr 直连约 2s。三个都失败时退回**本地缓存**
> （上次同步成功的那份），所以断网也能看到上次的列表。

### 安装

```
下载 → 校验 sha256 → 解压到临时目录 → 备份旧版本 → 原子换入 → 写元数据
```

- **下载**：`url` 里的 GitHub 直链会被展开成候选镜像，jsDelivr 优先、原址兜底，
  逐个试到 sha256 对上为止（防止镜像缓存了旧文件）
- **校验**：sha256 与索引记录不一致直接判失败，绝不把损坏的包装进去
- **原子**：先解压到临时目录，成功后把旧目录改名为 `.bak`、新目录就位；
  任何一步失败都回滚，用户不会处于「装了一半」的状态

### 更新

- 客户端把索引里的 `version` 与本地 `plugin.json` 的 `version` 做 **SemVer 比较**
  （正式版 > 同号预发布版；数字段排在字母段前），更大即判定「可更新」
- 「更新」与「安装」走同一条链路；「全部更新」批量处理所有「有新版本且未禁用」的插件
- 也可以随启动或手动「同步索引」触发

### 状态

列表每条会显示：未安装 / 已安装 / 可更新 / 已禁用 / 加载失败 / 索引异常。
卸载由客户端删除插件目录完成，与索引无关。

---

## 五、约定与注意

- **不要手改 `list.json`**：它由脚本生成，手改会在下次 push 时被覆盖。
- **包名一旦发布就别改**：`package_name` 是客户端的身份标识，改名等于换了个插件，
  老用户那边的「已安装」会失效、无法识别为更新。
- **升级就改 `plugin.json` 里的 `version`**，重新打包同名 `.cblplugin` 覆盖上传即可；
  内容一变 sha256 就变，客户端会认成新版本。
- **仓库体积**：包以历史版本形式累积。单个包建议控制在几百 KB 以内；
  若将来包体变大，可考虑改走 Releases 附件（客户端已兼容 `url` 指向任意 https 地址）。
- 旧的 `plugins` 分支（Python 版）与本分支**互不影响**，不要往本分支放 `.py` 插件。

---

*源项目采用 MIT 许可证。*
