using System.Text;
using System.Text.Json.Nodes;
using ai_harness_baselib;

namespace ai_harness_constants;

/// <summary>
/// プログラム中のハードコード値（数値・文字列リテラル）を、設定で許可した定数ファイル以外に
/// 書かせないよう強制するプラグイン。書き込み系ツール（Write / Edit / MultiEdit）の
/// <c>PostToolUse</c> で発火し、書き込んだファイルを tree-sitter で AST 解析する。
///
/// 値が 0 / 1 の整数リテラルは範囲外として検出しない（<see cref="LiteralDetector"/>）。
///
/// 判定（対応言語のソースファイルのみが検査対象。.md 等の非ソースは対象外＝許可）:
///   1. 設定が使用不可            → deny（フェイルクローズ。エラー内容を提示）
///   2. いずれかの allow にマッチ  → 許可（ハードコードを許可した定数ファイル）。
///                                  ただし same-string: true のエントリでは、その allow 群を横断して
///                                  同一文字列リテラルの重複を検査し、重複があれば deny
///   3. いずれかの pattern にマッチ → AST 解析。リテラルがあれば deny、無ければ許可
///   4. どの pattern にもマッチせず → 許可（このプラグインの管理対象外）
///
/// テストファイル等を除外したい場合は pattern に含めなければよい（4 で許可される）。
///
/// PostToolUse のため書き込み自体は止められない。deny（exit 2）で Claude に差し戻し、
/// リテラルを許可された定数ファイルへ移動させる。
///
/// hook とは別に、<c>ai-harness-main --fire</c> の能動スキャン（<see cref="Fire"/>）で既存ツリー全体を
/// 一括点検できる（走査範囲は設定 <c>fire.exclude</c> / <c>fire.gitignore</c> で絞る）。
/// </summary>
public sealed class ConstantsPlugin : PluginBase
{
    public override string PluginName => "ai-harness-constants";

    public override string Description =>
        "AST 解析で、許可した定数ファイル以外のハードコード値を deny する";

    /// <summary>PostToolUse の全ツールで発火し、Action 内で書き込み系ツールを自己フィルタ。</summary>
    public override IReadOnlyList<string> Events => new[] { "PostToolUse" };

    public override string ConfigName => "ai-harness-constants.yml";

    /// <summary>埋め込み rule（<c>constants.rule.md</c>）を各プロジェクトの <c>.claude/rules</c> へ配布する。</summary>
    public override bool ProvidesRule => true;

    /// <summary>ファイルを書き込む対象ツール。</summary>
    private static readonly HashSet<string> TargetTools =
        new(StringComparer.Ordinal) { "Write", "Edit", "MultiEdit" };

    /// <summary>reason に列挙する違反リテラルの最大件数。</summary>
    private const int MaxReported = 20;

    public override IEnumerable<LogEntry> Init()
    {
        yield return LogEntry.Info("初期化");
    }

    public override IEnumerable<LogEntry> Action(HookData data, PluginResult result)
    {
        if (data.Event != HookEvent.PostToolUse)
        {
            yield break;
        }
        var toolName = data.ToolName;
        if (toolName is null || !TargetTools.Contains(toolName))
        {
            yield break;
        }

        var filePath = ExtractFilePath(data);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            yield return LogEntry.Debug($"file_path を取得できないため検査スキップ（tool={toolName}）");
            yield break;
        }

        // 対応言語のソースファイル以外は対象外（.md/.txt 等をこのプラグインは扱わない）。
        if (!LiteralDetector.TryGetLanguageId(filePath, out var languageId))
        {
            yield return LogEntry.Debug($"対応言語外のため対象外: {filePath}");
            yield break;
        }

        var config = ConstantsConfig.Parse(Config);

        // 1. 設定が使用不可ならフェイルクローズで deny（対象がソースファイルの場合）。
        if (!config.IsUsable)
        {
            yield return LogEntry.Warning($"設定が使用不可のため deny（フェイルクローズ）: {filePath}");
            result.ExitCode = 2;
            result.Reason = BuildConfigErrorReason(config.Errors);
            yield break;
        }

        // 2. allow にマッチ = ハードコードを許可した定数ファイル。
        //    same-string: true のエントリでは、その allow 群を横断して文字列の重複を検査する。
        var allowEntries = config.Entries.Where(e => GlobMatcher.IsMatch(e.Allow, filePath)).ToList();
        if (allowEntries.Count > 0)
        {
            var sameStringEntries = allowEntries.Where(e => e.SameString).ToList();
            if (sameStringEntries.Count == 0)
            {
                yield return LogEntry.Debug($"allow 対象のため許可: {filePath}");
                yield break;
            }

            // allow 群の他ファイルを読むためプロジェクトルートが要る。特定できなければ検査できない。
            var root = data.Cwd;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                yield return LogEntry.Warning(
                    $"プロジェクトルート（cwd）を特定できないため same-string 検査をスキップ: {filePath}");
                yield break;
            }

            var warnings = new List<string>();
            var groups = new List<DuplicateGroup>();
            foreach (var entry in sameStringEntries)
            {
                var allowFiles = DuplicateStringChecker.CollectAllowFiles(root, entry.Allow);
                var duplicates = DuplicateStringChecker.Find(allowFiles, warnings);
                if (duplicates.Count > 0)
                {
                    groups.Add(new DuplicateGroup(entry.Allow, duplicates));
                }
            }
            foreach (var warning in warnings)
            {
                yield return LogEntry.Warning(warning);
            }

            if (groups.Count == 0)
            {
                yield return LogEntry.Debug($"allow 対象・文字列の重複なしのため許可: {filePath}");
                yield break;
            }

            yield return LogEntry.Warning(
                $"定数ファイル群で重複した文字列 {groups.Sum(g => g.Duplicates.Count)} 件を検出: {filePath}");
            result.ExitCode = 2;
            result.Reason = BuildDuplicateReason(groups);
            yield break;
        }

        // 3. pattern にマッチ = 検査対象。
        if (!config.Entries.Any(e => GlobMatcher.IsMatch(e.Pattern, filePath)))
        {
            // 4. どの pattern にもマッチしない = 管理対象外。
            yield return LogEntry.Debug($"どの pattern にもマッチせず対象外: {filePath}");
            yield break;
        }

        // 読めなければ検査できない。ブロックせず通す（書き込みは既に完了している）。
        string? source = null;
        string? readError = null;
        try
        {
            source = File.ReadAllText(filePath);
        }
        catch (Exception e)
        {
            readError = $"ファイルを読めないため検査スキップ: {filePath} ({e.GetType().Name})";
        }
        if (readError is not null)
        {
            yield return LogEntry.Warning(readError);
            yield break;
        }

        IReadOnlyList<Literal> literals = Array.Empty<Literal>();
        string? detectError = null;
        try
        {
            literals = LiteralDetector.Detect(languageId, source!);
        }
        catch (Exception e)
        {
            detectError = $"AST 解析に失敗（{languageId}）: {filePath} ({e.GetType().Name}: {e.Message})";
        }
        if (detectError is not null)
        {
            yield return LogEntry.Error(detectError);
            yield break;
        }

        if (literals.Count == 0)
        {
            yield return LogEntry.Debug($"ハードコード値なし: {filePath}");
            yield break;
        }

        yield return LogEntry.Warning($"ハードコード値 {literals.Count} 件を検出: {filePath}");
        result.ExitCode = 2;
        result.Reason = BuildViolationReason(filePath, literals);
    }

    /// <summary>
    /// 能動スキャン。<c>ai-harness-main --fire</c> から起動され、<paramref name="projectRoot"/> 配下を走査して、
    /// <c>files</c> の <c>pattern</c> に合致する全ソースを AST 解析し、<c>allow</c>（ハードコードを許可した定数ファイル）
    /// 以外にリテラルがあれば exit 2（検出）。走査対象は <c>fire.exclude</c>／<c>fire.gitignore</c> で絞る。
    ///
    /// hook（<see cref="Action"/>）が書き込みごとに 1 ファイルを検査するのに対し、こちらは既存ツリー全体を
    /// 一括点検する。hook のゲートではないため、非 0 は「差し戻し」ではなく検出結果のレポート表示。
    /// </summary>
    public override IEnumerable<LogEntry> Fire(string projectRoot, PluginResult result)
    {
        var config = ConstantsConfig.Parse(Config);

        // 設定が使えなければ検査対象を決められない（Action と同じフェイルクローズ）。
        if (!config.IsUsable)
        {
            yield return LogEntry.Warning("設定が使用不可のためスキャンできない（フェイルクローズ）");
            result.ExitCode = 2;
            result.Reason = BuildConfigErrorReason(config.Errors);
            yield break;
        }

        var options = FireScanner.ReadOptions(Config);
        yield return LogEntry.Info(
            $"ハードコードスキャン開始 root={projectRoot} 除外パターン=[{string.Join(", ", options.Exclude)}] gitignore={options.Gitignore}");

        var scan = FireScanner.Collect(projectRoot, options);
        if (scan.Warning is { } warning)
        {
            yield return LogEntry.Warning(warning);
        }

        var targets = SelectTargets(scan.Files, config);
        yield return LogEntry.Debug($"検査対象ファイル数: {targets.Count}（走査 {scan.Files.Count} 件）");

        var findings = new List<FileFinding>();
        foreach (var target in targets)
        {
            string? source = null;
            string? error = null;
            try
            {
                source = File.ReadAllText(target.Path);
            }
            catch (Exception e)
            {
                error = $"ファイルを読めないため検査スキップ: {target.Path} ({e.GetType().Name})";
            }
            if (error is not null)
            {
                yield return LogEntry.Warning(error);
                continue;
            }

            IReadOnlyList<Literal> literals = Array.Empty<Literal>();
            try
            {
                literals = LiteralDetector.Detect(target.LanguageId, source!);
            }
            catch (Exception e)
            {
                error = $"AST 解析に失敗（{target.LanguageId}）: {target.Path} ({e.GetType().Name}: {e.Message})";
            }
            if (error is not null)
            {
                yield return LogEntry.Error(error);
                continue;
            }

            if (literals.Count == 0)
            {
                continue;
            }
            yield return LogEntry.Warning($"ハードコード値 {literals.Count} 件を検出: {target.Path}");
            findings.Add(new FileFinding(target.Path, literals));
        }

        // same-string: true のエントリごとに、allow 群を横断して文字列の重複を検査する。
        var dupWarnings = new List<string>();
        var groups = new List<DuplicateGroup>();
        foreach (var entry in config.Entries.Where(e => e.SameString))
        {
            var allowFiles = scan.Files
                .Where(f => LiteralDetector.IsSupported(f) && GlobMatcher.IsMatch(entry.Allow, f))
                .ToList();
            var duplicates = DuplicateStringChecker.Find(allowFiles, dupWarnings);
            if (duplicates.Count > 0)
            {
                yield return LogEntry.Warning(
                    $"重複した文字列 {duplicates.Count} 件を検出（allow='{entry.Allow}' / {allowFiles.Count} ファイル）");
                groups.Add(new DuplicateGroup(entry.Allow, duplicates));
            }
        }
        foreach (var dupWarning in dupWarnings)
        {
            yield return LogEntry.Warning(dupWarning);
        }

        if (findings.Count == 0 && groups.Count == 0)
        {
            yield return LogEntry.Info("許可されていないハードコード値・定数ファイル内の文字列重複は見つからない");
            yield break; // ExitCode 0（許可）のまま
        }

        var reasons = new List<string>();
        if (findings.Count > 0)
        {
            yield return LogEntry.Warning($"ハードコード値を含むファイルを {findings.Count} 件検出");
            reasons.Add(BuildFireReason(findings));
        }
        if (groups.Count > 0)
        {
            reasons.Add(BuildDuplicateReason(groups));
        }
        result.ExitCode = 2;
        result.Reason = string.Join("\n\n", reasons);
    }

    /// <summary>
    /// 走査したファイルから検査対象を選ぶ。Action の判定順と同じく、対応言語のソースで、
    /// allow（ハードコード可の定数ファイル）に該当せず、いずれかの pattern に合致するものだけを残す。
    /// </summary>
    private static List<FireTarget> SelectTargets(IReadOnlyList<string> files, ConstantsConfig config)
    {
        var targets = new List<FireTarget>();
        foreach (var file in files)
        {
            if (!LiteralDetector.TryGetLanguageId(file, out var languageId))
            {
                continue; // 対応言語外（.md 等）は対象外
            }
            if (config.Entries.Any(e => GlobMatcher.IsMatch(e.Allow, file)))
            {
                continue; // allow = ハードコードを許可した定数ファイル
            }
            if (!config.Entries.Any(e => GlobMatcher.IsMatch(e.Pattern, file)))
            {
                continue; // どの pattern にも合致しない = 管理対象外
            }
            targets.Add(new FireTarget(file, languageId));
        }
        return targets;
    }

    /// <summary>検査対象 1 件（パスと解析に使う言語 ID）。</summary>
    private readonly record struct FireTarget(string Path, string LanguageId);

    /// <summary>スキャンで違反が見つかったファイル 1 件。</summary>
    private readonly record struct FileFinding(string Path, IReadOnlyList<Literal> Literals);

    /// <summary>reason に列挙する違反ファイルの最大件数と、1 ファイルあたりのリテラルの最大件数。</summary>
    private const int MaxReportedFiles = 50;
    private const int MaxReportedPerFile = 5;

    /// <summary>reason に列挙する重複文字列の最大件数と、1 文字列あたりの出現箇所の最大件数。</summary>
    private const int MaxReportedDuplicates = 20;
    private const int MaxReportedOccurrences = 5;

    /// <summary>same-string 検査で重複が見つかった allow 群 1 件。</summary>
    private readonly record struct DuplicateGroup(string Allow, IReadOnlyList<DuplicateString> Duplicates);

    /// <summary>
    /// same-string の違反 reason。allow（ハードコード可の定数ファイル）群を横断して同一文字列が
    /// 2 箇所以上に定義されている状態を、統合すべき重複として提示する。
    /// </summary>
    private static string BuildDuplicateReason(IReadOnlyList<DuplicateGroup> groups)
    {
        var total = groups.Sum(g => g.Duplicates.Count);
        var sb = new StringBuilder();
        sb.Append("定数ファイル内で同じ文字列リテラルが重複しています（計 ").Append(total).Append(" 件）:\n");
        foreach (var group in groups)
        {
            sb.Append("- allow='").Append(group.Allow).Append("'（same-string: true）\n");
            foreach (var duplicate in group.Duplicates.Take(MaxReportedDuplicates))
            {
                sb.Append("    - ").Append(duplicate.Text)
                    .Append("（").Append(duplicate.Occurrences.Count).Append(" 箇所）\n");
                foreach (var occurrence in duplicate.Occurrences.Take(MaxReportedOccurrences))
                {
                    sb.Append($"        - {occurrence.Path}: {occurrence.Line} 行目\n");
                }
                if (duplicate.Occurrences.Count > MaxReportedOccurrences)
                {
                    sb.Append($"        - …ほか {duplicate.Occurrences.Count - MaxReportedOccurrences} 箇所\n");
                }
            }
            if (group.Duplicates.Count > MaxReportedDuplicates)
            {
                sb.Append($"    - …ほか {group.Duplicates.Count - MaxReportedDuplicates} 件\n");
            }
        }
        sb.Append("\n重複した文字列は 1 つの定数へ統合し、各所からその定数を参照してください。");
        return sb.ToString();
    }

    private static string BuildFireReason(IReadOnlyList<FileFinding> findings)
    {
        var total = findings.Sum(f => f.Literals.Count);
        var sb = new StringBuilder();
        sb.Append("許可されていない場所にハードコード値（数値・文字列リテラル）があります（")
            .Append(findings.Count).Append(" ファイル / 計 ").Append(total).Append(" 件）:\n");
        foreach (var finding in findings.Take(MaxReportedFiles))
        {
            sb.Append("- ").Append(finding.Path).Append("（").Append(finding.Literals.Count).Append(" 件）\n");
            foreach (var lit in finding.Literals.Take(MaxReportedPerFile))
            {
                sb.Append($"    - {lit.Line} 行目 [{lit.Kind}]: {lit.Text}\n");
            }
            if (finding.Literals.Count > MaxReportedPerFile)
            {
                sb.Append($"    - …ほか {finding.Literals.Count - MaxReportedPerFile} 件\n");
            }
        }
        if (findings.Count > MaxReportedFiles)
        {
            sb.Append("- …ほか ").Append(findings.Count - MaxReportedFiles).Append(" ファイル\n");
        }
        sb.Append("\nこれらの値は設定 allow で許可された定数ファイルへ切り出し、各ファイルからは定数を参照してください。");
        return sb.ToString();
    }

    private static string BuildConfigErrorReason(IReadOnlyList<string> errors)
    {
        var sb = new StringBuilder();
        sb.Append("ai-harness-constants の設定が不正なため、ソースファイルの書き込みをブロックしました（フェイルクローズ）。\n");
        sb.Append("ai-harness-constants.yml を修正してください:\n- ");
        sb.Append(string.Join("\n- ", errors));
        return sb.ToString();
    }

    private static string BuildViolationReason(string filePath, IReadOnlyList<Literal> literals)
    {
        var sb = new StringBuilder();
        sb.Append("ハードコード値（数値・文字列リテラル）が許可されていない場所にあります: '").Append(filePath).Append("'\n\n");
        sb.Append("検出したリテラル:\n");
        foreach (var lit in literals.Take(MaxReported))
        {
            sb.Append($"- {lit.Line} 行目 [{lit.Kind}]: {lit.Text}\n");
        }
        if (literals.Count > MaxReported)
        {
            sb.Append($"- …ほか {literals.Count - MaxReported} 件\n");
        }
        sb.Append("\nこれらの値は設定 allow で許可された定数ファイルへ切り出し、このファイルからは定数を参照してください。");
        return sb.ToString();
    }

    /// <summary>検査対象のファイルパス。tool_input.file_path 優先、無ければトップレベル file_path。</summary>
    private static string? ExtractFilePath(HookData data) =>
        AsString(GetMember(data.ToolInput, "file_path")) ?? data.FilePath;

    private static JsonNode? GetMember(JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out var v) ? v : null;

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
