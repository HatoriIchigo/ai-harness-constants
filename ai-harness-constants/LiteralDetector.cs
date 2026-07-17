using TreeSitter;

namespace ai_harness_constants;

/// <summary>
/// 検出したハードコードリテラル 1 件。<see cref="Text"/> は表示用に 1 行へ畳んで切り詰めた元テキスト、
/// <see cref="Raw"/> は AST 上の元テキストそのもの（同一判定に使う）。
/// </summary>
public readonly record struct Literal(string Kind, int Line, string Text, string Raw);

/// <summary>
/// 検出しない文脈（設定 <c>ignore-context</c>）。定数へ切り出しても意味を持ちにくい位置の値を見逃す。
/// </summary>
[Flags]
public enum IgnoreContext
{
    /// <summary>除外なし（既定）。</summary>
    None = 0,

    /// <summary>アノテーション／デコレータ／属性の引数（<c>@Column(name = "id")</c> 等）。</summary>
    Annotation = 1,

    /// <summary>例外送出・エラー生成の引数（<c>throw new X("msg")</c> / <c>errors.New("msg")</c> 等）。</summary>
    Throw = 2,

    /// <summary>ログ出力・標準出力の引数（<c>logger.info("msg")</c> / <c>console.log("msg")</c> 等）。</summary>
    Log = 4,
}

/// <summary>
/// 検出の絞り込み。<see cref="Numbers"/> / <see cref="Strings"/> で種別ごとに検出の有無を、
/// <see cref="Ignore"/> で検出しない文脈を指定する。
/// </summary>
public readonly record struct DetectOptions(bool Numbers, bool Strings, IgnoreContext Ignore)
{
    /// <summary>絞り込み無し（数値・文字列を全て検出）。エントリ設定に依らない検出に使う。</summary>
    public static DetectOptions All => new(true, true, IgnoreContext.None);
}

/// <summary>
/// tree-sitter（TreeSitter.DotNet）で対象言語のソースを AST 解析し、数値・文字列リテラルを検出する。
/// 検出対象のノード型は言語ごとに実測して定義（<see cref="NumberTypes"/> / <see cref="StringTypes"/>）。
/// 文字列は wrapper ノード（<c>string_literal</c> 等）で検出し、そこから下へは降りない。
/// import/include のモジュール指定子の文字列は誤検知を避けるため除外する。
/// 値が 0 / 1 の整数リテラルも除外する（<see cref="IsExemptNumber"/>）。
/// </summary>
public static class LiteralDetector
{
    /// <summary>拡張子 → tree-sitter の言語 id。未対応拡張子は取得不可。</summary>
    private static readonly Dictionary<string, string> ExtToLang = new(StringComparer.OrdinalIgnoreCase)
    {
        [".c"] = "c",
        [".h"] = "c",
        [".cpp"] = "cpp",
        [".cc"] = "cpp",
        [".cxx"] = "cpp",
        [".c++"] = "cpp",
        [".hpp"] = "cpp",
        [".hh"] = "cpp",
        [".hxx"] = "cpp",
        [".java"] = "java",
        [".py"] = "python",
        [".pyi"] = "python",
        [".rs"] = "rust",
        [".go"] = "go",
        [".ts"] = "typescript",
        [".mts"] = "typescript",
        [".cts"] = "typescript",
        [".tsx"] = "tsx",
    };

    /// <summary>言語 id → 数値リテラルのノード型集合。</summary>
    private static readonly Dictionary<string, HashSet<string>> NumberTypes = new(StringComparer.Ordinal)
    {
        ["c"] = new(StringComparer.Ordinal) { "number_literal" },
        ["cpp"] = new(StringComparer.Ordinal) { "number_literal" },
        ["java"] = new(StringComparer.Ordinal)
        {
            "decimal_integer_literal", "hex_integer_literal", "octal_integer_literal",
            "binary_integer_literal", "decimal_floating_point_literal", "hex_floating_point_literal",
        },
        ["python"] = new(StringComparer.Ordinal) { "integer", "float" },
        ["rust"] = new(StringComparer.Ordinal) { "integer_literal", "float_literal" },
        ["go"] = new(StringComparer.Ordinal) { "int_literal", "float_literal", "imaginary_literal" },
        ["typescript"] = new(StringComparer.Ordinal) { "number" },
        ["tsx"] = new(StringComparer.Ordinal) { "number" },
    };

    /// <summary>言語 id → 文字列リテラルの wrapper ノード型集合（このノードで検出し、配下へは降りない）。</summary>
    private static readonly Dictionary<string, HashSet<string>> StringTypes = new(StringComparer.Ordinal)
    {
        ["c"] = new(StringComparer.Ordinal) { "string_literal" },
        ["cpp"] = new(StringComparer.Ordinal) { "string_literal", "raw_string_literal", "concatenated_string" },
        ["java"] = new(StringComparer.Ordinal) { "string_literal" },
        ["python"] = new(StringComparer.Ordinal) { "string" },
        ["rust"] = new(StringComparer.Ordinal) { "string_literal", "raw_string_literal" },
        ["go"] = new(StringComparer.Ordinal) { "interpreted_string_literal", "raw_string_literal" },
        ["typescript"] = new(StringComparer.Ordinal) { "string", "template_string" },
        ["tsx"] = new(StringComparer.Ordinal) { "string", "template_string" },
    };

    /// <summary>
    /// import / include のモジュール指定子を表す祖先ノード型。文字列リテラルがこの配下にある場合は
    /// 誤検知を避けるため検出しない（import は必然でありハードコード値ではない）。
    /// </summary>
    private static readonly HashSet<string> ImportContextTypes = new(StringComparer.Ordinal)
    {
        "import_statement",        // typescript: import x from "mod"
        "export_statement",        // typescript: export ... from "mod"
        "import_spec",             // go: import ( "fmt" )
        "import_declaration",      // go: import "fmt"
        "preproc_include",         // c/cpp: #include "foo.h"
        "import_from_statement",   // python: from "..." (稀)
    };

    /// <summary>アノテーション／デコレータ／属性を表す祖先ノード型（<see cref="IgnoreContext.Annotation"/>）。</summary>
    private static readonly HashSet<string> AnnotationTypes = new(StringComparer.Ordinal)
    {
        "annotation",              // java: @Column(name = "id")
        "marker_annotation",       // java: @Override
        "decorator",               // python / typescript: @app.route("/x")
        "attribute_item",          // rust: #[cfg(feature = "x")]
        "inner_attribute_item",    // rust: #![...]
        "attribute",               // rust（attribute_item 配下）
        "attribute_declaration",   // c / cpp: [[...]]
        "attribute_specifier",     // cpp: __attribute__((...))
    };

    /// <summary>例外送出を表す祖先ノード型（<see cref="IgnoreContext.Throw"/>）。</summary>
    private static readonly HashSet<string> ThrowStatementTypes = new(StringComparer.Ordinal)
    {
        "throw_statement",   // java / cpp / typescript
        "raise_statement",   // python
    };

    /// <summary>呼び出しを表すノード型。呼び出し先の名前で log / throw の文脈を判定する。</summary>
    private static readonly HashSet<string> CallTypes = new(StringComparer.Ordinal)
    {
        "call_expression",     // c / cpp / go / rust / typescript
        "method_invocation",   // java
        "call",                // python
        "macro_invocation",    // rust: panic!("msg")
    };

    /// <summary>例外送出・エラー生成とみなす呼び出し名（末尾の識別子。小文字で比較）。</summary>
    private static readonly HashSet<string> ThrowNames = new(StringComparer.Ordinal)
    {
        "panic", "unreachable", "todo", "unimplemented", "expect",
    };

    /// <summary>例外送出・エラー生成とみなす呼び出し先（末尾一致。小文字で比較）。</summary>
    private static readonly string[] ThrowCallees = { "errors.new", "fmt.errorf" };

    /// <summary>ログ出力・標準出力とみなす呼び出し名（末尾の識別子。小文字で比較）。</summary>
    private static readonly HashSet<string> LogNames = new(StringComparer.Ordinal)
    {
        "log", "logf", "debug", "debugf", "info", "infof", "warn", "warnf", "warning",
        "error", "errorf", "fatal", "fatalf", "trace", "tracef",
        "print", "printf", "println", "printfn", "eprint", "eprintln", "printstacktrace",
    };

    /// <summary>文脈判定で遡る祖先の最大段数。</summary>
    private const int MaxContextDepth = 16;

    /// <summary>ファイルパスの拡張子から対応言語 id を得る。未対応なら false。</summary>
    public static bool TryGetLanguageId(string filePath, out string languageId)
    {
        var ext = Path.GetExtension(filePath);
        return ExtToLang.TryGetValue(ext, out languageId!);
    }

    /// <summary>対応拡張子か（言語判定できるか）。</summary>
    public static bool IsSupported(string filePath) => TryGetLanguageId(filePath, out _);

    /// <summary>
    /// ソースを解析し、数値・文字列リテラルを列挙する。<paramref name="languageId"/> は
    /// <see cref="TryGetLanguageId"/> で得た値。未対応言語なら空を返す。
    /// <paramref name="options"/> で種別・文脈の絞り込みを指定する。
    /// </summary>
    public static IReadOnlyList<Literal> Detect(string languageId, string source, DetectOptions options)
    {
        if (!NumberTypes.TryGetValue(languageId, out var numberTypes)
            || !StringTypes.TryGetValue(languageId, out var stringTypes))
        {
            return Array.Empty<Literal>();
        }

        using var language = new Language(languageId);
        using var parser = new Parser(language);
        using var tree = parser.Parse(source);
        if (tree is null)
        {
            return Array.Empty<Literal>();
        }

        var found = new List<Literal>();
        Visit(tree.RootNode, numberTypes, stringTypes, options, found);
        return found;
    }

    private static void Visit(
        Node node, HashSet<string> numberTypes, HashSet<string> stringTypes, DetectOptions options, List<Literal> found)
    {
        var type = node.Type;

        if (stringTypes.Contains(type))
        {
            if (options.Strings && !IsInImportContext(node) && !IsIgnoredContext(node, options.Ignore))
            {
                found.Add(new Literal("string", node.StartPosition.Row + 1, Trim(node.Text), node.Text ?? ""));
            }
            return; // 文字列 wrapper の配下（fragment/interpolation）へは降りない。
        }

        if (numberTypes.Contains(type))
        {
            if (options.Numbers && !IsExemptNumber(node.Text) && !IsIgnoredContext(node, options.Ignore))
            {
                found.Add(new Literal("number", node.StartPosition.Row + 1, Trim(node.Text), node.Text ?? ""));
            }
            return;
        }

        foreach (var child in node.NamedChildren)
        {
            Visit(child, numberTypes, stringTypes, options, found);
        }
    }

    /// <summary>
    /// 検出対象外（範囲外）の数値リテラルか。値が <c>0</c> / <c>1</c> の整数のみ範囲外とする。
    /// 添字・初期値・増減・番兵など言語の必然として現れ、定数へ切り出しても意味を持たないため。
    /// 進数プレフィックス（<c>0x</c> / <c>0b</c> / <c>0o</c> / 先頭 0 の 8 進）・桁区切り（<c>_</c> / <c>'</c>）・
    /// 型接尾辞（<c>u</c> / <c>L</c> / <c>f</c> / <c>i32</c> / <c>n</c> 等）は許容する（<c>0x00</c> / <c>1L</c> は範囲外）。
    /// 2 以上の整数・小数・指数表記・虚数は意味を持つ値として検出する（<c>1.0</c> / <c>1e3</c> / <c>1i</c> は検出）。
    /// </summary>
    private static bool IsExemptNumber(string? rawText)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return false;
        }

        var text = rawText.Replace("_", "").Replace("'", "").ToLowerInvariant();

        // 進数プレフィックスごとに、数字部分として許される文字を決める。
        // プレフィックス無し = 10 進、および C 系の 8 進（先頭 0）。どちらも 0/1 判定は同じ扱いでよい。
        var (prefixLength, isDigit) = text switch
        {
            _ when text.StartsWith("0x", StringComparison.Ordinal) => (2, (Func<char, bool>)char.IsAsciiHexDigit),
            _ when text.StartsWith("0b", StringComparison.Ordinal) => (2, c => c is '0' or '1'),
            _ when text.StartsWith("0o", StringComparison.Ordinal) => (2, c => c is >= '0' and <= '7'),
            _ => (0, char.IsAsciiDigit),
        };

        var digitCount = 0;
        while (prefixLength + digitCount < text.Length && isDigit(text[prefixLength + digitCount]))
        {
            digitCount++;
        }
        if (digitCount == 0)
        {
            return false; // ".5" のように数字で始まらない = 小数。
        }

        // 数字部分の直後で整数かを見分ける。小数点・指数・虚数が続くなら整数ではない。
        // それ以外の残り（型接尾辞）は値に影響しないため無視してよい。
        var suffix = text[(prefixLength + digitCount)..];
        var isInteger = !suffix.StartsWith('.')
            && !(prefixLength == 0 && suffix.StartsWith('e'))   // 10 進の指数: 1e3
            && !(prefixLength == 2 && text[1] == 'x' && suffix.StartsWith('p'))   // 16 進の指数: 0x1p3
            && suffix is not ("i" or "j");   // 虚数: 1i（Go）/ 1j（Python）
        if (!isInteger)
        {
            return false;
        }

        var digits = text.Substring(prefixLength, digitCount).TrimStart('0');
        return digits.Length == 0 || digits == "1";
    }

    /// <summary>
    /// リテラルが <paramref name="ignore"/> で指定された文脈の配下にあるか。祖先を遡り、
    /// ノード型（アノテーション・throw 文）と、呼び出しノードの呼び出し先名（log / throw 相当）で判定する。
    ///
    /// 呼び出し先名による判定は言語をまたぐヒューリスティック。<c>logger.info</c> と同名の
    /// 無関係なメソッドも除外され得るが、緩める方向の誤りに倒してある。
    /// </summary>
    private static bool IsIgnoredContext(Node node, IgnoreContext ignore)
    {
        if (ignore == IgnoreContext.None)
        {
            return false;
        }

        var cur = node.Parent;
        for (var depth = 0; cur is not null && depth < MaxContextDepth; depth++)
        {
            var type = cur.Type;
            if (ignore.HasFlag(IgnoreContext.Annotation) && AnnotationTypes.Contains(type))
            {
                return true;
            }
            if (ignore.HasFlag(IgnoreContext.Throw) && ThrowStatementTypes.Contains(type))
            {
                return true;
            }
            if (CallTypes.Contains(type) && IsIgnoredCall(cur, ignore))
            {
                return true;
            }
            cur = cur.Parent;
        }
        return false;
    }

    /// <summary>呼び出しノードの呼び出し先が、除外対象の log / throw 相当か。</summary>
    private static bool IsIgnoredCall(Node call, IgnoreContext ignore)
    {
        var callee = Callee(call);
        if (callee is null)
        {
            return false;
        }
        if (ignore.HasFlag(IgnoreContext.Throw)
            && (ThrowNames.Contains(LastIdentifier(callee) ?? "")
                || ThrowCallees.Any(c => callee.EndsWith(c, StringComparison.Ordinal))))
        {
            return true;
        }
        return ignore.HasFlag(IgnoreContext.Log) && LogNames.Contains(LastIdentifier(callee) ?? "");
    }

    /// <summary>
    /// 呼び出しノードから呼び出し先の表記を取り出す（小文字化）。引数リストの手前までを見るため、
    /// <c>logger.info("x")</c> → <c>logger.info</c>、<c>panic!("x")</c> → <c>panic</c> となる。
    /// フィールド名の API に依存せず全言語で同じ扱いにできる。
    /// </summary>
    private static string? Callee(Node call)
    {
        var text = call.Text;
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        var paren = text.IndexOf('(');
        var head = (paren >= 0 ? text[..paren] : text).Trim();
        head = head.TrimEnd('!').Trim();   // rust のマクロ呼び出し: panic!
        return head.Length == 0 ? null : head.ToLowerInvariant();
    }

    /// <summary>呼び出し先表記の末尾の識別子（<c>logger.info</c> → <c>info</c>）。</summary>
    private static string? LastIdentifier(string callee)
    {
        var end = callee.Length;
        while (end > 0 && !IsIdentifierChar(callee[end - 1]))
        {
            end--;
        }
        var start = end;
        while (start > 0 && IsIdentifierChar(callee[start - 1]))
        {
            start--;
        }
        return end > start ? callee[start..end] : null;
    }

    private static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>文字列リテラルが import/include の指定子配下にあるか（祖先を数段だけ遡って判定）。</summary>
    private static bool IsInImportContext(Node node)
    {
        var cur = node.Parent;
        for (var depth = 0; cur is not null && depth < 4; depth++)
        {
            if (ImportContextTypes.Contains(cur.Type))
            {
                return true;
            }
            cur = cur.Parent;
        }
        return false;
    }

    private static string Trim(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        var oneLine = text.ReplaceLineEndings(" ");
        return oneLine.Length > 40 ? oneLine[..40] + "…" : oneLine;
    }
}
