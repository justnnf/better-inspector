using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace BetterInspector;

internal sealed class Module1 : Module
{
    private static Module1? _this;

    public static Module1 Current =>
        _this ??= (Module1)FrameworkApplication.FindModule("BetterInspector_Module");

    protected override bool CanUnload() => true;
}
