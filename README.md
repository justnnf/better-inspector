# Better Inspector

Better Inspector is a standalone ArcGIS Pro 3.3 add-in for reviewing utility-network attribute-rule errors directly from the Error Layers in the active map.

## Features

- Reads error point, line, polygon, and table layers in the active map.
- Filters errors to the visible map extent.
- Zooms to an error or selects its source feature/table record.
- Marks and clears error exceptions.
- Runs calculation and validation rule evaluation, including feature-service asynchronous evaluation when supported.
- Uses matching light and dark ribbon icons.

## Use in ArcGIS Pro

1. Add the relevant Error Layers to an active map.
2. Open the **Better Inspector** ribbon tab and click **Inspect Errors**.
3. Click **Refresh Error Layers** to load errors.
4. Use the **Evaluate** menu to choose rule types and evaluation extent, then click **Run**.

## Evaluation defaults and table columns

[Config/EvaluationDefaults.json](Config/EvaluationDefaults.json) controls the initial evaluation choices and the error table layout. It supports:

- `EvaluateCalculationRulesByDefault`
- `EvaluateValidationRulesByDefault`
- `UseVisibleEvaluationExtentByDefault`
- `EvaluateModifiedVersionByDefault`
- `RunEvaluationAsynchronouslyByDefault`
- `ErrorInspectorColumns`, where each entry has `Key`, `Order`, and `IsVisible`.

The file is bundled into the `.esriAddinX` package as `Config/EvaluationDefaults.json`. To customize a distributed package, edit that file in the archive and restart ArcGIS Pro.

## Build and install

Requirements: ArcGIS Pro 3.3 and the .NET 8 SDK.

```powershell
dotnet build .\BetterInspector.csproj
```

The build packages and registers:

`bin\Debug\net8.0-windows\BetterInspector.ArcPro.3.3.v.1.0.0.esriAddinX`

The package filename is controlled by the project assembly name. When changing the version, update the assembly name in `BetterInspector.csproj`, the `defaultAssembly` value in `Config.daml`, and `PackageFileName` in `InspectorConfig.cs` together.
