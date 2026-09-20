using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DialogHostAvalonia;
using System;
using System.Threading.Tasks;

namespace WorldBuilder.Lib;

public static class LegacyDatConversionDialogs {
    public static Task ShowMessageAsync(string dialogHost, string title, string message) {
        if (Dispatcher.UIThread.CheckAccess()) {
            return ShowMessageCoreAsync(dialogHost, title, message);
        }

        return Dispatcher.UIThread.InvokeAsync(() => ShowMessageCoreAsync(dialogHost, title, message));
    }

    public static Task<T> RunWithProgressAsync<T>(
        string dialogHost,
        string title,
        Func<Action<string>, T> work) {
        if (Dispatcher.UIThread.CheckAccess()) {
            return RunWithProgressCoreAsync(dialogHost, title, work);
        }

        return Dispatcher.UIThread.InvokeAsync(() => RunWithProgressCoreAsync(dialogHost, title, work));
    }

    private static async Task<T> RunWithProgressCoreAsync<T>(
        string dialogHost,
        string title,
        Func<Action<string>, T> work) {
        var statusText = new TextBlock {
            Text = "Starting conversion...",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480,
        };

        var dialogTask = DialogHost.Show(new StackPanel {
            Margin = new Thickness(24),
            Spacing = 12,
            Width = 420,
            Children = {
                new TextBlock {
                    Text = title,
                    FontSize = 16,
                    FontWeight = FontWeight.Bold,
                },
                statusText,
                new TextBlock {
                    Text = "The manifest is written when conversion finishes. Large worlds can take several minutes.",
                    FontSize = 11,
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 480,
                },
                new ProgressBar {
                    IsIndeterminate = true,
                    Height = 4,
                },
            },
        }, dialogHost);

        void Report(string message) {
            Console.WriteLine($"[LegacyConvert] {message}");
            Dispatcher.UIThread.Post(() => statusText.Text = message);
        }

        T result;
        try {
            result = await Task.Run(() => work(Report));
        }
        finally {
            try {
                DialogHost.Close(dialogHost);
            }
            catch {
                // DialogHost may already be closed.
            }

            try {
                await dialogTask;
            }
            catch {
                // Ignore close races.
            }
        }

        return result;
    }

    private static async Task ShowMessageCoreAsync(string dialogHost, string title, string message) {
        await DialogHost.Show(new StackPanel {
            Margin = new Thickness(18),
            Spacing = 10,
            Children = {
                new TextBlock {
                    Text = title,
                    FontSize = 16,
                    FontWeight = FontWeight.Bold,
                },
                new TextBlock {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 520,
                },
                new Button {
                    Content = "OK",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Command = new RelayCommand(() => DialogHost.Close(dialogHost)),
                },
            },
        }, dialogHost);
    }
}
