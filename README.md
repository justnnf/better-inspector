# Better Inspector

Better Inspector is a separate ArcGIS Pro add-in for looking at utility network errors. It reads the Error Layers that are already in the active map, so you can review errors without opening the built-in Error Inspector.

## What it does

- Lists errors from Error Point, Error Line, Error Polygon, and Error Table layers.
- Lets you show all errors or only the ones in the current map extent.
- Zooms to an error and selects the source feature or table record.
- Marks an error as an exception or clears an exception.
- Runs calculation rules and validation rules from the pane.

## Using it

Add the Error Layers to the map, open the **Better Inspector** tab, and click **Inspect Errors**. Use **Refresh Error Layers** to load the errors. The **Evaluate** dropdown controls what gets evaluated; click **Run** when the options are set the way you want them.

## Defaults

The defaults are in [Config/BetterInspector.dll.config](Config/BetterInspector.dll.config).

You can set the default evaluation options there:

- Calculation rules
- Validation rules
- Visible or full extent
- Modified features in the current version
- Asynchronous evaluation for feature services

`ErrorInspectorColumns` controls the table layout. Each `Column` has a `Key`, `Order`, and `IsVisible` attribute.

The settings file is included in the `.esriAddinX` file at `Install/<assembly>.dll.config`. To change defaults for a packaged copy, edit that file inside the archive, then restart ArcGIS Pro.

## Build

ArcGIS Pro 3.3 and the .NET 8 SDK are required.

```powershell
dotnet build .\BetterInspector.csproj
```

The build creates and registers:

`bin\Debug\net8.0-windows\BetterInspector.ArcPro.3.3.v.<version>.esriAddinX`

A versioned compiled copy is also written to `release`.
