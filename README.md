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

- **数値リテラル** … 整数・浮動小数（進数・指数・接尾辞・虚数含む）。ただし **値が 0 / 1 の整数は範囲外**（下記）
- **文字列リテラル** … 通常文字列・raw 文字列・テンプレート/f-string 等（wrapper ノードで 1 件として検出）

除外するもの:

- **値が 0 / 1 の整数リテラル**。添字・初期値・増減・番兵など言語の必然として現れ、定数へ切り出しても意味を持たないため検出しない。進数プレフィックス・桁区切り・型接尾辞は許容する（`0` / `1` / `0x00` / `0b1` / `01` / `1L` / `1u` / `1i32` / `1n` はいずれも範囲外）。
  2 以上の整数・小数・指数表記・虚数は意味を持つ値として**検出する**（`2` / `0xFF` / `017` / `0.5` / `1.0` / `1e3` / `1i` は検出）。
- **import / include のモジュール指定子**（`import x from "mod"` / `#include "foo.h"` / `import "fmt"` 等）は必然のため検出しない。
- **真偽値・文字リテラル・null**（`true` / `'a'` / `null` 等）は対象外（数値・文字列のみ）。
- テスト等を検査対象外にしたい場合は、単に `pattern` に含めなければよい（どの `pattern` にもマッチしないファイルは管理対象外＝許可）。

## 設定（config/ai-harness-constants.yml）

```yaml
files:
  - pattern: "src/main/java/**/*.java"
    allow: "src/main/java/com/example/app/constants/*.java"
    same-string: true
  - pattern: "frontend/main/**/*.ts"
    allow: "frontend/main/constants/*.ts"
```

各エントリは `pattern`（対象ソース）と `allow`（ハードコードを許可する定数ファイル）のマップ。`same-string` は省略可（既定 `false`）。

### allow の制約（違反すると設定は使用不可＝フェイルクローズ）

- **単一のみ**（リスト不可）
- **`**` を使用不可**（`*.java` のような形式は可）
- **`pattern` の内側**であること（`allow` にマッチするパスは `pattern` にもマッチする）

### same-string（定数ファイル内の文字列重複を禁止）

`true` にすると、その **`allow` にマッチする定数ファイル群を横断**して文字列リテラルを集計し、同一の文字列が 2 箇所以上にあれば **deny（exit 2）**。同じ値の定数が二重定義されている状態を潰す。

```
constants/Http.java:  CONTENT_TYPE = "application/json"
constants/Api.java:   MEDIA_TYPE   = "application/json"   ← 重複 → deny
```

- **allow 側にだけ効く**検査。`pattern` 側はそもそもリテラルが deny されるため重複を論じる余地がない。
- **同一判定はリテラル表記そのまま**。`"a"` と `'a'` は別物として扱う。
- **空文字列 `""` も対象**（2 箇所以上あれば重複）。
- **数値は対象外**。同じ数値が別意味の定数に現れるのは正当なため（`MAX = 100` と `TIMEOUT = 100`）。
- 省略時は `false`。`true` / `false` 以外の値は設定エラー＝フェイルクローズ。
- hook では、書き込んだファイルが `allow` にマッチしたときに同じ `allow` 群の他ファイルも読んで検査する（走査は `allow` の固定ディレクトリ配下に限定。プロジェクト全体は走査しない）。プロジェクトルート（hook の `cwd`）を特定できない場合は警告してスキップする。

## 判定フロー

対象言語のソースファイルのみが対象（非ソースは常に許可）。

```
1. 設定が使用不可            → deny（フェイルクローズ。エラー内容を提示）
2. いずれかの allow にマッチ  → 許可（ハードコード可の定数ファイル）
                              ただし same-string: true のエントリでは、その allow 群を横断して
                              文字列の重複を検査：重複あり→deny / なし→許可
3. いずれかの pattern にマッチ → AST 解析：リテラルあり→deny / なし→許可
4. どの pattern にもマッチせず → 許可（管理対象外）
```

テスト等を検査対象外にしたい場合は、単に `pattern` に含めなければよい（4 で許可される）。

- **フェイルクローズ**: `files` 未設定／エントリが 1 つも有効でない／エントリに不正（`allow` の `**`・リスト・pattern 外）があると、対象言語のソース書き込みを **全て deny** する。エラー内容は reason に列挙されるので設定を修正する。
- ファイルは PostToolUse 時点でディスク上にあるため、書き込んだ**ファイル全体**を解析する（当該編集箇所だけでなくファイル全体がハードコードフリーであることを求める）。

## 能動スキャン（`ai-harness-main --fire`）

hook は書き込みごとに 1 ファイルを検査する。これに対し `--fire` はプロジェクトの**既存ツリー全体**を一括点検する。`pattern` に合致する全ソースを走査し、`allow` 以外にハードコード値があれば **exit 2**（検出）。判定順は hook と同じ（対応言語 → `allow` は除外 → `pattern` に合致 → AST 解析）。`same-string: true` のエントリについては、加えて `allow` 群の文字列重複も集計してレポートする（どちらか一方でも検出があれば exit 2）。

hook のゲートではないため、exit 2 は書き込みの差し戻しではなく**スキャン結果のレポート**（CI 等で扱えるようコマンドの終了コードへ反映される）。設定が使用不可なら検査対象を決められないため、hook と同じくフェイルクローズで exit 2。

```yaml
fire:
  gitignore: true
  exclude:
    - .git
    - node_modules
```

- `exclude` … 一致するディレクトリを**部分木ごと**枝刈りし、一致するファイルも走査から外す。`pattern` と同じ glob（`**` / `*` / `?`）。フルパスと各 `/` 区切りサフィックスに照合されるため、名前指定（`node_modules`）・パス指定（`.claude/harness`）・glob（`"**/dist"`）のいずれも書ける。
- `gitignore` … `true` で、git が無視する（未追跡かつ ignore の）ファイル／ディレクトリも走査から外す。各階層の `.gitignore`・否定（`!`）・`core.excludesFile`・`.git/info/exclude` を尊重する（git に問い合わせる）。git 未導入・非リポジトリなら警告して無効化し、スキャンは継続。既定は `false`。
- 読めない／解析できないファイルは警告ログを出してスキップする（違反として扱わない）。

## エンジン

[TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet)（tree-sitter の .NET バインディング）を使用。ネイティブ grammar（`tree-sitter-*.dll`）を同梱し、Windows / Linux（x64）で動作する。

## ビルドと配置

```sh
dotnet build ai-harness-constants/ai-harness-constants/ai-harness-constants.csproj -c Release

# 配布物は lib の管理 DLL のみ: プラグイン DLL・TreeSitter.dll（マネージド）を lib/ へ。
# .deps.json は不要（host の ALC が lib 直下を直接プローブして TreeSitter.dll を解決する）。
BIN=ai-harness-constants/ai-harness-constants/bin/Release/net10.0
cp "$BIN/ai-harness-constants.dll"       <配置先>/lib/
cp "$BIN/TreeSitter.dll"                 <配置先>/lib/
# ネイティブ grammar（tree-sitter-*.dll）は**プラグイン側では配置しない**。汎用（どの tree-sitter
# プラグインでも同一）ゆえ host（ai-harness-main）のリリースに runtimes/<rid>/native として同梱され、
# host が起動時にフルパスで事前ロードして解決する。TreeSitter.DotNet はベア名でロードし .deps.json/ALC を
# 通らないため、この事前ロードで解決している。from-source で host を自前配置する場合のみ runtimes/ を
# 実行体隣へ置く（ai-harness-main/docs/build-and-deploy.md 参照）。

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
│   └── ai-harness-constants.yml   検査エントリ・テスト除外・fire の定義（配置元）
└── ai-harness-constants/
    ├── ai-harness-constants.csproj
    ├── ConstantsPlugin.cs         PostToolUse の発火・能動スキャン・判定・reason 生成
    ├── ConstantsConfig.cs         設定の解釈とバリデーション
    ├── LiteralDetector.cs         tree-sitter で AST 解析しリテラルを検出
    ├── DuplicateStringChecker.cs  same-string: allow 群を横断した文字列重複の検査
    ├── FireScanner.cs             能動スキャンの走査（fire.exclude / fire.gitignore）
    └── GlobMatcher.cs             ** 対応の glob 一致（directory-checker と同一）
```
