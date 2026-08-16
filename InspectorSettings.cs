using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace BetterInspector;

/// <summary>Defaults supplied with the add-in package.</summary>
internal static class InspectorSettings
{
    public static InspectorSettingsState Current { get; private set; } = Load();

    private static InspectorSettingsState Load()
    {
        try
        {
            foreach (var settingsPath in GetLooseSettingsPaths())
            {
                if (File.Exists(settingsPath))
                {
                    var settings = Deserialize(File.ReadAllText(settingsPath));
                    if (settings != null) return settings;
                }
            }

            var packagePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ArcGIS", "AddIns", "ArcGISPro", InspectorConfig.AddInId, InspectorConfig.PackageFileName);
            if (File.Exists(packagePath))
            {
                using var package = ZipFile.OpenRead(packagePath);
                var entry = package.GetEntry("Config/EvaluationDefaults.json");
                if (entry != null)
                {
                    using var reader = new StreamReader(entry.Open());
                    var settings = Deserialize(reader.ReadToEnd());
                    if (settings != null) return settings;
                }
            }
        }
        catch
        {
            // Invalid package configuration must not prevent ArcGIS Pro from loading the add-in.
        }

        var defaults = new InspectorSettingsState();
        defaults.Normalize();
        return defaults;
    }

    private static IEnumerable<string> GetLooseSettingsPaths()
    {
        var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrWhiteSpace(assemblyDirectory)) yield break;
        yield return Path.Combine(assemblyDirectory, "Config", "EvaluationDefaults.json");
        yield return Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "Config", "EvaluationDefaults.json"));
    }

    private static InspectorSettingsState? Deserialize(string json)
    {
        var settings = JsonSerializer.Deserialize<InspectorSettingsState>(json);
        settings?.Normalize();
        return settings;
    }
}

internal sealed class InspectorSettingsState
{
    public bool EvaluateCalculationRulesByDefault { get; set; } = true;
    public bool EvaluateValidationRulesByDefault { get; set; } = true;
    public bool UseVisibleEvaluationExtentByDefault { get; set; } = true;
    public bool EvaluateModifiedVersionByDefault { get; set; }
    public bool RunEvaluationAsynchronouslyByDefault { get; set; } = true;
    public List<ErrorInspectorColumnOption> ErrorInspectorColumns { get; set; } = ErrorInspectorColumnOption.CreateDefaults();

    public void Normalize()
    {
        ErrorInspectorColumns ??= [];
        var defaults = ErrorInspectorColumnOption.CreateDefaults();
        foreach (var option in defaults.Where(option => ErrorInspectorColumns.All(current => current.Key != option.Key)))
            ErrorInspectorColumns.Add(option);
        foreach (var option in ErrorInspectorColumns)
        {
            var defaultOption = defaults.FirstOrDefault(item => item.Key == option.Key);
            if (defaultOption == null) continue;
            option.Header = defaultOption.Header;
            if (option.Order <= 0) option.Order = defaultOption.Order;
        }
        ErrorInspectorColumns = ErrorInspectorColumns
            .Where(option => defaults.Any(defaultOption => defaultOption.Key == option.Key))
            .ToList();
    }
}

internal sealed class ErrorInspectorColumnOption
{
    public required string Key { get; set; }
    public string Header { get; set; } = string.Empty;
    public bool IsVisible { get; set; } = true;
    public int Order { get; set; }

    public static List<ErrorInspectorColumnOption> CreateDefaults() =>
    [
        new() { Key = "ShapeIndicator", Header = "Shape", Order = 1 },
        new() { Key = "RuleType", Header = "Rule Type", Order = 2 },
        new() { Key = "ExceptionStatus", Header = "Exception", Order = 3 },
        new() { Key = "FeatureObjectId", Header = "Feature ObjectID", Order = 4 },
        new() { Key = "FeatureClass", Header = "Feature Class", Order = 5 },
        new() { Key = "AssetGroup", Header = "Asset Group", Order = 6 },
        new() { Key = "FeatureGlobalId", Header = "Feature GlobalID", Order = 7 },
        new() { Key = "ErrorNumber", Header = "Error Number", Order = 8 },
        new() { Key = "Message", Header = "Error Message", Order = 9 },
        new() { Key = "Rule", Header = "Rule Name", Order = 10 },
        new() { Key = "Description", Header = "Description", Order = 11 },
        new() { Key = "Severity", Header = "Severity", Order = 12 }
    ];
}
