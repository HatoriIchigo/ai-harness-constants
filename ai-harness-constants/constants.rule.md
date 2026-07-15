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

設定が使用不可なら deny（フェイルクローズ）。PostToolUse ゆえ書き込みは止められず、deny で差し戻して
リテラルを許可された定数ファイルへ移動させる。

## 設定ファイル

`.claude/harness/config/ai-harness-constants.yml`

```yaml
files:
  - pattern: "src/main/java/**/*.java"
    allow: "src/main/java/com/example/app/constants/*.java"
    same-string: true
  - pattern: "frontend/main/**/*.ts"
    allow: "frontend/main/constants/*.ts"
```
