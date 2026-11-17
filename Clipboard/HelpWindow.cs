using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Clipboard;

/// <summary>
/// ショートカットキーの説明を表示するヘルプ画面。
/// </summary>
internal sealed class HelpWindow : Window
{
	private static readonly (string Title, (string Key, string Description)[] Items)[] Sections =
	[
		("どこでも使えるショートカット",
		[
			("Win + V", "クリップボード履歴を開く（積み上げ中は積み上げた内容を連結して貼り付け）"),
			("Win + C", "クリップボードの積み上げを開始（以降のコピーを積み上げる）"),
			("Win + B", "画像ギャラリーを開く"),
			("Win + F", "現在のクリップボードの内容を最前面に表示する"),
		]),
		("履歴ウィンドウ",
		[
			("↑ / ↓", "選択している履歴を移動する"),
			("Enter", "選択している履歴を貼り付ける"),
			("Esc", "履歴ウィンドウを閉じる"),
		]),
		("最前面表示（Win + F）",
		[
			("Ctrl + マウスホイール", "拡大・縮小する"),
			("Ctrl + 0", "拡大率を100%に戻す"),
			("Esc", "閉じる"),
		]),
		("画像ギャラリー",
		[
			("Esc", "画像ギャラリーを閉じる"),
		]),
	];

	public HelpWindow()
	{
		Title = "ヘルプ";
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		ResizeMode = ResizeMode.CanResize;
		Width = 560;
		Height = 480;
		Icon = LoadIcon();
		AppTheme.ApplyWindow(this);

		var root = new Grid
		{
			Margin = new Thickness(16)
		};
		root.SetResourceReference(Panel.BackgroundProperty, AppTheme.WindowBackgroundBrushKey);
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		var content = new StackPanel();
		foreach (var (title, items) in Sections)
		{
			content.Children.Add(CreateSection(title, items));
		}

		var scrollViewer = new ScrollViewer
		{
			Content = content,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
		};
		Grid.SetRow(scrollViewer, 0);
		root.Children.Add(scrollViewer);

		var closeButton = new Button
		{
			Content = "閉じる",
			Width = 75,
			Height = 28,
			Margin = new Thickness(0, 16, 0, 0),
			HorizontalAlignment = HorizontalAlignment.Right,
			IsDefault = true,
			IsCancel = true
		};
		AppTheme.ApplyButton(closeButton);
		closeButton.Click += (_, _) => Close();
		Grid.SetRow(closeButton, 1);
		root.Children.Add(closeButton);

		Content = root;
	}

	private static FrameworkElement CreateSection(string title, (string Key, string Description)[] items)
	{
		var section = new StackPanel
		{
			Margin = new Thickness(0, 0, 0, 16)
		};

		var header = new TextBlock
		{
			Text = title,
			FontWeight = FontWeights.Bold,
			Margin = new Thickness(0, 0, 0, 6)
		};
		header.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);
		section.Children.Add(header);

		var grid = new Grid
		{
			Margin = new Thickness(8, 0, 0, 0)
		};
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

		for (int i = 0; i < items.Length; i++)
		{
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

			var key = new TextBlock
			{
				Text = items[i].Key,
				Margin = new Thickness(0, 2, 12, 2),
				TextWrapping = TextWrapping.Wrap
			};
			key.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);
			Grid.SetRow(key, i);
			Grid.SetColumn(key, 0);
			grid.Children.Add(key);

			var description = new TextBlock
			{
				Text = items[i].Description,
				Margin = new Thickness(0, 2, 0, 2),
				TextWrapping = TextWrapping.Wrap
			};
			description.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.MutedTextBrushKey);
			Grid.SetRow(description, i);
			Grid.SetColumn(description, 1);
			grid.Children.Add(description);
		}

		section.Children.Add(grid);
		return section;
	}

	private static BitmapFrame? LoadIcon()
	{
		string path = Path.Combine(AppContext.BaseDirectory, "Clipboard.ico");
		if (!File.Exists(path))
		{
			path = Path.Combine(Directory.GetCurrentDirectory(), "Clipboard.ico");
		}

		return File.Exists(path) ? BitmapFrame.Create(new Uri(path)) : null;
	}
}
