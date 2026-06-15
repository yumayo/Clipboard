using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clipboard;

internal sealed class MigrationProgressWindow : Window
{
	private readonly ProgressBar _progressBar;
	private readonly TextBlock _messageText;
	private readonly TextBlock _detailText;
	private readonly TextBlock _countText;
	private bool _isRunning;
	private bool _started;

	public MigrationProgressWindow()
	{
		Title = "データ移行";
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		ResizeMode = ResizeMode.NoResize;
		Width = 460;
		Height = 190;
		ShowInTaskbar = true;
		Icon = LoadIcon();

		var root = new Grid
		{
			Margin = new Thickness(18)
		};
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		_messageText = new TextBlock
		{
			Text = "旧データをSQLiteへ移行しています...",
			FontSize = 15,
			FontWeight = FontWeights.Bold,
			Foreground = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
			TextWrapping = TextWrapping.Wrap
		};
		Grid.SetRow(_messageText, 0);
		root.Children.Add(_messageText);

		_detailText = new TextBlock
		{
			Text = "",
			Margin = new Thickness(0, 10, 0, 0),
			Foreground = new SolidColorBrush(Color.FromRgb(96, 96, 96)),
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		Grid.SetRow(_detailText, 1);
		root.Children.Add(_detailText);

		_progressBar = new ProgressBar
		{
			Height = 20,
			Margin = new Thickness(0, 14, 0, 0),
			Minimum = 0,
			Maximum = 1,
			IsIndeterminate = true
		};
		Grid.SetRow(_progressBar, 2);
		root.Children.Add(_progressBar);

		_countText = new TextBlock
		{
			Text = "",
			Margin = new Thickness(0, 8, 0, 0),
			Foreground = new SolidColorBrush(Color.FromRgb(96, 96, 96)),
			TextAlignment = TextAlignment.Right
		};
		Grid.SetRow(_countText, 3);
		root.Children.Add(_countText);

		Content = root;
		Loaded += (_, _) => StartMigration();
		Closing += MigrationProgressWindow_Closing;
	}

	public static bool RunIfNeeded()
	{
		try
		{
			if (!ClipboardLegacyMigrator.NeedsMigration())
			{
				return true;
			}
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "旧データ移行の要否判定に失敗しました。");
			MessageBox.Show("旧データ移行の確認に失敗しました。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
			return false;
		}

		var window = new MigrationProgressWindow();
		return window.ShowDialog() == true;
	}

	private async void StartMigration()
	{
		if (_started)
		{
			return;
		}

		_started = true;
		_isRunning = true;

		try
		{
			var progress = new Progress<ClipboardMigrationProgress>(UpdateProgress);
			await Task.Run(() => ClipboardLegacyMigrator.Migrate(progress, CancellationToken.None));
			_isRunning = false;
			DialogResult = true;
			Close();
		}
		catch (Exception ex)
		{
			_isRunning = false;
			Logger.Error(ex, "旧データの移行に失敗しました。");
			MessageBox.Show(this, "旧データの移行に失敗しました。アプリは起動しますが、旧履歴の一部が表示されない可能性があります。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
			DialogResult = false;
			Close();
		}
	}

	private void UpdateProgress(ClipboardMigrationProgress progress)
	{
		_messageText.Text = progress.Message;
		_detailText.Text = progress.Detail ?? "";

		if (progress.IsIndeterminate || progress.TotalItems <= 0)
		{
			_progressBar.IsIndeterminate = true;
			_progressBar.Maximum = 1;
			_progressBar.Value = 0;
			_countText.Text = "";
			return;
		}

		_progressBar.IsIndeterminate = false;
		_progressBar.Maximum = progress.TotalItems;
		_progressBar.Value = Math.Min(progress.ProcessedItems, progress.TotalItems);
		_countText.Text = $"{progress.ProcessedItems:N0} / {progress.TotalItems:N0}";
	}

	private void MigrationProgressWindow_Closing(object? sender, CancelEventArgs e)
	{
		if (_isRunning)
		{
			e.Cancel = true;
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
}
