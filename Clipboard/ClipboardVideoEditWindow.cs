using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clipboard;

internal sealed class ClipboardVideoEditWindow : Window
{
	private readonly long _historyId;
	private readonly string _path;
	private readonly ClipboardVideoPlayer _player;
	private readonly StackPanel _savedFrames;
	private readonly StackPanel _savedFramesHost;
	private readonly WrapPanel _editorToolbar;
	private readonly TextBlock _editingTime;
	private readonly CancellationTokenSource _cancellation = new();
	private ClipboardVideoFrameReader? _reader;
	private ImagePaintEditor? _frameEditor;
	private TimeSpan? _editingPosition;
	private bool _loadingFrame;
	private bool _closed;

	public ClipboardVideoEditWindow(long historyId, string path)
	{
		_historyId = historyId;
		_path = path;
		Title = $"動画のコマを編集 - {Path.GetFileName(path)}";
		Width = 960;
		Height = 820;
		MinWidth = 720;
		MinHeight = 600;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		UseLayoutRounding = true;
		Icon = ImagePaintImageFactory.LoadIcon();
		AppTheme.ApplyWindow(this);

		var root = new DockPanel();
		var guide = new TextBlock
		{
			Text = "「このコマを編集」で動画の表示領域に赤枠などを追加できます。Ctrl+S / Ctrl+C で保存して画像をコピーします。コマの切り替え・再生・終了時にも編集内容を保存します。",
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(10)
		};
		guide.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);
		DockPanel.SetDock(guide, Dock.Top);
		root.Children.Add(guide);

		_editorToolbar = new WrapPanel { Margin = new Thickness(10, 0, 10, 6), Visibility = Visibility.Collapsed };
		_editingTime = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
		_editingTime.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);
		_editorToolbar.Children.Add(_editingTime);
		_editorToolbar.Children.Add(CreateButton("保存して画像をコピー", () => TrySaveCurrentFrame()));
		_editorToolbar.Children.Add(CreateButton("再生に戻る", () => TryEndFrameEditing()));
		DockPanel.SetDock(_editorToolbar, Dock.Top);
		root.Children.Add(_editorToolbar);

		_savedFramesHost = new StackPanel { Margin = new Thickness(10, 0, 10, 6) };
		var label = new TextBlock
		{
			Text = "編集したコマ（クリックして再編集・画像をコピー）",
			Margin = new Thickness(0, 0, 0, 4)
		};
		label.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);
		_savedFramesHost.Children.Add(label);
		_savedFrames = new StackPanel { Orientation = Orientation.Horizontal };
		_savedFramesHost.Children.Add(new ScrollViewer
		{
			Content = _savedFrames,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
		});
		DockPanel.SetDock(_savedFramesHost, Dock.Top);
		root.Children.Add(_savedFramesHost);

		_player = new ClipboardVideoPlayer(path, autoPlay: false, editFrame: OpenFrameEditor);
		_player.BeforeNavigate = TryEndFrameEditing;
		root.Children.Add(_player);
		Content = root;
		RefreshSavedFrames();
		Closed += (_, _) =>
		{
			_closed = true;
			_cancellation.Cancel();
			_reader?.Dispose();
			_frameEditor?.Dispose();
			_player.Dispose();
			_cancellation.Dispose();
		};
	}

	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		if (_frameEditor != null && !_frameEditor.IsKeyboardFocusWithin &&
			(Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key is Key.S or Key.C)
		{
			TrySaveCurrentFrame();
			e.Handled = true;
		}
		else if (_frameEditor?.IsTextInputFocused != true && !_loadingFrame)
		{
			if (_player.HandleKeyDown(e)) e.Handled = true;
			else if (e.Key == Key.Escape)
			{
				if (_frameEditor != null) TryEndFrameEditing();
				else Close();
				e.Handled = true;
			}
		}
		base.OnPreviewKeyDown(e);
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		if (!_loadingFrame && !TryEndFrameEditing()) e.Cancel = true;
		base.OnClosing(e);
	}

	private void RefreshSavedFrames()
	{
		List<ClipboardVideoFrameSummary> frames = ClipboardDatabase.LoadVideoFrameSummaries(_historyId);
		var editedImages = new Dictionary<long, BitmapSource>();
		_savedFrames.Children.Clear();
		foreach (ClipboardVideoFrameSummary frame in frames)
		{
			BitmapSource thumbnail = ImagePaintImageFactory.LoadImage(frame.ThumbnailBytes);
			editedImages[frame.Position.Ticks] = thumbnail;
			var imageHost = new Grid { Height = 82, Width = 144 };
			imageHost.Children.Add(new Image { Source = thumbnail, Stretch = Stretch.Uniform });
			imageHost.Children.Add(ImageEditBadgeFactory.Create(VerticalAlignment.Top));
			var content = new StackPanel();
			content.Children.Add(imageHost);
			var time = new TextBlock
			{
				Text = FormatTime(frame.Position),
				TextAlignment = TextAlignment.Center
			};
			time.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);
			content.Children.Add(time);
			var button = new Button
			{
				Content = content,
				Margin = new Thickness(0, 0, 6, 0),
				Padding = new Thickness(3),
				Focusable = false,
				ToolTip = $"{FormatTime(frame.Position)} の編集画像を開く",
				Cursor = Cursors.Hand
			};
			AppTheme.ApplyButton(button);
			button.Click += (_, _) => OpenFrameEditor(frame.Position);
			_savedFrames.Children.Add(button);
		}
		_savedFramesHost.Visibility = frames.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		_player.SetEditedFrames(editedImages);
	}

	private async void OpenFrameEditor(TimeSpan position)
	{
		if (_loadingFrame || _closed || _editingPosition == position) return;
		if (!TryEndFrameEditing()) return;
		_player.SelectFrame(position);
		_loadingFrame = true;
		_player.PausePlayback();
		_player.IsEnabled = false;
		_savedFrames.IsEnabled = false;
		CancellationToken token = _cancellation.Token;
		try
		{
			ClipboardVideoFrameEdit? saved = await Task.Run(() => ClipboardDatabase.LoadVideoFrameEdit(_historyId, position), token);
			token.ThrowIfCancellationRequested();
			byte[] imageBytes;
			if (saved != null)
			{
				imageBytes = saved.ImageBytes;
			}
			else
			{
				_reader ??= new ClipboardVideoFrameReader(_path);
				IReadOnlyDictionary<long, BitmapSource> images = await _reader.ReadAsync([position], _player.FrameRate, token);
				token.ThrowIfCancellationRequested();
				if (!images.TryGetValue(position.Ticks, out BitmapSource? image))
					throw new InvalidOperationException("選択したコマの画像を取得できませんでした。");
				imageBytes = ImagePaintImageFactory.EncodePng(image);
			}

			void Save(BitmapSource image, string state, bool copyToClipboard)
			{
				ClipboardManager.SavePaintedVideoFrame(_historyId, position, image, state, copyToClipboard);
				RefreshSavedFrames();
			}

			ImagePaintEditor? editor = null;
			if (ImagePaintSerializedState.TryDeserialize(saved?.PaintStateJson, out ImagePaintSerializedState? savedState) && savedState != null)
			{
				try { editor = new ImagePaintEditor(savedState, Save); }
				catch (Exception ex)
				{
					Logger.Warning($"ClipboardVideoEditWindow: 編集状態を復元できないため保存画像を開きます。Error={ex.Message}");
				}
			}
			editor ??= new ImagePaintEditor(imageBytes, Save);
			_frameEditor = editor;
			_editingPosition = position;
			_editingTime.Text = $"編集中: {FormatTime(position)}";
			_editorToolbar.Visibility = Visibility.Visible;
			_player.ShowFrameEditor(editor);
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			Logger.Error(ex, $"ClipboardVideoEditWindow: コマを開けませんでした。Id={_historyId} Position={position}");
			if (!_closed)
				MessageBox.Show(this, "コマを開けませんでした。元動画の状態や対応形式を確認してください。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
		}
		finally
		{
			_loadingFrame = false;
			if (!_closed)
			{
				_player.IsEnabled = true;
				_savedFrames.IsEnabled = true;
			}
		}
	}

	private bool TrySaveCurrentFrame(bool requireChanges = false, bool copyToClipboard = true)
	{
		try
		{
			_frameEditor?.Save(requireChanges, copyToClipboard);
			return true;
		}
		catch (Exception ex)
		{
			Logger.Error(ex, $"ClipboardVideoEditWindow: コマの編集を保存できませんでした。Id={_historyId}");
			MessageBox.Show(this, "コマの編集を保存できませんでした。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
			return false;
		}
	}

	private bool TryEndFrameEditing()
	{
		if (_loadingFrame) return false;
		if (_frameEditor == null) return true;
		if (!TrySaveCurrentFrame(requireChanges: true, copyToClipboard: false)) return false;
		_player.ShowFrameEditor(null);
		_frameEditor.Dispose();
		_frameEditor = null;
		_editingPosition = null;
		_editorToolbar.Visibility = Visibility.Collapsed;
		return true;
	}

	private static Button CreateButton(string text, Action action)
	{
		var button = new Button
		{
			Content = text,
			Padding = new Thickness(8, 4, 8, 4),
			Margin = new Thickness(0, 0, 6, 0),
			Focusable = false
		};
		AppTheme.ApplyButton(button);
		button.Click += (_, _) => action();
		return button;
	}

	private static string FormatTime(TimeSpan time) => $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
}
