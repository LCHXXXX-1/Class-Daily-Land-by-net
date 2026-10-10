#!/usr/bin/env node
// ============================================================================
//  Class Daily Land · 插件索引生成器
//  ---------------------------------------------------------------------------
//  扫描 plugins/*.cblplugin，读取每个包内嵌的 plugin.json，算出 sha256 与体积，
//  生成/增量更新 list.json（客户端插件市场读取的聚合索引）。
//
//  用法：
//      node tools/build-index.mjs
//      node tools/build-index.mjs --repo LCHXXXX-1/Class-Daily-Land-by-net --branch plugin
//
//  设计要点：
//  · 零第三方依赖（只读 zip，不装包），GitHub Actions 与本地都能直接跑。
//  · 增量稳定：包内容没变的条目沿用旧的 uploaded_at，避免每次重建都刷出一堆
//    无意义的 diff。
//  · 校验从严：包名 / 版本号 / 入口文件任一不合格就整体失败（退出码 1），
//    宁可让 CI 红一次，也不把坏包写进用户能看到的索引。
// ============================================================================

import fs from "node:fs";
import path from "node:path";
import zlib from "node:zlib";
import { createHash } from "node:crypto";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const PLUGIN_DIR = path.join(ROOT, "plugins");
const INDEX_PATH = path.join(ROOT, "list.json");

// ---------------------------------------------------------------- 参数
const argv = process.argv.slice(2);
const flag = (name, dflt) => {
  const i = argv.indexOf(name);
  return i >= 0 && argv[i + 1] ? argv[i + 1] : dflt;
};

const REPO = flag("--repo", process.env.CDL_MARKET_REPO || "LCHXXXX-1/Class-Daily-Land-by-net");
const BRANCH = flag("--branch", process.env.CDL_MARKET_BRANCH || "plugin");

// 与客户端 RemoteListParser 完全一致的校验口径
const ID_RE = /^[A-Za-z0-9][A-Za-z0-9._-]*$/;
const SEMVER_RE = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/;

// ---------------------------------------------------------------- 极简 ZIP 读取
// 只做到「够用」：找中央目录 → 按需取某个成员 → 存储 / deflate 解压。
// 不支持 zip64、加密、分卷 —— 插件包都是几十 KB 的普通 zip，用不上。
function zipDirectory(buf) {
  const EOCD = 0x06054b50;
  let eocd = -1;
  const floor = Math.max(0, buf.length - 22 - 0xffff);
  for (let i = buf.length - 22; i >= floor; i--) {
    if (buf.readUInt32LE(i) === EOCD) { eocd = i; break; }
  }
  if (eocd < 0) throw new Error("不是有效的 ZIP（找不到 EOCD 记录）");

  const count = buf.readUInt16LE(eocd + 10);
  let p = buf.readUInt32LE(eocd + 16);

  const entries = new Map();
  for (let n = 0; n < count; n++) {
    if (buf.readUInt32LE(p) !== 0x02014b50) throw new Error("中央目录记录签名异常");
    const method = buf.readUInt16LE(p + 10);
    const compSize = buf.readUInt32LE(p + 20);
    const uncompSize = buf.readUInt32LE(p + 24);
    const nameLen = buf.readUInt16LE(p + 28);
    const extraLen = buf.readUInt16LE(p + 30);
    const commentLen = buf.readUInt16LE(p + 32);
    const localOffset = buf.readUInt32LE(p + 42);
    const name = buf.toString("utf8", p + 46, p + 46 + nameLen);

    entries.set(name, { method, compSize, uncompSize, localOffset });
    p += 46 + nameLen + extraLen + commentLen;
  }
  return entries;
}

function zipRead(buf, entry) {
  const { method, compSize, localOffset } = entry;
  if (buf.readUInt32LE(localOffset) !== 0x04034b50) throw new Error("本地文件头签名异常");

  const nameLen = buf.readUInt16LE(localOffset + 26);
  const extraLen = buf.readUInt16LE(localOffset + 28);
  const start = localOffset + 30 + nameLen + extraLen;
  const data = buf.subarray(start, start + compSize);

  if (method === 0) return data;                      // stored
  if (method === 8) return zlib.inflateRawSync(data);  // deflate
  throw new Error(`不支持的压缩方式（method=${method}）`);
}

/** 在 zip 里找 plugin.json —— 允许在根目录，也允许包一层顶层目录。 */
function findManifest(entries) {
  const candidates = [...entries.keys()]
    .filter((name) => !name.endsWith("/") && /(^|\/)plugin\.json$/i.test(name))
    .sort((a, b) => a.split("/").length - b.split("/").length || a.localeCompare(b));

  return candidates[0] || null;
}

// ---------------------------------------------------------------- 工具
const sha256 = (buf) => createHash("sha256").update(buf).digest("hex");

function shortName(packageName) {
  const parts = packageName.split(".");
  return parts[parts.length - 1] || packageName;
}

/** 从 package_name 里猜 author_id（class.<author>.<name> 形态）。 */
function guessAuthorId(packageName) {
  const parts = packageName.split(".");
  return parts.length >= 3 ? parts[parts.length - 2] : "";
}

function readExisting() {
  if (!fs.existsSync(INDEX_PATH)) return new Map();
  try {
    const parsed = JSON.parse(fs.readFileSync(INDEX_PATH, "utf8"));
    const map = new Map();
    for (const item of parsed.plugins || []) {
      if (item && item.package_name) map.set(item.package_name, item);
    }
    return map;
  } catch {
    return new Map();
  }
}

// ---------------------------------------------------------------- 主流程
function main() {
  const problems = [];
  const entriesOut = [];
  const seen = new Set();
  const previous = readExisting();

  if (!fs.existsSync(PLUGIN_DIR)) {
    fs.mkdirSync(PLUGIN_DIR, { recursive: true });
  }

  const packages = fs
    .readdirSync(PLUGIN_DIR)
    .filter((name) => /\.cblplugin$/i.test(name))
    .sort();

  for (const fileName of packages) {
    const packageName = fileName.replace(/\.cblplugin$/i, "");
    const filePath = path.join(PLUGIN_DIR, fileName);

    // --- 包名体检 ---
    if (!ID_RE.test(packageName)) {
      problems.push(`${fileName}：包名不合法（只许字母数字和 . _ -）`);
      continue;
    }
    if (seen.has(packageName)) {
      problems.push(`${fileName}：包名重复`);
      continue;
    }
    seen.add(packageName);

    // --- 读包 ---
    let buf;
    let manifest;
    let entryNames;
    try {
      buf = fs.readFileSync(filePath);
      const entries = zipDirectory(buf);
      entryNames = [...entries.keys()];

      const manifestName = findManifest(entries);
      if (!manifestName) {
        problems.push(`${fileName}：包里找不到 plugin.json`);
        continue;
      }
      manifest = JSON.parse(zipRead(buf, entries.get(manifestName)).toString("utf8"));
    } catch (ex) {
      problems.push(`${fileName}：读取失败（${ex.message}）`);
      continue;
    }

    // --- 清单体检 ---
    const version = String(manifest.version || "").trim();
    if (!version) problems.push(`${fileName}：plugin.json 缺少 version`);
    else if (!SEMVER_RE.test(version)) problems.push(`${fileName}：version 不是 SemVer（x.y.z）：'${version}'`);

    const entry = String(manifest.entry || "").trim();
    if (!entry) {
      problems.push(`${fileName}：plugin.json 缺少 entry（入口程序集，如 main.dll）`);
    } else if (!entryNames.includes(entry)) {
      problems.push(`${fileName}：entry 指向的文件不在包里：'${entry}'`);
    }

    const digest = sha256(buf);
    const old = previous.get(packageName);
    const unchanged = old && old.sha256 === digest;

    entriesOut.push({
      package_name: packageName,
      name: shortName(packageName),
      display_name: String(manifest.name || "").trim() || shortName(packageName),
      author_id: String(manifest.author || "").trim().toLowerCase() || guessAuthorId(packageName),
      author_name: String(manifest.author || "").trim() || guessAuthorId(packageName),
      version,
      api_version: Number.isFinite(manifest.api_version) ? manifest.api_version : 1,
      description: String(manifest.description || "").trim(),
      entry,
      requires: Array.isArray(manifest.requires) ? manifest.requires.map(String) : [],
      file: `plugins/${fileName}`,
      size: buf.length,
      sha256: digest,
      url: `https://github.com/${REPO}/raw/${BRANCH}/plugins/${fileName}`,
      // 内容没变就沿用旧时间戳，避免每次重建都产生假 diff
      uploaded_at: unchanged ? old.uploaded_at : new Date().toISOString(),
    });
  }

  entriesOut.sort((a, b) => a.package_name.localeCompare(b.package_name));

  const index = {
    schema: 1,
    repo: REPO,
    branch: BRANCH,
    count: entriesOut.length,
    plugins: entriesOut,
  };

  if (problems.length > 0) {
    console.error("✗ 索引生成中止，发现以下问题：");
    for (const p of problems) console.error(`   · ${p}`);
    process.exitCode = 1;
    return;
  }

  const json = JSON.stringify(index, null, 2) + "\n";
  const changed = !fs.existsSync(INDEX_PATH) || fs.readFileSync(INDEX_PATH, "utf8") !== json;

  fs.writeFileSync(INDEX_PATH, json, "utf8");

  const verb = changed ? "已更新" : "无变化";
  console.log(`✓ list.json ${verb} · ${entriesOut.length} 个插件（来源 ${REPO}@${BRANCH}）`);
  for (const e of entriesOut) {
    console.log(`   · ${e.package_name} v${e.version}  ${e.size}B  ${e.sha256.slice(0, 12)}…`);
  }
}

main();
