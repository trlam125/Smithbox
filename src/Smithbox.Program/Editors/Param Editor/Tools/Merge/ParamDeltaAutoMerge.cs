using Andre.Formats;
using Hexa.NET.ImGui;
using StudioCore.Application;
using StudioCore.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace StudioCore.Editors.ParamEditor;

public enum ParamDeltaConflictStrategy
{
    StopOnConflict = 0,
    PreferFirst = 1,
    PreferLast = 2
}

public enum ParamDeltaMergeConflictType
{
    FieldValue = 0,
    RowState = 1,
    RowName = 2
}

public enum ParamDeltaConflictResolution
{
    Unresolved = 0,
    UseEarlier = 1,
    UseLater = 2,
    Manual = 3
}

public sealed class ParamDeltaMergeConflict
{
    public ParamDeltaMergeConflictType Type { get; init; }
    public string ParamName { get; init; } = "";
    public int RowID { get; init; }
    public int RowIndex { get; init; }
    public string Field { get; init; } = "";
    public string ExistingSource { get; init; } = "";
    public string IncomingSource { get; init; } = "";
    public string BaseValue { get; init; } = "";
    public string ExistingValue { get; init; } = "";
    public string IncomingValue { get; init; } = "";
    public RowDeltaState ExistingState { get; init; }
    public RowDeltaState IncomingState { get; init; }
    public ParamDeltaConflictResolution Resolution { get; set; } = ParamDeltaConflictResolution.Unresolved;
    public string ManualValue { get; set; } = "";
    public string ManualValueType { get; init; } = "";
    public bool ManualValueValid { get; set; } = true;

    internal RowDelta ExistingRowSnapshot { get; init; }
    internal RowDelta IncomingRowSnapshot { get; init; }

    public bool IsResolved =>
        Resolution != ParamDeltaConflictResolution.Unresolved &&
        (Resolution != ParamDeltaConflictResolution.Manual || ManualValueValid);
}

public sealed class ParamDeltaAutoMergeResult
{
    public ParamDeltaPatch Patch { get; init; } = new();
    internal ParamDeltaPatch BaselinePatch { get; set; }
    public List<ParamDeltaMergeConflict> Conflicts { get; } = new();
    public List<string> Errors { get; } = new();
    public int SourceCount { get; set; }
    public int SafeRows { get; set; }
    public int SafeFields { get; set; }
    public int IdenticalFields { get; set; }
    public ParamDeltaConflictStrategy Strategy { get; set; }
    internal List<DeltaImportEntry> Sources { get; } = new();

    public int ResolvedConflicts => Conflicts.Count(e => e.IsResolved);
    public int UnresolvedConflicts => Conflicts.Count - ResolvedConflicts;

    public bool CanApply => Errors.Count == 0 && Conflicts.All(e => e.IsResolved);
}

public sealed class ParamDeltaAutoMergeEngine
{
    private readonly ParamDeltaPatcher Patcher;

    private sealed class MergedRow
    {
        public RowDelta Row { get; set; } = new();
        public string RowSource { get; set; } = "";
        public string NameSource { get; set; } = "";
        public Dictionary<string, string> FieldSources { get; } = new(StringComparer.Ordinal);
    }

    private readonly record struct RowKey(string ParamName, int ID, int Index);
    private readonly record struct ConflictChoice(ParamDeltaConflictResolution Resolution, string ManualValue);

    public ParamDeltaAutoMergeEngine(ParamDeltaPatcher patcher)
    {
        Patcher = patcher;
    }

    public static ParamDeltaConflictResolution ResolutionFromStrategy(ParamDeltaConflictStrategy strategy)
    {
        return strategy switch
        {
            ParamDeltaConflictStrategy.PreferFirst => ParamDeltaConflictResolution.UseEarlier,
            ParamDeltaConflictStrategy.PreferLast => ParamDeltaConflictResolution.UseLater,
            _ => ParamDeltaConflictResolution.Unresolved
        };
    }

    public void ApplyConflictResolutions(ParamDeltaAutoMergeResult result)
    {
        if (result == null || result.Sources.Count == 0)
            return;

        RebuildMergedState(result, preserveConflictChoices: true);
    }

    public ParamDeltaAutoMergeResult Merge(
        IReadOnlyList<DeltaImportEntry> sources,
        ParamDeltaConflictStrategy strategy)
    {
        var result = new ParamDeltaAutoMergeResult
        {
            SourceCount = sources.Count,
            Strategy = strategy,
            Patch = new ParamDeltaPatch
            {
                ProjectType = Patcher.Project.Descriptor.ProjectType,
                ParamVersion = Patcher.Project.Handler.ParamData.PrimaryBank.ParamVersion,
                Tag = LOC.Get("PARAM_AutoMerge_Patch_Tag", sources.Count)
            }
        };

        if (sources.Count < 2)
        {
            result.Errors.Add(LOC.Get("PARAM_AutoMerge_Missing_Two_Delta_Patches"));
            return result;
        }

        ValidateSources(sources, result);
        if (result.Errors.Count > 0)
            return result;

        foreach (var source in sources)
        {
            result.Sources.Add(new DeltaImportEntry
            {
                Filename = source.Filename,
                Delta = ClonePatch(source.Delta)
            });
        }

        RebuildMergedState(result, preserveConflictChoices: false);
        return result;
    }

    private void RebuildMergedState(ParamDeltaAutoMergeResult result, bool preserveConflictChoices)
    {
        Dictionary<string, ConflictChoice> previousChoices = null;
        if (preserveConflictChoices)
        {
            previousChoices = result.Conflicts
                .GroupBy(ConflictIdentity, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => new ConflictChoice(group.Last().Resolution, group.Last().ManualValue ?? ""),
                    StringComparer.Ordinal);
        }

        result.Conflicts.Clear();
        result.Patch.Params.Clear();
        result.SafeRows = 0;
        result.SafeFields = 0;
        result.IdenticalFields = 0;

        var mergedRows = new Dictionary<RowKey, MergedRow>();

        foreach (var source in result.Sources)
        {
            foreach (var paramDelta in source.Delta.Params)
            {
                foreach (var originalRow in paramDelta.Rows)
                {
                    var incoming = NormalizeRow(paramDelta.Name, originalRow);
                    var key = new RowKey(paramDelta.Name, incoming.ID, incoming.Index);

                    if (!mergedRows.TryGetValue(key, out var current))
                    {
                        var cloned = CloneRow(incoming);
                        current = new MergedRow
                        {
                            Row = cloned,
                            RowSource = source.Filename,
                            NameSource = cloned.Name != null ? source.Filename : ""
                        };

                        foreach (var field in cloned.Fields)
                            current.FieldSources[field.Field] = source.Filename;

                        mergedRows.Add(key, current);
                        result.SafeRows++;
                        result.SafeFields += cloned.Fields.Count;
                        continue;
                    }

                    MergeRow(key, current, incoming, source.Filename, result.Strategy, previousChoices, result);
                }
            }
        }

        foreach (var paramGroup in mergedRows
                     .OrderBy(e => e.Key.ParamName, StringComparer.Ordinal)
                     .ThenBy(e => e.Key.ID)
                     .ThenBy(e => e.Key.Index)
                     .GroupBy(e => e.Key.ParamName, StringComparer.Ordinal))
        {
            var paramDelta = new ParamDelta { Name = paramGroup.Key };

            foreach (var row in paramGroup)
            {
                if (row.Value.Row.State == RowDeltaState.Modified &&
                    row.Value.Row.Fields.Count == 0 &&
                    row.Value.Row.Name == null)
                {
                    continue;
                }

                paramDelta.Rows.Add(CloneRow(row.Value.Row));
            }

            if (paramDelta.Rows.Count > 0)
                result.Patch.Params.Add(paramDelta);
        }

        result.BaselinePatch = ClonePatch(result.Patch);
    }

    private void ValidateSources(IReadOnlyList<DeltaImportEntry> sources, ParamDeltaAutoMergeResult result)
    {
        var projectType = Patcher.Project.Descriptor.ProjectType;
        var paramVersion = Patcher.Project.Handler.ParamData.PrimaryBank.ParamVersion;

        foreach (var source in sources)
        {
            if (source.Delta.ProjectType != projectType)
            {
                result.Errors.Add(
                    LOC.Get("PARAM_AutoMerge_Invalid_Project_Type", source.Filename, source.Delta.ProjectType, projectType));
            }

            if (source.Delta.ParamVersion != paramVersion)
            {
                result.Errors.Add(
                    LOC.Get("PARAM_AutoMerge_Invalid_Param_Version", source.Filename,
                    ParamUtils.ParseRegulationVersion(source.Delta.ParamVersion),
                    ParamUtils.ParseRegulationVersion(paramVersion)));
            }
        }
    }

    private void MergeRow(
        RowKey key,
        MergedRow current,
        RowDelta incoming,
        string incomingSource,
        ParamDeltaConflictStrategy strategy,
        IReadOnlyDictionary<string, ConflictChoice> previousChoices,
        ParamDeltaAutoMergeResult result)
    {
        if (current.Row.State == RowDeltaState.Added && incoming.State == RowDeltaState.Added)
        {
            if (RowsEquivalent(current.Row, incoming))
            {
                result.SafeRows++;
                result.IdenticalFields += incoming.Fields.Count;
                return;
            }

            var conflict = new ParamDeltaMergeConflict
            {
                Type = ParamDeltaMergeConflictType.RowState,
                ParamName = key.ParamName,
                RowID = key.ID,
                RowIndex = key.Index,
                ExistingSource = current.RowSource,
                IncomingSource = incomingSource,
                BaseValue = "Missing",
                ExistingState = RowDeltaState.Added,
                IncomingState = RowDeltaState.Added,
                ExistingValue = RowSummary(current.Row),
                IncomingValue = RowSummary(incoming),
                Resolution = ResolutionFromStrategy(strategy),
                ExistingRowSnapshot = CloneRow(current.Row),
                IncomingRowSnapshot = CloneRow(incoming)
            };
            RestoreConflictChoice(conflict, previousChoices);
            result.Conflicts.Add(conflict);

            if (conflict.Resolution == ParamDeltaConflictResolution.UseLater)
                ReplaceCurrentRow(current, incoming, incomingSource);

            return;
        }

        if (current.Row.State == RowDeltaState.Deleted || incoming.State == RowDeltaState.Deleted)
        {
            if (current.Row.State == RowDeltaState.Deleted && incoming.State == RowDeltaState.Deleted)
                return;

            var conflict = new ParamDeltaMergeConflict
            {
                Type = ParamDeltaMergeConflictType.RowState,
                ParamName = key.ParamName,
                RowID = key.ID,
                RowIndex = key.Index,
                ExistingSource = current.RowSource,
                IncomingSource = incomingSource,
                BaseValue = VanillaRowExists(key.ParamName, key.ID, key.Index) ? "Exists" : "Missing",
                ExistingState = current.Row.State,
                IncomingState = incoming.State,
                ExistingValue = RowSummary(current.Row),
                IncomingValue = RowSummary(incoming),
                Resolution = ResolutionFromStrategy(strategy),
                ExistingRowSnapshot = CloneRow(current.Row),
                IncomingRowSnapshot = CloneRow(incoming)
            };
            RestoreConflictChoice(conflict, previousChoices);
            result.Conflicts.Add(conflict);

            if (conflict.Resolution == ParamDeltaConflictResolution.UseLater)
                ReplaceCurrentRow(current, incoming, incomingSource);

            return;
        }

        // The actual row state is determined against vanilla, not by the source delta.
        // This is important because Selected/All delta exports represent full rows as Added.
        current.Row.State = VanillaRowExists(key.ParamName, key.ID, key.Index)
            ? RowDeltaState.Modified
            : RowDeltaState.Added;

        MergeRowName(key, current, incoming, incomingSource, strategy, previousChoices, result);

        foreach (var incomingField in incoming.Fields)
        {
            var existingField = current.Row.Fields.FirstOrDefault(e => e.Field == incomingField.Field);
            if (existingField == null)
            {
                current.Row.Fields.Add(CloneField(incomingField));
                current.FieldSources[incomingField.Field] = incomingSource;
                result.SafeFields++;
                continue;
            }

            if (string.Equals(existingField.Value, incomingField.Value, StringComparison.Ordinal))
            {
                result.IdenticalFields++;
                continue;
            }

            var existingSource = current.FieldSources.TryGetValue(incomingField.Field, out var fieldSource)
                ? fieldSource
                : current.RowSource;

            var conflict = new ParamDeltaMergeConflict
            {
                Type = ParamDeltaMergeConflictType.FieldValue,
                ParamName = key.ParamName,
                RowID = key.ID,
                RowIndex = key.Index,
                Field = incomingField.Field,
                ExistingSource = existingSource,
                IncomingSource = incomingSource,
                BaseValue = GetVanillaFieldValue(key.ParamName, key.ID, key.Index, incomingField.Field),
                ExistingValue = existingField.Value,
                IncomingValue = incomingField.Value,
                ExistingState = current.Row.State,
                IncomingState = incoming.State,
                ManualValueType = GetVanillaFieldTypeName(key.ParamName, key.ID, key.Index, incomingField.Field),
                Resolution = ResolutionFromStrategy(strategy)
            };
            RestoreConflictChoice(conflict, previousChoices);
            conflict.ManualValueValid =
                conflict.Resolution != ParamDeltaConflictResolution.Manual ||
                IsValidManualFieldValue(key.ParamName, key.ID, key.Index, incomingField.Field, conflict.ManualValue);
            result.Conflicts.Add(conflict);

            switch (conflict.Resolution)
            {
                case ParamDeltaConflictResolution.UseLater:
                    existingField.Value = incomingField.Value;
                    current.FieldSources[incomingField.Field] = incomingSource;
                    break;
                case ParamDeltaConflictResolution.Manual when conflict.ManualValueValid:
                    existingField.Value = conflict.ManualValue ?? "";
                    current.FieldSources[incomingField.Field] = "Manual resolution";
                    break;
            }
        }
    }

    private void MergeRowName(
        RowKey key,
        MergedRow current,
        RowDelta incoming,
        string incomingSource,
        ParamDeltaConflictStrategy strategy,
        IReadOnlyDictionary<string, ConflictChoice> previousChoices,
        ParamDeltaAutoMergeResult result)
    {
        // Null means this source did not change the vanilla row name. Empty string is a
        // legitimate explicit rename and must therefore not be treated as missing.
        if (incoming.Name == null)
            return;

        if (current.Row.Name == null)
        {
            current.Row.Name = incoming.Name;
            current.NameSource = incomingSource;
            return;
        }

        if (string.Equals(current.Row.Name, incoming.Name, StringComparison.Ordinal))
            return;

        var conflict = new ParamDeltaMergeConflict
        {
            Type = ParamDeltaMergeConflictType.RowName,
            ParamName = key.ParamName,
            RowID = key.ID,
            RowIndex = key.Index,
            ExistingSource = string.IsNullOrWhiteSpace(current.NameSource) ? current.RowSource : current.NameSource,
            IncomingSource = incomingSource,
            BaseValue = GetVanillaRow(key.ParamName, key.ID, key.Index)?.Name ?? "",
            ExistingValue = current.Row.Name ?? "",
            IncomingValue = incoming.Name ?? "",
            ExistingState = current.Row.State,
            IncomingState = incoming.State,
            Resolution = ResolutionFromStrategy(strategy)
        };
        RestoreConflictChoice(conflict, previousChoices);
        result.Conflicts.Add(conflict);

        switch (conflict.Resolution)
        {
            case ParamDeltaConflictResolution.UseLater:
                current.Row.Name = incoming.Name;
                current.NameSource = incomingSource;
                break;
            case ParamDeltaConflictResolution.Manual:
                current.Row.Name = conflict.ManualValue ?? "";
                current.NameSource = "Manual resolution";
                break;
        }
    }

    private static void ReplaceCurrentRow(MergedRow current, RowDelta incoming, string incomingSource)
    {
        current.Row = CloneRow(incoming);
        current.RowSource = incomingSource;
        current.NameSource = incoming.Name != null ? incomingSource : "";
        current.FieldSources.Clear();
        foreach (var field in current.Row.Fields)
            current.FieldSources[field.Field] = incomingSource;
    }

    private static void RestoreConflictChoice(
        ParamDeltaMergeConflict conflict,
        IReadOnlyDictionary<string, ConflictChoice> previousChoices)
    {
        if (previousChoices == null || !previousChoices.TryGetValue(ConflictIdentity(conflict), out var choice))
            return;

        conflict.Resolution = choice.Resolution;
        conflict.ManualValue = choice.ManualValue ?? "";
    }

    private static string ConflictIdentity(ParamDeltaMergeConflict conflict)
    {
        return $"{conflict.Type}|{conflict.ParamName}|{conflict.RowID}|{conflict.RowIndex}|{conflict.Field}|{conflict.IncomingSource}";
    }

    private RowDelta NormalizeRow(string paramName, RowDelta source)
    {
        var normalized = CloneRow(source);
        var vanillaRow = GetVanillaRow(paramName, source.ID, source.Index);

        if (source.State == RowDeltaState.Deleted)
        {
            normalized.Fields.Clear();
            return normalized;
        }

        if (vanillaRow == null)
        {
            normalized.State = RowDeltaState.Added;
            return normalized;
        }

        normalized.State = RowDeltaState.Modified;

        // Null means "no row-name change". Preserve an explicit empty string so a source
        // can intentionally clear a row name.
        if (source.Name == null || string.Equals(source.Name, vanillaRow.Name, StringComparison.Ordinal))
            normalized.Name = null;

        // A full-row delta (Selected/All export) may label an existing vanilla row as Added.
        // Remove values that are identical to vanilla so they do not create false conflicts.
        if (source.State == RowDeltaState.Added)
        {
            normalized.Fields = normalized.Fields
                .Where(field => !FieldEqualsVanilla(vanillaRow, field))
                .ToList();
        }

        return normalized;
    }

    private bool FieldEqualsVanilla(Param.Row vanillaRow, FieldDelta field)
    {
        var column = vanillaRow.Columns.FirstOrDefault(e => e.Def.InternalName == field.Field);
        if (column == null)
            return false;

        var value = column.GetValue(vanillaRow);
        string vanillaValue;

        if (column.Def.InternalType == "dummy8" && column.Def.ArrayLength > 1)
            vanillaValue = ParamUtils.Dummy8Write((byte[])value);
        else
            vanillaValue = value?.ToString() ?? "";

        return string.Equals(vanillaValue, field.Value, StringComparison.Ordinal);
    }

    private string GetVanillaFieldTypeName(string paramName, int id, int index, string fieldName)
    {
        var vanillaRow = GetVanillaRow(paramName, id, index);
        var column = vanillaRow?.Columns.FirstOrDefault(e => e.Def.InternalName == fieldName);
        return column?.Def.DisplayType.ToString() ?? "Unknown";
    }

    private bool IsValidManualFieldValue(string paramName, int id, int index, string fieldName, string value)
    {
        var vanillaRow = GetVanillaRow(paramName, id, index);
        var column = vanillaRow?.Columns.FirstOrDefault(e => e.Def.InternalName == fieldName);
        if (column == null)
            return false;

        switch (column.Def.DisplayType)
        {
            case PARAMDEF.DefType.s8:
                return sbyte.TryParse(value, out _);
            case PARAMDEF.DefType.s16:
                return short.TryParse(value, out _);
            case PARAMDEF.DefType.s32:
            case PARAMDEF.DefType.b32:
                return int.TryParse(value, out _);
            case PARAMDEF.DefType.f32:
            case PARAMDEF.DefType.angle32:
                return float.TryParse(value, out _);
            case PARAMDEF.DefType.f64:
                return double.TryParse(value, out _);
            case PARAMDEF.DefType.u8:
                return byte.TryParse(value, out _);
            case PARAMDEF.DefType.dummy8:
                if (column.Def.ArrayLength <= 1)
                    return byte.TryParse(value, out _);

                try
                {
                    return ParamUtils.Dummy8Read(value, column.Def.ArrayLength)?.Length == column.Def.ArrayLength;
                }
                catch
                {
                    return false;
                }
            case PARAMDEF.DefType.u16:
                return ushort.TryParse(value, out _);
            case PARAMDEF.DefType.u32:
                return uint.TryParse(value, out _);
            case PARAMDEF.DefType.fixstr:
            case PARAMDEF.DefType.fixstrW:
                return true;
            default:
                return false;
        }
    }

    private string GetVanillaFieldValue(string paramName, int id, int index, string fieldName)
    {
        var vanillaRow = GetVanillaRow(paramName, id, index);
        if (vanillaRow == null)
            return "<missing row>";

        var column = vanillaRow.Columns.FirstOrDefault(e => e.Def.InternalName == fieldName);
        if (column == null)
            return "<missing field>";

        var value = column.GetValue(vanillaRow);
        if (column.Def.InternalType == "dummy8" && column.Def.ArrayLength > 1)
            return ParamUtils.Dummy8Write((byte[])value);

        return value?.ToString() ?? "";
    }

    private bool VanillaRowExists(string paramName, int id, int index)
    {
        return GetVanillaRow(paramName, id, index) != null;
    }

    private Param.Row GetVanillaRow(string paramName, int id, int index)
    {
        var vanillaBank = Patcher.Project.Handler.ParamData.VanillaBank;
        if (!vanillaBank.Params.TryGetValue(paramName, out var vanillaParam))
            return null;

        var occurrence = 0;
        foreach (var row in vanillaParam.Rows)
        {
            if (row.ID != id)
                continue;

            if (occurrence == index)
                return row;

            occurrence++;
        }

        return null;
    }

    private static bool RowsEquivalent(RowDelta left, RowDelta right)
    {
        if (left.ID != right.ID || left.Index != right.Index || left.State != right.State ||
            !string.Equals(left.Name ?? "", right.Name ?? "", StringComparison.Ordinal))
            return false;

        if (left.Fields.Count != right.Fields.Count)
            return false;

        var leftFields = left.Fields
            .GroupBy(e => e.Field, StringComparer.Ordinal)
            .ToDictionary(e => e.Key, e => e.Last().Value ?? "", StringComparer.Ordinal);
        var rightFields = right.Fields
            .GroupBy(e => e.Field, StringComparer.Ordinal)
            .ToDictionary(e => e.Key, e => e.Last().Value ?? "", StringComparer.Ordinal);

        return leftFields.Count == rightFields.Count &&
               leftFields.All(pair => rightFields.TryGetValue(pair.Key, out var value) &&
                                      string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private static string RowSummary(RowDelta row)
    {
        var parts = new List<string>();
        if (row.Name != null)
            parts.Add($"Name={row.Name}");

        parts.AddRange(row.Fields
            .OrderBy(e => e.Field, StringComparer.Ordinal)
            .Take(4)
            .Select(e => $"{e.Field}={e.Value}"));

        var suffix = row.Fields.Count > 4 ? $" (+{row.Fields.Count - 4} more)" : "";
        return string.Join(", ", parts) + suffix;
    }

    private static ParamDeltaPatch ClonePatch(ParamDeltaPatch patch)
    {
        var clone = new ParamDeltaPatch
        {
            ProjectType = patch.ProjectType,
            ParamVersion = patch.ParamVersion,
            Tag = patch.Tag
        };

        foreach (var param in patch.Params)
        {
            var paramClone = new ParamDelta { Name = param.Name };
            foreach (var row in param.Rows)
                paramClone.Rows.Add(CloneRow(row));
            clone.Params.Add(paramClone);
        }

        return clone;
    }

    private static void ResetPatch(ParamDeltaPatch target, ParamDeltaPatch baseline)
    {
        target.Params.Clear();
        foreach (var param in baseline.Params)
        {
            var paramClone = new ParamDelta { Name = param.Name };
            foreach (var row in param.Rows)
                paramClone.Rows.Add(CloneRow(row));
            target.Params.Add(paramClone);
        }
    }

    private static RowDelta CloneRow(RowDelta row)
    {
        var clone = new RowDelta
        {
            ID = row.ID,
            Index = row.Index,
            Name = row.Name,
            State = row.State
        };

        foreach (var field in row.Fields)
            clone.Fields.Add(CloneField(field));

        return clone;
    }

    private static FieldDelta CloneField(FieldDelta field)
    {
        return new FieldDelta
        {
            Field = field.Field,
            Value = field.Value
        };
    }
}

public sealed class ParamDeltaAutoMergeTool
{
    private readonly ParamDeltaPatcher Patcher;
    private readonly ParamDeltaAutoMergeEngine Engine;
    private readonly ParamRegulationAutoMerge RegulationMerge;
    private readonly ParamFullModAutoMerge FullModMerge;
    private readonly HashSet<string> SelectedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> SelectedFileOrder = new();
    private readonly List<string> RegulationPaths = new() { "", "" };
    private readonly List<string> FullModFolderPaths = new() { "", "" };

    private ParamDeltaAutoMergeResult LastResult;
    private RegulationMergeAnalysis LastRegulationAnalysis;
    private FullModMergeAnalysis LastFullModAnalysis;
    private ParamDeltaConflictStrategy Strategy = ParamDeltaConflictStrategy.StopOnConflict;
    private string OutputName = "auto_merged";
    private string RegulationDeltaOutputName = "auto_merged_regulation";
    private string RegulationOutputPath = "";
    private string RegulationBuildStatus = "";
    private string FullModOutputPath = "";
    private string FullModBuildStatus = "";
    private bool FullModEnableBinderMerge = true;
    private bool FullModIgnoreMetadata = true;
    private bool FullModWriteReport = true;
    private string FullModConflictFilter = "";
    private string ParamConflictFilter = "";
    private const int ConflictRowsPerPage = 100;
    private int FullModConflictPage = 0;
    private int ParamConflictPage = 0;

    private bool DisplayMergeReportSummary = true;
    private bool DisplayBinderMergeSummary = true;
    private bool DisplayConflictResolver = true;
    private bool DisplayWarnings = true;
    private bool DisplayErrors = true;

    public ParamDeltaAutoMergeTool(ParamDeltaPatcher patcher)
    {
        Patcher = patcher;
        Engine = new ParamDeltaAutoMergeEngine(patcher);
        RegulationMerge = new ParamRegulationAutoMerge(patcher, Engine);
        FullModMerge = new ParamFullModAutoMerge(RegulationMerge);
    }
    public void DisplayConflictPolicy()
    {
        GUI.SimpleHeader(
            LOC.Get("PARAM_AutoMerge_Conflict_Policy_Header"),
            LOC.Get("PARAM_AutoMerge_Conflict_Policy_Header_TT"));

        var strategyName = GetStrategyName(Strategy);
        if (ImGui.BeginCombo("##autoMergeStrategy", strategyName))
        {
            foreach (ParamDeltaConflictStrategy value in Enum.GetValues(typeof(ParamDeltaConflictStrategy)))
            {
                if (ImGui.Selectable(GetStrategyName(value), value == Strategy))
                {
                    Strategy = value;
                    LastResult = null;
                    LastRegulationAnalysis = null;
                    LastFullModAnalysis = null;
                    RegulationBuildStatus = "";
                    FullModBuildStatus = "";
                }
            }
            ImGui.EndCombo();
        }
    }

    public void DisplayDeltaPatchMerge()
    {
        GUI.Spacer();
        GUI.SimpleHeader(
            LOC.Get("PARAM_AutoMerge_Delta_Merge_Source_List_Header"),
            LOC.Get("PARAM_AutoMerge_Delta_Merge_Source_List_Header_TT"));
        GUI.WrappedText(LOC.Get("PARAM_AutoMerge_Source_Priority_Hint"));

        // Select All
        if (ImGui.Button($"{Icons.Bars}##selectAllAction", DPI.IconButtonSize))
        {
            foreach (var entry in Patcher.Selection.ImportList)
            {
                if (entry.Delta.ProjectType == Patcher.Project.Descriptor.ProjectType &&
                    entry.Delta.ParamVersion == Patcher.Project.Handler.ParamData.PrimaryBank.ParamVersion)
                    SelectedFiles.Add(entry.Filename);
            }

            SyncSelectedFileOrder();
            LastResult = null;
        }
        GUI.Tooltip(LOC.Get("PARAM_AutoMerge_Delta_Merge_Select_All_TT"));

        ImGui.SameLine();

        // Clear Selection
        if (ImGui.Button($"{Icons.Minus}##clearSelectionAction", DPI.IconButtonSize))
        {
            SelectedFiles.Clear();
            SelectedFileOrder.Clear();
            LastResult = null;
        }
        GUI.Tooltip(LOC.Get("PARAM_AutoMerge_Delta_Merge_Clear_Selection_TT"));

        ImGui.SameLine();

        // Refresh List
        if (ImGui.Button($"{Icons.Refresh}##refreshListAction", DPI.IconButtonSize))
        {
            Patcher.Selection.RefreshImportList();
            SyncSelectedFileOrder();
            LastResult = null;
        }
        GUI.Tooltip(LOC.Get("PARAM_AutoMerge_Delta_Merge_Refresh_List_TT"));

        // List
        ImGui.BeginChild("autoMergeSourceList", new System.Numerics.Vector2(0, 180), ImGuiChildFlags.Borders);

        if (SyncSelectedFileOrder())
            LastResult = null;
        var displayEntries = Patcher.Selection.ImportList
            .OrderBy(e =>
            {
                var priority = GetSelectedFileOrderIndex(e.Filename);
                return priority < 0 ? int.MaxValue : priority;
            })
            .ToList();

        foreach (var entry in displayEntries)
        {
            var isSelected = SelectedFiles.Contains(entry.Filename);
            var version = ParamUtils.ParseRegulationVersion(entry.Delta.ParamVersion);
            var sameGame = entry.Delta.ProjectType == Patcher.Project.Descriptor.ProjectType;
            var sameVersion = entry.Delta.ParamVersion == Patcher.Project.Handler.ParamData.PrimaryBank.ParamVersion;
            var compatibility = !sameGame ? LOC.Get("PARAM_AutoMerge_Delta_Merge_Diff_Game") : !sameVersion ? LOC.Get("PARAM_AutoMerge_Delta_Merge_Diff_Param_Ver") : "";
            var priorityIndex = GetSelectedFileOrderIndex(entry.Filename);
            var priorityLabel = priorityIndex >= 0 ? $"P{priorityIndex + 1} " : "";
            var label = $"{priorityLabel}{entry.Filename} [{version}]{compatibility}##autoMerge_{entry.Filename.GetHashCode()}";

            if (ImGui.Checkbox(label, ref isSelected))
            {
                if (isSelected)
                {
                    SelectedFiles.Add(entry.Filename);
                    if (GetSelectedFileOrderIndex(entry.Filename) < 0)
                        SelectedFileOrder.Add(entry.Filename);
                }
                else
                {
                    SelectedFiles.Remove(entry.Filename);
                    SelectedFileOrder.RemoveAll(e => string.Equals(e, entry.Filename, StringComparison.OrdinalIgnoreCase));
                }

                LastResult = null;
            }

            if (isSelected)
            {
                var priority = GetSelectedFileOrderIndex(entry.Filename);
                ImGui.SameLine();
                if (ImGui.SmallButton($"↑##autoMergeDeltaSourceUp{entry.Filename.GetHashCode()}") && priority > 0)
                {
                    (SelectedFileOrder[priority - 1], SelectedFileOrder[priority]) =
                        (SelectedFileOrder[priority], SelectedFileOrder[priority - 1]);
                    LastResult = null;
                }
                GUI.Tooltip(LOC.Get("PARAM_AutoMerge_Source_Move_Earlier_TT"));

                ImGui.SameLine();
                if (ImGui.SmallButton($"↓##autoMergeDeltaSourceDown{entry.Filename.GetHashCode()}") && priority >= 0 && priority + 1 < SelectedFileOrder.Count)
                {
                    (SelectedFileOrder[priority + 1], SelectedFileOrder[priority]) =
                        (SelectedFileOrder[priority], SelectedFileOrder[priority + 1]);
                    LastResult = null;
                }
                GUI.Tooltip(LOC.Get("PARAM_AutoMerge_Source_Move_Later_TT"));
            }
        }

        ImGui.EndChild();

        // Output Filename
        GUI.Spacer();
        GUI.SimpleHeader(
            LOC.Get("PARAM_AutoMerge_Delta_Merge_Output_Filename_Header"),
            LOC.Get("PARAM_AutoMerge_Delta_Merge_Output_Filename_Header_TT"));

        ImGui.InputTextWithHint("##autoMergeOutputName", LOC.Get("PARAM_AutoMerge_Delta_Merge_Output_Filename_Hint"), 
            ref OutputName, 255);

        // Actions
        GUI.Spacer();
        GUI.SimpleHeader(
            LOC.Get("PARAM_AutoMerge_Actions_Header"),
            LOC.Get("PARAM_AutoMerge_Actions_Header_TT"));

        GUI.MultiButtonInput("deltaMergeActions",
            "analyze",
            LOC.Get("PARAM_AutoMerge_Analyze_Delta_Merge_Action"),
            LOC.Get("PARAM_AutoMerge_Analyze_Delta_Merge_Action_TT"),
            AnalyzeDeltaPatches);

        if (LastResult == null)
            return;

        // Summary
        GUI.Spacer();
        DisplayMergeSummary(LastResult, "delta");

        if (!LastResult.CanApply)
            return;

        GUI.MultiButtonInput("summaryMergeActions",
            "saveMergedDelta",
            LOC.Get("PARAM_AutoMerge_Save_Merged_Delta"),
            LOC.Get("PARAM_AutoMerge_Save_Merged_Delta_TT"),
            SaveMergedDelta,

            "importMergedDelta",
            LOC.Get("PARAM_AutoMerge_Import_Merged_Delta"),
            LOC.Get("PARAM_AutoMerge_Import_Merged_Delta_TT"),
            ImportMergedDelta);
    }

    private void SaveMergedDelta()
    {
        Engine.ApplyConflictResolutions(LastResult);
        if (!LastResult.CanApply)
            return;

        var name = string.IsNullOrWhiteSpace(OutputName) ? "auto_merged" : OutputName.Trim();
        Patcher.WriteDeltaPatch(LastResult.Patch, name);
        Patcher.Selection.RefreshImportList();
    }

    private void ImportMergedDelta()
    {
        Engine.ApplyConflictResolutions(LastResult);
        if (!LastResult.CanApply)
            return;

        var name = string.IsNullOrWhiteSpace(OutputName) ? "auto_merged" : OutputName.Trim();
        ImportMergedPatchLocally(name, LastResult.Patch);
    }

    // Auto Merge needs two import behaviors that the shared Delta Patcher importer does not
    // currently provide: preserving explicit row-name changes and matching the first row whose
    // ID is 0 at duplicate index 0. Keep those behaviors local to Auto Merge so the existing
    // Smithbox Delta Patcher implementation remains completely unchanged.
    private void ImportMergedPatchLocally(string filename, ParamDeltaPatch patch)
    {
        try
        {
            var primaryBank = Patcher.Project.Handler.ParamData.PrimaryBank;
            var vanillaBank = Patcher.Project.Handler.ParamData.VanillaBank;

            foreach (var curParam in primaryBank.Params)
            {
                var pDelta = patch.Params.FirstOrDefault(e => e.Name == curParam.Key);
                if (pDelta == null)
                    continue;

                var param = curParam.Value;
                var srcRow = param.Rows.FirstOrDefault();
                if (srcRow == null)
                    continue;

                var vanillaParam = vanillaBank.Params.FirstOrDefault(e => e.Key == curParam.Key);
                foreach (var rowDelta in pDelta.Rows)
                    HandleMergedRowImport(curParam.Key, param, vanillaParam.Value, srcRow, rowDelta);
            }

            primaryBank.RefreshPrimaryDiffCaches(true);
            Smithbox.Log(this, LOC.Get("PARAM_DeltaPatcher_Importer_Finished_Import", filename));
        }
        catch (Exception ex)
        {
            Smithbox.LogError(this, LOC.Get("PARAM_DeltaPatcher_Importer_Failed_Import", filename), ex);
        }
    }

    private void HandleMergedRowImport(string paramName, Param srcParam, Param vanillaParam, Param.Row srcRow, RowDelta rowDelta)
    {
        var addRows = CFG.Current.ParamEditor_DeltaPatcher_Import_Added_Rows;
        var modRows = CFG.Current.ParamEditor_DeltaPatcher_Import_Modified_Rows;
        var delRows = CFG.Current.ParamEditor_DeltaPatcher_Import_Deleted_Rows;
        var restrictRowAdd = CFG.Current.ParamEditor_DeltaPatcher_Import_Restrict_Row_Add;
        var restrictRowMod = CFG.Current.ParamEditor_DeltaPatcher_Import_Restrict_Row_Modify;

        HashSet<Param.Row> vanillaDiffCache =
            Patcher.Project.Handler.ParamData.PrimaryBank.GetVanillaDiffRows(paramName);

        var rowStateIsAdded = rowDelta.State is RowDeltaState.Added;
        if (Patcher.ImportMode is DeltaImportMode.Simple)
            rowStateIsAdded = true;

        if (addRows && rowStateIsAdded)
        {
            var newRow = new Param.Row(srcRow)
            {
                ID = rowDelta.ID
            };

            if (rowDelta.Name != null)
                newRow.Name = rowDelta.Name;

            Patcher.Importer.HandleFieldImport(newRow, rowDelta);

            if (CFG.Current.ParamEditor_DeltaPatcher_Import_Allow_Row_Overwrite)
            {
                var matchRow = srcParam.Rows.FirstOrDefault(e => e.ID == rowDelta.ID);
                if (matchRow != null)
                {
                    var insertIndex = srcParam.Rows.ToList().IndexOf(matchRow);
                    srcParam.InsertRow(insertIndex, newRow);
                    srcParam.RemoveRow(matchRow);
                }
                else
                {
                    srcParam.AddRow(newRow);
                }
            }
            else
            {
                var insertRow = srcParam.Rows.FirstOrDefault(e => e.ID == rowDelta.ID);
                if (insertRow != null)
                {
                    if (!restrictRowAdd)
                    {
                        var insertIndex = srcParam.Rows.ToList().IndexOf(insertRow);
                        srcParam.InsertRow(insertIndex, newRow);
                    }
                }
                else
                {
                    srcParam.AddRow(newRow);
                }
            }

            return;
        }

        if (rowDelta.State is not (RowDeltaState.Deleted or RowDeltaState.Modified))
            return;

        var curRowID = 0;
        var hasCurRowID = false;
        var internalIndex = 0;
        Param.Row rowToDelete = null;

        foreach (var row in srcParam.Rows)
        {
            if (hasCurRowID && row.ID == curRowID)
                internalIndex++;
            else
                internalIndex = 0;

            if (rowDelta.ID == row.ID && rowDelta.Index == internalIndex)
            {
                if (modRows && rowDelta.State is RowDeltaState.Modified)
                {
                    var proceed = !(restrictRowMod && vanillaDiffCache.Contains(row));
                    if (proceed)
                    {
                        if (rowDelta.Name != null)
                            row.Name = rowDelta.Name;

                        Patcher.Importer.HandleFieldImport(row, rowDelta);
                    }
                }
                else if (delRows && rowDelta.State is RowDeltaState.Deleted)
                {
                    rowToDelete = row;
                }
            }

            curRowID = row.ID;
            hasCurRowID = true;
        }

        if (rowToDelete != null)
            srcParam.RemoveRow(rowToDelete);
    }

    public void DisplayDirectRegulationMerge()
    {
        if (!RegulationMerge.IsSupportedProject)
        {
            GUI.WrappedText(
                LOC.Get("PARAM_DirectMerge_Supported_Project_Type", RegulationMerge.SupportedProjectText));
            return;
        }

        // Options
        GUI.Spacer();
        GUI.SimpleHeader(
            LOC.Get("PARAM_DirectMerge_Options_Header"),
            LOC.Get("PARAM_DirectMerge_Options_Header_TT"));

        var autoUpgradeRegulation = RegulationMerge.AutoUpgradeMismatchedVersions;

        // Upgrade Param Version for Older Regulations
        if (ImGui.Checkbox($"{LOC.Get("PARAM_DirectMerge_Apply_ParamVer_AutoUpgrade")}##autoMergeRegUpgrade", ref autoUpgradeRegulation))
        {
            RegulationMerge.AutoUpgradeMismatchedVersions = autoUpgradeRegulation;
            LastRegulationAnalysis = null;
        }
        GUI.Tooltip(LOC.Get("PARAM_DirectMerge_Apply_ParamVer_AutoUpgrade_TT"));
        if (autoUpgradeRegulation)
            GUI.WrappedText(LOC.Get("PARAM_AutoMerge_AutoUpgrade_Warning"));

        // Sources
        GUI.Spacer();
        GUI.SimpleHeader(
            LOC.Get("PARAM_DirectMerge_Sources_Header"),
            LOC.Get("PARAM_DirectMerge_Sources_Header_TT"));
        GUI.WrappedText(LOC.Get("PARAM_AutoMerge_Source_Priority_Hint"));

        // Add
        if (ImGui.Button($"{Icons.Plus}##addRegulationSourceAction", DPI.IconButtonSize))
        {
            RegulationPaths.Add("");
            LastRegulationAnalysis = null;
        }
        GUI.Tooltip(LOC.Get("PARAM_DirectMerge_Add_Regulation_Source_TT"));

        ImGui.SameLine();

        // Remove
        if (RegulationPaths.Count <= 2)
        {
            ImGui.BeginDisabled();

            if (ImGui.Button($"{Icons.Minus}##removeRegulationSourceAction", DPI.IconButtonSize))
            {
            }
            GUI.Tooltip(LOC.Get("PARAM_DirectMerge_Remove_Regulation_Source_TT"));

            ImGui.EndDisabled();
        }
        else
        {
            if (ImGui.Button($"{Icons.Minus}##removeLastRegulationSourceAction", DPI.IconButtonSize))
            {
                RegulationPaths.RemoveAt(RegulationPaths.Count - 1);
                LastRegulationAnalysis = null;
            }
            GUI.Tooltip(LOC.Get("PARAM_DirectMerge_Remove_Regulation_Source_TT"));
        }

        ImGui.SameLine();

        // Reset
        if (ImGui.Button($"{LOC.Get("PARAM_DirectMerge_Reset_Source_List")}##resetRegulationSourceList", DPI.SelectorButtonSize))
        {
            for (var i = 0; i < RegulationPaths.Count; i++)
                RegulationPaths[i] = "";

            LastRegulationAnalysis = null;
            RegulationOutputPath = "";
            RegulationBuildStatus = "";
        }
        GUI.Tooltip(LOC.Get("PARAM_DirectMerge_Reset_Source_List_TT"));

        // Sources
        for (var i = 0; i < RegulationPaths.Count; i++)
        {
            // Select
            if(ImGui.Button($"{LOC.Get("PARAM_DirectMerge_Select_Path")}##selectPath{i}", DPI.SelectorButtonSize))
            {
                var dialog = PlatformUtils.Instance.OpenFileDialog(LOC.Get("PARAM_DirectMerge_Select_Regulation"), out var path);

                if(dialog)
                {
                    RegulationPaths[i] = path;
                    LastRegulationAnalysis = null;
                    RegulationBuildStatus = "";
                }
            }

            ImGui.SameLine();

            if (ImGui.SmallButton($"↑##autoMergeRegSourceUp{i}") && i > 0)
            {
                (RegulationPaths[i - 1], RegulationPaths[i]) = (RegulationPaths[i], RegulationPaths[i - 1]);
                LastRegulationAnalysis = null;
                RegulationBuildStatus = "";
            }
            GUI.Tooltip(LOC.Get("PARAM_AutoMerge_Source_Move_Earlier_TT"));

            ImGui.SameLine();

            if (ImGui.SmallButton($"↓##autoMergeRegSourceDown{i}") && i + 1 < RegulationPaths.Count)
            {
                (RegulationPaths[i + 1], RegulationPaths[i]) = (RegulationPaths[i], RegulationPaths[i + 1]);
                LastRegulationAnalysis = null;
                RegulationBuildStatus = "";
            }
            GUI.Tooltip(LOC.Get("PARAM_AutoMerge_Source_Move_Later_TT"));

            ImGui.SameLine();
            if (RegulationPaths.Count > 2 && ImGui.SmallButton($"×##autoMergeRegSourceRemove{i}"))
            {
                RegulationPaths.RemoveAt(i);
                LastRegulationAnalysis = null;
                RegulationBuildStatus = "";
                break;
            }
            GUI.Tooltip(LOC.Get("PARAM_AutoMerge_Source_Remove_TT"));

            ImGui.SameLine();

            var value = RegulationPaths[i];
            if (ImGui.InputText($"{LOC.Get("PARAM_DirectMerge_Source", i + 1)}##autoMergeRegSource{i}", ref value, 1024))
            {
                RegulationPaths[i] = value;
                LastRegulationAnalysis = null;
                RegulationBuildStatus = "";
            }
        }

        GUI.MultiButtonInput("directMergeActions",
            "analyze",
            LOC.Get("PARAM_DirectMerge_Analyze_Regulation_Files"),
            LOC.Get("PARAM_DirectMerge_Analyze_Regulation_Files_TT"),
            AnalyzeRegulations);

        if (LastRegulationAnalysis == null)
            return;

        if (LastRegulationAnalysis.Sources.Count > 0)
        {
            GUI.Spacer();
            GUI.SimpleHeader(
                LOC.Get("PARAM_DirectMerge_Loaded_Sources_Header"),
                LOC.Get("PARAM_DirectMerge_Loaded_Sources_Header_TT"));

            foreach (var source in LastRegulationAnalysis.Sources)
            {
                var versionText = source.AutoUpgraded
                    ? $"{ParamUtils.ParseRegulationVersion(source.OriginalParamVersion)} -> {ParamUtils.ParseRegulationVersion(source.ParamVersion)} {LOC.Get("PARAM_DirectMerge_Loaded_Autoupgraded")}"
                    : ParamUtils.ParseRegulationVersion(source.ParamVersion);

                GUI.WrappedText(
                    $"• {source.Path} | {versionText} | " +
                    $"{source.ParsedParamCount} {LOC.Get("PARAM_DirectMerge_Loaded_Params")} | {source.Delta.Params.Sum(e => e.Rows.Count)} {LOC.Get("PARAM_DirectMerge_Loaded_Changed_Rows")}");
            }
        }

        if (LastRegulationAnalysis.Warnings.Count > 0)
        {
            GUI.Spacer();
            GUI.ConditionalHeader(
                LOC.Get("PARAM_DirectMerge_Warnings_Header"),
                LOC.Get("PARAM_DirectMerge_Warnings_Header_TT"),
                ref DisplayWarnings);

            if(DisplayWarnings)
            {
                ImGui.BeginChild("warningsSection", new Vector2(0, 100));

                foreach (var warning in LastRegulationAnalysis.Warnings.Take(100))
                {
                    GUI.WrappedText($"• {warning}");
                }

                ImGui.EndChild();
            }
        }

        if (LastRegulationAnalysis.Errors.Count > 0)
        {
            GUI.Spacer();
            GUI.ConditionalHeader(
                LOC.Get("PARAM_DirectMerge_Errors_Header"),
                LOC.Get("PARAM_DirectMerge_Errors_Header_TT"),
                ref DisplayErrors);

            if (DisplayErrors)
            {
                foreach (var error in LastRegulationAnalysis.Errors)
                {
                    GUI.WrappedText($"• {error}");
                }
            }

            return;
        }

        if (LastRegulationAnalysis.MergeResult == null)
            return;

        GUI.Spacer();
        DisplayMergeSummary(LastRegulationAnalysis.MergeResult, "reg");

        if (!LastRegulationAnalysis.MergeResult.CanApply)
            return;

        GUI.Spacer();
        GUI.SimpleHeader(
            LOC.Get("PARAM_DirectMerge_Output_Header"),
            LOC.Get("PARAM_DirectMerge_Output_Header_TT"));

        // Output Path
        if (ImGui.Button($"{LOC.Get("PARAM_DirectMerge_Select_Path")}##selectOutputPath", DPI.SelectorButtonSize))
        {
            var filters = Patcher.Project.Descriptor.ProjectType == ProjectType.DS3
                ? new[] { FilterStrings.Data0Filter }
                : new[] { FilterStrings.RegulationBinFilter };
            var dialog = PlatformUtils.Instance.SaveFileDialog("Save Merged Regulation", filters, out var path);

            if (dialog)
            {
                RegulationOutputPath = path;
            }
        }

        ImGui.SameLine();

        ImGui.InputTextWithHint(
            $"{LOC.Get("PARAM_DirectMerge_OutputPath")}##autoMergeRegOutput", 
            LOC.Get("PARAM_DirectMerge_OutputPath_Hint"),
            ref RegulationOutputPath, 1024);

        ImGui.InputTextWithHint(
            $"{"Merged Delta Filename"}##autoMergeRegDeltaOutput",
            "Enter a filename for Save Merged Delta...",
            ref RegulationDeltaOutputName, 255);

        GUI.Spacer();

        GUI.MultiButtonInput("directMergeBuildActions",
            "buildRegulation",
            LOC.Get("PARAM_DirectMerge_Build_Regulation_Action"),
            LOC.Get("PARAM_DirectMerge_Build_Regulation_Action_TT"),
            BuildRegulation,

            "importMergedChanges",
            LOC.Get("PARAM_DirectMerge_Import_Merged_Changes"),
            LOC.Get("PARAM_DirectMerge_Import_Merged_Changes_TT"),
            ImportMergedRegulationChanges,

            "saveMergedChanges",
            LOC.Get("PARAM_DirectMerge_Save_Merged_Delta"),
            LOC.Get("PARAM_DirectMerge_Save_Merged_Delta_TT"),
            SaveMergedRegulationChanges);

        if (!string.IsNullOrWhiteSpace(RegulationBuildStatus))
        {
            GUI.Spacer();
            GUI.WrappedText(RegulationBuildStatus);
        }
    }

    private void ImportMergedRegulationChanges()
    {
        Engine.ApplyConflictResolutions(LastRegulationAnalysis.MergeResult);
        if (!LastRegulationAnalysis.MergeResult.CanApply)
        {
            RegulationBuildStatus = LOC.Get("PARAM_AutoMerge_ConflictEditor_Cannot_Apply_Hint");
            return;
        }

        ImportMergedPatchLocally("direct_regulation_auto_merge", LastRegulationAnalysis.MergeResult.Patch);
        RegulationBuildStatus = LOC.Get("PARAM_DirectMerge_Imported_Merged_Changes");
    }

    private void SaveMergedRegulationChanges()
    {
        Engine.ApplyConflictResolutions(LastRegulationAnalysis.MergeResult);
        if (!LastRegulationAnalysis.MergeResult.CanApply)
        {
            RegulationBuildStatus = LOC.Get("PARAM_AutoMerge_ConflictEditor_Cannot_Apply_Hint");
            return;
        }

        var name = string.IsNullOrWhiteSpace(RegulationDeltaOutputName)
            ? "auto_merged_regulation"
            : RegulationDeltaOutputName.Trim();
        Patcher.WriteDeltaPatch(LastRegulationAnalysis.MergeResult.Patch, name);
        Patcher.Selection.RefreshImportList();
        RegulationBuildStatus = LOC.Get("PARAM_DirectMerge_Saved_Merged_Changes", name);
    }

    public void DisplayFullModMerge()
    {
        // Options
        GUI.Spacer();
        GUI.SimpleHeader(
            LOC.Get("PARAM_ProjectMerge_Options_Header"),
            LOC.Get("PARAM_ProjectMerge_Options_Header_TT"));

        // Merge Binder Containers by Internal Entry
        if (ImGui.Checkbox($"{LOC.Get("PARAM_ProjectMerge_Merge_By_Internal_Entry")}##autoMergeFullBinder", ref FullModEnableBinderMerge))
        {
            LastFullModAnalysis = null;
        }
        GUI.Tooltip(LOC.Get("PARAM_ProjectMerge_Merge_By_Internal_Entry_TT"));

        // Auto-upgrade Regulation Versions
        var autoUpgradeRegulation = RegulationMerge.AutoUpgradeMismatchedVersions;
        if (ImGui.Checkbox($"{LOC.Get("PARAM_ProjectMerge_AutoUpgrade_Regulation")}##autoMergeFullUpgradeReg", ref autoUpgradeRegulation))
        {
            RegulationMerge.AutoUpgradeMismatchedVersions = autoUpgradeRegulation;
            LastFullModAnalysis = null;
        }
        GUI.Tooltip(LOC.Get("PARAM_ProjectMerge_AutoUpgrade_Regulation_TT"));
        if (autoUpgradeRegulation)
            GUI.WrappedText(LOC.Get("PARAM_AutoMerge_AutoUpgrade_Warning"));

        // Ignore Metadata Files
        if (ImGui.Checkbox($"{LOC.Get("PARAM_ProjectMerge_Merge_Metadata")}##autoMergeFullMetadata", ref FullModIgnoreMetadata))
        {
            LastFullModAnalysis = null;
        }
        GUI.Tooltip(LOC.Get("PARAM_ProjectMerge_Merge_Metadata_TT"));

        // Write Merge Report
        ImGui.Checkbox($"{LOC.Get("PARAM_ProjectMerge_Write_Merge_Report")}##autoMergeFullReport", ref FullModWriteReport);
        GUI.Tooltip(LOC.Get("PARAM_ProjectMerge_Write_Merge_Report_TT"));

        // Sources
        GUI.Spacer();
        GUI.SimpleHeader(
            LOC.Get("PARAM_ProjectMerge_Sources_Header"),
            LOC.Get("PARAM_ProjectMerge_Sources_Header_TT"));
        GUI.WrappedText(LOC.Get("PARAM_AutoMerge_Source_Priority_Hint"));


        // Add
        if (ImGui.Button($"{Icons.Plus}##addProjectSourceAction", DPI.IconButtonSize))
        {
            FullModFolderPaths.Add("");
            LastFullModAnalysis = null;
        }
        GUI.Tooltip(LOC.Get("PARAM_ProjectMerge_Add_Project_Source_TT"));

        ImGui.SameLine();

        // Remove
        if (FullModFolderPaths.Count <= 2)
        {
            ImGui.BeginDisabled();

            if (ImGui.Button($"{Icons.Minus}##removeProjectSourceAction", DPI.IconButtonSize))
            {
            }
            GUI.Tooltip(LOC.Get("PARAM_ProjectMerge_Remove_Project_Source_TT"));

            ImGui.EndDisabled();
        }
        else
        {
            if (ImGui.Button($"{Icons.Minus}##removeLastProjectSourceAction", DPI.IconButtonSize))
            {
                FullModFolderPaths.RemoveAt(FullModFolderPaths.Count - 1);
                LastFullModAnalysis = null;
            }
            GUI.Tooltip(LOC.Get("PARAM_ProjectMerge_Remove_Project_Source_TT"));
        }

        ImGui.SameLine();

        // Reset
        if (ImGui.Button($"{LOC.Get("PARAM_ProjectMerge_Reset_Source_List")}##resetProjectSourceList", DPI.SelectorButtonSize))
        {
            for (var i = 0; i < FullModFolderPaths.Count; i++)
            {
                FullModFolderPaths[i] = "";
            }

            LastFullModAnalysis = null;
            FullModOutputPath = "";
            FullModBuildStatus = "";
        }
        GUI.Tooltip(LOC.Get("PARAM_ProjectMerge_Reset_Source_List_TT"));

        // Sources
        for (var i = 0; i < FullModFolderPaths.Count; i++)
        {
            // Select
            if (ImGui.Button($"{LOC.Get("PARAM_ProjectMerge_Select_Path")}##selectPath{i}", DPI.SelectorButtonSize))
            {
                var dialog = PlatformUtils.Instance.OpenFolderDialog(LOC.Get("PARAM_ProjectMerge_Select_Project_Folder"), out var path);

                if (dialog)
                {
                    FullModFolderPaths[i] = path;
                    LastFullModAnalysis = null;
                    FullModBuildStatus = "";
                }
            }

            ImGui.SameLine();

            if (ImGui.SmallButton($"↑##autoMergeProjectSourceUp{i}") && i > 0)
            {
                (FullModFolderPaths[i - 1], FullModFolderPaths[i]) = (FullModFolderPaths[i], FullModFolderPaths[i - 1]);
                LastFullModAnalysis = null;
                FullModBuildStatus = "";
            }
            GUI.Tooltip(LOC.Get("PARAM_AutoMerge_Source_Move_Earlier_TT"));

            ImGui.SameLine();

            if (ImGui.SmallButton($"↓##autoMergeProjectSourceDown{i}") && i + 1 < FullModFolderPaths.Count)
            {
                (FullModFolderPaths[i + 1], FullModFolderPaths[i]) = (FullModFolderPaths[i], FullModFolderPaths[i + 1]);
                LastFullModAnalysis = null;
                FullModBuildStatus = "";
            }
            GUI.Tooltip(LOC.Get("PARAM_AutoMerge_Source_Move_Later_TT"));

            ImGui.SameLine();
            if (FullModFolderPaths.Count > 2 && ImGui.SmallButton($"×##autoMergeProjectSourceRemove{i}"))
            {
                FullModFolderPaths.RemoveAt(i);
                LastFullModAnalysis = null;
                FullModBuildStatus = "";
                break;
            }
            GUI.Tooltip(LOC.Get("PARAM_AutoMerge_Source_Remove_TT"));

            ImGui.SameLine();

            var value = FullModFolderPaths[i];
            if (ImGui.InputText($"{LOC.Get("PARAM_ProjectMerge_Source", i + 1)}##autoMergeProjectSource{i}", ref value, 1024))
            {
                FullModFolderPaths[i] = value;
                LastFullModAnalysis = null;
                FullModBuildStatus = "";
            }
        }

        GUI.MultiButtonInput("analyzeActions",
            "analyze",
            LOC.Get("PARAM_ProjectMerge_Analyze_Mod_Folders"),
            LOC.Get("PARAM_ProjectMerge_Analyze_Mod_Folders_TT"),
            AnalyzeFullModFolders);

        GUI.Spacer();
        GUI.SimpleHeader(
            LOC.Get("PARAM_ProjectMerge_Output_Header"),
            LOC.Get("PARAM_ProjectMerge_Output_Header_TT"));

        // Select
        if (ImGui.Button($"{LOC.Get("PARAM_ProjectMerge_Select_Path")}##selectPath_output", DPI.SelectorButtonSize))
        {
            var dialog = PlatformUtils.Instance.OpenFolderDialog(LOC.Get("PARAM_ProjectMerge_Select_Project_Folder"), out var path);

            if (dialog)
            {
                FullModOutputPath = path;
            }
        }

        ImGui.SameLine();

        ImGui.InputText($"{LOC.Get("PARAM_ProjectMerge_Output_Folder")}##autoMergeFullOutput", ref FullModOutputPath, 1024);

        if (LastFullModAnalysis == null)
            return;

        // Actions
        GUI.Spacer();
        GUI.SimpleHeader(
            LOC.Get("PARAM_ProjectMerge_Actions_Header"),
            LOC.Get("PARAM_ProjectMerge_Actions_Header_TT"));

        GUI.ConditionalMultiButtonInput("buildActions",
            "build",
            LOC.Get("PARAM_ProjectMerge_Build_Merge"),
            LOC.Get("PARAM_ProjectMerge_Build_Merge_TT"),
            BuildFullModFolder,
            LastFullModAnalysis.CanBuild);

        if (!string.IsNullOrWhiteSpace(FullModBuildStatus))
        {
            GUI.Spacer();
            GUI.WrappedText(FullModBuildStatus);
        }

        // Summary
        GUI.Spacer();
        GUI.ConditionalHeader(
            LOC.Get("PARAM_ProjectMerge_Summary_Header"),
            LOC.Get("PARAM_ProjectMerge_Summary_Header_TT"),
            ref DisplayMergeReportSummary);

        if (DisplayMergeReportSummary)
        {
            ImGui.Text($"{LOC.Get("PARAM_ProjectMerge_Source_Folders", LastFullModAnalysis.SourceFolders.Count)}");
            ImGui.Text($"{LOC.Get("PARAM_ProjectMerge_Files_Scanned", LastFullModAnalysis.ScannedFiles)}");
            ImGui.Text($"{LOC.Get("PARAM_ProjectMerge_Unique_Files_to_Copy", LastFullModAnalysis.UniqueFiles)}");
            ImGui.Text($"{LOC.Get("PARAM_ProjectMerge_Identical_Duplicate_Files", LastFullModAnalysis.IdenticalFiles)}");
            ImGui.Text($"{LOC.Get("PARAM_ProjectMerge_Binder_Files_to_Merge", LastFullModAnalysis.BinderFiles)}");
            ImGui.Text($"{LOC.Get("PARAM_ProjectMerge_Regulation_Merges", LastFullModAnalysis.RegulationFiles)}");
            ImGui.Text($"{LOC.Get("PARAM_ProjectMerge_Ignored_Metadata_Files", LastFullModAnalysis.IgnoredFiles)}");
            var nonRegulationConflictCount = LastFullModAnalysis.Conflicts.Count(e => e.Type != FullModMergeConflictType.Regulation);
            var regulationConflictCount = LastFullModAnalysis.RegulationAnalysis?.MergeResult?.Conflicts.Count ?? 0;
            ImGui.Text($"{LOC.Get("PARAM_ProjectMerge_Conflicts", nonRegulationConflictCount + regulationConflictCount)}");
        }

        if (LastFullModAnalysis.Files.Any(e => e.Action == FullModMergeAction.BinderMerge))
        {
            GUI.Spacer();
            GUI.ConditionalHeader(
                LOC.Get("PARAM_ProjectMerge_Binder_Merge_Summary_Header"),
                LOC.Get("PARAM_ProjectMerge_Binder_Merge_Summary_Header_TT"),
                ref DisplayBinderMergeSummary);

            if(DisplayBinderMergeSummary)
            {
                foreach (var plan in LastFullModAnalysis.Files.Where(e => e.Action == FullModMergeAction.BinderMerge).Take(100))
                {
                    var summary = plan.BinderSummary;
                    if (summary == null)
                        continue;

                    GUI.WrappedText(
                        LOC.Get("PARAM_ProjectMerge_Binder_Merge_Log",
                        plan.RelativePath,
                        summary.AddedEntries,
                        summary.IdenticalEntries,
                        summary.NestedBinderMerges,
                        summary.MatbinSemanticMerges,
                        summary.Conflicts));
                }
            }
        }

        // Warnings
        if (LastFullModAnalysis.Warnings.Count > 0)
        {
            GUI.Spacer();
            GUI.ConditionalHeader(
                LOC.Get("PARAM_ProjectMerge_Warnings_Header"),
                LOC.Get("PARAM_ProjectMerge_Warnings_Header_TT", LastFullModAnalysis.Warnings.Count),
                ref DisplayWarnings);

            if (DisplayWarnings)
            {
                ImGui.BeginChild("warningsSection", new Vector2(0, 100));

                foreach (var warning in LastFullModAnalysis.Warnings.Take(200))
                {
                    GUI.WrappedText($"• {warning}");
                }

                ImGui.EndChild();
            }
        }

        if (LastFullModAnalysis.Errors.Count > 0)
        {
            GUI.Spacer();
            GUI.ConditionalHeader(
                LOC.Get("PARAM_ProjectMerge_Errors_Header"),
                LOC.Get("PARAM_ProjectMerge_Errors_Header_TT", LastFullModAnalysis.Errors.Count),
                ref DisplayErrors);

            if (DisplayErrors)
            {
                foreach (var error in LastFullModAnalysis.Errors)
                {
                    GUI.WrappedText($"• {error}");
                }
            }

            return;
        }

        if (LastFullModAnalysis.Conflicts.Any(e => e.Type != FullModMergeConflictType.Regulation))
        {
            GUI.Spacer();
            DisplayFullModConflictResolver(LastFullModAnalysis);
        }

        if (LastFullModAnalysis.RegulationAnalysis?.MergeResult?.Conflicts.Count > 0)
        {
            GUI.Spacer();
            GUI.SimpleHeader(
                LOC.Get("PARAM_ProjectMerge_RegulationConflicts_Header"),
                LOC.Get("PARAM_ProjectMerge_RegulationConflicts_Header_TT"));

            DisplayMergeSummary(LastFullModAnalysis.RegulationAnalysis.MergeResult, "fullreg");
        }
    }

    private void DisplayFullModConflictResolver(FullModMergeAnalysis analysis)
    {
        var editableConflicts = analysis.Conflicts.Where(e => e.Type != FullModMergeConflictType.Regulation).ToList();
        var unresolved = editableConflicts.Count(e => !e.IsResolved);

        GUI.ConditionalHeader(
            LOC.Get("PARAM_ProjectMerge_FileConflictResolve_Header", editableConflicts.Count),
            LOC.Get("PARAM_ProjectMerge_FileConflictResolve_Header_TT"),
            ref DisplayConflictResolver);

        if (DisplayConflictResolver)
        {
            ImGui.Text(LOC.Get("PARAM_ProjectMerge_Resolved_Unresolved_Hint", editableConflicts.Count - unresolved, unresolved));

            ImGui.InputText(
                $"{LOC.Get("PARAM_AutoMerge_ConflictEditor_Filter")}##autoMergeFullConflictFilter",
                ref FullModConflictFilter, 512);

            // Visible -> Earlier
            if (ImGui.Button($"{LOC.Get("PARAM_AutoMerge_ConflictEditor_Visible_Earlier")} ({FilterFullModConflicts(analysis).Count()})##autoMergeFullResolveEarlier"))
            {
                foreach (var conflict in FilterFullModConflicts(analysis))
                {
                    conflict.Resolution = FullModConflictResolution.UseEarlier;
                }
                FullModMerge.RefreshResolvedBinderConflicts(analysis);
            }
            GUI.Tooltip(LOC.Get("PARAM_AutoMerge_ConflictEditor_Bulk_Filtered_TT"));

            ImGui.SameLine();

            // Visible -> Later
            if (ImGui.Button($"{LOC.Get("PARAM_AutoMerge_ConflictEditor_Visible_Later")} ({FilterFullModConflicts(analysis).Count()})##autoMergeFullResolveLater"))
            {
                foreach (var conflict in FilterFullModConflicts(analysis))
                {
                    conflict.Resolution = FullModConflictResolution.UseLater;
                }
                FullModMerge.RefreshResolvedBinderConflicts(analysis);
            }
            GUI.Tooltip(LOC.Get("PARAM_AutoMerge_ConflictEditor_Bulk_Filtered_TT"));

            ImGui.SameLine();

            // Reset Visible
            if (ImGui.Button($"{LOC.Get("PARAM_AutoMerge_ConflictEditor_Visible_Reset")}##autoMergeFullResolveReset"))
            {
                foreach (var conflict in FilterFullModConflicts(analysis))
                {
                    conflict.Resolution = FullModConflictResolution.Unresolved;
                }
                FullModMerge.RefreshResolvedBinderConflicts(analysis);
            }
            GUI.Tooltip(LOC.Get("PARAM_AutoMerge_ConflictEditor_Bulk_Filtered_TT"));

            var filtered = FilterFullModConflicts(analysis).ToList();
            var pageCount = Math.Max(1, (filtered.Count + ConflictRowsPerPage - 1) / ConflictRowsPerPage);
            FullModConflictPage = Math.Clamp(FullModConflictPage, 0, pageCount - 1);

            if (ImGui.Button($"{LOC.Get("PARAM_AutoMerge_ConflictEditor_Page_Back")}##autoMergeFullConflictPrev") && FullModConflictPage > 0)
            {
                FullModConflictPage--;
            }

            ImGui.SameLine();

            ImGui.Text($"{LOC.Get("PARAM_AutoMerge_ConflictEditor_Page", FullModConflictPage + 1, pageCount, filtered.Count)}");

            ImGui.SameLine();

            if (ImGui.Button($"{LOC.Get("PARAM_AutoMerge_ConflictEditor_Page_Next")}##autoMergeFullConflictNext") && FullModConflictPage + 1 < pageCount)
            {
                FullModConflictPage++;
            }

            var page = filtered.Skip(FullModConflictPage * ConflictRowsPerPage).Take(ConflictRowsPerPage);

            if (ImGui.BeginTable(
                    "autoMergeFullConflictTable",
                    6,
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersOuterH | ImGuiTableFlags.BordersOuterV |
                    ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableSetupColumn(LOC.Get("PARAM_AutoMerge_ConflictTable_Path"));
                ImGui.TableSetupColumn(LOC.Get("PARAM_AutoMerge_ConflictTable_Earlier"));
                ImGui.TableSetupColumn(LOC.Get("PARAM_AutoMerge_ConflictTable_Later"));
                ImGui.TableSetupColumn(LOC.Get("PARAM_AutoMerge_ConflictTable_Resolution"));
                ImGui.TableSetupColumn(LOC.Get("PARAM_AutoMerge_ConflictTable_Manual_Value"));
                ImGui.TableSetupColumn(LOC.Get("PARAM_AutoMerge_ConflictEditor_Reason"));
                ImGui.TableHeadersRow();

                var index = FullModConflictPage * ConflictRowsPerPage;
                foreach (var conflict in page)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    GUI.WrappedText(string.IsNullOrWhiteSpace(conflict.InternalPath)
                        ? conflict.RelativePath
                        : $"{conflict.RelativePath} :: {conflict.InternalPath}");

                    ImGui.TableSetColumnIndex(1);
                    GUI.WrappedText(string.IsNullOrWhiteSpace(conflict.ExistingValue)
                        ? conflict.ExistingSource
                        : $"{conflict.ExistingValue}\n{conflict.ExistingSource}");

                    ImGui.TableSetColumnIndex(2);
                    GUI.WrappedText(string.IsNullOrWhiteSpace(conflict.IncomingValue)
                        ? conflict.IncomingSource
                        : $"{conflict.IncomingValue}\n{conflict.IncomingSource}");

                    ImGui.TableSetColumnIndex(3);
                    var label = GetFullModResolutionName(conflict.Resolution);
                    if (ImGui.BeginCombo($"##fullConflictResolution{index}", label))
                    {
                        foreach (FullModConflictResolution resolution in Enum.GetValues(typeof(FullModConflictResolution)))
                        {
                            if (resolution == FullModConflictResolution.Manual && !conflict.SupportsManual)
                                continue;

                            if (ImGui.Selectable(GetFullModResolutionName(resolution), conflict.Resolution == resolution))
                            {
                                conflict.Resolution = resolution;
                                if (resolution == FullModConflictResolution.Manual && string.IsNullOrEmpty(conflict.ManualValue))
                                {
                                    conflict.ManualValue = conflict.Type == FullModMergeConflictType.File
                                        ? conflict.CandidateSources.FirstOrDefault() ?? ""
                                        : conflict.ExistingValue;
                                }

                                if ((conflict.Type is FullModMergeConflictType.BinderEntry or FullModMergeConflictType.MatbinValue) &&
                                    conflict.IsManualValueValid)
                                {
                                    FullModMerge.RefreshResolvedBinderConflicts(analysis);
                                }
                            }
                        }
                        ImGui.EndCombo();
                    }

                    ImGui.TableSetColumnIndex(4);
                    if (conflict.SupportsManual && conflict.Resolution == FullModConflictResolution.Manual)
                    {
                        if (conflict.Type == FullModMergeConflictType.File)
                        {
                            var selectedSource = string.IsNullOrWhiteSpace(conflict.ManualValue)
                                ? "-"
                                : conflict.ManualValue;

                            if (ImGui.BeginCombo($"##fullConflictManualSource{index}", selectedSource))
                            {
                                foreach (var candidate in conflict.CandidateSources)
                                {
                                    var selected = string.Equals(candidate, conflict.ManualValue, StringComparison.OrdinalIgnoreCase);
                                    if (ImGui.Selectable(candidate, selected))
                                        conflict.ManualValue = candidate;
                                }

                                ImGui.EndCombo();
                            }
                        }
                        else
                        {
                            var manual = conflict.ManualValue ?? "";
                            if (ImGui.InputText($"##fullConflictManual{index}", ref manual, 1024))
                            {
                                conflict.ManualValue = manual;
                                if (conflict.IsManualValueValid)
                                    FullModMerge.RefreshResolvedBinderConflicts(analysis);
                            }

                            if (!conflict.IsManualValueValid)
                            {
                                GUI.WrappedText(LOC.Get(
                                    "PARAM_AutoMerge_Manual_Value_Invalid",
                                    conflict.ValueKind.ToString()));
                            }
                        }
                    }
                    else
                    {
                        ImGui.TextDisabled("-");
                    }

                    ImGui.TableSetColumnIndex(5);
                    GUI.WrappedText(conflict.Message);
                    index++;
                }

                ImGui.EndTable();
            }
        }
    }

    private IEnumerable<FullModMergeConflict> FilterFullModConflicts(FullModMergeAnalysis analysis)
    {
        var source = analysis.Conflicts.Where(e => e.Type != FullModMergeConflictType.Regulation);
        if (string.IsNullOrWhiteSpace(FullModConflictFilter))
            return source;

        var filter = FullModConflictFilter.Trim();
        return source.Where(e =>
            e.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            e.InternalPath.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            e.ExistingSource.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            e.IncomingSource.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            e.Message.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetFullModResolutionName(FullModConflictResolution resolution)
    {
        return resolution switch
        {
            FullModConflictResolution.UseEarlier => LOC.Get("PARAM_AutoMerge_ConflictResolution_UseEarlier"),
            FullModConflictResolution.UseLater => LOC.Get("PARAM_AutoMerge_ConflictResolution_UseLater"),
            FullModConflictResolution.Manual => LOC.Get("PARAM_AutoMerge_ConflictResolution_Manual"),
            _ => LOC.Get("PARAM_AutoMerge_ConflictResolution_Unresolved")
        };
    }

    private void AnalyzeFullModFolders()
    {
        FullModBuildStatus = "";
        FullModConflictPage = 0;
        LastFullModAnalysis = FullModMerge.Analyze(
            FullModFolderPaths,
            Strategy,
            FullModEnableBinderMerge,
            FullModIgnoreMetadata);

        if (LastFullModAnalysis.SourceFolders.Count > 0 && string.IsNullOrWhiteSpace(FullModOutputPath))
        {
            var first = LastFullModAnalysis.SourceFolders[0];
            var parent = System.IO.Directory.GetParent(first)?.FullName ?? first;
            FullModOutputPath = System.IO.Path.Combine(parent, "Merged_Mod");
        }
    }

    private void BuildFullModFolder()
    {
        try
        {
            FullModMerge.Build(LastFullModAnalysis, FullModOutputPath, FullModWriteReport);
            var cleanOutputPath = FullModOutputPath.Trim().Trim('"');
            FullModBuildStatus = LOC.Get("PARAM_ProjectMerge_Merged_Mod", Path.GetFullPath(cleanOutputPath));
        }
        catch (Exception ex)
        {
            FullModBuildStatus = LOC.Get("PARAM_ProjectMerge_Merged_Mod_Failed", ex.Message);
        }
    }

    private void AnalyzeRegulations()
    {
        RegulationBuildStatus = "";
        LastRegulationAnalysis = RegulationMerge.Analyze(RegulationPaths, Strategy);

        if (LastRegulationAnalysis.Sources.Count > 0 && string.IsNullOrWhiteSpace(RegulationOutputPath))
        {
            var firstPath = LastRegulationAnalysis.Sources[0].Path;
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(firstPath));
            var outputFileName = Patcher.Project.Descriptor.ProjectType == ProjectType.DS3
                ? "merged_Data0.bdt"
                : "merged_regulation.bin";
            RegulationOutputPath = System.IO.Path.Combine(directory ?? "", outputFileName);
        }
    }

    private void BuildRegulation()
    {
        try
        {
            RegulationMerge.BuildMergedRegulation(LastRegulationAnalysis, RegulationOutputPath);
            var cleanOutputPath = RegulationOutputPath.Trim().Trim('"');
            RegulationBuildStatus = LOC.Get("PARAM_DirectMerge_Merged_Regulation", Path.GetFullPath(cleanOutputPath));
        }
        catch (Exception ex)
        {
            RegulationBuildStatus = LOC.Get("PARAM_ProjectMerge_Merged_Mod_Failed", ex.Message);
        }
    }

    private void AnalyzeDeltaPatches()
    {
        SyncSelectedFileOrder();

        var sources = new List<DeltaImportEntry>();
        foreach (var filename in SelectedFileOrder)
        {
            var source = Patcher.Selection.ImportList.FirstOrDefault(e =>
                string.Equals(e.Filename, filename, StringComparison.OrdinalIgnoreCase));
            if (source != null)
                sources.Add(source);
        }

        LastResult = Engine.Merge(sources, Strategy);
    }

    private bool SyncSelectedFileOrder()
    {
        var beforeFiles = SelectedFiles.Count;
        var beforeOrder = string.Join("\n", SelectedFileOrder);
        var available = new HashSet<string>(
            Patcher.Selection.ImportList.Select(e => e.Filename),
            StringComparer.OrdinalIgnoreCase);

        SelectedFiles.RemoveWhere(e => !available.Contains(e));
        SelectedFileOrder.RemoveAll(e => !available.Contains(e) || !SelectedFiles.Contains(e));

        foreach (var entry in Patcher.Selection.ImportList)
        {
            if (SelectedFiles.Contains(entry.Filename) && GetSelectedFileOrderIndex(entry.Filename) < 0)
                SelectedFileOrder.Add(entry.Filename);
        }

        return beforeFiles != SelectedFiles.Count ||
               !string.Equals(beforeOrder, string.Join("\n", SelectedFileOrder), StringComparison.Ordinal);
    }

    private int GetSelectedFileOrderIndex(string filename)
    {
        return SelectedFileOrder.FindIndex(e =>
            string.Equals(e, filename, StringComparison.OrdinalIgnoreCase));
    }

    private void DisplayMergeSummary(ParamDeltaAutoMergeResult result, string idSuffix)
    {
        GUI.SimpleHeader(
            LOC.Get("PARAM_AutoMerge_Merge_Summary_Header"),
            LOC.Get("PARAM_AutoMerge_Merge_Summary_Header_TT"));

        ImGui.Text(LOC.Get("PARAM_AutoMerge_Summary_Sources", result.SourceCount));
        ImGui.Text(LOC.Get("PARAM_AutoMerge_Summary_Rows_Merged", result.Patch.Params.Sum(e => e.Rows.Count)));
        ImGui.Text(LOC.Get("PARAM_AutoMerge_Summary_Safe_Field_Changes", result.SafeFields));
        ImGui.Text(LOC.Get("PARAM_AutoMerge_Summary_Identical_Fields", result.IdenticalFields));
        ImGui.Text(LOC.Get("PARAM_AutoMerge_Summary_Conflicts_Resolved_Unresolved", result.Conflicts.Count, result.ResolvedConflicts, result.UnresolvedConflicts));

        if (result.Errors.Count > 0)
        {
            GUI.Spacer();
            GUI.SimpleHeader(
                LOC.Get("PARAM_AutoMerge_Merge_Errors_Header"),
                LOC.Get("PARAM_AutoMerge_Merge_Errors_Header_TT"));

            foreach (var error in result.Errors)
            {
                GUI.WrappedText($"• {error}");
            }
        }

        if (result.Conflicts.Count > 0)
        {
            GUI.Spacer();

            if (ImGui.CollapsingHeader($"{LOC.Get("PARAM_AutoMerge_ConflictEditor_Header", result.Conflicts.Count)}##autoMergeConflicts_{idSuffix}", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.InputText($"{LOC.Get("PARAM_AutoMerge_ConflictEditor_Filter")}##paramConflictFilter_{idSuffix}", 
                    ref ParamConflictFilter, 512);

                var filtered = result.Conflicts.Where(conflict =>
                    string.IsNullOrWhiteSpace(ParamConflictFilter) ||
                    conflict.ParamName.Contains(ParamConflictFilter, StringComparison.OrdinalIgnoreCase) ||
                    conflict.Field.Contains(ParamConflictFilter, StringComparison.OrdinalIgnoreCase) ||
                    conflict.ExistingSource.Contains(ParamConflictFilter, StringComparison.OrdinalIgnoreCase) ||
                    conflict.IncomingSource.Contains(ParamConflictFilter, StringComparison.OrdinalIgnoreCase)).ToList();

                if (ImGui.Button($"{LOC.Get("PARAM_AutoMerge_ConflictEditor_Visible_Earlier")} ({filtered.Count})##paramResolveEarlier_{idSuffix}"))
                {
                    foreach (var conflict in filtered)
                    {
                        conflict.Resolution = ParamDeltaConflictResolution.UseEarlier;
                    }

                    Engine.ApplyConflictResolutions(result);
                }
                GUI.Tooltip(LOC.Get("PARAM_AutoMerge_ConflictEditor_Bulk_Filtered_TT"));

                ImGui.SameLine();
                if (ImGui.Button($"{LOC.Get("PARAM_AutoMerge_ConflictEditor_Visible_Later")} ({filtered.Count})##paramResolveLater_{idSuffix}"))
                {
                    foreach (var conflict in filtered)
                    {
                        conflict.Resolution = ParamDeltaConflictResolution.UseLater;
                    }

                    Engine.ApplyConflictResolutions(result);
                }
                GUI.Tooltip(LOC.Get("PARAM_AutoMerge_ConflictEditor_Bulk_Filtered_TT"));

                ImGui.SameLine();
                if (ImGui.Button($"{LOC.Get("PARAM_AutoMerge_ConflictEditor_Visible_Reset")}##paramResolveReset_{idSuffix}"))
                {
                    foreach (var conflict in filtered)
                    {
                        conflict.Resolution = ParamDeltaConflictResolution.Unresolved;
                    }
                }
                GUI.Tooltip(LOC.Get("PARAM_AutoMerge_ConflictEditor_Bulk_Filtered_TT"));

                var pageCount = Math.Max(1, (filtered.Count + ConflictRowsPerPage - 1) / ConflictRowsPerPage);
                ParamConflictPage = Math.Clamp(ParamConflictPage, 0, pageCount - 1);
                if (ImGui.Button($"{LOC.Get("PARAM_AutoMerge_ConflictEditor_Page_Back")}##paramConflictPrev_{idSuffix}") && ParamConflictPage > 0)
                {
                    ParamConflictPage--;
                }

                ImGui.SameLine();
                ImGui.Text(LOC.Get("PARAM_AutoMerge_ConflictEditor_Page", ParamConflictPage + 1, pageCount, filtered.Count));

                ImGui.SameLine();

                if (ImGui.Button($"{LOC.Get("PARAM_AutoMerge_ConflictEditor_Page_Next")}##paramConflictNext_{idSuffix}") && ParamConflictPage + 1 < pageCount)
                {
                    ParamConflictPage++;
                }

                var page = filtered.Skip(ParamConflictPage * ConflictRowsPerPage).Take(ConflictRowsPerPage);
                if (ImGui.BeginTable(
                        $"paramConflictTable_{idSuffix}",
                        7,
                        ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersOuterH | ImGuiTableFlags.BordersOuterV |
                        ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame))
                {
                    ImGui.TableSetupColumn(LOC.Get("PARAM_AutoMerge_ConflictTable_ParamRowField"));
                    ImGui.TableSetupColumn("Base");
                    ImGui.TableSetupColumn(LOC.Get("PARAM_AutoMerge_ConflictTable_Earlier"));
                    ImGui.TableSetupColumn(LOC.Get("PARAM_AutoMerge_ConflictTable_Later"));
                    ImGui.TableSetupColumn(LOC.Get("PARAM_AutoMerge_ConflictTable_Resolution"));
                    ImGui.TableSetupColumn(LOC.Get("PARAM_AutoMerge_ConflictTable_Manual_Value"));
                    ImGui.TableSetupColumn(LOC.Get("PARAM_AutoMerge_ConflictTable_Type"));
                    ImGui.TableHeadersRow();

                    var index = ParamConflictPage * ConflictRowsPerPage;
                    foreach (var conflict in page)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        GUI.WrappedText(conflict.Type switch
                        {
                            ParamDeltaMergeConflictType.FieldValue => $"{conflict.ParamName} / {conflict.RowID}:{conflict.RowIndex} / {conflict.Field}",
                            ParamDeltaMergeConflictType.RowName => $"{conflict.ParamName} / {conflict.RowID}:{conflict.RowIndex} / {"Row name"}",
                            _ => $"{conflict.ParamName} / {conflict.RowID}:{conflict.RowIndex}"
                        });

                        ImGui.TableSetColumnIndex(1);
                        GUI.WrappedText(conflict.BaseValue);

                        ImGui.TableSetColumnIndex(2);
                        GUI.WrappedText(conflict.Type is ParamDeltaMergeConflictType.FieldValue or ParamDeltaMergeConflictType.RowName
                            ? $"{conflict.ExistingSource}: {conflict.ExistingValue}"
                            : $"{conflict.ExistingSource}: {conflict.ExistingState}" +
                              (string.IsNullOrWhiteSpace(conflict.ExistingValue) ? "" : $"\n{conflict.ExistingValue}"));

                        ImGui.TableSetColumnIndex(3);
                        GUI.WrappedText(conflict.Type is ParamDeltaMergeConflictType.FieldValue or ParamDeltaMergeConflictType.RowName
                            ? $"{conflict.IncomingSource}: {conflict.IncomingValue}"
                            : $"{conflict.IncomingSource}: {conflict.IncomingState}" +
                              (string.IsNullOrWhiteSpace(conflict.IncomingValue) ? "" : $"\n{conflict.IncomingValue}"));

                        ImGui.TableSetColumnIndex(4);
                        var resolutionLabel = GetParamResolutionName(conflict.Resolution);
                        if (ImGui.BeginCombo($"##paramResolution_{idSuffix}_{index}", resolutionLabel))
                        {
                            foreach (ParamDeltaConflictResolution resolution in Enum.GetValues(typeof(ParamDeltaConflictResolution)))
                            {
                                if (conflict.Type == ParamDeltaMergeConflictType.RowState && resolution == ParamDeltaConflictResolution.Manual)
                                    continue;

                                if (ImGui.Selectable(GetParamResolutionName(resolution), conflict.Resolution == resolution))
                                {
                                    conflict.Resolution = resolution;
                                    if (resolution == ParamDeltaConflictResolution.Manual && string.IsNullOrEmpty(conflict.ManualValue))
                                        conflict.ManualValue = conflict.ExistingValue;
                                    Engine.ApplyConflictResolutions(result);
                                }
                            }
                            ImGui.EndCombo();
                        }

                        ImGui.TableSetColumnIndex(5);
                        if ((conflict.Type is ParamDeltaMergeConflictType.FieldValue or ParamDeltaMergeConflictType.RowName) &&
                            conflict.Resolution == ParamDeltaConflictResolution.Manual)
                        {
                            var manual = conflict.ManualValue ?? "";
                            if (ImGui.InputText($"##manualConflict_{idSuffix}_{index}", ref manual, 1024))
                            {
                                conflict.ManualValue = manual;
                                Engine.ApplyConflictResolutions(result);
                            }

                            if (conflict.Type == ParamDeltaMergeConflictType.FieldValue && !conflict.ManualValueValid)
                            {
                                GUI.WrappedText(LOC.Get(
                                    "PARAM_AutoMerge_Manual_Value_Invalid",
                                    conflict.ManualValueType));
                            }
                        }
                        else
                        {
                            ImGui.TextDisabled("-");
                        }

                        ImGui.TableSetColumnIndex(6);
                        ImGui.Text(conflict.Type switch
                        {
                            ParamDeltaMergeConflictType.FieldValue => LOC.Get("PARAM_AutoMerge_ConflictEditor_Field"),
                            ParamDeltaMergeConflictType.RowName => "Row name",
                            _ => LOC.Get("PARAM_AutoMerge_ConflictEditor_RowState")
                        });
                        index++;
                    }

                    ImGui.EndTable();
                }
            }
        }

        Engine.ApplyConflictResolutions(result);

        if (!result.CanApply)
        {
            GUI.Spacer();
            GUI.WrappedText(LOC.Get("PARAM_AutoMerge_ConflictEditor_Cannot_Apply_Hint"));
        }
    }

    private static string GetParamResolutionName(ParamDeltaConflictResolution resolution)
    {
        return resolution switch
        {
            ParamDeltaConflictResolution.UseEarlier => LOC.Get("PARAM_AutoMerge_ConflictResolution_UseEarlier"),
            ParamDeltaConflictResolution.UseLater => LOC.Get("PARAM_AutoMerge_ConflictResolution_UseLater"),
            ParamDeltaConflictResolution.Manual => LOC.Get("PARAM_AutoMerge_ConflictResolution_Manual"),
            _ => LOC.Get("PARAM_AutoMerge_ConflictResolution_Unresolved")
        };
    }

    private static string GetStrategyName(ParamDeltaConflictStrategy strategy)
    {
        return strategy switch
        {
            ParamDeltaConflictStrategy.StopOnConflict => LOC.Get("PARAM_AutoMerge_ConflictStrategy_StopOnConflict"),
            ParamDeltaConflictStrategy.PreferFirst => LOC.Get("PARAM_AutoMerge_ConflictStrategy_PreferEarlierSource"),
            ParamDeltaConflictStrategy.PreferLast => LOC.Get("PARAM_AutoMerge_ConflictStrategy_PreferLaterSource"),
            _ => strategy.ToString()
        };
    }
}
