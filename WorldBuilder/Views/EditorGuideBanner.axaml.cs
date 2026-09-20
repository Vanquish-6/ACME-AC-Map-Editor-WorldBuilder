using Avalonia;
using Avalonia.Controls;

namespace WorldBuilder.Views;

public partial class EditorGuideBanner : UserControl {
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<EditorGuideBanner, string>(nameof(Title), "How this editor works");

    public static readonly StyledProperty<string> BodyProperty =
        AvaloniaProperty.Register<EditorGuideBanner, string>(nameof(Body), "");

    public string Title {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Body {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public EditorGuideBanner() {
        InitializeComponent();
    }
}
