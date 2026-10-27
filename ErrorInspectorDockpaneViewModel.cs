using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Desktop.Core;
using ArcGIS.Core.Data;

namespace BetterInspector;

internal sealed class ErrorInspectorDockpaneViewModel : DockPane
{
    private readonly ErrorInspectionService _service = new();
    private ErrorInspectionItem? _selectedError;
    private string _status = "Refresh to read attribute-rule Error Layers from the active map.";
    private bool _isRefreshing;
    private bool _refreshPending;
    private bool _hasEvaluationSource;
    private EvaluationExtent _selectedExtent;
    private bool _modifiedInVersion;
    private bool _evaluateCalculation;
    private bool _evaluateValidation;
    private bool _runAsynchronously;
    private bool _isFeatureServiceWorkspace;
    private bool _canEvaluateVersionChanges;
    private bool _showVisibleErrors;

    public ErrorInspectorDockpaneViewModel()
    {
        var defaults = InspectorSettings.Current;
        _selectedExtent = defaults.UseVisibleEvaluationExtentByDefault ? EvaluationExtent.Visible : EvaluationExtent.Full;
        _modifiedInVersion = defaults.EvaluateModifiedVersionByDefault;
        _evaluateCalculation = defaults.EvaluateCalculationRulesByDefault;
        _evaluateValidation = defaults.EvaluateValidationRulesByDefault;
        _runAsynchronously = defaults.RunEvaluationAsynchronouslyByDefault;

        RefreshCommand = new RelayCommandAsync(async () => { await RefreshAsync(); }, () => !_isRefreshing);
        ZoomToErrorCommand = new RelayCommandAsync(ZoomToErrorAsync, () => SelectedError?.Geometry != null);
        SelectSourceFeatureCommand = new RelayCommandAsync(SelectSourceFeatureAsync,
            () => SelectedError?.SourceObjectId > 0);
        MarkExceptionCommand = new RelayCommandAsync(() => SetExceptionAsync(true), CanChangeException);
        ClearExceptionCommand = new RelayCommandAsync(() => SetExceptionAsync(false), CanChangeException);
        EvaluateCommand = new RelayCommandAsync(EvaluateSelectedRulesAsync, () => CanEvaluate() && (EvaluateCalculation || EvaluateValidation));
    }

    public static void Show()
    {
        if (FrameworkApplication.DockPaneManager.Find(InspectorConfig.DockPaneId) is DockPane pane)
            pane.Activate();
    }

    public void RefreshWhenMapReady() => _ = RefreshWhenMapReadyAsync();

    private async Task RefreshWhenMapReadyAsync()
    {
        if (_isRefreshing) return;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (GetAvailableMapView() != null)
            {
                await RefreshAsync();
                return;
            }
            await Task.Delay(250);
        }
        Status = "Activate a map to inspect attribute-rule Error Layers.";
    }

    public ObservableCollection<ErrorInspectionItem> Errors { get; } = [];
    public RelayCommandAsync RefreshCommand { get; }
    public RelayCommandAsync ZoomToErrorCommand { get; }
    public RelayCommandAsync SelectSourceFeatureCommand { get; }
    public RelayCommandAsync MarkExceptionCommand { get; }
    public RelayCommandAsync ClearExceptionCommand { get; }
    public RelayCommandAsync EvaluateCommand { get; }

    public EvaluationExtent SelectedExtent
    {
        get => _selectedExtent;
        set
        {
            if (!SetProperty(ref _selectedExtent, value, () => SelectedExtent)) return;
            NotifyPropertyChanged(() => IsVisibleExtent);
            NotifyPropertyChanged(() => IsFullExtent);
        }
    }

    public bool ModifiedInVersion
    {
        get => _modifiedInVersion;
        set => SetProperty(ref _modifiedInVersion, value, () => ModifiedInVersion);
    }

    public bool RunAsynchronously
    {
        get => _runAsynchronously;
        set => SetProperty(ref _runAsynchronously, value, () => RunAsynchronously);
    }

    public bool IsFeatureServiceWorkspace
    {
        get => _isFeatureServiceWorkspace;
        private set => SetProperty(ref _isFeatureServiceWorkspace, value, () => IsFeatureServiceWorkspace);
    }

    public bool CanEvaluateVersionChanges
    {
        get => _canEvaluateVersionChanges;
        private set => SetProperty(ref _canEvaluateVersionChanges, value, () => CanEvaluateVersionChanges);
    }

    public bool EvaluateCalculation
    {
        get => _evaluateCalculation;
        set
        {
            if (!SetProperty(ref _evaluateCalculation, value, () => EvaluateCalculation)) return;
            EvaluateCommand.RaiseCanExecuteChanged();
        }
    }

    public bool EvaluateValidation
    {
        get => _evaluateValidation;
        set
        {
            if (!SetProperty(ref _evaluateValidation, value, () => EvaluateValidation)) return;
            EvaluateCommand.RaiseCanExecuteChanged();
        }
    }

    public bool ShowVisibleErrors
    {
        get => _showVisibleErrors;
        set
        {
            if (!SetProperty(ref _showVisibleErrors, value, () => ShowVisibleErrors)) return;
            NotifyPropertyChanged(() => ShowAllErrors);
            _ = RefreshAsync();
        }
    }

    public bool ShowAllErrors
    {
        get => !ShowVisibleErrors;
        set
        {
            if (value) ShowVisibleErrors = false;
        }
    }

    public bool IsVisibleExtent
    {
        get => SelectedExtent == EvaluationExtent.Visible;
        set
        {
            if (!value) return;
            SelectedExtent = EvaluationExtent.Visible;
        }
    }

    public bool IsFullExtent
    {
        get => SelectedExtent == EvaluationExtent.Full;
        set
        {
            if (!value) return;
            SelectedExtent = EvaluationExtent.Full;
        }
    }

    public ErrorInspectionItem? SelectedError
    {
        get => _selectedError;
        set
        {
            if (!SetProperty(ref _selectedError, value, () => SelectedError)) return;
            ZoomToErrorCommand.RaiseCanExecuteChanged();
            SelectSourceFeatureCommand.RaiseCanExecuteChanged();
            MarkExceptionCommand.RaiseCanExecuteChanged();
            ClearExceptionCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value, () => Status);
    }

    private async Task<bool> RefreshAsync(bool allowWhileBusy = false)
    {
        if (_isRefreshing && !allowWhileBusy)
        {
            _refreshPending = true;
            return false;
        }
        try
        {
            _isRefreshing = true;
            RefreshCommand.RaiseCanExecuteChanged();
            Status = "Reading Error Layers...";
            var result = await QueuedTask.Run(() => _service.ScanActiveMap(ShowVisibleErrors));
            _hasEvaluationSource = result.ErrorLayerCount > 0;
            IsFeatureServiceWorkspace = result.IsFeatureService;
            var previouslySupportedVersionChanges = CanEvaluateVersionChanges;
            CanEvaluateVersionChanges = result.CanEvaluateVersionChanges;
            if (!CanEvaluateVersionChanges)
                ModifiedInVersion = false;
            else if (!previouslySupportedVersionChanges)
                ModifiedInVersion = InspectorSettings.Current.EvaluateModifiedVersionByDefault;
            Errors.Clear();
            foreach (var error in result.Items.OrderBy(item => item.ErrorLayer).ThenBy(item => item.ErrorNumber))
                Errors.Add(error);
            Status = result.ErrorLayerCount == 0
                ? "No Error Layers were found. Add Error Point, Error Line, Error Polygon, or Error Table to the active map."
                : $"Loaded {Errors.Count:n0} error(s) from {result.ErrorLayerCount} Error Layer(s)." +
                  (ShowVisibleErrors ? " Showing errors in the current map extent." : string.Empty);
            if (result.ScanWarnings.Count > 0)
                Status += $" Skipped {result.ScanWarnings.Count:n0} unreadable Error Layer(s): " +
                    string.Join(" | ", result.ScanWarnings.Take(4)) +
                    (result.ScanWarnings.Count > 4 ? " | More details in the debug output." : string.Empty);
            if (!string.IsNullOrWhiteSpace(InspectorSettings.LoadWarning))
                Status += $" Configuration warning: {InspectorSettings.LoadWarning}";
            foreach (var warning in result.ScanWarnings)
                System.Diagnostics.Debug.WriteLine($"Error-layer scan warning: {warning}");
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Error refresh failed: {ex.Message}";
            return false;
        }
        finally
        {
            _isRefreshing = false;
            RefreshCommand.RaiseCanExecuteChanged();
            EvaluateCommand.RaiseCanExecuteChanged();
            if (_refreshPending)
            {
                _refreshPending = false;
                _ = RefreshAsync();
            }
        }
    }

    private async Task ZoomToErrorAsync()
    {
        var geometry = SelectedError?.Geometry;
        var mapView = GetAvailableMapView();
        if (geometry == null || mapView == null) return;
        await QueuedTask.Run(() => mapView.ZoomTo(geometry.Extent));
    }

    private async Task SelectSourceFeatureAsync()
    {
        var item = SelectedError;
        if (item == null || item.SourceObjectId <= 0) return;
        try
        {
            var selectedMember = await QueuedTask.Run(() =>
            {
                var map = GetAvailableMapView()?.Map
                    ?? throw new InvalidOperationException("No active map is available.");
                var filter = new QueryFilter { ObjectIDs = [item.SourceObjectId] };
                var sourceClass = item.FeatureClass;
                var candidates = new List<MapMember>();
                var mapTables = map.GetStandaloneTablesAsFlattenedList();
                var availableTables = mapTables.Where(table => table is not SubtypeGroupTable)
                    .Concat(mapTables.OfType<SubtypeGroupTable>().SelectMany(group => group.StandaloneTables))
                    .Distinct().ToArray();
                var availableMembers = map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                    .Cast<MapMember>().Concat(availableTables).ToArray();
                if (item.SourceMapMember is SubtypeGroupTable groupTable)
                    candidates.AddRange(groupTable.StandaloneTables.Where(table => availableTables.Contains(table)));
                else if (item.SourceMapMember != null && availableMembers.Contains(item.SourceMapMember))
                    candidates.Add(item.SourceMapMember);

                foreach (var layer in availableMembers.OfType<FeatureLayer>())
                {
                    try
                    {
                        using var table = layer.GetTable();
                        if (MatchesSource(table, item.SourceClassId, sourceClass)) candidates.Add(layer);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Source candidate skipped {layer.Name}: {ex}");
                    }
                }
                foreach (var tableMember in availableTables)
                {
                    try
                    {
                        using var table = tableMember.GetTable();
                        if (MatchesSource(table, item.SourceClassId, sourceClass)) candidates.Add(tableMember);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Source candidate skipped {tableMember.Name}: {ex}");
                    }
                }

                Exception? selectionFailure = null;
                foreach (var candidate in candidates.Distinct())
                {
                    try
                    {
                        switch (candidate)
                        {
                            case FeatureLayer featureLayer:
                                using (var selection = featureLayer.Select(filter, SelectionCombinationMethod.New))
                                    if (selection.GetCount() > 0) return candidate;
                                break;
                            case StandaloneTable standaloneTable:
                                if (!standaloneTable.IsSelectable) break;
                                using (var selection = standaloneTable.Select(filter, SelectionCombinationMethod.New))
                                    if (selection.GetCount() > 0 && standaloneTable.SelectionCount > 0)
                                        return candidate;
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        selectionFailure = ex;
                        System.Diagnostics.Debug.WriteLine($"Source selection failed in {candidate.Name}: {ex}");
                    }
                }
                throw new InvalidOperationException(
                    $"Record {item.SourceObjectId} was not found in a matching source layer or table in the active map.",
                    selectionFailure);
            });
            Status = $"Selected source record {item.SourceObjectId} in {selectedMember.Name}.";
            try
            {
                if (selectedMember is StandaloneTable)
                    FrameworkApplication.Panes.OpenTablePane(selectedMember, TableViewMode.eSelectedRecords);
                else
                {
                    await FrameworkApplication.SetCurrentToolAsync(InspectorConfig.DefaultSelectionToolId);
                    if (FrameworkApplication.GetPlugInWrapper("esri_editing_ShowAttributes") is ICommand showAttributes &&
                        showAttributes.CanExecute(null))
                        showAttributes.Execute(null);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Selected source UI could not open: {ex}");
                Status += $" Could not open its details: {FormatException(ex)}";
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Select source exception: {ex}");
            Status = $"Could not select the source record: {FormatException(ex)}";
        }
    }

    private static bool MatchesSource(Table table, long sourceClassId, string sourceClass)
    {
        try
        {
            if (sourceClassId > 0 && table.GetID() == sourceClassId) return true;
        }
        catch
        {
            // Service layer IDs can differ from their geodatabase class IDs, so use
            // the layer name if the IDs do not match.
        }
        var tableName = table.GetName();
        var separator = tableName.LastIndexOf('.');
        if (separator >= 0) tableName = tableName[(separator + 1)..];
        tableName = StripServiceLayerPrefix(tableName.Trim());
        return string.Equals(tableName, sourceClass, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripServiceLayerPrefix(string name)
    {
        if (name.Length <= 2 || (name[0] != 'L' && name[0] != 'l')) return name;
        var nameStart = 1;
        while (nameStart < name.Length && char.IsDigit(name[nameStart])) nameStart++;
        return nameStart > 1 && nameStart < name.Length ? name[nameStart..] : name;
    }

    private bool CanEvaluate() => !_isRefreshing && _hasEvaluationSource;

    private void ApplyEvaluationDefaults()
    {
        var defaults = InspectorSettings.Current;
        EvaluateCalculation = defaults.EvaluateCalculationRulesByDefault;
        EvaluateValidation = defaults.EvaluateValidationRulesByDefault;
        SelectedExtent = defaults.UseVisibleEvaluationExtentByDefault ? EvaluationExtent.Visible : EvaluationExtent.Full;
        ModifiedInVersion = CanEvaluateVersionChanges && defaults.EvaluateModifiedVersionByDefault;
        RunAsynchronously = defaults.RunEvaluationAsynchronouslyByDefault;
    }

    private Task EvaluateSelectedRulesAsync()
    {
        if (!EvaluateCalculation && !EvaluateValidation)
            return Task.CompletedTask;

        var ruleType = EvaluateCalculation && EvaluateValidation ? AttributeRuleType.All
            : EvaluateCalculation ? AttributeRuleType.Calculation : AttributeRuleType.Validation;
        var operationName = EvaluateCalculation && EvaluateValidation ? "Batch calculation and validation"
            : EvaluateCalculation ? "Batch calculation" : "Batch validation";
        return EvaluateRulesAsync(ruleType, operationName);
    }

    private async Task EvaluateRulesAsync(AttributeRuleType ruleType, string operationName)
    {
        if (!_hasEvaluationSource) return;
        try
        {
            _isRefreshing = true;
            RefreshCommand.RaiseCanExecuteChanged();
            EvaluateCommand.RaiseCanExecuteChanged();
            Status = $"Running {operationName.ToLowerInvariant()}...";
            var result = await QueuedTask.Run(() =>
                _service.EvaluateRules(ruleType, SelectedExtent, ModifiedInVersion,
                    IsFeatureServiceWorkspace && RunAsynchronously));
            var refreshSucceeded = await RefreshAsync(allowWhileBusy: true);
            if (!refreshSucceeded)
            {
                Status = $"{operationName} completed with {result.NumberOfErrors:n0} error(s), " +
                    "but the Error Layers could not be refreshed. " + Status;
                return;
            }
            Status = $"{operationName} completed with {result.NumberOfErrors:n0} error(s). " +
                $"Loaded {Errors.Count:n0} error(s) from the Error Layers." +
                (result.RanAsynchronously ? " Server-side asynchronous evaluation was used." : string.Empty) +
                (string.IsNullOrWhiteSpace(result.ScopeNotice) ? string.Empty : $" {result.ScopeNotice}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EvaluateRules exception: {ex}");
            var diagnostic = FormatException(ex);
            Status = FormatEvaluationFailure(operationName, ex, diagnostic);
        }
        finally
        {
            _isRefreshing = false;
            RefreshCommand.RaiseCanExecuteChanged();
            EvaluateCommand.RaiseCanExecuteChanged();
        }
    }

    private static string FormatException(Exception exception)
    {
        var lines = new List<string>();
        for (var current = exception; current != null; current = current.InnerException)
        {
            var message = string.IsNullOrWhiteSpace(current.Message)
                ? current.GetType().Name
                : current.Message.Trim();
            var hresult = current.HResult == 0 ? string.Empty : $" (0x{current.HResult:X8})";
            lines.Add(message + hresult);
        }
        return string.Join(" -> ", lines.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private string FormatEvaluationFailure(string operationName, Exception exception, string diagnostic)
    {
        if (!IsServiceTimeout(exception))
            return $"{operationName} failed: {diagnostic}";

        var nextStep = IsFeatureServiceWorkspace && !RunAsynchronously
            ? "Try a smaller extent or select Run asynchronously."
            : "Try a smaller extent or ask the service administrator to increase the service usage timeout.";
        return $"{operationName} timed out: the validation service exceeded its request time limit. " +
               $"{nextStep} Technical detail: {diagnostic}";
    }

    private static bool IsServiceTimeout(Exception exception)
    {
        // ArcGIS Pro can surface a service usage-timeout as this otherwise generic
        // geodatabase HRESULT. Also recognize messages retained by other Pro versions.
        const int serviceTimeoutHResult = unchecked((int)0x80045308);
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current.HResult == serviceTimeoutHResult)
                return true;

            var message = current.Message ?? string.Empty;
            if (message.Contains("usage timeout", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("internal server error", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("status code 500", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private bool CanChangeException() => !_isRefreshing && SelectedError != null;

    private static MapView? GetAvailableMapView() => MapView.Active ??
        FrameworkApplication.Panes.OfType<IMapPane>()
            .Select(pane => pane.MapView)
            .FirstOrDefault(mapView => mapView != null);

    private async Task SetExceptionAsync(bool isException)
    {
        var item = SelectedError;
        if (item == null) return;
        try
        {
            _isRefreshing = true;
            MarkExceptionCommand.RaiseCanExecuteChanged();
            ClearExceptionCommand.RaiseCanExecuteChanged();
            var action = isException ? "Marking error as exception" : "Clearing error exception";
            Status = $"{action}...";
            await QueuedTask.Run(() => _service.SetException(item.ErrorType, item.ObjectId, isException));
            Status = isException ? "Error marked as an exception. Refreshing Error Layers..." : "Error exception cleared. Refreshing Error Layers...";
            await RefreshAsync(allowWhileBusy: true);
        }
        catch (Exception ex)
        {
            Status = $"Could not update the exception: {ex.Message}";
        }
        finally
        {
            _isRefreshing = false;
            MarkExceptionCommand.RaiseCanExecuteChanged();
            ClearExceptionCommand.RaiseCanExecuteChanged();
        }
    }
}

internal enum EvaluationExtent { Visible, Full }
