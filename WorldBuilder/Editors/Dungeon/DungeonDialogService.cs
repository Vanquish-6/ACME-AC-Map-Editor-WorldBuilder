using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Acme.Dat;
using DialogHostAvalonia;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WorldBuilder.Lib;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Services;
using Acme.Render.GL.Lib;

namespace WorldBuilder.Editors.Dungeon {

    /// <summary>
    /// All dialog construction for the dungeon editor. Returns parsed results so
    /// the ViewModel never builds UI controls directly.
    /// </summary>
    public class DungeonDialogService {

        public async Task<uint?> ShowNewDungeonDialog() {
            uint? result = null;
            var textBox = new TextBox {
                Text = "",
                Width = 300,
                Watermark = "Landblock hex ID for new dungeon (e.g. FFFF)"
            };
            var errorText = new TextBlock {
                Text = "",
                Foreground = Brushes.Red,
                FontSize = 12,
                IsVisible = false
            };

            await DialogHost.Show(new StackPanel {
                Margin = new Thickness(20),
                Spacing = 10,
                Children = {
                    new TextBlock {
                        Text = "New Dungeon",
                        FontSize = 16,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock {
                        Text = "Enter a landblock ID for the new dungeon.",
                        FontSize = 12,
                        Opacity = 0.7
                    },
                    textBox,
                    errorText,
                    new StackPanel {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = {
                            new Button {
                                Content = "Cancel",
                                Command = new RelayCommand(() => DialogHost.Close("DungeonDialogHost"))
                            },
                            new Button {
                                Content = "Create",
                                Command = new RelayCommand(() => {
                                    var parsed = ParseLandblockInput(textBox.Text);
                                    if (parsed != null) {
                                        result = parsed;
                                        DialogHost.Close("DungeonDialogHost");
                                    }
                                    else {
                                        errorText.Text = "Invalid hex ID.";
                                        errorText.IsVisible = true;
                                    }
                                })
                            }
                        }
                    }
                }
            }, "DungeonDialogHost");

            return result;
        }

        public async Task<uint?> ShowOpenDungeonDialog(string currentLandblockText, Action<string> setLandblockText) {
            uint? result = null;
            var textBox = new TextBox {
                Text = currentLandblockText,
                Width = 400,
                Watermark = "Search dungeon by name or enter hex ID (e.g. 01D9)"
            };
            var errorText = new TextBlock {
                Text = "",
                Foreground = Brushes.Red,
                FontSize = 12,
                IsVisible = false
            };

            var locationList = new ListBox {
                MaxHeight = 300,
                Width = 400,
                IsVisible = false,
                FontSize = 12,
            };

            void UpdateLocationResults(string? query) {
                if (string.IsNullOrWhiteSpace(query) || query.Length < 2) {
                    locationList.IsVisible = false;
                    locationList.ItemsSource = null;
                    return;
                }

                var results = LocationDatabase.Search(query, typeFilter: "Dungeon").Take(50).ToList();
                if (results.Count > 0) {
                    locationList.ItemsSource = results.Select(r => $"{r.Name}  [{r.LandblockHex}]").ToList();
                    locationList.IsVisible = true;
                }
                else {
                    locationList.IsVisible = false;
                    locationList.ItemsSource = null;
                }
            }

            textBox.TextChanged += (s, e) => {
                errorText.IsVisible = false;
                UpdateLocationResults(textBox.Text);
            };

            locationList.SelectionChanged += (s, e) => {
                if (locationList.SelectedIndex < 0) return;
                var query = textBox.Text;
                if (string.IsNullOrWhiteSpace(query)) return;
                var results = LocationDatabase.Search(query, typeFilter: "Dungeon").Take(50).ToList();
                if (locationList.SelectedIndex < results.Count) {
                    var selected = results[locationList.SelectedIndex];
                    setLandblockText(selected.Name);
                    result = selected.CellId;
                    DialogHost.Close("DungeonDialogHost");
                }
            };

            await DialogHost.Show(new StackPanel {
                Margin = new Thickness(20),
                Spacing = 10,
                Children = {
                    new TextBlock {
                        Text = "Open Dungeon",
                        FontSize = 16,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock {
                        Text = "Search by dungeon name or enter a\nlandblock ID in hex (e.g. 01D9).",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 400,
                        FontSize = 12,
                        Opacity = 0.7
                    },
                    textBox,
                    locationList,
                    errorText,
                    new StackPanel {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = {
                            new Button {
                                Content = "Cancel",
                                Command = new RelayCommand(() => DialogHost.Close("DungeonDialogHost"))
                            },
                            new Button {
                                Content = "Open",
                                Command = new RelayCommand(() => {
                                    if (result != null) {
                                        DialogHost.Close("DungeonDialogHost");
                                        return;
                                    }
                                    var parsed = ParseLandblockInput(textBox.Text);
                                    if (parsed != null) {
                                        setLandblockText(textBox.Text ?? "");
                                        result = parsed;
                                        DialogHost.Close("DungeonDialogHost");
                                    }
                                    else {
                                        errorText.Text = "Invalid input. Try a dungeon name or hex ID.";
                                        errorText.IsVisible = true;
                                    }
                                })
                            }
                        }
                    }
                }
            }, "DungeonDialogHost");

            return result;
        }

        public void ShowValidationDialog(
            List<DungeonDocument.ValidationResult> results,
            Action<DungeonDocument.ValidationResult> goToProblem,
            Action autoFixPortals,
            Action computeVisibility) {

            var errors = results.Count(r => r.Severity == DungeonDocument.ValidationSeverity.Error);
            var warnings = results.Count(r => r.Severity == DungeonDocument.ValidationSeverity.Warning);
            var title = errors > 0 ? $"Validation: {errors} error(s), {warnings} warning(s)"
                : warnings > 0 ? $"Validation: {warnings} warning(s)"
                : "Validation: All clear";

            var listBox = new ListBox {
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                Background = Brushes.Transparent,
                MaxHeight = 350,
            };
            foreach (var r in results) {
                var color = r.Severity switch {
                    DungeonDocument.ValidationSeverity.Error => "#ff6666",
                    DungeonDocument.ValidationSeverity.Warning => "#ffcc44",
                    _ => "#88cc88"
                };
                var item = new ListBoxItem {
                    Content = new StackPanel {
                        Spacing = 2,
                        Children = {
                            new TextBlock {
                                Text = $"{r.Icon} {r.Message}",
                                Foreground = new SolidColorBrush(Color.Parse(color)),
                                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                                FontSize = 11,
                            },
                            new TextBlock {
                                Text = string.IsNullOrEmpty(r.Explanation) ? (r.LocationLabel ?? "") : r.Explanation,
                                Foreground = new SolidColorBrush(Color.Parse("#8a7a9e")),
                                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                                FontSize = 10,
                                IsVisible = !string.IsNullOrEmpty(r.Explanation) || !string.IsNullOrEmpty(r.LocationLabel),
                            }
                        }
                    },
                    Tag = r,
                };
                listBox.Items.Add(item);
            }

            listBox.SelectionChanged += (s, e) => {
                if (listBox.SelectedItem is ListBoxItem li && li.Tag is DungeonDocument.ValidationResult result) {
                    goToProblem(result);
                }
            };

            var autoFixBtn = new Button {
                Content = "Auto-Fix One-Way Portals",
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(12, 6),
                FontSize = 11,
            };

            var computeVisBtn = new Button {
                Content = "Compute VisibleCells",
                Margin = new Thickness(8, 8, 0, 0),
                Padding = new Thickness(12, 6),
                FontSize = 11,
            };

            var panel = new StackPanel { Width = 550, Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#c0b0d8")),
                Margin = new Thickness(0, 0, 0, 8),
            });
            panel.Children.Add(listBox);

            var btnRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            if (results.Any(r => r.Message.Contains("aren't connected both ways") || r.Message.Contains("One-way")))
                btnRow.Children.Add(autoFixBtn);
            if (results.Any(r => r.Message.Contains("VisibleCells")))
                btnRow.Children.Add(computeVisBtn);
            if (btnRow.Children.Count > 0)
                panel.Children.Add(btnRow);

            autoFixBtn.Click += (s, e) => {
                autoFixPortals();
                DialogHost.Close("DungeonDialogHost");
            };

            computeVisBtn.Click += (s, e) => {
                computeVisibility();
                DialogHost.Close("DungeonDialogHost");
            };

            DialogHost.Show(panel, "DungeonDialogHost");
        }

        public async System.Threading.Tasks.Task<bool> ShowConfirmDiscard(string title, string message) {
            bool confirmed = false;
            var discard = new Button { Content = "Discard changes", Padding = new Thickness(12, 6) };
            var keep = new Button { Content = "Keep editing", Padding = new Thickness(12, 6), Margin = new Thickness(8, 0, 0, 0) };
            discard.Click += (_, _) => { confirmed = true; DialogHost.Close("DungeonDialogHost"); };
            keep.Click += (_, _) => DialogHost.Close("DungeonDialogHost");
            await DialogHost.Show(new StackPanel {
                Width = 420,
                Margin = new Thickness(16),
                Spacing = 10,
                Children = {
                    new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#e8e0f4")) },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#c0b0d8")) },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { discard, keep } }
                }
            }, "DungeonDialogHost");
            return confirmed;
        }

        public void ShowErrorDialog(string title, string message) {
            var win = new Window { Title = title, Width = 400, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14 });
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            var okBtn = new Button { Content = "OK", Width = 80 };
            okBtn.Click += (s, e) => win.Close();
            panel.Children.Add(okBtn);
            win.Content = panel;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
                win.Show(desktop.MainWindow);
            else
                win.Show();
        }

        public void ShowAnalysisResultDialog(DungeonRoomAnalyzer.AnalysisReport report, string outPath) {
            var jsonPath = Path.Combine(Path.GetDirectoryName(outPath) ?? "", Path.GetFileNameWithoutExtension(outPath) + ".json");
            var txtPath = Path.Combine(Path.GetDirectoryName(outPath) ?? "", Path.GetFileNameWithoutExtension(outPath) + ".txt");

            var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock {
                Text = "Dungeon Room Analysis Complete",
                FontWeight = FontWeight.SemiBold,
                FontSize = 14
            });
            panel.Children.Add(new TextBlock {
                Text = $"Scanned {report.TotalLandblocksScanned} landblocks, {report.TotalCellsScanned} cells.\n" +
                       $"Found {report.UniqueRoomTypes} unique room types.\n\n" +
                       $"Top starter candidates (for preset list):",
                TextWrapping = TextWrapping.Wrap
            });
            var sb = new System.Text.StringBuilder();
            foreach (var r in report.TopStarterCandidates.Take(12)) {
                sb.AppendLine($"  {r.PortalCount}P: 0x{r.EnvFileId:X8} / #{r.CellStructIndex}  (used {r.UsageCount}x)");
            }
            panel.Children.Add(new TextBlock {
                Text = sb.ToString(),
                FontFamily = "Consolas",
                FontSize = 10,
                TextWrapping = TextWrapping.NoWrap
            });
            panel.Children.Add(new TextBlock {
                Text = $"Report saved to:\n{txtPath}\n{jsonPath}",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 138, 122, 158)),
                TextWrapping = TextWrapping.Wrap
            });
            var win = new Window { Title = "Dungeon Room Analysis", WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var okBtn = new Button { Content = "OK", Width = 80 };
            okBtn.Click += (s, e) => win.Close();
            panel.Children.Add(okBtn);
            win.Content = new ScrollViewer { Content = panel };
            win.Width = 480;
            win.Height = 420;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null) {
                win.Show(desktop.MainWindow);
            }
            else {
                win.Show();
            }
        }

        public async Task<GeneratorParams?> ShowGenerateDialog(DungeonKnowledgeBase kb, IDatReaderWriter? dats = null, TextureImportService? textureImport = null, HashSet<string>? favoritePrefabSignatures = null, List<DungeonPrefab>? customPrefabs = null) {
            GeneratorParams? result = null;
            int favCount = favoritePrefabSignatures?.Count ?? 0;

            var styles = new List<string> { "All" };
            var seenStyles = kb.Catalog
                .Select(c => c.Style)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .OrderBy(s => s);
            styles.AddRange(seenStyles);

            var roomCount = new NumericUpDown { Value = 64, Minimum = 8, Maximum = 512, Width = 110, FontSize = 12 };

            var themeLookup = kb.StyleThemes.ToDictionary(t => t.Name, t => t, StringComparer.OrdinalIgnoreCase);

            var styleItems = new List<StackPanel>();
            foreach (var styleName in styles) {
                var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
                if (themeLookup.TryGetValue(styleName, out var theme) && dats != null) {
                    var wallThumb = RenderSurfaceMiniThumb(dats, theme.WallSurface);
                    var floorThumb = RenderSurfaceMiniThumb(dats, theme.FloorSurface);
                    if (wallThumb != null)
                        row.Children.Add(new Avalonia.Controls.Image { Source = wallThumb, Width = 20, Height = 20 });
                    if (floorThumb != null)
                        row.Children.Add(new Avalonia.Controls.Image { Source = floorThumb, Width = 20, Height = 20 });
                }
                row.Children.Add(new TextBlock {
                    Text = styleName == "All" ? "All (mixed)" : styleName,
                    FontSize = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                });
                styleItems.Add(row);
            }

            // Add "Custom" entry at the end
            var customRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            customRow.Children.Add(new TextBlock {
                Text = "Custom...",
                FontSize = 12, FontStyle = FontStyle.Italic,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            styleItems.Add(customRow);
            int customIndex = styleItems.Count - 1;

            var styleCombo = new ComboBox {
                FontSize = 12, Width = 220,
                ItemsSource = styleItems,
                SelectedIndex = 0,
            };

            ushort customWall = 0x032A;
            ushort customFloor = 0x032B;
            var customWallImg = new Avalonia.Controls.Image { Width = 28, Height = 28 };
            var customFloorImg = new Avalonia.Controls.Image { Width = 28, Height = 28 };
            var customPanel = new StackPanel {
                Spacing = 6, IsVisible = false, Margin = new Thickness(0, 4, 0, 0)
            };

            // Collect all unique dungeon surfaces from KB + custom imports
            var dungeonSurfaceIds = new HashSet<ushort>();
            foreach (var room in kb.Catalog) {
                if (room.SampleSurfaces != null)
                    foreach (var s in room.SampleSurfaces)
                        dungeonSurfaceIds.Add(s);
            }
            var customImportIds = new List<ushort>();
            if (textureImport != null) {
                foreach (var entry in textureImport.Store.GetDungeonSurfaces()) {
                    if (entry.SurfaceGid != 0) {
                        ushort unqual = (ushort)(entry.SurfaceGid & 0xFFFF);
                        customImportIds.Add(unqual);
                        dungeonSurfaceIds.Add(unqual);
                    }
                }
            }
            var sortedSurfaceIds = dungeonSurfaceIds.OrderBy(id => id).ToArray();

            // Build thumbnail cache (render once, reuse for both wall/floor grids)
            var thumbCache = new Dictionary<ushort, Avalonia.Media.Imaging.WriteableBitmap?>();
            if (dats != null) {
                foreach (var sid in sortedSurfaceIds)
                    thumbCache[sid] = RenderSurfaceMiniThumb(dats, sid);
            }

            // Selecting target: 0 = wall, 1 = floor
            int pickingTarget = 0;
            var wallInput = new TextBox { Text = "032A", Width = 80, FontSize = 10, Watermark = "Hex ID" };
            var floorInput = new TextBox { Text = "032B", Width = 80, FontSize = 10, Watermark = "Hex ID" };

            if (dats != null) {
                customWallImg.Source = RenderSurfaceMiniThumb(dats, customWall);
                customFloorImg.Source = RenderSurfaceMiniThumb(dats, customFloor);
            }

            // Surface grid (shared between wall/floor picking)
            var surfaceGrid = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            var surfaceScroll = new ScrollViewer {
                MaxHeight = 120, Content = surfaceGrid,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            };
            var pickLabel = new TextBlock {
                Text = "Pick wall texture:", FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse("#c0b0d8")),
                Margin = new Thickness(0, 2, 0, 2)
            };

            void PopulateSurfaceGrid() {
                surfaceGrid.Children.Clear();
                foreach (var sid in sortedSurfaceIds) {
                    thumbCache.TryGetValue(sid, out var bmp);
                    var img = new Avalonia.Controls.Image { Width = 24, Height = 24, Source = bmp };
                    var btn = new Button {
                        Content = img, Padding = new Thickness(1),
                        Margin = new Thickness(1), MinWidth = 0, MinHeight = 0
                    };
                    Avalonia.Controls.ToolTip.SetTip(btn, $"0x{sid:X4}" +
                        (customImportIds.Contains(sid) ? " (custom)" : ""));
                    ushort capturedId = sid;
                    btn.Click += (s, e) => {
                        if (pickingTarget == 0) {
                            customWall = capturedId;
                            wallInput.Text = $"{capturedId:X4}";
                            if (dats != null) customWallImg.Source = RenderSurfaceMiniThumb(dats, customWall);
                        } else {
                            customFloor = capturedId;
                            floorInput.Text = $"{capturedId:X4}";
                            if (dats != null) customFloorImg.Source = RenderSurfaceMiniThumb(dats, customFloor);
                        }
                        RefreshPickButtons();
                    };
                    surfaceGrid.Children.Add(btn);
                }
            }
            var wallBtn = new Button { Padding = new Thickness(4, 2), FontSize = 10 };
            var floorBtn = new Button { Padding = new Thickness(4, 2), FontSize = 10 };

            void RefreshPickButtons() {
                var wallContent = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
                wallContent.Children.Add(new Avalonia.Controls.Image { Width = 20, Height = 20, Source = customWallImg.Source });
                wallContent.Children.Add(new TextBlock { Text = $"Wall: 0x{customWall:X4}", FontSize = 10,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.Parse("#c0b0d8")) });
                wallBtn.Content = wallContent;

                var floorContent = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
                floorContent.Children.Add(new Avalonia.Controls.Image { Width = 20, Height = 20, Source = customFloorImg.Source });
                floorContent.Children.Add(new TextBlock { Text = $"Floor: 0x{customFloor:X4}", FontSize = 10,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.Parse("#c0b0d8")) });
                floorBtn.Content = floorContent;
            }
            RefreshPickButtons();

            PopulateSurfaceGrid();

            wallInput.TextChanged += (s, e) => {
                var hex = (wallInput.Text ?? "").Trim().TrimStart('0', 'x', 'X');
                if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)) {
                    customWall = id;
                    if (dats != null) customWallImg.Source = RenderSurfaceMiniThumb(dats, customWall);
                    RefreshPickButtons();
                }
            };
            floorInput.TextChanged += (s, e) => {
                var hex = (floorInput.Text ?? "").Trim().TrimStart('0', 'x', 'X');
                if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)) {
                    customFloor = id;
                    if (dats != null) customFloorImg.Source = RenderSurfaceMiniThumb(dats, customFloor);
                    RefreshPickButtons();
                }
            };

            wallBtn.Click += (s, e) => {
                pickingTarget = 0;
                pickLabel.Text = "Pick wall texture:";
                wallBtn.BorderBrush = new SolidColorBrush(Color.Parse("#c0b0d8"));
                floorBtn.BorderBrush = null;
            };
            floorBtn.Click += (s, e) => {
                pickingTarget = 1;
                pickLabel.Text = "Pick floor texture:";
                floorBtn.BorderBrush = new SolidColorBrush(Color.Parse("#c0b0d8"));
                wallBtn.BorderBrush = null;
            };
            wallBtn.BorderBrush = new SolidColorBrush(Color.Parse("#c0b0d8"));

            var btnRow2 = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            btnRow2.Children.Add(wallBtn);
            btnRow2.Children.Add(floorBtn);

            var manualRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            manualRow.Children.Add(new TextBlock { Text = "Or hex ID:", FontSize = 9, Width = 56,
                Foreground = new SolidColorBrush(Color.Parse("#6a5a7e")),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            manualRow.Children.Add(wallInput);
            manualRow.Children.Add(floorInput);

            customPanel.Children.Add(btnRow2);
            customPanel.Children.Add(pickLabel);
            customPanel.Children.Add(surfaceScroll);
            customPanel.Children.Add(manualRow);

            styleCombo.SelectionChanged += (s, e) => {
                customPanel.IsVisible = styleCombo.SelectedIndex == customIndex;
            };

            var seedBox = new TextBox { Text = "0", Width = 100, FontSize = 12, Watermark = "Random" };
            var randomizeSeedBtn = new Button {
                Content = "⟳",
                Padding = new Thickness(6, 2),
                FontSize = 14,
            };
            Avalonia.Controls.ToolTip.SetTip(randomizeSeedBtn, "Generate a random seed");
            randomizeSeedBtn.Click += (s, e) => {
                seedBox.Text = new Random().Next(1, 999999).ToString();
            };
            var seedRow = new StackPanel {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6
            };
            seedRow.Children.Add(seedBox);
            seedRow.Children.Add(randomizeSeedBtn);

            var estimateLabel = new TextBlock {
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse("#6a5a7e")),
                Margin = new Thickness(0, -4, 0, 0)
            };

            void UpdateEstimate() {
                int target = (int)(roomCount.Value ?? 64);
                int capEstimate = Math.Max(4, (int)(target * 0.25));
                estimateLabel.Text = $"~{target} content rooms + ~{capEstimate} cap rooms = ~{target + capEstimate} total cells";
            }
            UpdateEstimate();
            roomCount.ValueChanged += (s, e) => UpdateEstimate();

            var sizePresetCombo = new ComboBox {
                FontSize = 12,
                Width = 150,
                ItemsSource = new[] {
                    "Small (32)",
                    "Medium (64)",
                    "Large (128)",
                    "Huge (192)",
                    "Massive (256)"
                },
                SelectedIndex = 1
            };
            sizePresetCombo.SelectionChanged += (s, e) => {
                roomCount.Value = sizePresetCombo.SelectedIndex switch {
                    0 => 32,
                    1 => 64,
                    2 => 128,
                    3 => 192,
                    4 => 256,
                    _ => roomCount.Value
                };
            };

            var panel = new StackPanel { Width = 380, Margin = new Thickness(16), Spacing = 8 };
            panel.Children.Add(new TextBlock {
                Text = "Generate Dungeon",
                FontSize = 16, FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#c0b0d8")),
                Margin = new Thickness(0, 0, 0, 4)
            });
            panel.Children.Add(new TextBlock {
                Text = $"Builds a new dungeon by chaining multi-cell pieces using {kb.TotalEdges:N0} proven connections from {kb.DungeonsScanned:N0} real dungeons. " +
                       "Each piece is 2-5 cells with correct internal geometry. Connection points use portal transforms from original game data.",
                FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#8a7a9e")),
                TextWrapping = TextWrapping.Wrap, MaxWidth = 350,
                Margin = new Thickness(0, 0, 0, 4)
            });
            panel.Children.Add(new TextBlock {
                Text = "Tip: Use size presets for quick results, or type any custom target size.",
                FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#6a5a7e")),
                TextWrapping = TextWrapping.Wrap, MaxWidth = 350,
                Margin = new Thickness(0, -2, 0, 4)
            });

            void AddRow(string label, Control control) {
                var row = new DockPanel { Margin = new Thickness(0, 2) };
                row.Children.Add(new TextBlock {
                    Text = label, FontSize = 11, Width = 120,
                    Foreground = new SolidColorBrush(Color.Parse("#8a7a9e")),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                });
                row.Children.Add(control);
                panel.Children.Add(row);
            }

            var branchingCombo = new ComboBox {
                FontSize = 12, Width = 150,
                ItemsSource = new[] { "Linear", "Moderate", "Heavy" },
                SelectedIndex = 1
            };
            var roomSizeCombo = new ComboBox {
                FontSize = 12, Width = 150,
                ItemsSource = new[] { "Small rooms", "Mixed", "Large rooms" },
                SelectedIndex = 1
            };

            var lockStyleCheck = new CheckBox {
                Content = "Keep consistent style", IsChecked = true, FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#c0b0d8"))
            };
            var allowVerticalCheck = new CheckBox {
                Content = "Allow vertical connections (ramps/stairs)", IsChecked = false, FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#c0b0d8"))
            };
            var furnishRoomsCheck = new CheckBox {
                Content = "Furnish rooms (torches, furniture, decorations)", IsChecked = true, FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#c0b0d8"))
            };

            var favLabel = favCount > 0
                ? $"Generate from favorites ({favCount} favorited)"
                : "Generate from favorites (no favorites yet)";
            var useFavoritesCheck = new CheckBox {
                Content = favLabel,
                IsChecked = false,
                IsEnabled = favCount > 0,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#c0b0d8"))
            };
            var favHint = new TextBlock {
                Text = "Builds new dungeons using the room shapes found in your favorites.\n" +
                       "Favorite whole dungeons or individual pieces — both work.",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse("#6a5a7e")),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 350,
                IsVisible = false,
                Margin = new Thickness(20, -4, 0, 0)
            };
            useFavoritesCheck.IsCheckedChanged += (s, e) => {
                bool favOn = useFavoritesCheck.IsChecked == true;
                styleCombo.IsEnabled = !favOn;
                lockStyleCheck.IsEnabled = !favOn;
                favHint.IsVisible = favOn;
                if (favOn) {
                    styleCombo.SelectedIndex = 0;
                    lockStyleCheck.IsChecked = false;
                }
            };

            AddRow("Dungeon Size:", roomCount);
            panel.Children.Add(estimateLabel);
            AddRow("Size Preset:", sizePresetCombo);
            AddRow("Style:", styleCombo);
            panel.Children.Add(customPanel);
            AddRow("Branching:", branchingCombo);
            AddRow("Room Size:", roomSizeCombo);
            AddRow("Seed (optional):", seedRow);
            panel.Children.Add(new Border { Height = 4 });
            panel.Children.Add(useFavoritesCheck);
            panel.Children.Add(favHint);
            panel.Children.Add(lockStyleCheck);
            panel.Children.Add(allowVerticalCheck);
            panel.Children.Add(furnishRoomsCheck);

            var btnRow = new StackPanel {
                Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
            };
            var genBtn = new Button { Content = "Generate", Padding = new Thickness(16, 8), FontSize = 12 };
            var cancelBtn = new Button { Content = "Cancel", Padding = new Thickness(16, 8), FontSize = 12 };

            genBtn.Click += (s, e) => {
                int.TryParse(seedBox.Text, out var seed);
                bool useFavs = useFavoritesCheck.IsChecked == true && favCount > 0;
                int styleIdx = styleCombo.SelectedIndex;
                bool isCustom = styleIdx == customIndex;
                string selectedStyle = isCustom ? "Custom"
                    : (styleIdx >= 0 && styleIdx < styles.Count ? styles[styleIdx] : "All");
                result = new GeneratorParams {
                    RoomCount = (int)(roomCount.Value ?? 10),
                    Style = useFavs ? "All" : selectedStyle,
                    Seed = seed,
                    AllowVertical = allowVerticalCheck.IsChecked == true,
                    LockStyle = useFavs ? false : (isCustom ? false : lockStyleCheck.IsChecked == true),
                    UseFavoritesOnly = useFavs,
                    FavoritePrefabSignatures = useFavs ? favoritePrefabSignatures : null,
                    CustomPrefabs = useFavs ? customPrefabs : null,
                    Branching = branchingCombo.SelectedIndex,
                    RoomSize = roomSizeCombo.SelectedIndex,
                    CustomWallSurface = isCustom ? customWall : (ushort)0,
                    CustomFloorSurface = isCustom ? customFloor : (ushort)0,
                    FurnishRooms = furnishRoomsCheck.IsChecked == true,
                };
                DialogHost.Close("DungeonDialogHost");
            };
            cancelBtn.Click += (s, e) => DialogHost.Close("DungeonDialogHost");

            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(genBtn);
            panel.Children.Add(btnRow);

            await DialogHost.Show(panel, "DungeonDialogHost");
            return result;
        }

        /// <returns>(sourceLb, targetLb) on success, null if cancelled.</returns>
        public async Task<(ushort sourceLb, ushort targetLb)?> ShowStartFromTemplateDialog(Action<ushort, ushort> copyTemplate) {
            var dungeons = LocationDatabase.Dungeons
                .OrderBy(d => d.Name)
                .Take(200)
                .ToList();

            if (dungeons.Count == 0) return null;

            var listBox = new ListBox {
                MaxHeight = 280,
                Width = 450,
                FontSize = 12,
                ItemsSource = dungeons.Select(d => $"{d.Name}  [LB {d.LandblockHex}]").ToList()
            };
            var targetTextBox = new TextBox {
                Text = "",
                Width = 120,
                Watermark = "e.g. FFFF"
            };
            var errorText = new TextBlock {
                Text = "",
                Foreground = Brushes.Red,
                FontSize = 12,
                IsVisible = false
            };

            await DialogHost.Show(new StackPanel {
                Margin = new Thickness(20),
                Spacing = 10,
                Children = {
                    new TextBlock {
                        Text = "Start from template",
                        FontSize = 16,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock {
                        Text = "Pick a dungeon template, then choose the landblock where to create a copy. " +
                            "The structure (rooms, portals, statics) is copied with new locations; portal connections are preserved.",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 450,
                        FontSize = 12,
                        Opacity = 0.8
                    },
                    listBox,
                    new StackPanel {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 8,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Children = {
                            new TextBlock { Text = "Target landblock:", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                            targetTextBox,
                            errorText
                        }
                    },
                    new StackPanel {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = {
                            new Button {
                                Content = "Cancel",
                                Command = new RelayCommand(() => DialogHost.Close("DungeonDialogHost"))
                            },
                            new Button {
                                Content = "Create copy",
                                Command = new RelayCommand(() => {
                                    var idx = listBox.SelectedIndex;
                                    if (idx < 0 || idx >= dungeons.Count) {
                                        errorText.Text = "Select a template.";
                                        errorText.IsVisible = true;
                                        return;
                                    }
                                    var targetParsed = ParseLandblockInput(targetTextBox.Text);
                                    if (targetParsed == null) {
                                        errorText.Text = "Invalid landblock ID.";
                                        errorText.IsVisible = true;
                                        return;
                                    }
                                    var sourceLb = dungeons[idx].LandblockId;
                                    var targetLb = (ushort)(targetParsed.Value >> 16);
                                    if (targetLb == 0) targetLb = (ushort)(targetParsed.Value & 0xFFFF);
                                    if (sourceLb == targetLb) {
                                        errorText.Text = "Target must differ from template.";
                                        errorText.IsVisible = true;
                                        return;
                                    }
                                    copyTemplate(sourceLb, targetLb);
                                    DialogHost.Close("DungeonDialogHost");
                                })
                            }
                        }
                    }
                }
            }, "DungeonDialogHost");

            return null;
        }

        public static uint? ParseLandblockInput(string? input) {
            if (string.IsNullOrWhiteSpace(input)) return null;
            input = input.Trim();

            var hex = input;
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex[2..];

            if (hex.Length > 4 && hex.Length <= 8) {
                if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fullId))
                    return fullId;
                return null;
            }

            if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lbId))
                return (uint)(lbId << 16);

            return null;
        }

        private static WriteableBitmap? RenderSurfaceMiniThumb(IDatReaderWriter dats, ushort surfaceId) {
            const int Size = 20;
            uint fullId = (uint)(surfaceId | 0x08000000);
            try {
                if (!dats.TryGet<Surface>(fullId, out var surface)) return null;

                if (surface.Type.HasFlag(SurfaceType.Base1Solid)) {
                    var solidData = TextureHelpers.CreateSolidColorTexture(surface.ColorValue, Size, Size);
                    return SurfaceBrowserViewModel.CreateBitmap(solidData, Size, Size);
                }

                if (!dats.TryGet<SurfaceTexture>(surface.OrigTextureId, out var surfTex) ||
                    surfTex.Textures?.Any() != true)
                    return null;

                var rsId = surfTex.Textures.Last();
                if (!dats.TryGet<RenderSurface>(rsId, out var rs)) return null;

                int w = rs.Width, h = rs.Height;
                byte[]? rgba = null;
                uint paletteId = surface.OrigPaletteId != 0
                    ? (uint)surface.OrigPaletteId
                    : rs.DefaultPaletteId;
                switch (rs.FormatEnum) {
                    case PixelFormat.PFID_A8R8G8B8:
                        rgba = DatIconLoader.SwizzleBgraToRgba(rs.SourceData, w * h);
                        break;
                    case PixelFormat.PFID_R8G8B8:
                        rgba = new byte[w * h * 4];
                        for (int i = 0; i < w * h; i++) {
                            rgba[i * 4 + 0] = rs.SourceData[i * 3 + 2];
                            rgba[i * 4 + 1] = rs.SourceData[i * 3 + 1];
                            rgba[i * 4 + 2] = rs.SourceData[i * 3 + 0];
                            rgba[i * 4 + 3] = 255;
                        }
                        break;
                    case PixelFormat.PFID_INDEX16:
                        if (dats.TryGet<Palette>(paletteId, out var pal)) {
                            rgba = new byte[w * h * 4];
                            bool transparentLowClipIndices = surface.Type.HasFlag(SurfaceType.Base1ClipMap);
                            bool expand256Palette = !WorldBuilder.Shared.Lib.LegacyDatReader.IsSyntheticRenderSurfaceId(rs.Id);
                            TextureHelpers.FillIndex16(rs.SourceData, pal, rgba.AsSpan(), w, h, transparentLowClipIndices, expand256Palette);
                        }
                        break;
                    case PixelFormat.PFID_DXT1:
                        rgba = SurfaceBrowserViewModel.DecompressDxt1(rs.SourceData, w, h);
                        break;
                    case PixelFormat.PFID_DXT3:
                    case PixelFormat.PFID_DXT5:
                        rgba = SurfaceBrowserViewModel.DecompressDxt5(rs.SourceData, w, h,
                            rs.FormatEnum == PixelFormat.PFID_DXT3);
                        break;
                }

                if (rgba == null) return null;
                if (w != Size || h != Size)
                    rgba = SurfaceBrowserViewModel.DownsampleNearest(rgba, w, h, Size, Size);
                return SurfaceBrowserViewModel.CreateBitmap(rgba, Size, Size);
            }
            catch { return null; }
        }

    
    }
}
