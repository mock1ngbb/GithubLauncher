using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GithubLauncher.Services
{
    public static class MarkdownParser
    {
        /// <summary>
        /// Parse markdown text into Avalonia controls.
        /// </summary>
        /// <param name="markdown">Raw markdown string.</param>
        /// <param name="openUrlAction">Optional callback to invoke when a link is clicked. If null, uses Process.Start.</param>
        public static List<Control> ParseMarkdown(string markdown, Action<string>? openUrlAction = null)
        {
            var controls = new List<Control>();
            if (string.IsNullOrWhiteSpace(markdown))
            {
                controls.Add(new SelectableTextBlock
                {
                    Text = "No changelog available.",
                    Foreground = new SolidColorBrush(Color.Parse("#B8B8B8")),
                    FontSize = 14
                });
                return controls;
            }

            var lines = markdown.Split('\n');
            var listItems = new List<string>();
            var codeBlockLines = new List<string>();
            bool inCodeBlock = false;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');

                // Code blocks
                if (line.TrimStart().StartsWith("```"))
                {
                    if (inCodeBlock)
                    {
                        if (codeBlockLines.Count > 0)
                        {
                            var codeBlock = new Border
                            {
                                Background = new SolidColorBrush(Color.Parse("#1e1e1e")),
                                BorderBrush = new SolidColorBrush(Color.Parse("#2d2d30")),
                                BorderThickness = new Thickness(1),
                                CornerRadius = new CornerRadius(4),
                                Padding = new Thickness(12),
                                Margin = new Thickness(0, 8, 0, 8)
                            };
                            codeBlock.Child = new SelectableTextBlock
                            {
                                Text = string.Join("\n", codeBlockLines),
                                FontFamily = new FontFamily("Consolas,Courier New,monospace"),
                                FontSize = 13,
                                Foreground = new SolidColorBrush(Color.Parse("#d4d4d4"))
                            };
                            controls.Add(codeBlock);
                            codeBlockLines.Clear();
                        }
                        inCodeBlock = false;
                    }
                    else
                    {
                        FlushListItems(controls, listItems);
                        inCodeBlock = true;
                    }
                    continue;
                }

                if (inCodeBlock)
                {
                    codeBlockLines.Add(line);
                    continue;
                }

                // GitHub alerts: > [!NOTE], etc.
                var alertMatch = Regex.Match(line.TrimStart(),
                    @"^>\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]",
                    RegexOptions.IgnoreCase);

                if (alertMatch.Success)
                {
                    FlushListItems(controls, listItems);

                    var alertType = alertMatch.Groups[1].Value.ToUpper();
                    var alertContentLines = new List<string>();

                    // Move to the first line of content
                    i++;

                    // Collect all lines belonging to the alert block
                    while (i < lines.Length)
                    {
                        var nextLine = lines[i].TrimEnd('\r');

                        // Stop if the line is empty
                        if (string.IsNullOrWhiteSpace(nextLine))
                        {
                            break;
                        }

                        var trimmedLine = nextLine.TrimStart();

                        if (trimmedLine.StartsWith(">"))
                        {
                            // Standard blockquote line: strip the '>'
                            var content = trimmedLine.Substring(1);
                            if (content.StartsWith(" ")) content = content.Substring(1);
                            alertContentLines.Add(content);
                        }
                        else
                        {
                            // Lazy continuation
                            alertContentLines.Add(trimmedLine);
                        }
                        i++;
                    }

                    // Define colors and icon names
                    var (borderColorHex, iconPath) = alertType switch
                    {
                        "NOTE" => ("#0969da", "markdown_info.png"),
                        "TIP" => ("#1a7f37", "markdown_tip.png"),
                        "IMPORTANT" => ("#8250df", "markdown_important.png"),
                        "WARNING" => ("#9a6700", "markdown_warning.png"),
                        "CAUTION" => ("#d1242f", "markdown_caution.png"),
                        _ => ("#2d2d30", "markdown_info.png")
                    };

                    var alertColor = Color.Parse(borderColorHex);
                    var alertBrush = new SolidColorBrush(alertColor);

                    var alertBorder = new Border
                    {
                        BorderBrush = alertBrush,
                        BorderThickness = new Thickness(4, 0, 0, 0),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(16, 12, 16, 12),
                        Margin = new Thickness(0, 8, 0, 8),
                        Background = new SolidColorBrush(alertColor) { Opacity = 0.05 }
                    };

                    var alertPanel = new StackPanel();

                    // Title row with icon tinted to border color
                    var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

                    try
                    {
                        // Use a Rectangle + OpacityMask to draw the icon in the alert's color
                        var iconRect = new Rectangle
                        {
                            Width = 16,
                            Height = 16,
                            Margin = new Thickness(0, 0, 8, 0),
                            Fill = alertBrush,
                            VerticalAlignment = VerticalAlignment.Center,
                            OpacityMask = new ImageBrush
                            {
                                Source = new Bitmap(
                                    AssetLoader.Open(
                                        new Uri($"avares://GithubLauncher/Assets/{iconPath}")))
                            }
                        };
                        titlePanel.Children.Add(iconRect);
                    }
                    catch (Exception)
                    {
                        // Fallback circle if icon load fails
                        titlePanel.Children.Add(new Ellipse
                        {
                            Width = 8,
                            Height = 8,
                            Fill = alertBrush,
                            Margin = new Thickness(0, 0, 8, 0)
                        });
                    }

                    titlePanel.Children.Add(new SelectableTextBlock
                    {
                        Text = alertType,
                        FontSize = 14,
                        FontWeight = FontWeight.Bold,
                        Foreground = alertBrush,
                        VerticalAlignment = VerticalAlignment.Center
                    });

                    alertPanel.Children.Add(titlePanel);

                    // Parse the inner content for markdown (bold, links, etc.)
                    var contentText = string.Join("\n", alertContentLines);
                    var contentBlocks = ParseInlineMarkdown(contentText, openUrlAction);
                    foreach (var block in contentBlocks)
                    {
                        block.Margin = new Thickness(0, 2, 0, 2);
                        alertPanel.Children.Add(block);
                    }

                    alertBorder.Child = alertPanel;
                    controls.Add(alertBorder);
                    continue;
                }

                // Headers
                if (line.StartsWith("#"))
                {
                    FlushListItems(controls, listItems);
                    int level = 0;
                    while (level < line.Length && line[level] == '#') level++;
                    var headerText = line.Substring(level).Trim();
                    var fontSize = level switch { 1 => 24, 2 => 20, 3 => 18, 4 => 16, _ => 14 };
                    var fontWeight = level <= 2 ? FontWeight.Bold : FontWeight.SemiBold;

                    controls.Add(new SelectableTextBlock
                    {
                        Text = headerText,
                        FontSize = fontSize,
                        FontWeight = fontWeight,
                        Foreground = new SolidColorBrush(Colors.White),
                        Margin = new Thickness(0, level == 1 ? 16 : 12, 0, 8)
                    });
                    continue;
                }

                // Lists
                if (line.TrimStart().StartsWith("* ") || line.TrimStart().StartsWith("- "))
                {
                    var itemText = line.TrimStart().Substring(2);
                    listItems.Add("• " + itemText);
                    continue;
                }

                var orderedMatch = Regex.Match(line.TrimStart(), @"^(\d+)\.\s+(.+)");
                if (orderedMatch.Success)
                {
                    listItems.Add(orderedMatch.Groups[1].Value + ". " + orderedMatch.Groups[2].Value);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line) || line.Trim().StartsWith("---"))
                {
                    FlushListItems(controls, listItems);
                    if (line.Trim().StartsWith("---"))
                        controls.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#2d2d30")), Margin = new Thickness(0, 12, 0, 12) });
                    continue;
                }

                FlushListItems(controls, listItems);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var blocks = ParseInlineMarkdown(line, openUrlAction);
                    foreach (var block in blocks)
                    {
                        block.Margin = new Thickness(0, 0, 0, 8);
                        controls.Add(block);
                    }
                }
            }

            FlushListItems(controls, listItems);
            return controls;
        }

        public static List<Control> ParseInlineMarkdown(string text, Action<string>? openUrlAction = null)
        {
            var blocks = new List<Control>();
            var panel = new WrapPanel { Orientation = Orientation.Horizontal };

            int i = 0;
            var currentText = new StringBuilder();

            void FlushText()
            {
                if (currentText.Length > 0)
                {
                    panel.Children.Add(new SelectableTextBlock
                    {
                        Text = currentText.ToString(),
                        FontSize = 14,
                        Foreground = new SolidColorBrush(Color.Parse("#B8B8B8")),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap
                    });
                    currentText.Clear();
                }
            }

            void AddLineBreak()
            {
                if (panel.Children.Count > 0)
                {
                    blocks.Add(panel);
                    panel = new WrapPanel { Orientation = Orientation.Horizontal };
                }
            }

            while (i < text.Length)
            {
                // Check for line breaks
                if (text[i] == '\n' || text[i] == '\r')
                {
                    FlushText();
                    AddLineBreak();

                    if (i + 1 < text.Length && (text[i + 1] == '\n' || text[i + 1] == '\r') && text[i] != text[i + 1])
                    {
                        i++;
                    }
                    i++;
                    continue;
                }

                // Bold **text**
                if (i < text.Length - 1 && text[i] == '*' && text[i + 1] == '*')
                {
                    FlushText();
                    i += 2;
                    var boldText = new StringBuilder();
                    while (i < text.Length - 1 && !(text[i] == '*' && text[i + 1] == '*')) { boldText.Append(text[i]); i++; }
                    if (i < text.Length - 1) i += 2;
                    panel.Children.Add(new SelectableTextBlock
                    {
                        Text = boldText.ToString(),
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Colors.White),
                        FontSize = 14,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap
                    });
                    continue;
                }

                // Inline code `text`
                if (text[i] == '`')
                {
                    FlushText();
                    i++;
                    var codeText = new StringBuilder();
                    while (i < text.Length && text[i] != '`') { codeText.Append(text[i]); i++; }
                    if (i < text.Length) i++;
                    panel.Children.Add(new SelectableTextBlock
                    {
                        Text = codeText.ToString(),
                        FontFamily = new FontFamily("Consolas,Courier New,monospace"),
                        Foreground = new SolidColorBrush(Color.Parse("#d4d4d4")),
                        FontSize = 14,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap
                    });
                    continue;
                }

                // Links [text](url)
                if (text[i] == '[')
                {
                    var linkMatch = Regex.Match(text.Substring(i), @"^\[([^\]]+)\]\(([^\)]+)\)");
                    if (linkMatch.Success)
                    {
                        FlushText();
                        var linkText = linkMatch.Groups[1].Value;
                        var linkUrl = linkMatch.Groups[2].Value;

                        var linkButton = new Button
                        {
                            Content = linkText,
                            Foreground = new SolidColorBrush(Color.Parse("#0969da")),
                            Background = Brushes.Transparent,
                            BorderThickness = new Thickness(0),
                            Padding = new Thickness(0),
                            Cursor = new Cursor(StandardCursorType.Hand),
                            FontSize = 14,
                            VerticalAlignment = VerticalAlignment.Center,
                            Tag = linkUrl
                        };

                        linkButton.Click += (s, e) =>
                        {
                            if (linkButton.Tag is string url)
                            {
                                var handler = openUrlAction ?? OpenUrl;
                                try { handler(url); } catch { }
                            }
                        };

                        panel.Children.Add(linkButton);
                        i += linkMatch.Length;
                        continue;
                    }
                }

                currentText.Append(text[i]);
                i++;
            }

            FlushText();

            if (panel.Children.Count > 0)
            {
                blocks.Add(panel);
            }

            return blocks;
        }

        public static void FlushListItems(List<Control> controls, List<string> listItems)
        {
            if (listItems.Count > 0)
            {
                var listPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
                foreach (var item in listItems)
                {
                    var blocks = ParseInlineMarkdown(item);
                    foreach (var block in blocks)
                    {
                        block.Margin = new Thickness(0, 2, 0, 2);
                        listPanel.Children.Add(block);
                    }
                }
                controls.Add(listPanel);
                listItems.Clear();
            }
        }

        public static async Task<string> FetchChangelogAsync(string repository, string? gitHubApiToken = null)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "GithubLauncher");

                if (!string.IsNullOrEmpty(gitHubApiToken))
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"token {gitHubApiToken}");
                }

                var url = $"https://api.github.com/repos/{repository}/releases/latest";
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    return "Failed to fetch changelog from GitHub.";
                }

                var json = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (root.TryGetProperty("body", out var bodyElement))
                {
                    var body = bodyElement.GetString();
                    if (!string.IsNullOrEmpty(body))
                    {
                        return body;
                    }
                }

                return "No changelog available for this release.";
            }
            catch (Exception ex)
            {
                return $"Error fetching changelog: {ex.Message}";
            }
        }

        private static void OpenUrl(string url)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                else if (OperatingSystem.IsLinux())
                {
                    Process.Start("xdg-open", $"\"{url}\"");
                }
                else if (OperatingSystem.IsMacOS())
                {
                    Process.Start("open", $"\"{url}\"");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open URL: {ex.Message}");
            }
        }
    }
}
