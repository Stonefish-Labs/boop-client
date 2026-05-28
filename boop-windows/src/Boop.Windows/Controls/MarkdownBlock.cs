using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Boop.Windows.Controls;

public sealed class MarkdownBlock : StackPanel
{
    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(
            nameof(Markdown),
            typeof(string),
            typeof(MarkdownBlock),
            new PropertyMetadata("", (_, args) =>
            {
                if (args.NewValue is string markdown && _.GetValue(MarkdownProperty) is not null)
                {
                    ((MarkdownBlock)_).Render(markdown);
                }
            }));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownBlock()
    {
        Spacing = 8;
    }

    private void Render(string markdown)
    {
        Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return;
        }

        var document = Markdig.Markdown.Parse(markdown, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
        foreach (var block in document)
        {
            RenderBlock(block);
        }
    }

    private void RenderBlock(Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                Children.Add(new TextBlock
                {
                    Text = ExtractInlineText(heading.Inline),
                    FontSize = heading.Level <= 1 ? 22 : heading.Level == 2 ? 18 : 15,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0),
                });
                break;
            case ParagraphBlock paragraph:
                Children.Add(RichTextFromInline(paragraph.Inline));
                break;
            case QuoteBlock quote:
                var quotePanel = new StackPanel { Spacing = 6, Padding = new Thickness(12, 6, 0, 6) };
                foreach (var child in quote)
                {
                    if (child is ParagraphBlock quoteParagraph)
                    {
                        quotePanel.Children.Add(RichTextFromInline(quoteParagraph.Inline));
                    }
                }
                Children.Add(new Border
                {
                    BorderBrush = new SolidColorBrush(Colors.Gray),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Child = quotePanel,
                });
                break;
            case ListBlock list:
                RenderList(list);
                break;
            case FencedCodeBlock fenced:
                Children.Add(CodeBlock(string.Join(Environment.NewLine, fenced.Lines.Lines.Select(line => line.ToString()))));
                break;
            case CodeBlock code:
                Children.Add(CodeBlock(string.Join(Environment.NewLine, code.Lines.Lines.Select(line => line.ToString()))));
                break;
            case ThematicBreakBlock:
                Children.Add(new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Colors.Gray),
                    Opacity = 0.35,
                    Margin = new Thickness(0, 6, 0, 6),
                });
                break;
        }
    }

    private void RenderList(ListBlock list)
    {
        var index = 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = list.IsOrdered ? $"{index++}." : "•",
                Width = 28,
                TextAlignment = TextAlignment.Right,
            });
            var content = new StackPanel { Spacing = 4 };
            foreach (var child in item)
            {
                if (child is ParagraphBlock paragraph)
                {
                    content.Children.Add(RichTextFromInline(paragraph.Inline));
                }
            }
            row.Children.Add(content);
            Children.Add(row);
        }
    }

    private static RichTextBlock RichTextFromInline(ContainerInline? inline)
    {
        var block = new RichTextBlock { TextWrapping = TextWrapping.Wrap };
        var paragraph = new Paragraph();
        AppendInlines(paragraph.Inlines, inline);
        block.Blocks.Add(paragraph);
        return block;
    }

    private static void AppendInlines(InlineCollection target, ContainerInline? inline)
    {
        if (inline is null)
        {
            return;
        }
        foreach (var child in inline)
        {
            switch (child)
            {
                case LiteralInline literal:
                    target.Add(new Run { Text = literal.Content.ToString() });
                    break;
                case CodeInline code:
                    target.Add(new Run
                    {
                        Text = code.Content,
                        FontFamily = new FontFamily("Consolas"),
                    });
                    break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterCount == 2 ? new Bold() : new Italic();
                    AppendInlines(span.Inlines, emphasis);
                    target.Add(span);
                    break;
                case LinkInline { IsImage: false } link:
                    if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri))
                    {
                        var hyperlink = new Hyperlink { NavigateUri = uri };
                        AppendInlines(hyperlink.Inlines, link);
                        target.Add(hyperlink);
                    }
                    else
                    {
                        AppendInlines(target, link);
                    }
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
            }
        }
    }

    private static Border CodeBlock(string text)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(28, 128, 128, 128)),
            Padding = new Thickness(10),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
        };
    }

    private static string ExtractInlineText(ContainerInline? inline)
    {
        if (inline is null)
        {
            return "";
        }
        return string.Concat(inline.Select(child => child switch
        {
            LiteralInline literal => literal.Content.ToString(),
            CodeInline code => code.Content,
            ContainerInline container => ExtractInlineText(container),
            _ => "",
        }));
    }
}
