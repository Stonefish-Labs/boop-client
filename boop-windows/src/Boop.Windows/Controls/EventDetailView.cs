using Boop.Windows.Models;
using Boop.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Boop.Windows.Controls;

public sealed class EventDetailView : UserControl
{
    public static readonly DependencyProperty EventProperty =
        DependencyProperty.Register(nameof(Event), typeof(BoopEvent), typeof(EventDetailView), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(MainViewModel), typeof(EventDetailView), new PropertyMetadata(null));

    private readonly StackPanel _content = new() { Spacing = 16, Padding = new Thickness(20) };

    public EventDetailView()
    {
        Content = new ScrollViewer { Content = _content };
    }

    public BoopEvent? Event
    {
        get => (BoopEvent?)GetValue(EventProperty);
        set => SetValue(EventProperty, value);
    }

    public MainViewModel? ViewModel
    {
        get => (MainViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((EventDetailView)sender).Render();
    }

    private void Render()
    {
        _content.Children.Clear();
        var boopEvent = Event;
        if (boopEvent is null)
        {
            _content.Children.Add(new TextBlock
            {
                Text = "No notification selected",
                Opacity = 0.65,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }

        _content.Children.Add(new TextBlock
        {
            Text = boopEvent.Title,
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        _content.Children.Add(new TextBlock
        {
            Text = $"{boopEvent.Status} • {boopEvent.CreatedAt.ToLocalTime():g}",
            Opacity = 0.65,
        });

        if (!string.IsNullOrWhiteSpace(boopEvent.ImageUrl) && Uri.TryCreate(boopEvent.ImageUrl, UriKind.Absolute, out var imageUri))
        {
            _content.Children.Add(new Image
            {
                Source = new BitmapImage(imageUri),
                MaxHeight = 280,
                Stretch = Stretch.UniformToFill,
            });
        }

        if (!string.IsNullOrWhiteSpace(boopEvent.BodyMarkdown))
        {
            _content.Children.Add(new MarkdownBlock { Markdown = boopEvent.BodyMarkdown });
        }

        if (boopEvent.Fields.Count > 0)
        {
            var grid = new Grid { ColumnSpacing = 14, RowSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < boopEvent.Fields.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var field = boopEvent.Fields[i];
                var label = new TextBlock { Text = field.Label, Opacity = 0.65 };
                var value = new TextBlock { Text = field.Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                Grid.SetRow(label, i);
                Grid.SetColumn(label, 0);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                grid.Children.Add(label);
                grid.Children.Add(value);
            }
            _content.Children.Add(grid);
        }

        foreach (var link in boopEvent.Links)
        {
            if (Uri.TryCreate(NormalizeUrl(link.Url), UriKind.Absolute, out var uri))
            {
                var button = new Button
                {
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = string.IsNullOrWhiteSpace(link.Label) ? uri.Host : link.Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                            new TextBlock { Text = uri.Host + uri.AbsolutePath, Opacity = 0.65 },
                        },
                    },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                button.Click += async (_, _) => await Launcher.LaunchUriAsync(uri);
                _content.Children.Add(button);
            }
        }

        if (boopEvent.Result is not null)
        {
            _content.Children.Add(new TextBlock
            {
                Text = $"Handled: {boopEvent.Result.ActionLabel}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
        }
        else if (boopEvent.Status == "pending" && boopEvent.Actions.Count > 0)
        {
            var actions = new StackPanel { Spacing = 8 };
            foreach (var action in boopEvent.Actions)
            {
                var button = new Button
                {
                    Content = action.Label,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                button.Click += async (_, _) => await SubmitActionAsync(boopEvent, action);
                actions.Children.Add(button);
            }
            _content.Children.Add(actions);
        }
    }

    private async Task SubmitActionAsync(BoopEvent boopEvent, BoopAction action)
    {
        if (ViewModel is null)
        {
            return;
        }

        string? text = null;
        if (action.RequiresText)
        {
            var input = new TextBox
            {
                AcceptsReturn = true,
                MinHeight = 96,
                PlaceholderText = action.TextPlaceholder ?? "Notes",
            };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = action.Label,
                Content = input,
                PrimaryButtonText = "Submit",
                CloseButtonText = "Cancel",
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
            text = input.Text;
        }

        await ViewModel.SubmitActionAsync(boopEvent, action, text);
    }

    private static string NormalizeUrl(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + trimmed;
        }
        if (trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            return "https://" + trimmed;
        }
        return trimmed;
    }
}
