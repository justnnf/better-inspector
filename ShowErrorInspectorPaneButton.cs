using ArcGIS.Desktop.Framework.Contracts;

namespace BetterInspector;

internal sealed class ShowErrorInspectorPaneButton : Button
{
    protected override void OnClick() => ErrorInspectorDockpaneViewModel.Show();
}
