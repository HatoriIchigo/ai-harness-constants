using TreeSitter;

namespace ai_harness_constants;

/// <summary>検出したハードコードリテラル 1 件（種別・行番号・元テキスト）。</summary>
public readonly record struct Literal(string Kind, int Line, string Text);

/// <summary>
/// tree-sitter（TreeSitter.DotNet）で対象言語のソースを AST 解析し、数値・文字列リテラルを検出する。
/// 検出対象のノード型は言語ごとに実測して定義（<see cref="NumberTypes"/> / <see cref="StringTypes"/>）。
/// 文字列は wrapper ノード（<c>string_literal</c> 等）で検出し、そこから下へは降りない。
/// import/include のモジュール指定子の文字列は誤検知を避けるため除外する。
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
    /// </summary>
    public static IReadOnlyList<Literal> Detect(string languageId, string source)
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
        Visit(tree.RootNode, numberTypes, stringTypes, found);
        return found;
    }

    private static void Visit(Node node, HashSet<string> numberTypes, HashSet<string> stringTypes, List<Literal> found)
    {
        var type = node.Type;

        if (stringTypes.Contains(type))
        {
            if (!IsInImportContext(node))
            {
                found.Add(new Literal("string", node.StartPosition.Row + 1, Trim(node.Text)));
            }
            return; // 文字列 wrapper の配下（fragment/interpolation）へは降りない。
        }

        if (numberTypes.Contains(type))
        {
            found.Add(new Literal("number", node.StartPosition.Row + 1, Trim(node.Text)));
            return;
        }

        foreach (var child in node.NamedChildren)
        {
            Visit(child, numberTypes, stringTypes, found);
        }
    }

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
