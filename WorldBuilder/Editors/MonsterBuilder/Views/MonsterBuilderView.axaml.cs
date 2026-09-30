using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System;
using WorldBuilder.Lib;

namespace WorldBuilder.Editors.MonsterBuilder.Views;

public partial class MonsterBuilderView : UserControl {
    public MonsterBuilderView() {
        InitializeComponent();
        if (Design.IsDesignMode) return;
        var vm = ProjectManager.Instance.GetProjectService<MonsterBuilderViewModel>()
            ?? throw new InvalidOperationException("MonsterBuilderViewModel is not registered.");
        DataContext = vm;
        if (ProjectManager.Instance.CurrentProject != null)
            vm.Init(ProjectManager.Instance.CurrentProject);
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

public sealed class DepthMarginConverter : Avalonia.Data.Converters.IValueConverter {
    public static DepthMarginConverter Instance { get; } = new();
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is int depth ? new Avalonia.Thickness(4 + depth * 14, 3, 4, 3) : new Avalonia.Thickness(4, 3);
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
