# 自定义语言 / Custom languages

此目录位于 Studio 软件目录中。内置 `zh-CN.json`（简体中文）和 `en-US.json`（English）。

1. 修改已有 JSON 文件中的右侧译文，或复制 `en-US.json` 并改名，例如 `de-DE.json`。
2. 文件名只能使用英文字母、数字、`-` 和 `_`。修改 `$name` 的值，作为设置中的语言名称。
3. 保留左侧词条键名；它们是程序查找译文的依据。保留 `{0}`、`{1:N0}` 等占位符，可以调整其顺序，不能删除、增加或更改编号。`\n` 表示换行。
4. 文件保存为 UTF-8 JSON。在 Studio 的“设置 → 界面语言”中选择，即时生效并自动保存。编辑当前语言文件后可切换到其他语言再切回来以重新读取，无需重启。重新激活设置窗口会刷新语言列表。

缺少的词条会使用内置英文；简体中文缺词时使用原中文。格式错误的自定义语言文件不会出现在列表中。更新程序时请保留已有语言文件，不要覆盖用户的改动；新词条有内置回退，无须替换整份文件。

可选方块名词条：`"block.minecraft:stone": "自定义石头名称"`。方块 ID、文件路径、用户内容和底层诊断数据不作为界面语言翻译。

## English

Edit the values in an existing file, or copy `en-US.json` to a new language ID such as `de-DE.json`. Set `$name` to its display name. File names may contain ASCII letters, digits, hyphens and underscores only.

Keep the original keys and all format placeholders (`{0}`, `{1:N0}`, etc.). Save valid UTF-8 JSON, select your language in Settings; it applies and saves immediately. To reload an edited file, switch to another language and back. Missing translations fall back to built-in English (Chinese for `zh-CN`). Invalid custom files are skipped.

An optional `block.minecraft:stone` entry overrides a block's display name. Keep existing language files when updating Studio; do not overwrite user translations. World contents, paths, block IDs and underlying diagnostic data are not translated.
