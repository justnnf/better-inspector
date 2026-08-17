using System;
using System.Collections.Generic;
using System.Linq;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Core.Data.UtilityNetwork;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Mapping;

namespace BetterInspector;

/// <summary>Reads and updates the active map's Error Layers.</summary>
internal sealed class ErrorInspectionService
{
    private static readonly string[] ErrorLayerNames =
        ["Error Point", "Error Line", "Error Polygon", "Error Table", "Point Errors", "Line Errors", "Polygon Errors", "Object Errors"];

    public ErrorInspectionScanResult ScanActiveMap(bool visibleExtentOnly = false)
    {
        var mapView = ResolveMapView();
        var map = mapView.Map ?? throw new InvalidOperationException("The active map is still loading. Use Refresh Error Layers once it is ready.");
        var visibleExtent = visibleExtentOnly ? mapView.Extent : null;
        var items = new List<ErrorInspectionItem>();
        var workspaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanWarnings = new List<string>();
        var scannedLayers = 0;
        using var sourceResolver = ErrorSourceResolver.TryCreate(map);

        foreach (var layer in map.GetLayersAsFlattenedList().OfType<FeatureLayer>())
        {
            try
            {
                if (!IsErrorLayer(layer)) continue;
                scannedLayers++;
                AddWorkspace(layer.GetTable(), workspaces);
                items.AddRange(ReadFeatures(layer, sourceResolver));
            }
            catch (Exception ex)
            {
                scanWarnings.Add($"{layer.Name}: {ex.Message}");
            }
        }

        foreach (var table in map.GetStandaloneTablesAsFlattenedList())
        {
            try
            {
                if (!IsErrorTable(table)) continue;
                scannedLayers++;
                AddWorkspace(table.GetTable(), workspaces);
                items.AddRange(ReadTable(table, sourceResolver));
            }
            catch (Exception ex)
            {
                scanWarnings.Add($"{table.Name}: {ex.Message}");
            }
        }

        sourceResolver?.PopulateAssetGroups(items);

        if (visibleExtent != null)
        {
            items = items.Where(item => item.Geometry != null &&
                GeometryEngine.Instance.Intersects(item.Geometry, visibleExtent)).ToList();
        }

        var capabilities = GetEvaluationCapabilities(map);
        return new ErrorInspectionScanResult(items, scannedLayers, workspaces.ToArray(),
            capabilities.IsFeatureService, capabilities.CanEvaluateVersionChanges, scanWarnings);
    }

    /// <summary>Runs the selected calculation or validation rules.</summary>
    public RuleEvaluationOutcome EvaluateRules(AttributeRuleType ruleType,
        EvaluationExtent extentScope, bool changesInVersion, bool runAsynchronously)
    {
        var mapView = ResolveMapView();
        var map = mapView.Map ?? throw new InvalidOperationException(
            "The active map is still loading. Wait for it to finish loading and run validation again.");
        if (!HasErrorLayers(map))
            throw new InvalidOperationException("No Error Layers are available in the active map.");

        // Use a real utility-network source for evaluation so Pro keeps the active
        // feature-service and version connection.
        using var geodatabase = OpenUtilityNetworkGeodatabase(map);

        using var manager = geodatabase.GetAttributeRuleManager();
        if (!manager.IsEvaluationSupported())
            throw new InvalidOperationException(
                "This geodatabase does not support SDK evaluation of batch calculation or validation rules.");

        // Only use ChangesInVersion for a named service version. It is not valid
        // for file geodatabases or the default version.
        var canEvaluateVersionChanges = CanEvaluateChangesInVersion(geodatabase);
        var useVersionChanges = changesInVersion && canEvaluateVersionChanges;
        var versionScope = useVersionChanges
            ? VersionEvaluationScope.ChangesInVersion
            : VersionEvaluationScope.EntireVersion;
        var description = extentScope == EvaluationExtent.Visible
            ? new AttributeRuleEvaluationDescription(ruleType, versionScope, mapView.Extent)
            : new AttributeRuleEvaluationDescription(ruleType, versionScope);
        if (geodatabase.GetGeodatabaseType() == GeodatabaseType.Service)
        {
            description.ServiceSynchronizationType = runAsynchronously
                ? ServiceSynchronizationType.Asynchronous
                : ServiceSynchronizationType.Synchronous;
        }

        // This also creates an undoable edit operation and refreshes affected layers.
        var result = manager.EvaluateInEditOperation(description) ?? throw new InvalidOperationException(
            "The validation service completed without returning an evaluation result. " +
            "Check the service logs for the corresponding validation request.");
        var scopeNotice = changesInVersion && !useVersionChanges
            ? "This local or nonversioned workspace has no version delta; pending rows were evaluated using EntireVersion scope and the selected extent."
            : string.Empty;
        return new RuleEvaluationOutcome(result.NumberOfErrors, scopeNotice,
            geodatabase.GetGeodatabaseType() == GeodatabaseType.Service && runAsynchronously);
    }

    public void SetException(ValidationErrorType errorType, long errorObjectId, bool isException)
    {
        var map = ResolveMapView().Map;
        using var geodatabase = OpenUtilityNetworkGeodatabase(map);
        using var manager = geodatabase.GetAttributeRuleManager();
        if (!manager.IsEvaluationSupported())
            throw new InvalidOperationException("This workspace does not support attribute-rule error updates.");

        using var errorTable = manager.GetErrorTable(errorType);
        using var cursor = errorTable.Search(new QueryFilter { ObjectIDs = [errorObjectId] }, false);
        if (!cursor.MoveNext())
            throw new InvalidOperationException($"Error row {errorObjectId} was not found in the {errorType} error table.");

        using var row = cursor.Current;
        var error = manager.CreateAttributeRuleError(row);
        error.IsException = isException;
        manager.UpdateErrorsInEditOperation([error]);
    }

    private static EvaluationCapabilities GetEvaluationCapabilities(Map map)
    {
        try
        {
            using var geodatabase = OpenUtilityNetworkGeodatabase(map);
            var isFeatureService = geodatabase.GetGeodatabaseType() == GeodatabaseType.Service;
            return new EvaluationCapabilities(isFeatureService,
                isFeatureService && CanEvaluateChangesInVersion(geodatabase));
        }
        catch
        {
            // Reading errors can still work when evaluation is not available.
            return new EvaluationCapabilities(false, false);
        }
    }

    private static bool CanEvaluateChangesInVersion(Geodatabase geodatabase)
    {
        // Version-delta evaluation only works with a versioned feature service.
        if (geodatabase.GetGeodatabaseType() != GeodatabaseType.Service ||
            !geodatabase.IsVersioningSupported())
            return false;

        using var versionManager = geodatabase.GetVersionManager();
        using var currentVersion = versionManager.GetCurrentVersion();
        using var defaultVersion = versionManager.GetDefaultVersion();
        return !string.Equals(currentVersion.GetName(), defaultVersion.GetName(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static MapView ResolveMapView() => MapView.Active ??
        FrameworkApplication.Panes.OfType<IMapPane>()
            .Select(pane => pane.MapView)
            .FirstOrDefault(mapView => mapView != null)
        ?? throw new InvalidOperationException("Open a map before inspecting errors.");

    private static bool HasErrorLayers(Map map) =>
        map.GetLayersAsFlattenedList().OfType<FeatureLayer>().Any(IsErrorLayer) ||
        map.GetStandaloneTablesAsFlattenedList().Any(IsErrorTable);

    private static Geodatabase OpenUtilityNetworkGeodatabase(Map map)
    {
        var errorWorkspacePath = GetErrorWorkspacePath(map);
        foreach (var utilityNetworkLayer in map.GetLayersAsFlattenedList().OfType<UtilityNetworkLayer>())
        {
            using var utilityNetwork = utilityNetworkLayer.GetUtilityNetwork();
            if (utilityNetwork == null) continue;
            using var definition = utilityNetwork.GetDefinition();
            foreach (var source in definition.GetNetworkSources())
            {
                using var sourceTable = utilityNetwork.GetTable(source);
                var datastore = sourceTable.GetDatastore();
                if (datastore is not Geodatabase geodatabase)
                {
                    datastore.Dispose();
                    continue;
                }

                var sourceWorkspacePath = geodatabase.GetPath()?.AbsolutePath;
                if (string.Equals(sourceWorkspacePath, errorWorkspacePath, StringComparison.OrdinalIgnoreCase))
                    return geodatabase;
                geodatabase.Dispose();
            }
        }

        throw new InvalidOperationException(
            "No utility network in the active map uses the same geodatabase as the Error Layers.");
    }

    private static string GetErrorWorkspacePath(Map map)
    {
        foreach (var layer in map.GetLayersAsFlattenedList().OfType<FeatureLayer>())
        {
            if (!IsErrorLayer(layer)) continue;
            using var table = layer.GetTable();
            using var datastore = table.GetDatastore();
            var path = datastore.GetPath()?.AbsolutePath;
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }
        foreach (var tableMember in map.GetStandaloneTablesAsFlattenedList())
        {
            if (!IsErrorTable(tableMember)) continue;
            using var table = tableMember.GetTable();
            using var datastore = table.GetDatastore();
            var path = datastore.GetPath()?.AbsolutePath;
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }
        throw new InvalidOperationException("The Error Layers do not expose a usable geodatabase path.");
    }

    private static bool IsErrorLayer(FeatureLayer layer) =>
        IsKnownErrorLayerName(layer.Name) || HasAttributeRuleErrorSchema(layer.GetTable());

    private static bool IsErrorTable(StandaloneTable table) =>
        IsKnownErrorLayerName(table.Name) || HasAttributeRuleErrorSchema(table.GetTable());

    private static bool IsKnownErrorLayerName(string name) => ErrorLayerNames.Any(expected =>
        string.Equals(name, expected, StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith($" - {expected}", StringComparison.OrdinalIgnoreCase));

    private static bool HasAttributeRuleErrorSchema(Table table)
    {
        using (table)
        using (var definition = table.GetDefinition())
        {
            var fields = definition.GetFields().Select(field => field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return fields.Contains("ErrorNumber") && fields.Contains("ErrorMessage") &&
                   fields.Contains("RuleName") && fields.Contains("IsException");
        }
    }

    private static void AddWorkspace(Table table, ISet<string> workspaces)
    {
        using (table)
        {
            using var datastore = table.GetDatastore();
            var path = datastore.GetPath()?.AbsolutePath;
            if (!string.IsNullOrWhiteSpace(path)) workspaces.Add(path);
        }
    }

    private static IEnumerable<ErrorInspectionItem> ReadFeatures(FeatureLayer layer,
        ErrorSourceResolver? sourceResolver)
    {
        using var table = layer.GetTable();
        using var cursor = table.Search(null, false);
        using var definition = table.GetDefinition();
        var fields = definition.GetFields().Select(field => field.Name).ToArray();
        var objectIdField = definition.GetObjectIDField();
        while (cursor.MoveNext())
        {
            using var row = cursor.Current;
            yield return CreateItem(layer.Name, row, fields, objectIdField, sourceResolver,
                (row as Feature)?.GetShape());
        }
    }

    private static IEnumerable<ErrorInspectionItem> ReadTable(StandaloneTable standaloneTable,
        ErrorSourceResolver? sourceResolver)
    {
        using var table = standaloneTable.GetTable();
        using var cursor = table.Search(null, false);
        using var definition = table.GetDefinition();
        var fields = definition.GetFields().Select(field => field.Name).ToArray();
        var objectIdField = definition.GetObjectIDField();
        while (cursor.MoveNext())
        {
            using var row = cursor.Current;
            yield return CreateItem(standaloneTable.Name, row, fields, objectIdField, sourceResolver, null);
        }
    }

    private static ErrorInspectionItem CreateItem(string errorLayer, Row row, IReadOnlyCollection<string> fields,
        string objectIdField, ErrorSourceResolver? sourceResolver, Geometry? geometry)
    {
        var errorType = GetErrorType(errorLayer);
        var errorObjectId = ToInt64(Value(row, fields, objectIdField));
        var sourceId = ToInt64(FirstRawValue(row, fields,
            "FeatureClassID", "OriginTableID", "SourceLayerID"));
        var sourceObjectId = ToInt64(FirstRawValue(row, fields,
            "FeatureObjectID", "FeatureOID", "OriginObjectID", "FeatureID"));
        var sourceGlobalId = FirstValue(row, fields, "FeatureGlobalID", "OriginGlobalID");
        var source = sourceResolver?.Resolve(row, errorType, errorObjectId,
                sourceId, sourceObjectId, sourceGlobalId)
            ?? new ErrorSourceDetails(sourceId > 0 ? $"Class {sourceId}" : string.Empty,
                string.Empty, null, sourceObjectId, sourceGlobalId);
        sourceObjectId = source.ObjectId;
        return new ErrorInspectionItem
        {
            ErrorLayer = errorLayer,
            ObjectId = errorObjectId,
            Rule = FirstValue(row, fields, "RuleName", "RuleID", "RuleDescription"),
            RuleType = FormatRuleType(FirstValue(row, fields, "RuleType")),
            ErrorNumber = FirstValue(row, fields, "ErrorNumber", "ErrorCode"),
            Message = FirstValue(row, fields, "ErrorMessage", "ErrorDescription", "Description"),
            ExceptionStatus = FormatBoolean(FirstRawValue(row, fields, "IsException", "Exception")),
            FeatureClass = source.FeatureClass,
            AssetGroup = source.AssetGroup,
            FeatureObjectId = sourceObjectId > 0 ? sourceObjectId.ToString() : string.Empty,
            FeatureGlobalId = source.GlobalId,
            Description = FirstValue(row, fields, "Description", "RuleDescription"),
            Severity = FirstValue(row, fields, "Severity"),
            ShapeIndicator = errorType.ToString(),
            SourceClassId = sourceId,
            SourceObjectId = sourceObjectId,
            ErrorType = errorType,
            SourceMapMember = source.MapMember,
            Geometry = geometry
        };
    }

    private static ValidationErrorType GetErrorType(string errorLayer)
    {
        if (errorLayer.Contains("Point", StringComparison.OrdinalIgnoreCase)) return ValidationErrorType.Point;
        if (errorLayer.Contains("Line", StringComparison.OrdinalIgnoreCase)) return ValidationErrorType.Line;
        if (errorLayer.Contains("Polygon", StringComparison.OrdinalIgnoreCase)) return ValidationErrorType.Polygon;
        return ValidationErrorType.Object;
    }

    private static string FormatRuleType(string value)
    {
        if (!long.TryParse(value, out var code)) return value;
        return code switch
        {
            0 => "Calculation",
            1 => "Constraint",
            2 => "Validation",
            _ => value
        };
    }

    private static string FormatBoolean(object? value)
    {
        if (value == null || value == DBNull.Value) return string.Empty;
        if (value is bool boolean) return boolean ? "Yes" : "No";
        return long.TryParse(Convert.ToString(value), out var numeric)
            ? numeric == 0 ? "No" : "Yes"
            : Convert.ToString(value) ?? string.Empty;
    }

    private static object? FirstRawValue(Row row, IReadOnlyCollection<string> fields, params string[] candidates)
    {
        var field = candidates.FirstOrDefault(candidate => fields.Any(name =>
            string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)));
        return field == null ? null : Value(row, fields, field);
    }

    private static string FirstValue(Row row, IReadOnlyCollection<string> fields, params string[] candidates)
    {
        var field = candidates.FirstOrDefault(candidate => fields.Any(name =>
            string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)));
        return field == null ? string.Empty : Convert.ToString(Value(row, fields, field)) ?? string.Empty;
    }

    private static object? Value(Row row, IReadOnlyCollection<string> fields, string field)
    {
        var actualField = fields.FirstOrDefault(name => string.Equals(name, field, StringComparison.OrdinalIgnoreCase));
        return actualField == null ? null : row[actualField];
    }

    private static long ToInt64(object? value) => value == null || value == DBNull.Value ? 0 : Convert.ToInt64(value);

    private sealed class ErrorSourceResolver : IDisposable
    {
        private readonly UtilityNetwork _utilityNetwork;
        private readonly UtilityNetworkDefinition _definition;
        private readonly Geodatabase? _geodatabase;
        private readonly AttributeRuleManager? _attributeRuleManager;
        private readonly IReadOnlyList<NetworkSource> _sources;
        private readonly Dictionary<long, NetworkSource> _sourcesById = [];
        private readonly Dictionary<long, NetworkSource> _sourcesByDatasetId = [];
        private readonly Dictionary<string, NetworkSource> _sourcesByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<long, List<MapMember>> _mapMembersByDatasetId;
        private readonly Dictionary<string, List<MapMember>> _mapMembersByName;
        private readonly Dictionary<long, Dictionary<int, string>> _assetGroupsBySource = [];
        private readonly string _assetGroupField;

        private ErrorSourceResolver(UtilityNetwork utilityNetwork, UtilityNetworkDefinition definition,
            Geodatabase? geodatabase, AttributeRuleManager? attributeRuleManager,
            IReadOnlyList<NetworkSource> sources, MapMemberIndex mapMembers)
        {
            _utilityNetwork = utilityNetwork;
            _definition = definition;
            _geodatabase = geodatabase;
            _attributeRuleManager = attributeRuleManager;
            _sources = sources;
            _mapMembersByDatasetId = mapMembers.ByDatasetId;
            _mapMembersByName = mapMembers.ByName;
            _assetGroupField = definition.GetAssetGroupField();
            foreach (var source in sources)
            {
                _sourcesById[source.ID] = source;
                _sourcesByName[NormalizeName(source.Name)] = source;
                try
                {
                    using var sourceTable = utilityNetwork.GetTable(source);
                    _sourcesByDatasetId[sourceTable.GetID()] = source;
                }
                catch
                {
                    // System sources are not always available as tables.
                }
            }
        }

        public static ErrorSourceResolver? TryCreate(Map map)
        {
            var layer = map.GetLayersAsFlattenedList().OfType<UtilityNetworkLayer>().FirstOrDefault();
            if (layer == null) return null;

            UtilityNetwork? utilityNetwork = null;
            UtilityNetworkDefinition? definition = null;
            Geodatabase? geodatabase = null;
            AttributeRuleManager? attributeRuleManager = null;
            try
            {
                utilityNetwork = layer.GetUtilityNetwork();
                if (utilityNetwork == null) return null;
                definition = utilityNetwork.GetDefinition();
                var sources = definition.GetNetworkSources();
                foreach (var source in sources)
                {
                    try
                    {
                        using var sourceTable = utilityNetwork.GetTable(source);
                        var datastore = sourceTable.GetDatastore();
                        if (datastore is not Geodatabase sourceGeodatabase)
                        {
                            datastore.Dispose();
                            continue;
                        }
                        geodatabase = sourceGeodatabase;
                        attributeRuleManager = geodatabase.GetAttributeRuleManager();
                        break;
                    }
                    catch
                    {
                        attributeRuleManager?.Dispose();
                        geodatabase?.Dispose();
                        attributeRuleManager = null;
                        geodatabase = null;
                    }
                }
                return new ErrorSourceResolver(utilityNetwork, definition, geodatabase,
                    attributeRuleManager, sources, BuildMapMemberIndex(map));
            }
            catch
            {
                attributeRuleManager?.Dispose();
                geodatabase?.Dispose();
                definition?.Dispose();
                utilityNetwork?.Dispose();
                return null;
            }
        }

        public ErrorSourceDetails Resolve(Row errorRow, ValidationErrorType errorType, long errorObjectId,
            long sourceId, long sourceObjectId, string sourceGlobalId)
        {
            var originClass = string.Empty;
            if (_attributeRuleManager != null)
            {
                try
                {
                    AttributeRuleError error;
                    try
                    {
                        error = _attributeRuleManager.CreateAttributeRuleError(errorRow);
                    }
                    catch
                    {
                        using var errorTable = _attributeRuleManager.GetErrorTable(errorType);
                        using var cursor = errorTable.Search(
                            new QueryFilter { ObjectIDs = [errorObjectId] }, false);
                        if (!cursor.MoveNext()) throw;
                        using var managerRow = cursor.Current;
                        error = _attributeRuleManager.CreateAttributeRuleError(managerRow);
                    }
                    originClass = NormalizeName(error.OriginClass);
                    sourceObjectId = error.OriginObjectID;
                    sourceGlobalId = error.OriginGlobalID == Guid.Empty
                        ? sourceGlobalId
                        : error.OriginGlobalID.ToString();
                }
                catch
                {
                    // Older workspaces may only expose the values in the error table.
                }
            }

            NetworkSource? source = null;
            if (!string.IsNullOrWhiteSpace(originClass))
                _sourcesByName.TryGetValue(originClass, out source);
            if (source == null && !_sourcesByDatasetId.TryGetValue(sourceId, out source))
                _sourcesById.TryGetValue(sourceId, out source);

            if (source == null)
            {
                var unresolvedClass = !string.IsNullOrWhiteSpace(originClass)
                    ? originClass
                    : sourceId > 0 ? $"Class {sourceId}" : string.Empty;
                return new ErrorSourceDetails(unresolvedClass, string.Empty,
                    ResolveMapMember(sourceId, unresolvedClass, string.Empty), sourceObjectId, sourceGlobalId);
            }

            var featureClass = NormalizeName(source.Name);
            var member = ResolveMapMember(sourceId, featureClass, string.Empty);
            return new ErrorSourceDetails(featureClass, string.Empty, member, sourceObjectId, sourceGlobalId);
        }

        public void PopulateAssetGroups(IReadOnlyList<ErrorInspectionItem> items)
        {
            if (string.IsNullOrWhiteSpace(_assetGroupField)) return;
            var itemsBySource = items.Where(item => item.SourceObjectId > 0)
                .GroupBy(ResolveNetworkSource)
                .Where(group => group.Key != null);
            foreach (var group in itemsBySource)
            {
                try
                {
                    PopulateAssetGroups(group.Key!, group.ToArray());
                }
                catch
                {
                    // Asset-group lookup enriches the inspector; a failed lookup must not hide errors.
                }
            }
        }

        private NetworkSource? ResolveNetworkSource(ErrorInspectionItem item)
        {
            if (_sourcesByDatasetId.TryGetValue(item.SourceClassId, out var source)) return source;
            if (_sourcesById.TryGetValue(item.SourceClassId, out source)) return source;
            return _sourcesByName.TryGetValue(NormalizeName(item.FeatureClass), out source) ? source : null;
        }

        private void PopulateAssetGroups(NetworkSource source, IReadOnlyList<ErrorInspectionItem> items)
        {
            using var table = _utilityNetwork.GetTable(source);
            using var tableDefinition = table.GetDefinition();
            if (tableDefinition.FindField(_assetGroupField) < 0) return;
            var objectIdField = tableDefinition.GetObjectIDField();
            var objectIds = items.Select(item => item.SourceObjectId).Distinct().ToArray();
            var codesByObjectId = new Dictionary<long, int>();
            using var cursor = table.Search(new QueryFilter { ObjectIDs = objectIds }, false);
            while (cursor.MoveNext())
            {
                using var row = cursor.Current;
                var objectId = ToInt64(row[objectIdField]);
                var assetGroupValue = row[_assetGroupField];
                if (assetGroupValue != null && assetGroupValue != DBNull.Value)
                    codesByObjectId[objectId] = Convert.ToInt32(assetGroupValue);
            }

            foreach (var item in items)
            {
                if (codesByObjectId.TryGetValue(item.SourceObjectId, out var code))
                    item.AssetGroup = ResolveAssetGroupName(source, code);
            }
        }

        private string ResolveAssetGroupName(NetworkSource source, int code)
        {
            if (!_assetGroupsBySource.TryGetValue(source.ID, out var namesByCode))
            {
                namesByCode = [];
                var groups = source.GetAssetGroups();
                try
                {
                    foreach (var group in groups) namesByCode[group.Code] = group.Name;
                }
                finally
                {
                    foreach (var group in groups) group.Dispose();
                }
                _assetGroupsBySource[source.ID] = namesByCode;
            }
            return namesByCode.TryGetValue(code, out var name) ? name : code.ToString();
        }

        private MapMember? ResolveMapMember(long datasetId, string featureClass, string assetGroup)
        {
            if (!_mapMembersByDatasetId.TryGetValue(datasetId, out var candidates) &&
                !_mapMembersByName.TryGetValue(NormalizeName(featureClass), out candidates))
                return null;
            return candidates.FirstOrDefault(candidate =>
                       string.Equals(candidate.Name, assetGroup, StringComparison.OrdinalIgnoreCase))
                   ?? candidates.FirstOrDefault();
        }

        private static MapMemberIndex BuildMapMemberIndex(Map map)
        {
            var byDatasetId = new Dictionary<long, List<MapMember>>();
            var byName = new Dictionary<string, List<MapMember>>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in map.GetLayersAsFlattenedList().OfType<FeatureLayer>().Where(layer => !IsErrorLayer(layer)))
            {
                using var table = layer.GetTable();
                AddMapMember(byDatasetId, byName, table, layer);
            }
            foreach (var standaloneTable in map.GetStandaloneTablesAsFlattenedList().Where(table => !IsErrorTable(table)))
            {
                using var table = standaloneTable.GetTable();
                AddMapMember(byDatasetId, byName, table, standaloneTable);
            }
            return new MapMemberIndex(byDatasetId, byName);
        }

        private static void AddMapMember(IDictionary<long, List<MapMember>> byDatasetId,
            IDictionary<string, List<MapMember>> byName, Table table, MapMember mapMember)
        {
            var datasetId = table.GetID();
            if (!byDatasetId.TryGetValue(datasetId, out var idMembers))
                byDatasetId[datasetId] = idMembers = [];
            idMembers.Add(mapMember);

            var key = NormalizeName(table.GetName());
            if (!byName.TryGetValue(key, out var nameMembers)) byName[key] = nameMembers = [];
            nameMembers.Add(mapMember);
        }

        private static string NormalizeName(string name)
        {
            var separator = name.LastIndexOf('.');
            var normalized = (separator >= 0 ? name[(separator + 1)..] : name).Trim();
            if (normalized.Length > 2 && (normalized[0] == 'L' || normalized[0] == 'l'))
            {
                var nameStart = 1;
                while (nameStart < normalized.Length && char.IsDigit(normalized[nameStart])) nameStart++;
                if (nameStart > 1 && nameStart < normalized.Length)
                    normalized = normalized[nameStart..];
            }
            return normalized;
        }

        public void Dispose()
        {
            foreach (var source in _sources) source.Dispose();
            _attributeRuleManager?.Dispose();
            _geodatabase?.Dispose();
            _definition.Dispose();
            _utilityNetwork.Dispose();
        }

        private sealed record MapMemberIndex(Dictionary<long, List<MapMember>> ByDatasetId,
            Dictionary<string, List<MapMember>> ByName);
    }
}

internal sealed record ErrorSourceDetails(string FeatureClass, string AssetGroup, MapMember? MapMember,
    long ObjectId, string GlobalId);

internal sealed record ErrorInspectionScanResult(IReadOnlyList<ErrorInspectionItem> Items, int ErrorLayerCount,
    IReadOnlyList<string> EvaluationWorkspaces, bool IsFeatureService, bool CanEvaluateVersionChanges,
    IReadOnlyList<string> ScanWarnings);
internal sealed record EvaluationCapabilities(bool IsFeatureService, bool CanEvaluateVersionChanges);
internal sealed record RuleEvaluationOutcome(long NumberOfErrors, string ScopeNotice, bool RanAsynchronously);
