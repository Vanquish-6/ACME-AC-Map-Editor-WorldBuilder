using System.Windows.Input;

namespace WorldBuilder.ViewModels;

public sealed class ReadOnlyInfoViewModel : ViewModelBase {
    public string Title { get; }
    public string Message { get; }
    public string? ActionText { get; }
    public ICommand? ActionCommand { get; }
    public bool HasAction => ActionCommand != null && !string.IsNullOrWhiteSpace(ActionText);

    public ReadOnlyInfoViewModel(
        string title,
        string message,
        string? actionText = null,
        ICommand? actionCommand = null) {
        Title = title;
        Message = message;
        ActionText = actionText;
        ActionCommand = actionCommand;
    }
}
