---
paths:
  - .claude/harness/config/ai-harness-constants.yml
---

## 概要

ai-harness-constants は書き込み系ツール（Write / Edit / MultiEdit）の `PostToolUse` で発火し、
書き込んだソースを tree-sitter で AST 解析して、許可した定数ファイル（`allow`）以外の
ハードコード値（数値・文字列リテラル）を deny（exit 2）する。C/C++/Java/Python/Rust/Go/TypeScript 対応。
値が 0 / 1 の整数リテラルは範囲外（検出しない）。`.md` 等の非ソースは対象外＝許可。

- `pattern` … ハードコード値を許可しない対象ソースの glob（`**` / `*` / `?`）
- `allow` … ハードコード値を許可する定数ファイルの glob（単一・`**` 不可・`pattern` の内側であること）
- `same-string` … `true` で `allow` 群を横断し、同一文字列リテラルが 2 箇所以上にあれば deny（数値は対象外・既定 `false`）

検査を緩める設定（エントリ単位。省略時は従来どおり数値・文字列を全て検出）:

- `numbers` / `strings` … 種別ごとに検出の有無（既定 `true`）
- `ignore-context` … 検出しない文脈のリスト。`annotation`（アノテーション／デコレータ／属性の引数）／
  `throw`（例外送出・エラー生成の引数）／`log`（ログ出力・標準出力の引数）
- `min-occurrences` … 同一値がファイル内で N 回以上のときだけ違反（既定 `1` ＝全て違反）

設定が使用不可なら deny（フェイルクローズ）。PostToolUse ゆえ書き込みは止められず、deny で差し戻して
リテラルを許可された定数ファイルへ移動させる。

## 設定ファイル

`.claude/harness/config/ai-harness-constants.yml`

```yaml
files:
  - pattern: "src/main/java/**/*.java"
    allow: "src/main/java/com/example/app/constants/*.java"
    same-string: true
    ignore-context:      # ログ文言・例外メッセージ・アノテーション引数は検出しない
      - annotation
      - throw
      - log
  - pattern: "frontend/main/**/*.ts"
    allow: "frontend/main/constants/*.ts"
    strings: false       # 数値のマジックナンバーだけ禁止
    min-occurrences: 2   # 同じ値が 2 箇所以上に散らばっているときだけ違反
```

ファイルが複数の `pattern` に合致する場合は**先頭優先**（エントリごとに緩和設定が異なるため）。
