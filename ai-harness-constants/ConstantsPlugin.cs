using System.Text;
using System.Text.Json.Nodes;
using ai_harness_baselib;

namespace ai_harness_constants;

/// <summary>
/// プログラム中のハードコード値（数値・文字列リテラル）を、設定で許可した定数ファイル以外に
/// 書かせないよう強制するプラグイン。書き込み系ツール（Write / Edit / MultiEdit）の
/// <c>PostToolUse</c> で発火し、書き込んだファイルを tree-sitter で AST 解析する。
///
/// 判定（対応言語のソースファイルのみが検査対象。.md 等の非ソースは対象外＝許可）:
///   1. 設定が使用不可            → deny（フェイルクローズ。エラー内容を提示）
///   2. いずれかの allow にマッチ  → 許可（ハードコードを許可した定数ファイル）
///   3. いずれかの pattern にマッチ → AST 解析。リテラルがあれば deny、無ければ許可
///   4. どの pattern にもマッチせず → 許可（このプラグインの管理対象外）
///
/// テストファイル等を除外したい場合は pattern に含めなければよい（4 で許可される）。
///
/// PostToolUse のため書き込み自体は止められない。deny（exit 2）で Claude に差し戻し、
/// リテラルを許可された定数ファイルへ移動させる。
/// </summary>
public sealed class ConstantsPlugin : PluginBase
{
    public override string PluginName => "ai-harness-constants";

    /// <summary>PostToolUse の全ツールで発火し、Action 内で書き込み系ツールを自己フィルタ。</summary>
    public override IReadOnlyList<string> Events => new[] { "PostToolUse" };

    public override string ConfigName => "ai-harness-constants.yml";

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
        if (config.Entries.Any(e => GlobMatcher.IsMatch(e.Allow, filePath)))
        {
            yield return LogEntry.Debug($"allow 対象のため許可: {filePath}");
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
