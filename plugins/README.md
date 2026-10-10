# plugins/ —— 插件包存放目录

把编译好的插件打包成 `<package_name>.cblplugin` 放进本目录，推送后 GitHub Actions
会自动重建上一级的 `list.json`。

- 文件名（去掉 `.cblplugin`）就是市场里的插件 id，命名规则：`class.<作者>.<插件名>`
- 包内必须有一份 `plugin.json`，且其中 `entry` 指向的文件要真实存在于包里
- 具体字段与打包步骤见上级 `README.md`
