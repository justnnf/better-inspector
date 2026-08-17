using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace BetterInspector;

/// <summary>Reads the defaults packaged with the add-in.</summary>
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
                    var settings = DeserializeSettingsFile(settingsPath, File.ReadAllText(settingsPath));
                    if (settings != null) return settings;
                }
            }

            var assemblyName = GetAssemblyName();
            var packageFileName = $"{assemblyName}.esriAddinX";
            var packagePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ArcGIS", "AddIns", "ArcGISPro", InspectorConfig.AddInId, packageFileName);
            if (File.Exists(packagePath))
            {
                using var package = ZipFile.OpenRead(packagePath);
                var entry = package.GetEntry($"Install/{assemblyName}.dll.config");
                if (entry != null)
                {
                    using var reader = new StreamReader(entry.Open());
                    var settings = DeserializeDllConfig(reader.ReadToEnd());
                    if (settings != null) return settings;
                }

                // Compatibility with packages created before the .dll.config file.
                entry = package.GetEntry("Config/EvaluationDefaults.json");
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
            // A bad config file should not stop the add-in from loading.
        }

        var defaults = new InspectorSettingsState();
        defaults.Normalize();
        return defaults;
    }

    private static IEnumerable<string> GetLooseSettingsPaths()
    {
        var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrWhiteSpace(assemblyDirectory)) yield break;
        yield return Path.Combine(assemblyDirectory, $"{GetAssemblyName()}.dll.config");
        yield return Path.Combine(assemblyDirectory, "Config", "EvaluationDefaults.json");
        yield return Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "Config", "EvaluationDefaults.json"));
    }

    private static string GetAssemblyName() => Assembly.GetExecutingAssembly().GetName().Name
        ?? "BetterInspector";

    private static InspectorSettingsState? DeserializeSettingsFile(string path, string content) =>
        path.EndsWith(".dll.config", StringComparison.OrdinalIgnoreCase)
            ? DeserializeDllConfig(content)
            : Deserialize(content);

    private static InspectorSettingsState? Deserialize(string json)
    {
        var settings = JsonSerializer.Deserialize<InspectorSettingsState>(json);
        settings?.Normalize();
        return settings;
    }

    private static InspectorSettingsState? DeserializeDllConfig(string xml)
    {
        var settingsElement = XDocument.Parse(xml).Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "BetterInspectorSettings");
        if (settingsElement == null) return null;

        var settings = new InspectorSettingsState();
        settings.EvaluateCalculationRulesByDefault = ReadBoolean(settingsElement,
            nameof(settings.EvaluateCalculationRulesByDefault), settings.EvaluateCalculationRulesByDefault);
        settings.EvaluateValidationRulesByDefault = ReadBoolean(settingsElement,
            nameof(settings.EvaluateValidationRulesByDefault), settings.EvaluateValidationRulesByDefault);
        settings.UseVisibleEvaluationExtentByDefault = ReadBoolean(settingsElement,
            nameof(settings.UseVisibleEvaluationExtentByDefault), settings.UseVisibleEvaluationExtentByDefault);
        settings.EvaluateModifiedVersionByDefault = ReadBoolean(settingsElement,
            nameof(settings.EvaluateModifiedVersionByDefault), settings.EvaluateModifiedVersionByDefault);
        settings.RunEvaluationAsynchronouslyByDefault = ReadBoolean(settingsElement,
            nameof(settings.RunEvaluationAsynchronouslyByDefault), settings.RunEvaluationAsynchronouslyByDefault);

        var columns = settingsElement.Descendants().Where(element => element.Name.LocalName == "Column")
            .Select(column => new ErrorInspectorColumnOption
            {
                Key = (string?)column.Attribute("Key") ?? string.Empty,
                Order = ReadInteger(column, "Order"),
                IsVisible = ReadBoolean(column, "IsVisible", true)
            })
            .Where(column => !string.IsNullOrWhiteSpace(column.Key))
            .ToList();
        if (columns.Count > 0) settings.ErrorInspectorColumns = columns;
        settings.Normalize();
        return settings;
    }

    private static bool ReadBoolean(XElement element, string attributeName, bool defaultValue) =>
        bool.TryParse((string?)element.Attribute(attributeName), out var value) ? value : defaultValue;

    private static int ReadInteger(XElement element, string attributeName) =>
        int.TryParse((string?)element.Attribute(attributeName), out var value) ? value : 0;
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
