using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Clipboard;

internal sealed class SettingsWindow : Window
{
	private readonly TextBox _separatorTextBox;

	public SettingsWindow()
	{
		Title = "設定";
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		ResizeMode = ResizeMode.NoResize;
		Width = 420;
		Height = 230;
		Icon = LoadIcon();

		var root = new Grid
		{
			Margin = new Thickness(16)
		};
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		var label = new TextBlock
		{
			Text = "連結文字",
			Margin = new Thickness(0, 0, 0, 8)
		};
		Grid.SetRow(label, 0);
		root.Children.Add(label);

		_separatorTextBox = new TextBox
		{
			AcceptsReturn = true,
			AcceptsTab = true,
			TextWrapping = TextWrapping.NoWrap,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Text = ToTextBoxNewLines(ClipboardSettings.GetCopy().ConcatenationSeparator)
		};
		Grid.SetRow(_separatorTextBox, 1);
		root.Children.Add(_separatorTextBox);

		var buttonPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0, 16, 0, 0)
		};
		Grid.SetRow(buttonPanel, 2);

		var saveButton = new Button
		{
			Content = "保存",
			Width = 75,
			Height = 28,
			Margin = new Thickness(0, 0, 8, 0),
			IsDefault = true
		};
		saveButton.Click += SaveButton_Click;
		buttonPanel.Children.Add(saveButton);

		var cancelButton = new Button
		{
			Content = "キャンセル",
			Width = 75,
			Height = 28,
			IsCancel = true
		};
		buttonPanel.Children.Add(cancelButton);
		root.Children.Add(buttonPanel);

		Content = root;
	}

	private void SaveButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			ClipboardSettings.Save(new ClipboardSettingsData
			{
				ConcatenationSeparator = _separatorTextBox.Text
			});
			DialogResult = true;
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "設定の保存に失敗しました。");
			MessageBox.Show(this, "設定の保存に失敗しました。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
		}
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

	private static string ToTextBoxNewLines(string text)
	{
		return text.Replace("\n", "\r\n");
	}
}
