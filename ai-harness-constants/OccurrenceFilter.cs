namespace ai_harness_constants;

/// <summary>
/// 設定 <c>min-occurrences</c> で検出結果を絞る。同一の値がファイル内で N 回以上現れたものだけを違反とする。
///
/// 1 箇所にしか無い値は、定数へ切り出しても参照が 1 つで DRY の観点から得が薄い。閾値を上げると
/// 「散らばっている値」だけを違反にでき、1 回きりの値（ログ文言・メッセージ等）を見逃せる。
/// 既定は 1（絞り込み無し＝全て違反）。
///
/// 同一判定は種別（number / string）とリテラル表記そのままの組で行う（<c>"a"</c> と <c>'a'</c> は別物）。
/// 判定はファイル単位で閉じる（hook が 1 ファイルしか見ないため、ファイルを跨いだ集計はしない）。
/// </summary>
public static class OccurrenceFilter
{
    /// <summary>出現回数が <paramref name="min"/> 未満の値を除いた結果を返す。</summary>
    public static IReadOnlyList<Literal> Apply(IReadOnlyList<Literal> literals, int min)
    {
        if (min <= 1)
        {
            return literals;
        }

        var counts = new Dictionary<(string Kind, string Raw), int>();
        foreach (var literal in literals)
        {
            var key = (literal.Kind, literal.Raw);
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        var kept = new List<Literal>();
        foreach (var literal in literals)
        {
            if (counts[(literal.Kind, literal.Raw)] >= min)
            {
                kept.Add(literal);
            }
        }
        return kept;
    }
}
