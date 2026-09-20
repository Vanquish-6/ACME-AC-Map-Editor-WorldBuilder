using System.Collections;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace WorldBuilder.Lib.Docking {
    public partial class DockRegionView : UserControl {
        public static readonly StyledProperty<IEnumerable?> PanelsProperty =
            AvaloniaProperty.Register<DockRegionView, IEnumerable?>(nameof(Panels));

        public static readonly StyledProperty<bool> IsTabbedProperty =
            AvaloniaProperty.Register<DockRegionView, bool>(nameof(IsTabbed), defaultValue: true);

        public static readonly StyledProperty<bool> IsSectionedProperty =
            AvaloniaProperty.Register<DockRegionView, bool>(nameof(IsSectioned));

        public static readonly StyledProperty<ICommand?> ToggleModeCommandProperty =
            AvaloniaProperty.Register<DockRegionView, ICommand?>(nameof(ToggleModeCommand));

        public static readonly StyledProperty<bool> StackSectionsVerticallyProperty =
            AvaloniaProperty.Register<DockRegionView, bool>(nameof(StackSectionsVertically), defaultValue: true);

        public IEnumerable? Panels {
            get => GetValue(PanelsProperty);
            set => SetValue(PanelsProperty, value);
        }

        public bool IsTabbed {
            get => GetValue(IsTabbedProperty);
            set => SetValue(IsTabbedProperty, value);
        }

        public bool IsSectioned {
            get => GetValue(IsSectionedProperty);
            set => SetValue(IsSectionedProperty, value);
        }

        public ICommand? ToggleModeCommand {
            get => GetValue(ToggleModeCommandProperty);
            set => SetValue(ToggleModeCommandProperty, value);
        }

        public bool StackSectionsVertically {
            get => GetValue(StackSectionsVerticallyProperty);
            set => SetValue(StackSectionsVerticallyProperty, value);
        }

        public DockRegionView() {
            InitializeComponent();
            AttachedToVisualTree += (_, _) => EnsureTabSelection();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
            base.OnPropertyChanged(change);
            if (change.Property == PanelsProperty) {
                if (change.OldValue is INotifyCollectionChanged oldCollection) {
                    oldCollection.CollectionChanged -= OnPanelsCollectionChanged;
                }

                if (change.NewValue is INotifyCollectionChanged newCollection) {
                    newCollection.CollectionChanged += OnPanelsCollectionChanged;
                }

                EnsureTabSelection();
            }
            else if (change.Property == IsTabbedProperty) {
                EnsureTabSelection();
            }
        }

        private void OnPanelsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
            EnsureTabSelection();
        }

        private void EnsureTabSelection() {
            Dispatcher.UIThread.Post(() => {
                if (this.FindControl<TabStrip>("Tabs") is not { } tabs) {
                    return;
                }

                if (tabs.SelectedItem == null && tabs.ItemCount > 0) {
                    tabs.SelectedIndex = 0;
                }
            }, DispatcherPriority.Loaded);
        }
    }
}
