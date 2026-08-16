using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Linq;
using System.Reflection;
using System.Text;

namespace BetterInspector;

public partial class ErrorInspectorDockpaneView : UserControl
{
    private DataGridCell? _contextCell;

    public ErrorInspectorDockpaneView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyErrorColumnSettings();
        };
    }

    private void MenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void ErrorsGrid_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        var element = e.OriginalSource as DependencyObject;
        _contextCell = null;
        while (element != null && element is not DataGridCell)
            element = VisualTreeHelper.GetParent(element);
        _contextCell = element as DataGridCell;

        element = e.OriginalSource as DependencyObject;
        while (element != null && element is not DataGridRow)
            element = VisualTreeHelper.GetParent(element);

        if (element is DataGridRow row)
            grid.SelectedItem = row.Item;
    }

    private void CopyCell_OnClick(object sender, RoutedEventArgs e)
    {
        if (_contextCell?.DataContext == null || _contextCell.Column == null) return;
        Clipboard.SetText(GetCellText(_contextCell.Column, _contextCell.DataContext));
    }

    private void CopyRowsWithHeaders_OnClick(object sender, RoutedEventArgs e)
    {
        var rows = ErrorsGrid.SelectedItems.Cast<object>().ToArray();
        if (rows.Length == 0 && _contextCell?.DataContext != null)
            rows = [_contextCell.DataContext];
        if (rows.Length == 0) return;

        var columns = ErrorsGrid.Columns.Where(column => column.Visibility == Visibility.Visible).ToArray();
        var result = new StringBuilder();
        result.AppendLine(string.Join("\t", columns.Select(column => column.Header?.ToString() ?? string.Empty)));
        foreach (var row in rows)
            result.AppendLine(string.Join("\t", columns.Select(column => GetCellText(column, row))));
        Clipboard.SetText(result.ToString());
    }

    private void ApplyErrorColumnSettings()
    {
        if (ErrorsGrid == null) return;
        var options = InspectorSettings.Current.ErrorInspectorColumns;
        foreach (var column in ErrorsGrid.Columns)
        {
            var option = options.FirstOrDefault(item => item.Key == column.SortMemberPath);
            column.Visibility = option?.IsVisible != false ? Visibility.Visible : Visibility.Collapsed;
        }

        var index = 0;
        foreach (var option in options.Where(item => item.IsVisible).OrderBy(item => item.Order))
        {
            var column = ErrorsGrid.Columns.FirstOrDefault(item => item.SortMemberPath == option.Key);
            if (column != null) column.DisplayIndex = index++;
        }
    }

    private static string GetCellText(DataGridColumn column, object row)
    {
        var property = row.GetType().GetProperty(column.SortMemberPath, BindingFlags.Instance | BindingFlags.Public);
        return property?.GetValue(row)?.ToString() ?? string.Empty;
    }
}
