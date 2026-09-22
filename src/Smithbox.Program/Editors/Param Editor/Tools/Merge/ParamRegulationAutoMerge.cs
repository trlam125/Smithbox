using Andre.Formats;
using SoulsFormats;
using StudioCore.Application;
using StudioCore.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace StudioCore.Editors.ParamEditor;

public sealed class RegulationMergeSource
{
    public string Path { get; init; } = "";
    public string Filename => System.IO.Path.GetFileName(Path);
    public ulong OriginalParamVersion { get; init; }
    public ulong ParamVersion { get; init; }
    public bool AutoUpgraded { get; init; }
    public int ParsedParamCount { get; init; }
    public ParamDeltaPatch Delta { get; init; } = new();
}

public sealed class RegulationMergeAnalysis
{
    public List<RegulationMergeSource> Sources { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public ParamDeltaAutoMergeResult MergeResult { get; set; }

    public bool CanBuild => Errors.Count == 0 && MergeResult != null && MergeResult.CanApply;
}

/// <summary>
/// Converts complete regulation files into vanilla-relative Param Delta patches,
/// feeds them through the normal auto-merge engine, and can materialize the merged
/// patch back into a new encrypted regulation file.
/// </summary>
public sealed class ParamRegulationAutoMerge
{
    private readonly ParamDeltaPatcher Patcher;
    private readonly ParamDeltaAutoMergeEngine Engine;

    public bool AutoUpgradeMismatchedVersions { get; set; } = true;

    private readonly record struct RowKey(int ID, int Index);

    public ParamRegulationAutoMerge(ParamDeltaPatcher patcher, ParamDeltaAutoMergeEngine engine)
    {
        Patcher = patcher;
        Engine = engine;
    }

    public bool IsSupportedProject => Patcher.Project.Descriptor.ProjectType is
        ProjectType.ER or ProjectType.AC6 or ProjectType.NR or ProjectType.DS3;

    public string RootRegulationRelativePath => Patcher.Project.Descriptor.ProjectType == ProjectType.DS3
        ? "Data0.bdt"
        : "regulation.bin";

    public bool IsRootRegulationPath(string relativePath)
    {
        var normalized = (relativePath ?? "").Replace('\\', '/').TrimStart('/');
        return string.Equals(normalized, RootRegulationRelativePath, StringComparison.OrdinalIgnoreCase);
    }

    public string SupportedProjectText => LOC.Get("PARAM_AutoMerge_RegulationMerge_Supported_Hint");

    public RegulationMergeAnalysis Analyze(
        IReadOnlyList<string> sourcePaths,
        ParamDeltaConflictStrategy strategy)
    {
        var analysis = new RegulationMergeAnalysis();

        if (!IsSupportedProject)
        {
            analysis.Errors.Add(LOC.Get("PARAM_DirectMerge_Supported_Project_Type", SupportedProjectText));
            return analysis;
        }

        var normalizedPaths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in sourcePaths.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            try
            {
                var fullPath = System.IO.Path.GetFullPath(NormalizeUserPath(rawPath));
                if (seenPaths.Add(fullPath))
                    normalizedPaths.Add(fullPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                analysis.Errors.Add($"{rawPath}: {ex.Message}");
            }
        }

        if (normalizedPaths.Count < 2)
        {
            analysis.Errors.Add(LOC.Get("PARAM_AutoMerge_Error_Select_Two_Regulations"));
            return analysis;
        }

        foreach (var path in normalizedPaths)
        {
            if (!File.Exists(path))
            {
                analysis.Errors.Add(LOC.Get("PARAM_AutoMerge_Error_File_Not_Found", path));
                continue;
            }

            try
            {
                var source = BuildSource(path, analysis.Warnings);
                analysis.Sources.Add(source);
            }
            catch (Exception ex)
            {
                analysis.Errors.Add($"{System.IO.Path.GetFileName(path)}: {ex.Message}");
            }
        }

        if (analysis.Errors.Count > 0 || analysis.Sources.Count < 2)
            return analysis;

        var entries = analysis.Sources
            .Select(source => new DeltaImportEntry
            {
                // Regulation files are usually all named regulation.bin. Use the full source
                // path as the merge label so conflict rows identify the actual project/source.
                Filename = source.Path,
                Delta = source.Delta
            })
            .ToList();

        analysis.MergeResult = Engine.Merge(entries, strategy);
        analysis.Errors.AddRange(analysis.MergeResult.Errors);
        return analysis;
    }

    public void BuildMergedRegulation(
        RegulationMergeAnalysis analysis,
        string outputPath)
    {
        if (analysis?.MergeResult != null)
            Engine.ApplyConflictResolutions(analysis.MergeResult);

        if (analysis == null || !analysis.CanBuild)
            throw new InvalidOperationException(LOC.Get("PARAM_AutoMerge_Error_Invalid_Regulation_Merge"));

        if (analysis.Sources.Count == 0)
            throw new InvalidOperationException(LOC.Get("PARAM_AutoMerge_Error_Missing_Regulation_Source"));

        outputPath = NormalizeUserPath(outputPath);
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new InvalidOperationException(LOC.Get("PARAM_AutoMerge_Error_Missing_Regulation_Output_Path"));

        var outputFullPath = System.IO.Path.GetFullPath(outputPath);
        foreach (var source in analysis.Sources)
        {
            if (string.Equals(System.IO.Path.GetFullPath(source.Path), outputFullPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(LOC.Get("PARAM_AutoMerge_Error_Regulation_Output_Collision"));
        }

        if (File.Exists(outputFullPath))
            throw new InvalidOperationException($"Output file already exists: {outputFullPath}. Choose a new path or remove the existing file first.");

        // Always build on the currently loaded game's vanilla regulation. This is important
        // when one or more input regulations were automatically upgraded from an older version.
        using var binder = ReadCurrentVanillaRegulation();

        if (!ulong.TryParse(binder.Version, out var version))
            throw new InvalidDataException(LOC.Get("PARAM_AutoMerge_Error_Invalid_Vanilla_Param_Version"));

        var loadedVersion = Patcher.Project.Handler.ParamData.PrimaryBank.ParamVersion;

        var displayVersion = ParamUtils.ParseRegulationVersion(version);
        var displayProjectVersion = ParamUtils.ParseRegulationVersion(loadedVersion);

        if (version != loadedVersion)
        {
            throw new InvalidDataException(
                LOC.Get("PARAM_AutoMerge_Error_Mismatched_Vanilla_Param_Version",
                displayVersion,
                displayProjectVersion));
        }

        var originalParamTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        var parsedParams = ParseBinderParams(binder, version, null, strict: false, originalParamTypes);

        // Conflict selections/manual values were materialized and revalidated before the
        // build-safety checks above. Apply that final patch to the current vanilla regulation.
        ApplyMergedPatch(parsedParams, analysis.MergeResult.Patch);

        foreach (var file in binder.Files)
        {
            if (!file.Name.EndsWith(".PARAM", StringComparison.OrdinalIgnoreCase))
                continue;

            var paramName = System.IO.Path.GetFileNameWithoutExtension(
                file.Name.Replace('\\', System.IO.Path.DirectorySeparatorChar));

            if (parsedParams.TryGetValue(paramName, out var param))
            {
                var mappedType = param.ParamType;
                if (originalParamTypes.TryGetValue(paramName, out var originalType))
                    param.ParamType = string.IsNullOrEmpty(originalType) ? null : originalType;

                file.Bytes = param.Write();
                param.ParamType = mappedType;
            }
        }

        var outputDirectory = System.IO.Path.GetDirectoryName(outputFullPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        var fileName = System.IO.Path.GetFileNameWithoutExtension(outputFullPath);
        var extension = System.IO.Path.GetExtension(outputFullPath);
        var tempPath = System.IO.Path.Combine(
            string.IsNullOrWhiteSpace(outputDirectory) ? Directory.GetCurrentDirectory() : outputDirectory,
            $".{fileName}.smithboxmerge-{Guid.NewGuid():N}{extension}");

        try
        {
            WriteRegulation(tempPath, binder);

            // Validate that the newly produced file can be decrypted again before replacing
            // an existing output. A failed write therefore never destroys the previous file.
            using (var verificationBinder = ReadRegulation(tempPath))
            {
                if (!ulong.TryParse(verificationBinder.Version, out var verificationVersion) ||
                    verificationVersion != loadedVersion)
                {
                    throw new InvalidDataException(
                        $"Merged regulation verification failed for '{outputFullPath}'.");
                }
            }

            File.Move(tempPath, outputFullPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private RegulationMergeSource BuildSource(string path, List<string> warnings)
    {
        using var binder = ReadRegulation(path);

        if (!ulong.TryParse(binder.Version, out var version))
            throw new InvalidDataException(LOC.Get("PARAM_AutoMerge_Error_Invalid_Param_Version"));

        var loadedVersion = Patcher.Project.Handler.ParamData.PrimaryBank.ParamVersion;
        var sourceParams = ParseBinderParams(binder, version, warnings, strict: false);

        ParamDeltaPatch delta;
        var autoUpgraded = false;

        var displayVersion = ParamUtils.ParseRegulationVersion(version);
        var displayProjectVersion = ParamUtils.ParseRegulationVersion(loadedVersion);

        if (version == loadedVersion)
        {
            delta = BuildDelta(
                path, sourceParams, loadedVersion, warnings,
                Patcher.Project.Handler.ParamData.VanillaBank.Params);
        }
        else
        {
            if (!AutoUpgradeMismatchedVersions)
            {
                throw new InvalidDataException(
                    LOC.Get("PARAM_AutoMerge_Error_Mismatched_Param_Version", displayVersion, displayProjectVersion));
            }

            if (Patcher.Project.Descriptor.ProjectType is not (ProjectType.ER or ProjectType.AC6 or ProjectType.NR))
            {
                throw new InvalidDataException(
                    LOC.Get("PARAM_AutoMerge_Error_Auto_Reg_Upgrade_Not_Available", displayVersion, displayProjectVersion));
            }

            if (version > loadedVersion)
            {
                throw new InvalidDataException(
                    LOC.Get("PARAM_AutoMerge_Error_Auto_Reg_Upgrade_Not_Supported", displayVersion, displayProjectVersion));
            }

            var oldVanillaParams = LoadHistoricalVanillaParams(version, warnings);
            delta = BuildDelta(path, sourceParams, loadedVersion, warnings, oldVanillaParams);
            NormalizeDeltaForLoadedVersion(delta, warnings);
            autoUpgraded = true;

            warnings?.Add(
                LOC.Get("PARAM_AutoMerge_Warning_Auto_Upgrade_Complete",
                Path.GetFileName(path),
                displayVersion,
                displayProjectVersion));
        }

        return new RegulationMergeSource
        {
            Path = path,
            OriginalParamVersion = version,
            ParamVersion = loadedVersion,
            AutoUpgraded = autoUpgraded,
            ParsedParamCount = sourceParams.Count,
            Delta = delta
        };
    }

    private BND4 ReadRegulation(string path)
    {
        return Patcher.Project.Descriptor.ProjectType switch
        {
            ProjectType.ER => SFUtil.DecryptERRegulation(path),
            ProjectType.AC6 => SFUtil.DecryptAC6Regulation(path),
            ProjectType.NR => SFUtil.DecryptNightreignRegulation(path),
            ProjectType.DS3 => SFUtil.DecryptDS3Regulation(path),
            _ => throw new NotSupportedException(LOC.Get("PARAM_AutoMerge_Error_Direct_Merge_Not_Supported", Patcher.Project.Descriptor.ProjectType))
        };
    }

    private BND4 ReadRegulation(byte[] data)
    {
        return Patcher.Project.Descriptor.ProjectType switch
        {
            ProjectType.ER => SFUtil.DecryptERRegulation(data),
            ProjectType.AC6 => SFUtil.DecryptAC6Regulation(data),
            ProjectType.NR => SFUtil.DecryptNightreignRegulation(data),
            ProjectType.DS3 => SFUtil.DecryptDS3Regulation(data),
            _ => throw new NotSupportedException(LOC.Get("PARAM_AutoMerge_Error_Direct_Merge_Not_Supported", Patcher.Project.Descriptor.ProjectType))
        };
    }

    private BND4 ReadCurrentVanillaRegulation()
    {
        var relativePath = RootRegulationRelativePath;

        var fs = Patcher.Project.VFS.VanillaRealFS;
        if (!fs.FileExists(relativePath))
            throw new FileNotFoundException(
                LOC.Get("PARAM_AutoMerge_Error_Current_Vanilla_Reg_Not_Found", relativePath));

        return ReadRegulation(fs.GetFile(relativePath).GetData().ToArray());
    }

    private void WriteRegulation(string path, BND4 binder)
    {
        switch (Patcher.Project.Descriptor.ProjectType)
        {
            case ProjectType.ER:
                SFUtil.EncryptERRegulation(path, binder);
                break;
            case ProjectType.AC6:
                SFUtil.EncryptAC6Regulation(path, binder);
                break;
            case ProjectType.NR:
                SFUtil.EncryptNightreignRegulation(path, binder);
                break;
            case ProjectType.DS3:
                SFUtil.EncryptDS3Regulation(path, binder);
                break;
            default:
                throw new NotSupportedException(LOC.Get("PARAM_AutoMerge_Error_Direct_Merge_Not_Supported", Patcher.Project.Descriptor.ProjectType));
        }
    }

    private Dictionary<string, Param> ParseBinderParams(
        BND4 binder,
        ulong version,
        List<string> warnings,
        bool strict,
        Dictionary<string, string> originalParamTypes = null)
    {
        var result = new Dictionary<string, Param>(StringComparer.Ordinal);

        foreach (var file in binder.Files)
        {
            if (!file.Name.EndsWith(".PARAM", StringComparison.OrdinalIgnoreCase))
                continue;

            var paramName = System.IO.Path.GetFileNameWithoutExtension(
                file.Name.Replace('\\', System.IO.Path.DirectorySeparatorChar));

            if (result.ContainsKey(paramName))
                continue;

            try
            {
                var param = Param.ReadIgnoreCompression(file.Bytes);
                if (!PrepareParamType(paramName, param, originalParamTypes))
                {
                    var message = LOC.Get("PARAM_AutoMerge_Error_ParamParse_No_Compatible_ParamDefType", paramName);
                    if (strict)
                        throw new InvalidDataException(message);

                    warnings?.Add(message);
                    continue;
                }

                ApplyParamFixups(param, version);

                var def = Patcher.Project.Handler.ParamData.ParamDefs[param.ParamType];
                param.ApplyParamdef(def, version, paramName);
                result.Add(paramName, param);
            }
            catch (Exception ex)
            {
                if (strict)
                    throw new InvalidDataException(
                        LOC.Get("PARAM_AutoMerge_Error_ParamParse_Failed", paramName, ex.Message), ex);

                warnings?.Add(LOC.Get("PARAM_AutoMerge_Warning_ParamParse_Failed", paramName, ex.Message));
            }
        }

        return result;
    }

    private bool PrepareParamType(
        string paramName,
        Param param,
        Dictionary<string, string> originalParamTypes)
    {
        var projectType = Patcher.Project.Descriptor.ProjectType;
        var defs = Patcher.Project.Handler.ParamData.ParamDefs;

        if (projectType is ProjectType.AC6 or ProjectType.DS3)
        {
            var mustMap = string.IsNullOrEmpty(param.ParamType) ||
                !defs.ContainsKey(param.ParamType) ||
                Patcher.Project.Handler.ParamData.ParamTypeInfo.Exceptions.Contains(paramName);

            if (mustMap)
            {
                if (!Patcher.Project.Handler.ParamData.ParamTypeInfo.Mapping.TryGetValue(paramName, out var mappedType))
                    return false;

                if (originalParamTypes != null)
                    originalParamTypes[paramName] = param.ParamType ?? "";

                param.ParamType = mappedType;
            }
        }

        return !string.IsNullOrEmpty(param.ParamType) && defs.ContainsKey(param.ParamType);
    }

    private void ApplyParamFixups(Param param, ulong version)
    {
        if (Patcher.Project.Descriptor.ProjectType != ProjectType.ER)
            return;

        if (version >= 10601000 && param.ParamType == "CHR_MODEL_PARAM_ST")
            param.ExpandParamSize(12, 16);

        if (version >= 11210015)
        {
            if (param.ParamType == "GAME_SYSTEM_COMMON_PARAM_ST")
                param.ExpandParamSize(880, 1024);
            if (param.ParamType == "POSTURE_CONTROL_PARAM_WEP_RIGHT_ST")
                param.ExpandParamSize(112, 144);
            if (param.ParamType == "SIGN_PUDDLE_PARAM_ST")
                param.ExpandParamSize(32, 48);
        }
    }

    private Dictionary<string, Param> LoadHistoricalVanillaParams(ulong sourceVersion, List<string> warnings)
    {
        var gameDirectory = ProjectUtils.GetGameDirectory(Patcher.Project);
        var assetRoot = Path.Join(AppContext.BaseDirectory, "Assets", "PARAM", gameDirectory);
        var infoPath = Path.Join(assetRoot, "Upgrader Information.json");

        if (!File.Exists(infoPath))
        {
            throw new FileNotFoundException(
                LOC.Get("PARAM_AutoMerge_Error_ParamUpgrader_Not_Found", infoPath));
        }

        var infoJson = File.ReadAllText(infoPath);
        var info = JsonSerializer.Deserialize(
            infoJson, ParamEditorJsonSerializerContext.Default.ParamUpgraderInfo);

        if (info == null)
        {
            throw new InvalidDataException(LOC.Get("PARAM_AutoMerge_Error_ParamUpgrader_Not_Loaded"));
        }

        var sourceVersionText = ParamUtils.ParseRegulationVersion(sourceVersion);
        var entry = info.RegulationEntries.FirstOrDefault(e => e.Version == sourceVersionText);
        if (entry == null)
        {
            throw new InvalidDataException(
                LOC.Get("PARAM_AutoMerge_Error_ParamUpgrader_No_Data", sourceVersionText));
        }

        var regulationPath = Path.Join(assetRoot, "Regulations", entry.Folder, "regulation.bin");
        if (!File.Exists(regulationPath))
        {
            throw new FileNotFoundException(
                LOC.Get("PARAM_AutoMerge_Error_ParamUpgrader_Vanilla_Reg_Not_Found", regulationPath));
        }

        using var oldVanillaBinder = ReadRegulation(regulationPath);
        if (!ulong.TryParse(oldVanillaBinder.Version, out var oldVersion))
        {
            throw new InvalidDataException(
                LOC.Get("PARAM_AutoMerge_Error_ParamUpgrader_Invalid_Vanilla_Reg"));
        }

        if (oldVersion != sourceVersion)
        {
            var oldVer = ParamUtils.ParseRegulationVersion(oldVersion);

            throw new InvalidDataException(
                LOC.Get("PARAM_AutoMerge_Error_ParamUpgrader_Version_Mismatch", oldVer, sourceVersionText));
        }

        return ParseBinderParams(oldVanillaBinder, oldVersion, warnings, strict: false);
    }

    private void NormalizeDeltaForLoadedVersion(ParamDeltaPatch patch, List<string> warnings)
    {
        var currentVanilla = Patcher.Project.Handler.ParamData.VanillaBank.Params;
        var removedParams = 0;
        var removedFields = 0;
        var redundantDeletedRows = 0;

        for (var paramIndex = patch.Params.Count - 1; paramIndex >= 0; paramIndex--)
        {
            var paramDelta = patch.Params[paramIndex];
            if (!currentVanilla.TryGetValue(paramDelta.Name, out var currentParam))
            {
                patch.Params.RemoveAt(paramIndex);
                removedParams++;
                continue;
            }

            var validFields = currentParam.Rows
                .SelectMany(row => row.Columns)
                .Select(column => column.Def.InternalName)
                .ToHashSet(StringComparer.Ordinal);

            for (var rowIndex = paramDelta.Rows.Count - 1; rowIndex >= 0; rowIndex--)
            {
                var row = paramDelta.Rows[rowIndex];
                var currentRow = FindRow(currentParam, row.ID, row.Index);

                if (row.State == RowDeltaState.Modified && currentRow == null)
                {
                    throw new InvalidDataException(
                        $"Automatic upgrade cannot carry forward modified row {paramDelta.Name} {row.ID}:{row.Index} because that row no longer exists in the loaded vanilla regulation.");
                }

                if (row.State == RowDeltaState.Added && currentRow != null)
                {
                    throw new InvalidDataException(
                        $"Automatic upgrade cannot carry forward added row {paramDelta.Name} {row.ID}:{row.Index} because the loaded vanilla regulation now contains a row at the same identity.");
                }

                if (row.State == RowDeltaState.Deleted && currentRow == null)
                {
                    paramDelta.Rows.RemoveAt(rowIndex);
                    redundantDeletedRows++;
                    continue;
                }

                for (var fieldIndex = row.Fields.Count - 1; fieldIndex >= 0; fieldIndex--)
                {
                    if (validFields.Contains(row.Fields[fieldIndex].Field))
                        continue;

                    row.Fields.RemoveAt(fieldIndex);
                    removedFields++;
                }

                if (row.State == RowDeltaState.Modified && row.Fields.Count == 0 && row.Name == null)
                    paramDelta.Rows.RemoveAt(rowIndex);
            }

            if (paramDelta.Rows.Count == 0)
                patch.Params.RemoveAt(paramIndex);
        }

        if (removedParams > 0 || removedFields > 0 || redundantDeletedRows > 0)
        {
            warnings?.Add(
                $"Automatic upgrade skipped {removedParams} obsolete params, {removedFields} obsolete fields, and {redundantDeletedRows} redundant row deletions that no longer exist in the loaded version.");
        }
    }

    private ParamDeltaPatch BuildDelta(
        string sourcePath,
        IReadOnlyDictionary<string, Param> sourceParams,
        ulong targetVersion,
        List<string> warnings,
        IReadOnlyDictionary<string, Param> baselineParams)
    {
        var patch = new ParamDeltaPatch
        {
            ProjectType = Patcher.Project.Descriptor.ProjectType,
            ParamVersion = targetVersion,
            Tag = $"Regulation: {System.IO.Path.GetFileName(sourcePath)}"
        };

        foreach (var pair in sourceParams.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!baselineParams.TryGetValue(pair.Key, out var vanillaParam))
            {
                warnings?.Add(
                    LOC.Get("PARAM_AutoMerge_Error_Build_Delta_Skipped_Param", pair.Key));

                continue;
            }

            var paramDelta = CompareParam(pair.Key, pair.Value, vanillaParam);
            if (paramDelta.Rows.Count > 0)
                patch.Params.Add(paramDelta);
        }

        return patch;
    }

    private ParamDelta CompareParam(string paramName, Param source, Param vanilla)
    {
        var result = new ParamDelta { Name = paramName };
        var sourceRows = BuildRowMap(source);
        var vanillaRows = BuildRowMap(vanilla);

        foreach (var sourcePair in sourceRows)
        {
            if (!vanillaRows.TryGetValue(sourcePair.Key, out var vanillaRow))
            {
                result.Rows.Add(CreateAddedRow(sourcePair.Key, sourcePair.Value));
                continue;
            }

            var modified = CreateModifiedRow(sourcePair.Key, sourcePair.Value, vanillaRow);
            if (modified.Fields.Count > 0 || modified.Name != null)
                result.Rows.Add(modified);
        }

        foreach (var vanillaPair in vanillaRows)
        {
            if (sourceRows.ContainsKey(vanillaPair.Key))
                continue;

            result.Rows.Add(new RowDelta
            {
                ID = vanillaPair.Key.ID,
                Index = vanillaPair.Key.Index,
                Name = vanillaPair.Value.Name,
                State = RowDeltaState.Deleted
            });
        }

        return result;
    }

    private Dictionary<RowKey, Param.Row> BuildRowMap(Param param)
    {
        var result = new Dictionary<RowKey, Param.Row>();
        var occurrenceById = new Dictionary<int, int>();

        foreach (var row in param.Rows)
        {
            occurrenceById.TryGetValue(row.ID, out var occurrence);
            result[new RowKey(row.ID, occurrence)] = row;
            occurrenceById[row.ID] = occurrence + 1;
        }

        return result;
    }

    private RowDelta CreateAddedRow(RowKey key, Param.Row row)
    {
        var delta = new RowDelta
        {
            ID = key.ID,
            Index = key.Index,
            Name = row.Name,
            State = RowDeltaState.Added
        };

        foreach (var column in row.Columns)
        {
            var value = column.GetValue(row);
            var serialized = column.Def.InternalType == "dummy8" && column.Def.ArrayLength > 1
                ? ParamUtils.Dummy8Write((byte[])value)
                : value?.ToString() ?? "";

            delta.Fields.Add(new FieldDelta
            {
                Field = column.Def.InternalName,
                Value = serialized
            });
        }

        return delta;
    }

    private RowDelta CreateModifiedRow(RowKey key, Param.Row row, Param.Row vanillaRow)
    {
        var delta = new RowDelta
        {
            ID = key.ID,
            Index = key.Index,
            Name = string.Equals(row.Name, vanillaRow.Name, StringComparison.Ordinal) ? null : row.Name,
            State = RowDeltaState.Modified
        };

        foreach (var column in row.Columns)
        {
            var vanillaColumn = vanillaRow.Columns.FirstOrDefault(e => e.Def.InternalName == column.Def.InternalName);
            if (vanillaColumn == null)
                continue;

            var value = column.GetValue(row);
            var vanillaValue = vanillaColumn.GetValue(vanillaRow);
            if (FieldValuesEqual(value, vanillaValue))
                continue;

            var serialized = column.Def.InternalType == "dummy8" && column.Def.ArrayLength > 1
                ? ParamUtils.Dummy8Write((byte[])value)
                : value?.ToString() ?? "";

            delta.Fields.Add(new FieldDelta
            {
                Field = column.Def.InternalName,
                Value = serialized
            });
        }

        return delta;
    }

    private static bool FieldValuesEqual(object left, object right)
    {
        if (left is byte[] leftBytes && right is byte[] rightBytes)
            return leftBytes.SequenceEqual(rightBytes);

        return Equals(left, right);
    }


    private void ApplyMergedPatch(Dictionary<string, Param> paramsByName, ParamDeltaPatch patch)
    {
        foreach (var paramDelta in patch.Params)
        {
            if (!paramsByName.TryGetValue(paramDelta.Name, out var param))
            {
                throw new InvalidDataException(
                    LOC.Get("PARAM_AutoMerge_Error_Base_Reg_Missing_Param", paramDelta.Name));
            }

            foreach (var rowDelta in paramDelta.Rows)
            {
                ApplyRowDelta(paramDelta.Name, param, rowDelta);
            }
        }
    }

    private void ApplyRowDelta(string paramName, Param param, RowDelta rowDelta)
    {
        var existing = FindRow(param, rowDelta.ID, rowDelta.Index);

        if (rowDelta.State == RowDeltaState.Deleted)
        {
            if (existing != null)
                param.RemoveRow(existing);
            return;
        }

        if (existing == null)
        {
            Param.Row template;

            if (rowDelta.State == RowDeltaState.Modified)
            {
                template = FindVanillaRow(paramName, rowDelta.ID, rowDelta.Index);
                if (template == null)
                {
                    throw new InvalidDataException(
                        LOC.Get("PARAM_AutoMerge_Error_Row_Delta_Failed_Reconstruction",
                        paramName, rowDelta.ID, rowDelta.Index));
                }
            }
            else
            {
                template = param.Rows.FirstOrDefault() ??
                    Patcher.Project.Handler.ParamData.VanillaBank.Params[paramName].Rows.FirstOrDefault();

                if (template == null)
                {
                    throw new InvalidDataException(
                        LOC.Get("PARAM_AutoMerge_Error_Row_Delta_Failed_Add", paramName));
                }
            }

            existing = new Param.Row(template, param)
            {
                ID = rowDelta.ID,
                Name = rowDelta.Name ?? template.Name
            };

            InsertRowAtIdentity(param, existing, rowDelta.ID, rowDelta.Index);
        }
        else if (rowDelta.Name != null)
        {
            existing.Name = rowDelta.Name;
        }

        Patcher.Importer.HandleFieldImport(existing, rowDelta);
    }

    private Param.Row FindVanillaRow(string paramName, int id, int index)
    {
        if (!Patcher.Project.Handler.ParamData.VanillaBank.Params.TryGetValue(paramName, out var vanillaParam))
            return null;

        return FindRow(vanillaParam, id, index);
    }

    private static Param.Row FindRow(Param param, int id, int index)
    {
        var occurrence = 0;
        foreach (var row in param.Rows)
        {
            if (row.ID != id)
                continue;

            if (occurrence == index)
                return row;

            occurrence++;
        }

        return null;
    }

    private static void InsertRowAtIdentity(Param param, Param.Row newRow, int id, int index)
    {
        var rows = param.Rows.ToList();
        var matchingPositions = rows
            .Select((row, position) => new { row, position })
            .Where(e => e.row.ID == id)
            .Select(e => e.position)
            .ToList();

        if (matchingPositions.Count == 0)
        {
            param.AddRow(newRow);
            return;
        }

        if (index < matchingPositions.Count)
        {
            param.InsertRow(matchingPositions[index], newRow);
            return;
        }

        param.InsertRow(matchingPositions[^1] + 1, newRow);
    }

    private static string NormalizeUserPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        return path.Trim().Trim('"');
    }
}
