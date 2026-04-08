using System.Text;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Objects;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace MHUpkManager.UiEditor;

internal sealed class EnemyClientUiTarget
{
    public string PackagePath { get; init; } = string.Empty;
    public int ExportIndex { get; init; }
    public string ExportPath { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public int SerialOffset { get; init; }
    public int SerialSize { get; init; }
    public List<string> RawStringHits { get; } = [];
    public List<string> FieldHits { get; } = [];
    public List<string> ContractHints { get; } = [];
    public int RelevanceScore { get; init; }
}

internal sealed class EnemyClientUiTargetFinder
{
    private static readonly string[] PreferredExportNames =
    [
        "marvelfrontend.marvel_character_select",
        "marvelhud.!uisource",
        "marvelhud.characterunlockdialog",
        "marvelhud.rosterpanel",
        "marvelhud.hud_manager",
        "marvelhud.mtxstorepanel",
        "marvelfrontend.!uisource",
        "marvelhud.teamuppanel_v2"
    ];

    private static readonly string[] ContractPatterns =
    [
        "PrimaryAvatar",
        "avatarIndexTypeString",
        "itemRenderer",
        "RosterTileList",
        "CharacterSelectTileList",
        "StartingHeroRosterListItemRenderer",
        "MarvelRosterListItemRenderer",
        "TeamUpRosterListItemRenderer",
        "Btn_HeroPortrait",
        "StartGameAsHero",
        "HeroSelect"
    ];

    public async Task<IReadOnlyList<EnemyClientUiTarget>> FindTargetsAsync(string packagePath, string heroName, bool isTeamUp = false)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            return Array.Empty<EnemyClientUiTarget>();

        string compactHero = string.Concat((heroName ?? string.Empty).Where(static ch => !char.IsWhiteSpace(ch)));
        string normalizedHero = compactHero.ToLowerInvariant();

        HashSet<string> patterns =
        [
            "HeroSelect", "Roster", "Team-Up", "TeamUp", "bHeroUnlocked", "CharacterUnlock",
            "currentStoreTransactionSkuId", "ButtonRoster", "ButtonStore", "PrimaryAvatar",
            "avatarIndexTypeString", "itemRenderer", "RosterTileList", "CharacterSelectTileList",
        ];

        if (!string.IsNullOrWhiteSpace(heroName))
            patterns.Add(heroName);

        if (!string.IsNullOrWhiteSpace(compactHero))
        {
            patterns.Add(compactHero);
            patterns.Add($"btn_{normalizedHero}");
            patterns.Add($"store_{normalizedHero}");
            patterns.Add($"btn_teamup_{normalizedHero}");
            patterns.Add($"store_teamup_{normalizedHero}");
            patterns.Add($"Avatar{compactHero}");
            patterns.Add($"PlayerStashForAvatar{compactHero}");
            patterns.Add($"TeamUp{compactHero}");
            patterns.Add($"Btn_TeamUp_{compactHero}");
        }

        if (isTeamUp)
        {
            patterns.Add("Team-Up Purchase");
            patterns.Add("Roster_Team-Up");
            patterns.Add("teamup");
        }

        UpkFileRepository repository = new();
        UnrealHeader header = await repository.LoadUpkFile(packagePath).ConfigureAwait(false);
        await header.ReadHeaderAsync(null).ConfigureAwait(false);

        List<EnemyClientUiTarget> matches = [];
        foreach (UnrealExportTableEntry export in header.ExportTable)
        {
            string exportPath = export.GetPathName();
            string className = export.ClassReferenceNameIndex?.Name ?? string.Empty;
            byte[] raw = export.UnrealObjectReader?.GetBytes() ?? [];
            List<string> rawHits = ExtractPrintableStrings(raw)
                .Where(value => patterns.Any(pattern => value.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();

            List<string> fieldHits = [];
            try
            {
                await export.ParseUnrealObject(false, false).ConfigureAwait(false);
                if (export.UnrealObject is IUnrealObject unrealObject)
                {
                    fieldHits.AddRange(unrealObject.FieldNodes
                        .Select(static node => node.Text)
                        .Where(static text => !string.IsNullOrWhiteSpace(text))
                        .Where(text => patterns.Any(pattern => text.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(20));
                }
            }
            catch
            {
            }

            bool exportPathMatches = patterns.Any(pattern => exportPath.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            bool preferredByName = PreferredExportNames.Contains(exportPath, StringComparer.OrdinalIgnoreCase);
            if (!preferredByName && !exportPathMatches && rawHits.Count == 0 && fieldHits.Count == 0)
                continue;

            List<string> contractHints = rawHits
                .Concat(fieldHits)
                .Where(value => ContractPatterns.Any(pattern => value.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();

            int relevanceScore = 0;
            if (preferredByName) relevanceScore += 100;
            if (exportPathMatches) relevanceScore += 60;
            if (exportPath.Contains("marvel_character_select", StringComparison.OrdinalIgnoreCase)) relevanceScore += 40;
            if (exportPath.Contains("rosterpanel", StringComparison.OrdinalIgnoreCase)) relevanceScore += 35;
            if (exportPath.Contains("characterunlockdialog", StringComparison.OrdinalIgnoreCase)) relevanceScore += 30;
            if (exportPath.Contains("!uisource", StringComparison.OrdinalIgnoreCase)) relevanceScore += 20;
            if (exportPath.Contains($"store_teamup_{normalizedHero}", StringComparison.OrdinalIgnoreCase)) relevanceScore += 80;
            if (exportPath.Contains($"btn_teamup_{normalizedHero}", StringComparison.OrdinalIgnoreCase)) relevanceScore += 70;
            if (exportPath.Contains("store_", StringComparison.OrdinalIgnoreCase)) relevanceScore += 45;
            if (exportPath.Contains("btn_", StringComparison.OrdinalIgnoreCase)) relevanceScore += 40;
            if (exportPath.Contains("teamuppanel", StringComparison.OrdinalIgnoreCase)) relevanceScore += 45;
            relevanceScore += rawHits.Count * 2;
            relevanceScore += fieldHits.Count;
            relevanceScore += contractHints.Count * 5;

            EnemyClientUiTarget target = new()
            {
                PackagePath = packagePath,
                ExportIndex = export.TableIndex,
                ExportPath = exportPath,
                ClassName = className,
                SerialOffset = export.SerialDataOffset,
                SerialSize = export.SerialDataSize,
                RelevanceScore = relevanceScore
            };
            target.RawStringHits.AddRange(rawHits);
            target.FieldHits.AddRange(fieldHits);
            target.ContractHints.AddRange(contractHints);
            matches.Add(target);
        }

        return matches
            .OrderByDescending(static target => target.RelevanceScore)
            .ThenByDescending(static target => PreferredExportNames.Contains(target.ExportPath, StringComparer.OrdinalIgnoreCase))
            .ThenByDescending(static target => target.ContractHints.Count)
            .ThenByDescending(static target => target.RawStringHits.Count)
            .ThenBy(static target => target.ExportPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> ExtractPrintableStrings(byte[] bytes)
    {
        int index = 0;
        while (index < bytes.Length)
        {
            int start = index;
            StringBuilder builder = new();
            while (index < bytes.Length && bytes[index] is >= 0x20 and <= 0x7E)
            {
                builder.Append((char)bytes[index]);
                index++;
            }

            if (builder.Length >= 4)
                yield return builder.ToString();

            index = Math.Max(index + 1, start + 1);
        }
    }
}
