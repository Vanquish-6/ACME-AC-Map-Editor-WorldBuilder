using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using DialogHostAvalonia;
using System.Threading.Tasks;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Lib;

/// <summary>
/// Legacy (DM) to retail conversion mode picker. Exactly two supported modes:
/// Full DM (everything DM + DM UI replacing the retail UI) and
/// Half DM (DM world/textures/objs/setups/cells with the retail UI shell).
/// </summary>
public static class LegacyDatExportPolicyPicker {
    public static async Task<LegacyDatExportPolicy?> PickExportPolicyAsync(string dialogHost = "MainDialogHost") {
        LegacyDatExportPolicy? selected = null;

        var fullDm = new RadioButton {
            Content = ModeOption(
                "Full DM",
                "Everything Dark Majesty: DM world, textures, objects, setups and cells, DM CharGen + appearance tables, "
                + "and the retail UI replaced with DM wording and layout shapes (login/connect shell art stays retail; "
                + "DM dats do not ship those surfaces)."),
            GroupName = "mode",
            IsChecked = true,
        };
        var halfDm = new RadioButton {
            Content = ModeOption(
                "Half DM",
                "DM world, textures, objects, setups and cells only. Retail UI, retail CharGen, retail appearance and "
                + "spell/skill/vital tables (best compatibility with retail-style servers)."),
            GroupName = "mode",
        };

        var continueButton = new Button { Content = "Continue" };
        continueButton.Click += (_, _) => {
            selected = LegacyDatExportPolicy.FromPreset(
                halfDm.IsChecked == true ? LegacyDatExportPreset.HalfDm : LegacyDatExportPreset.FullDm);
            DialogHost.Close(dialogHost);
        };

        await DialogHost.Show(new StackPanel {
            Margin = new Thickness(18),
            Spacing = 12,
            MaxWidth = 560,
            Children = {
                new TextBlock {
                    Text = "Legacy DAT conversion mode",
                    FontSize = 16,
                    FontWeight = FontWeight.Bold,
                },
                fullDm,
                halfDm,
                new TextBlock {
                    Text = "Both modes export retail-format client_*.dat files (overlay on the retail seed) that load "
                        + "on the retail client.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85,
                },
                new StackPanel {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = {
                        new Button {
                            Content = "Cancel",
                            Command = new RelayCommand(() => DialogHost.Close(dialogHost)),
                        },
                        continueButton,
                    },
                },
            },
        }, dialogHost);

        return selected;
    }

    private static StackPanel ModeOption(string title, string description) => new() {
        Spacing = 2,
        MaxWidth = 500,
        Children = {
            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
            new TextBlock {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
            },
        },
    };
}
