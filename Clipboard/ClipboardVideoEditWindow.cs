using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clipboard;

internal sealed class ClipboardVideoEditWindow : Window
{
	private readonly long _historyId;
	private readonly string _path;
	private readonly ClipboardVideoPlayer _player;
	private readonly StackPanel _savedFrames;
	private readonly StackPanel _savedFramesHost;
	private readonly WrapPanel _editorToolbar;
	private readonly TextBlock _frameError;
	private readonly DispatcherTimer _autoSaveTimer;
	private readonly ContentControl _toolOptionsHost;
	private readonly Dictionary<PaintMode, Button> _modeButtons = new();
	private CancellationTokenSource? _frameCancellation;
	private ClipboardVideoFrameReader? _reader;
	private ImagePaintEditor? _frameEditor;
	private TimeSpan? _editingPosition;
	private TimeSpan? _loadingPosition;
	private PaintMode _paintMode = PaintMode.RedOutlineRectangle;
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
		_editorToolbar = new WrapPanel { Margin = new Thickness(10, 10, 10, 6) };
		AddModeButton(PaintMode.RedOutlineRectangle, "赤枠", ImagePaintToolbarFactory.CreateOutlineIcon());
		AddModeButton(PaintMode.BlackFillRectangle, "塗りつぶし", ImagePaintToolbarFactory.CreateFillIcon());
		AddModeButton(PaintMode.ArrowTextRectangle, "矢印矩形", ImagePaintToolbarFactory.CreateArrowTextRectangleIcon());
		AddModeButton(PaintMode.MoveImage, "画像の移動", ImagePaintToolbarFactory.CreateMoveImageIcon());
		DockPanel.SetDock(_editorToolbar, Dock.Top);
		root.Children.Add(_editorToolbar);
		_frameError = new TextBlock
		{
			Text = "コマ画像を取得できません（再生・シークは使用できます）",
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(10, 0, 10, 6),
			Visibility = Visibility.Collapsed
		};
		_frameError.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);
		DockPanel.SetDock(_frameError, Dock.Top);
		root.Children.Add(_frameError);
		_toolOptionsHost = new ContentControl
		{
			MinHeight = 48,
			HorizontalContentAlignment = HorizontalAlignment.Center,
			IsEnabled = false
		};
		DockPanel.SetDock(_toolOptionsHost, Dock.Top);
		root.Children.Add(_toolOptionsHost);

		_savedFramesHost = new StackPanel { Margin = new Thickness(10, 0, 10, 6) };
		var label = new TextBlock
		{
			Text = "編集したコマ",
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

		_player = new ClipboardVideoPlayer(path, autoPlay: false);
		_player.BeforeNavigate = TryEndFrameEditing;
		_player.PausedFrameChanged += OpenFrameEditor;
		root.Children.Add(_player);
		Content = root;
		_autoSaveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
		{
			Interval = TimeSpan.FromMilliseconds(500)
		};
		_autoSaveTimer.Tick += (_, _) =>
		{
			// ドラッグの途中で保存して描画や移動を確定しない。
			if (Mouse.LeftButton == MouseButtonState.Pressed) return;
			_autoSaveTimer.Stop();
			TrySaveCurrentFrame(requireChanges: true, copyToClipboard: false, focusEditor: false);
		};
		RefreshSavedFrames();
		Closed += (_, _) =>
		{
			_closed = true;
			_autoSaveTimer.Stop();
			_frameCancellation?.Cancel();
			_reader?.Dispose();
			_frameEditor?.Dispose();
			_player.Dispose();
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
		else if (Keyboard.FocusedElement is not TextBox)
		{
			if (_player.HandleKeyDown(e)) e.Handled = true;
			else if (e.Key == Key.Escape)
			{
				Close();
				e.Handled = true;
			}
		}
		base.OnPreviewKeyDown(e);
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		if (!TryEndFrameEditing()) e.Cancel = true;
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
			button.Click += (_, _) => _player.SelectFrame(frame.Position);
			_savedFrames.Children.Add(button);
		}
		_savedFramesHost.Visibility = frames.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		_player.SetEditedFrames(editedImages);
	}

	private async void OpenFrameEditor(TimeSpan position)
	{
		if (_closed || _editingPosition == position || _loadingPosition == position) return;
		if (!TryEndFrameEditing()) return;
		using var cancellation = new CancellationTokenSource();
		_frameCancellation = cancellation;
		_loadingPosition = position;
		_frameError.Visibility = Visibility.Collapsed;
		CancellationToken token = cancellation.Token;
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
				_reader ??= new ClipboardVideoFrameReader(_path, fullResolution: true);
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
			_toolOptionsHost.Content = editor.UseVideoViewport(_paintMode);
			_toolOptionsHost.IsEnabled = true;
			_frameEditor = editor;
			editor.Changed += FrameEditor_Changed;
			_editingPosition = position;
			_player.ShowFrameEditor(editor);
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			if (token.IsCancellationRequested) return;
			Logger.Error(ex, $"ClipboardVideoEditWindow: コマを開けませんでした。Id={_historyId} Position={position}");
			if (!_closed) _frameError.Visibility = Visibility.Visible;
		}
		finally
		{
			if (ReferenceEquals(_frameCancellation, cancellation))
			{
				_frameCancellation = null;
				_loadingPosition = null;
			}
		}
	}

	private void FrameEditor_Changed(object? sender, EventArgs e)
	{
		_autoSaveTimer.Stop();
		_autoSaveTimer.Start();
	}

	private bool TrySaveCurrentFrame(bool requireChanges = false, bool copyToClipboard = true, bool focusEditor = true)
	{
		try
		{
			_frameEditor?.Save(requireChanges, copyToClipboard, focusEditor);
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
		if (!TrySaveCurrentFrame(requireChanges: true, copyToClipboard: false)) return false;
		_autoSaveTimer.Stop();
		_frameCancellation?.Cancel();
		_frameCancellation = null;
		_loadingPosition = null;
		_player.ShowFrameEditor(null);
		if (_frameEditor != null)
		{
			_frameEditor.Changed -= FrameEditor_Changed;
			_frameEditor.Dispose();
		}
		_frameEditor = null;
		_editingPosition = null;
		_toolOptionsHost.IsEnabled = false;
		_frameError.Visibility = Visibility.Collapsed;
		return true;
	}

	private void AddModeButton(PaintMode mode, string text, UIElement icon)
	{
		Button button = ImagePaintToolbarFactory.CreateModeButton(text, icon);
		button.Margin = new Thickness(0, 0, 6, 4);
		button.Click += (_, _) =>
		{
			_paintMode = mode;
			foreach (var (buttonMode, modeButton) in _modeButtons)
				ImagePaintToolbarFactory.UpdateModeButton(modeButton, buttonMode == mode);
			_frameEditor?.UseVideoViewport(mode);
			_player.PausePlayback();
		};
		ImagePaintToolbarFactory.UpdateModeButton(button, mode == _paintMode);
		_modeButtons.Add(mode, button);
		_editorToolbar.Children.Add(button);
	}

	private static string FormatTime(TimeSpan time) => $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
}
