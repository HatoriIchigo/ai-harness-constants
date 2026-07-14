using System.Collections;

namespace ai_harness_constants;

/// <summary>
/// 有効な検査エントリ 1 件。対象 glob（ハードコード禁止）＋ allow glob（ハードコード許可）。
/// <paramref name="SameString"/> が true なら、allow にマッチする定数ファイル群を横断して
/// 同一文字列リテラルの重複も検査する（既定 false）。
/// </summary>
public readonly record struct ConstantsEntry(string Pattern, string Allow, bool SameString);

/// <summary>
/// ai-harness-constants の設定を解釈・検証した結果。
///
/// 設定スキーマ:
/// <code>
/// files:
///   - pattern: "src/main/java/**/*.java"     # ハードコードを許可しない対象
///     allow:   "src/main/java/.../constants/*.java"   # ハードコードを許可する単一 glob
///     same-string: true                      # allow 群での文字列重複も禁止（省略時 false）
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
///   - same-string は省略可。指定するなら <c>true</c> / <c>false</c> であること
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

        entries.Add(new ConstantsEntry(pattern.Trim(), allow, sameString));
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
