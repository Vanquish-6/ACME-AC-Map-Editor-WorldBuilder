using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;
using WorldBuilder.Lib.Aetherium;
using WorldBuilder.Lib.Settings;

namespace WorldBuilder.ViewModels {
    public partial class AetheriumConnectWindowViewModel : ObservableObject {
        private readonly WorldBuilderSettings _settings;

        public AetheriumConnectionSettings Connection => _settings.Aetherium;

        [ObservableProperty] private string _statusText = "Not checked.";
        [ObservableProperty] private bool _isBusy;

        public AetheriumConnectWindowViewModel(WorldBuilderSettings settings) {
            _settings = settings;
        }

        [RelayCommand]
        private void Save() {
            _settings.Save();
            StatusText = "Saved.";
        }

        [RelayCommand]
        private async Task Test() {
            if (IsBusy) return;
            IsBusy = true;
            StatusText = "Contacting Aetherium...";
            try {
                _settings.Save();
                StatusText = await WorldPatchClient.ProbeAsync(Connection);
            }
            catch (Exception ex) {
                StatusText = ex.GetBaseException().Message;
            }
            finally {
                IsBusy = false;
            }
        }
    }
}
