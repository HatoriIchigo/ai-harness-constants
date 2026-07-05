# ai-harness-constants

> ハードコード値（数値・文字列リテラル）を、許可した定数ファイル以外に書かせない ai-harness プラグイン。

書き込み系ツール（`Write` / `Edit` / `MultiEdit`）の `PostToolUse` で発火し、書き込んだソースファイルを **tree-sitter で AST 解析**する。設定 `allow` で許可した定数ファイル以外のソースに数値・文字列リテラルがあれば **deny（exit 2）** する。

PostToolUse のため書き込み自体は止められない。deny 時は検出したリテラル（行番号・種別・テキスト）を reason で返し、Claude がそれらを許可された定数ファイルへ切り出して参照へ置き換える。

## 対象言語

拡張子で判定する。数値・文字列リテラルのノード型は言語ごとに実測して定義。

| 言語 | 拡張子 |
|---|---|
| C | `.c` `.h` |
| C++ | `.cpp` `.cc` `.cxx` `.hpp` `.hh` `.hxx` |
| Java | `.java` |
| Python | `.py` `.pyi` |
| Rust | `.rs` |
| Go | `.go` |
| TypeScript | `.ts` `.mts` `.cts` `.tsx` |

上記以外の拡張子（`.md` 等）は対象外＝常に許可。

## 検出対象

- **数値リテラル** … 整数・浮動小数（進数・指数・接尾辞・虚数含む）
- **文字列リテラル** … 通常文字列・raw 文字列・テンプレート/f-string 等（wrapper ノードで 1 件として検出）

除外するもの:

- **import / include のモジュール指定子**（`import x from "mod"` / `#include "foo.h"` / `import "fmt"` 等）は必然のため検出しない。
- **真偽値・文字リテラル・null**（`true` / `'a'` / `null` 等）は対象外（数値・文字列のみ）。
- テスト等を検査対象外にしたい場合は、単に `pattern` に含めなければよい（どの `pattern` にもマッチしないファイルは管理対象外＝許可）。

## 設定（config/ai-harness-constants.yml）

```yaml
files:
  - pattern: "src/main/java/**/*.java"
    allow: "src/main/java/com/example/app/constants/*.java"
  - pattern: "frontend/main/**/*.ts"
    allow: "frontend/main/constants/*.ts"
```

各エントリは `pattern`（対象ソース）と `allow`（ハードコードを許可する定数ファイル）のマップ。

### allow の制約（違反すると設定は使用不可＝フェイルクローズ）

- **単一のみ**（リスト不可）
- **`**` を使用不可**（`*.java` のような形式は可）
- **`pattern` の内側**であること（`allow` にマッチするパスは `pattern` にもマッチする）

## 判定フロー

対象言語のソースファイルのみが対象（非ソースは常に許可）。

```
1. 設定が使用不可            → deny（フェイルクローズ。エラー内容を提示）
2. いずれかの allow にマッチ  → 許可（ハードコード可の定数ファイル）
3. いずれかの pattern にマッチ → AST 解析：リテラルあり→deny / なし→許可
4. どの pattern にもマッチせず → 許可（管理対象外）
```

テスト等を検査対象外にしたい場合は、単に `pattern` に含めなければよい（4 で許可される）。

- **フェイルクローズ**: `files` 未設定／エントリが 1 つも有効でない／エントリに不正（`allow` の `**`・リスト・pattern 外）があると、対象言語のソース書き込みを **全て deny** する。エラー内容は reason に列挙されるので設定を修正する。
- ファイルは PostToolUse 時点でディスク上にあるため、書き込んだ**ファイル全体**を解析する（当該編集箇所だけでなくファイル全体がハードコードフリーであることを求める）。

## エンジン

[TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet)（tree-sitter の .NET バインディング）を使用。ネイティブ grammar（`tree-sitter-*.dll`）を同梱し、Windows / Linux（x64）で動作する。

## ビルドと配置

```sh
dotnet build ai-harness-constants/ai-harness-constants/ai-harness-constants.csproj -c Release

# プラグイン DLL・TreeSitter.dll（マネージド）と .deps.json は lib/ へ。
BIN=ai-harness-constants/ai-harness-constants/bin/Release/net10.0
cp "$BIN/ai-harness-constants.dll"       <配置先>/lib/
cp "$BIN/ai-harness-constants.deps.json" <配置先>/lib/
cp "$BIN/TreeSitter.dll"                 <配置先>/lib/
# ネイティブ grammar（tree-sitter-*.dll）は**実行体（ai-harness-main）の隣**の runtimes/ へ。
# TreeSitter.DotNet は grammar を「ベア名」で NativeLibrary.Load するため .deps.json/ALC では解決できず、
# host が起動時に runtimes/<rid>/native をフルパスで事前ロードして解決する（lib/ 側は探索されない）。
cp -r "$BIN/runtimes"                    <配置先>/runtimes

cp ai-harness-constants/config/ai-harness-constants.yml  <プロジェクト>/.claude/harness/config/

# common.yml の tools で有効化してから
<配置先>/ai-harness-main --restart
```

`baselib.dll` は host が共有ロードするため `lib/` に置かない（プラグイン出力にも含まれない）。`common.yml` の `tools` に `- ai-harness-constants: true` を追加して有効化する。詳細は `ai-harness-main/docs/plugin-development.md` を参照。

> 補足: プラグイン出力には baselib の推移依存 `YamlDotNet.dll` も含まれるが、本プラグインは使用しないため `lib/` に置いても置かなくてもよい（置いても host 側の YamlDotNet と競合しない）。

## 構成

```
ai-harness-constants/
├── README.md
├── config/
│   └── ai-harness-constants.yml   検査エントリ・テスト除外の定義（配置元）
└── ai-harness-constants/
    ├── ai-harness-constants.csproj
    ├── ConstantsPlugin.cs         PostToolUse の発火・判定・reason 生成
    ├── ConstantsConfig.cs         設定の解釈とバリデーション
    ├── LiteralDetector.cs         tree-sitter で AST 解析しリテラルを検出
    └── GlobMatcher.cs             ** 対応の glob 一致（directory-checker と同一）
```
