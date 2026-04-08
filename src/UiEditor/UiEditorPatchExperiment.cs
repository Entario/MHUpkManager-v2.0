using System.Text;
using System.Text.Json;

namespace MHUpkManager.UiEditor;

internal sealed class EnemyClientUiPatchExperiment
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly EnemyClientUiTargetFinder _targetFinder = new();
    private readonly UpkRawExportPatcher _exportPatcher = new();

    public async Task<EnemyClientUiPatchExperimentResult> CreateDryRunPatchedCopyAsync(string packagePath, string heroId, string outputDirectory, Action<string> log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(heroId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(log);
        Directory.CreateDirectory(outputDirectory);

        bool isTeamUp = packagePath.Contains("MarvelHUD_SF.upk", StringComparison.OrdinalIgnoreCase) &&
                        (heroId.Contains("agent", StringComparison.OrdinalIgnoreCase) || heroId.Contains("team", StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<EnemyClientUiTarget> targets = await _targetFinder.FindTargetsAsync(packagePath, heroId, isTeamUp).ConfigureAwait(false);
        string outputPath = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(packagePath)}.dryrun.upk");

        EnemyClientUiTarget swfTarget = SelectDryRunSwfTarget(targets, isTeamUp);
        EnemyClientUiPatchExperimentResult result;
        if (swfTarget != null)
        {
            byte[] originalBytes = await File.ReadAllBytesAsync(packagePath).ConfigureAwait(false);
            byte[] patchedBytes = PatchFirstMatchingString(originalBytes, swfTarget, isTeamUp, out EnemyClientUiStringPatch appliedPatch);
            await _exportPatcher.PatchExportsAsync(packagePath, new Dictionary<int, byte[]> { [swfTarget.ExportIndex] = patchedBytes }, outputPath).ConfigureAwait(false);
            result = new EnemyClientUiPatchExperimentResult
            {
                SourcePackagePath = packagePath,
                OutputPackagePath = outputPath,
                HeroId = heroId,
                TargetExportIndex = swfTarget.ExportIndex,
                TargetExportPath = swfTarget.ExportPath,
                TargetClassName = swfTarget.ClassName,
                AppliedPatch = appliedPatch,
                PatchMode = "ExportBuffer"
            };
        }
        else
        {
            EnemyClientUiTarget logicalTarget = SelectDryRunLogicalTarget(targets)
                ?? throw new InvalidOperationException($"No suitable UI export or icon target was found in {Path.GetFileName(packagePath)} for hero '{heroId}'.");
            byte[] logicalBytes = await _exportPatcher.GetLogicalPackageBytesAsync(packagePath).ConfigureAwait(false);
            EnemyClientUiStringPatch appliedPatch = PatchFirstMatchingLogicalString(logicalBytes, logicalTarget, out int logicalOffset);
            await _exportPatcher.PatchLogicalOffsetsAsync(packagePath, new Dictionary<int, byte[]> { [logicalOffset] = Encoding.ASCII.GetBytes(appliedPatch.PatchedValue) }, outputPath).ConfigureAwait(false);
            result = new EnemyClientUiPatchExperimentResult
            {
                SourcePackagePath = packagePath,
                OutputPackagePath = outputPath,
                HeroId = heroId,
                TargetExportIndex = logicalTarget.ExportIndex,
                TargetExportPath = logicalTarget.ExportPath,
                TargetClassName = logicalTarget.ClassName,
                AppliedPatch = appliedPatch,
                PatchMode = "LogicalPackage",
                LogicalPackageOffset = logicalOffset
            };
        }

        string reportPath = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(packagePath)}.dryrun.report.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(result, JsonOptions)).ConfigureAwait(false);
        result.ReportPath = reportPath;
        return result;
    }

    private static EnemyClientUiTarget SelectDryRunSwfTarget(IReadOnlyList<EnemyClientUiTarget> targets, bool isTeamUp)
    {
        if (isTeamUp)
        {
            return targets.FirstOrDefault(static t => t.ClassName.Equals("swfmovie", StringComparison.OrdinalIgnoreCase) && t.ExportPath.Equals("marvelhud.teamuppanel_v2", StringComparison.OrdinalIgnoreCase))
                ?? targets.FirstOrDefault(static t => t.ClassName.Equals("swfmovie", StringComparison.OrdinalIgnoreCase) && t.ExportPath.Equals("marvelhud.rosterpanel", StringComparison.OrdinalIgnoreCase))
                ?? targets.FirstOrDefault(static t => t.ClassName.Equals("swfmovie", StringComparison.OrdinalIgnoreCase) && t.ExportPath.Equals("marvelhud.mtxstorepanel", StringComparison.OrdinalIgnoreCase))
                ?? targets.FirstOrDefault(static t => t.ClassName.Equals("swfmovie", StringComparison.OrdinalIgnoreCase));
        }

        return targets.FirstOrDefault(static t => t.ClassName.Equals("swfmovie", StringComparison.OrdinalIgnoreCase) && t.ExportPath.Equals("marvelhud.rosterpanel", StringComparison.OrdinalIgnoreCase))
            ?? targets.FirstOrDefault(static t => t.ClassName.Equals("swfmovie", StringComparison.OrdinalIgnoreCase) && t.ExportPath.Equals("marvelhud.!uisource", StringComparison.OrdinalIgnoreCase))
            ?? targets.FirstOrDefault(static t => t.ClassName.Equals("swfmovie", StringComparison.OrdinalIgnoreCase));
    }

    private static EnemyClientUiTarget SelectDryRunLogicalTarget(IReadOnlyList<EnemyClientUiTarget> targets)
    {
        return targets.FirstOrDefault(static t => t.ExportPath.Contains("btn_", StringComparison.OrdinalIgnoreCase))
            ?? targets.FirstOrDefault(static t => t.ExportPath.Contains("store_", StringComparison.OrdinalIgnoreCase))
            ?? targets.FirstOrDefault(static t => t.ClassName.Equals("texture2d", StringComparison.OrdinalIgnoreCase))
            ?? targets.FirstOrDefault();
    }

    private static byte[] PatchFirstMatchingString(byte[] packageBytes, EnemyClientUiTarget target, bool isTeamUp, out EnemyClientUiStringPatch appliedPatch)
    {
        byte[] exportBytes = new byte[target.SerialSize];
        Buffer.BlockCopy(packageBytes, target.SerialOffset, exportBytes, 0, exportBytes.Length);
        string[] candidateStrings = isTeamUp
            ? ["TeamUpInventoryPanel", "teamUpName_tf", "CONTEXT_TEAMUP", "highlightTeamUps", "RosterPanel"]
            : ["HeroSelectRenderer", "characterUnlockDialog", "RosterPanel", "ButtonRoster", "ButtonStore", "IronMan"];

        foreach (string original in candidateStrings)
        {
            int offset = FindAscii(exportBytes, original);
            if (offset < 0) continue;
            string patched = BuildSameLengthPatchValue(original);
            byte[] patchedBytes = (byte[])exportBytes.Clone();
            Encoding.ASCII.GetBytes(patched, 0, patched.Length, patchedBytes, offset);
            appliedPatch = new EnemyClientUiStringPatch { OriginalValue = original, PatchedValue = patched, ExportRelativeOffset = offset };
            return patchedBytes;
        }

        throw new InvalidOperationException($"No supported dry-run marker string was found inside export {target.ExportPath}.");
    }

    private static EnemyClientUiStringPatch PatchFirstMatchingLogicalString(byte[] logicalBytes, EnemyClientUiTarget target, out int logicalOffset)
    {
        string objectName = target.ExportPath.Contains('.') ? target.ExportPath[(target.ExportPath.LastIndexOf('.') + 1)..] : target.ExportPath;
        foreach (string original in new[] { objectName, target.ExportPath }.Where(static v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            int offset = FindAscii(logicalBytes, original);
            if (offset < 0) continue;
            logicalOffset = offset;
            return new EnemyClientUiStringPatch { OriginalValue = original, PatchedValue = BuildSameLengthPatchValue(original), ExportRelativeOffset = -1 };
        }

        throw new InvalidOperationException($"No supported dry-run marker string was found for logical target {target.ExportPath}.");
    }

    private static string BuildSameLengthPatchValue(string original)
    {
        if (original.Length == 0) return original;
        char last = original[^1];
        char replacement = last switch { 'Z' => 'Y', 'z' => 'y', '9' => '8', _ => (char)(last + 1) };
        return original[..^1] + replacement;
    }

    private static int FindAscii(byte[] bytes, string value)
    {
        byte[] needle = Encoding.ASCII.GetBytes(value);
        for (int index = 0; index <= bytes.Length - needle.Length; index++)
        {
            bool matched = true;
            for (int needleIndex = 0; needleIndex < needle.Length; needleIndex++)
            {
                if (bytes[index + needleIndex] == needle[needleIndex]) continue;
                matched = false;
                break;
            }
            if (matched) return index;
        }
        return -1;
    }
}

internal sealed class EnemyClientUiPatchExperimentResult
{
    public string SourcePackagePath { get; set; } = string.Empty;
    public string OutputPackagePath { get; set; } = string.Empty;
    public string ReportPath { get; set; } = string.Empty;
    public string HeroId { get; set; } = string.Empty;
    public int TargetExportIndex { get; set; }
    public string TargetExportPath { get; set; } = string.Empty;
    public string TargetClassName { get; set; } = string.Empty;
    public string PatchMode { get; set; } = string.Empty;
    public int LogicalPackageOffset { get; set; } = -1;
    public EnemyClientUiStringPatch AppliedPatch { get; set; } = new();
}

internal sealed class EnemyClientUiStringPatch
{
    public string OriginalValue { get; set; } = string.Empty;
    public string PatchedValue { get; set; } = string.Empty;
    public int ExportRelativeOffset { get; set; }
}
