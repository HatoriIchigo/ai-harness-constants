using System.Collections;

namespace ai_harness_constants;

/// <summary>
/// 有効な検査エントリ 1 件。対象 glob（ハードコード禁止）＋ allow glob（ハードコード許可）。
/// <paramref name="SameString"/> が true なら、allow にマッチする定数ファイル群を横断して
/// 同一文字列リテラルの重複も検査する（既定 false）。
///
/// <paramref name="Numbers"/> / <paramref name="Strings"/> / <paramref name="Ignore"/> /
/// <paramref name="MinOccurrences"/> は検査を緩める側の設定で、既定値は従来どおりの
/// 「数値・文字列を全て検出」（true / true / None / 1）。pattern 側の検査にのみ効き、
/// same-string（allow 群の重複検査）には影響しない。
/// </summary>
public readonly record struct ConstantsEntry(
    string Pattern,
    string Allow,
    bool SameString,
    bool Numbers,
    bool Strings,
    IgnoreContext Ignore,
    int MinOccurrences);

/// <summary>
/// ai-harness-constants の設定を解釈・検証した結果。
///
/// 設定スキーマ:
/// <code>
/// files:
///   - pattern: "src/main/java/**/*.java"     # ハードコードを許可しない対象
///     allow:   "src/main/java/.../constants/*.java"   # ハードコードを許可する単一 glob
///     same-string: true                      # allow 群での文字列重複も禁止（省略時 false）
///     numbers: true                          # 数値リテラルを検出するか（省略時 true）
///     strings: false                         # 文字列リテラルを検出するか（省略時 true）
///     ignore-context:                        # 検出しない文脈（省略時は無し）
///       - annotation                         #   アノテーション／デコレータ／属性の引数
///       - throw                              #   例外送出・エラー生成の引数
///       - log                                #   ログ出力・標準出力の引数
///     min-occurrences: 2                     # 同一値がファイル内で N 回以上なら違反（省略時 1）
/// </code>
///
/// テストファイル等を検査対象外にしたい場合は、単に pattern に含めなければよい
/// （どの pattern にもマッチしないファイルは管理対象外＝許可）。
///
/// バリデーション（いずれか違反でそのエントリは不正 → 設定は使用不可＝フェイルクローズ）:
///   - pattern / allow が非空の文字列であること
///   - allow は単一スカラ（リスト不可）
///   - allow に <c>**</c> を含まないこと
///   - allow が pattern の内側（pattern にマッチするパス）であること
///   - same-string / numbers / strings は省略可。指定するなら <c>true</c> / <c>false</c> であること
///   - ignore-context は省略可。指定するならリストで、各要素が
///     <c>annotation</c> / <c>throw</c> / <c>log</c> のいずれかであること
///   - min-occurrences は省略可。指定するなら 1 以上の整数であること
/// </summary>
public sealed class ConstantsConfig
{
    public IReadOnlyList<ConstantsEntry> Entries { get; }
    public IReadOnlyList<string> Errors { get; }

    /// <summary>設定として使用可能か（有効エントリが 1 つ以上あり、エラーが無い）。</summary>
    public bool IsUsable => Errors.Count == 0 && Entries.Count > 0;

    private ConstantsConfig(IReadOnlyList<ConstantsEntry> entries, IReadOnlyList<string> errors)
    {
        Entries = entries;
        Errors = errors;
    }

    /// <summary>プラグインの <c>Config</c>（YAML マッピング）を解釈・検証する。</summary>
    public static ConstantsConfig Parse(IReadOnlyDictionary<string, object> config)
    {
        var entries = new List<ConstantsEntry>();
        var errors = new List<string>();

        var filesRaw = config.TryGetValue("files", out var f) ? f : null;
        if (filesRaw is not IList filesList || filesRaw is string)
        {
            errors.Add("files が未設定、またはリストではありません。");
        }
        else
        {
            for (var i = 0; i < filesList.Count; i++)
            {
                ParseEntry(filesList[i], i, entries, errors);
            }
            if (entries.Count == 0 && errors.Count == 0)
            {
                errors.Add("files に有効なエントリがありません。");
            }
        }

        return new ConstantsConfig(entries, errors);
    }

    private static void ParseEntry(object? item, int index, List<ConstantsEntry> entries, List<string> errors)
    {
        if (item is not IDictionary map)
        {
            errors.Add($"files[{index}]: pattern/allow を持つマップである必要があります。");
            return;
        }

        var pattern = GetString(map, "pattern");
        var allowRaw = Get(map, "allow");

        if (string.IsNullOrWhiteSpace(pattern))
        {
            errors.Add($"files[{index}]: pattern が未設定です。");
            return;
        }

        // allow は単一スカラのみ（リスト不可）。string は IList を実装しないため IList 判定で足りる。
        if (allowRaw is IList)
        {
            errors.Add($"files[{index}] (pattern='{pattern}'): allow はリスト不可（単一の glob を指定）。");
            return;
        }
        var allow = allowRaw?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(allow))
        {
            errors.Add($"files[{index}] (pattern='{pattern}'): allow が未設定です。");
            return;
        }

        // allow に ** は不可。
        if (allow.Contains("**", StringComparison.Ordinal))
        {
            errors.Add($"files[{index}] (pattern='{pattern}'): allow に ** は使用できません（allow='{allow}'）。");
            return;
        }

        // allow は pattern の内側であること（allow にマッチするパスは pattern にもマッチする）。
        if (!IsWithin(pattern.Trim(), allow))
        {
            errors.Add($"files[{index}]: allow が対象(pattern)の内側にありません（pattern='{pattern}', allow='{allow}'）。");
            return;
        }

        // same-string は省略可（既定 false）。指定するなら真偽値であること。
        var sameString = false;
        var sameStringRaw = Get(map, "same-string");
        if (sameStringRaw is not null)
        {
            if (!bool.TryParse(sameStringRaw.ToString(), out sameString))
            {
                errors.Add(
                    $"files[{index}] (pattern='{pattern}'): same-string は true / false で指定してください（same-string='{sameStringRaw}'）。");
                return;
            }
        }

        // 以下は検査を緩める側の設定。省略時は従来どおりの「数値・文字列を全て検出」。
        var errorCount = errors.Count;
        var numbers = ParseBool(map, "numbers", true, index, pattern, errors);
        var strings = ParseBool(map, "strings", true, index, pattern, errors);
        var ignore = ParseIgnoreContext(map, index, pattern, errors);
        var minOccurrences = ParseMinOccurrences(map, index, pattern, errors);
        if (errors.Count != errorCount)
        {
            return;
        }

        entries.Add(new ConstantsEntry(
            pattern.Trim(), allow, sameString, numbers, strings, ignore, minOccurrences));
    }

    /// <summary>真偽を解釈。省略時は <paramref name="fallback"/>。true/false 以外はエラー。</summary>
    private static bool ParseBool(
        IDictionary map, string key, bool fallback, int index, string pattern, List<string> errors)
    {
        var raw = Get(map, key);
        if (raw is null)
        {
            return fallback;
        }
        if (bool.TryParse(raw.ToString(), out var value))
        {
            return value;
        }
        errors.Add($"files[{index}] (pattern='{pattern}'): {key} は true / false で指定してください（{key}='{raw}'）。");
        return fallback;
    }

    /// <summary>ignore-context を解釈。省略時は除外なし。リスト以外・語彙外はエラー。</summary>
    private static IgnoreContext ParseIgnoreContext(IDictionary map, int index, string pattern, List<string> errors)
    {
        var raw = Get(map, "ignore-context");
        if (raw is null)
        {
            return IgnoreContext.None;
        }
        // string も IList を実装しないため、スカラ指定はここで弾ける。
        if (raw is not IList list || raw is string)
        {
            errors.Add(
                $"files[{index}] (pattern='{pattern}'): ignore-context はリストで指定してください（ignore-context='{raw}'）。");
            return IgnoreContext.None;
        }

        var ignore = IgnoreContext.None;
        foreach (var item in list)
        {
            var name = item?.ToString()?.Trim();
            var parsed = name?.ToLowerInvariant() switch
            {
                "annotation" => IgnoreContext.Annotation,
                "throw" => IgnoreContext.Throw,
                "log" => IgnoreContext.Log,
                _ => IgnoreContext.None,
            };
            if (parsed == IgnoreContext.None)
            {
                errors.Add(
                    $"files[{index}] (pattern='{pattern}'): ignore-context は annotation / throw / log のいずれかを指定してください（値='{name}'）。");
                continue;
            }
            ignore |= parsed;
        }
        return ignore;
    }

    /// <summary>min-occurrences を解釈。省略時は 1（全て違反）。1 以上の整数以外はエラー。</summary>
    private static int ParseMinOccurrences(IDictionary map, int index, string pattern, List<string> errors)
    {
        var raw = Get(map, "min-occurrences");
        var s = raw?.ToString()?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return 1;
        }
        if (int.TryParse(s, out var n) && n >= 1)
        {
            return n;
        }
        errors.Add(
            $"files[{index}] (pattern='{pattern}'): min-occurrences は 1 以上の整数で指定してください（値='{s}'）。");
        return 1;
    }

    /// <summary>allow が pattern の内側か（allow パターン文字列が pattern の glob にマッチするか）。</summary>
    private static bool IsWithin(string pattern, string allow)
    {
        var regex = GlobMatcher.ToRegex(pattern.Replace('\\', '/'));
        return regex.IsMatch(allow.Replace('\\', '/'));
    }

    // ---- YamlDotNet の既定型（マップ=IDictionary, リスト=IList, スカラ=string）ヘルパ ----

    private static object? Get(IDictionary map, string key) =>
        map.Contains(key) ? map[key] : null;

    private static string? GetString(IDictionary map, string key) => Get(map, key)?.ToString();
}
