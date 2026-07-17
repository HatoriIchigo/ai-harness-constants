namespace ai_harness_constants;

/// <summary>重複した文字列リテラルの出現箇所 1 件。</summary>
public readonly record struct StringOccurrence(string Path, int Line);

/// <summary>同一の文字列リテラルが 2 箇所以上に現れたグループ（<see cref="Text"/> は表示用テキスト）。</summary>
public readonly record struct DuplicateString(string Text, IReadOnlyList<StringOccurrence> Occurrences);

/// <summary>
/// <c>same-string: true</c> のエントリで、allow（ハードコードを許可した定数ファイル）群を横断し、
/// 同一の文字列リテラルが 2 箇所以上に定義されていないかを調べる。同じ値の定数の二重定義を潰す用途。
///
/// 同一判定はリテラル表記そのまま（<c>"a"</c> と <c>'a'</c> は別物）。空文字列 <c>""</c> も対象に含める。
/// 数値は対象外（同じ数値が別意味の定数に現れるのは正当なため）。
/// </summary>
public static class DuplicateStringChecker
{
    /// <summary>重複とみなす最小の出現回数。</summary>
    private const int DuplicateThreshold = 2;

    /// <summary>
    /// <paramref name="allow"/> の glob に一致する対応言語ソースを <paramref name="root"/> 配下から集める。
    /// allow に <c>**</c> は使えないため、glob が現れる手前までの固定ディレクトリを起点に走査すれば足りる
    /// （プロジェクト全体を走査しない）。アクセス不能なパスは黙ってスキップする。
    /// </summary>
    public static IReadOnlyList<string> CollectAllowFiles(string root, string allow)
    {
        var baseDir = Path.Combine(root, FixedDirectory(allow));
        if (!Directory.Exists(baseDir))
        {
            return Array.Empty<string>();
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };

        var files = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(baseDir, "*", options))
            {
                if (LiteralDetector.IsSupported(file) && GlobMatcher.IsMatch(allow, file))
                {
                    files.Add(file);
                }
            }
        }
        catch (Exception)
        {
            return files; // 走査中の削除競合・権限不足等。集められた分だけで検査する。
        }
        return files;
    }

    /// <summary>
    /// allow の glob から、glob 文字（<c>*</c> / <c>?</c>）が現れる手前までのディレクトリ部分を返す。
    /// 末尾セグメントはファイル名部のため常に除く（<c>constants/Values.java</c> → <c>constants</c>）。
    /// </summary>
    private static string FixedDirectory(string allow)
    {
        var segments = allow.Replace('\\', '/').Split('/');
        var dirs = new List<string>();
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Contains('*') || segments[i].Contains('?'))
            {
                break;
            }
            dirs.Add(segments[i]);
        }
        return dirs.Count == 0 ? "" : Path.Combine(dirs.ToArray());
    }

    /// <summary>
    /// ファイル群を AST 解析し、2 箇所以上に現れた文字列リテラルを最初の出現順で返す。
    /// 読めない／解析できないファイルは <paramref name="warnings"/> に理由を積んでスキップする（違反として扱わない）。
    /// </summary>
    public static IReadOnlyList<DuplicateString> Find(IEnumerable<string> files, List<string> warnings)
    {
        // 出現順を保つため、キーの初出順を別に持つ（Dictionary は順序を保証しない）。
        var occurrences = new Dictionary<string, List<StringOccurrence>>(StringComparer.Ordinal);
        var order = new List<string>();
        var displayText = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            if (!LiteralDetector.TryGetLanguageId(file, out var languageId))
            {
                continue;
            }

            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch (Exception e)
            {
                warnings.Add($"ファイルを読めないため文字列重複の検査をスキップ: {file} ({e.GetType().Name})");
                continue;
            }

            IReadOnlyList<Literal> literals;
            try
            {
                // 重複検査は allow（定数ファイル）が対象。エントリの緩和設定（numbers / strings /
                // ignore-context / min-occurrences）は pattern 側の検査にのみ効くため、ここでは絞らない。
                literals = LiteralDetector.Detect(languageId, source, DetectOptions.All);
            }
            catch (Exception e)
            {
                warnings.Add($"AST 解析に失敗（{languageId}）: {file} ({e.GetType().Name}: {e.Message})");
                continue;
            }

            foreach (var literal in literals)
            {
                if (literal.Kind != "string")
                {
                    continue; // 重複検査は文字列のみ
                }
                if (!occurrences.TryGetValue(literal.Raw, out var list))
                {
                    list = new List<StringOccurrence>();
                    occurrences[literal.Raw] = list;
                    order.Add(literal.Raw);
                    displayText[literal.Raw] = literal.Text;
                }
                list.Add(new StringOccurrence(file, literal.Line));
            }
        }

        var duplicates = new List<DuplicateString>();
        foreach (var key in order)
        {
            var list = occurrences[key];
            if (list.Count >= DuplicateThreshold)
            {
                duplicates.Add(new DuplicateString(displayText[key], list));
            }
        }
        return duplicates;
    }
}
