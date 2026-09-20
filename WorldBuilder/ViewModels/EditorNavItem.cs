using CommunityToolkit.Mvvm.Input;

namespace WorldBuilder.ViewModels;

public sealed class EditorNavItem {
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public bool IsSelected { get; init; }
    public bool IsVisible { get; init; } = true;
    public IRelayCommand<string>? SelectCommand { get; init; }
}
