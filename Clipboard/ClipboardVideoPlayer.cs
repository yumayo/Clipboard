using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clipboard;

internal sealed class ClipboardVideoPlayer : UserControl, IDisposable
{
	private readonly MediaElement _media;
	private readonly DispatcherTimer _timer;
	private readonly Slider _seek;
	private readonly Button _playButton;
	private readonly Button _previousButton;
	private readonly Button _nextButton;
	private readonly bool _autoPlay;
	private readonly TextBlock _time;
	private readonly TextBlock _message;
	private readonly TextBlock _frameRateText;
	private readonly ClipboardVideoFrameStrip _frameStrip;
	private readonly Grid _editorHost;
	private readonly Grid _viewport;
	private ImagePaintEditor? _frameEditor;
	private Size _videoImageSize;
	private Rect _videoImageBounds = Rect.Empty;
	private readonly string _path;
	private TimeSpan _duration;
	private TimeSpan _position;
	private double _frameRate;
	private bool _opened;
	private bool _playing;
	private bool _updatingPosition;
	private bool _dragging;
	private bool _resumeAfterSeek;
	private bool _disposed;

	public ClipboardVideoPlayer(string path, bool autoPlay = true)
	{
		_path = path;
		_autoPlay = autoPlay;
		Focusable = true;
		var panel = new DockPanel();
		var footer = new StackPanel { Margin = new Thickness(10) };
		DockPanel.SetDock(footer, Dock.Bottom);
		panel.Children.Add(footer);
		_frameStrip = new ClipboardVideoFrameStrip(path, position =>
		{
			if (!_opened || _disposed) return;
			Pause();
			SeekTo(position);
		});
		footer.Children.Add(_frameStrip);

		var name = new TextBlock
		{
			Text = Path.GetFileName(path),
			ToolTip = path,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(0, 0, 0, 6)
		};
		name.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);
		footer.Children.Add(name);

		_seek = new Slider
		{
			Minimum = 0,
			Maximum = 1,
			IsEnabled = false,
			Focusable = false
		};
		_seek.ValueChanged += (_, _) =>
		{
			if (!_updatingPosition && _opened)
			{
				SeekTo(TimeSpan.FromSeconds(_seek.Value));
			}
		};
		_seek.PreviewMouseLeftButtonDown += BeginSeek;
		_seek.PreviewMouseMove += ContinueSeek;
		_seek.PreviewMouseLeftButtonUp += (_, e) =>
		{
			if (_dragging)
			{
				SeekFromMouse(e);
				EndSeek();
				e.Handled = true;
			}
		};
		_seek.LostMouseCapture += (_, _) => EndSeek();
		footer.Children.Add(_seek);

		var controls = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
		_playButton = CreateButton("▶", "再生／一時停止 (Space)", TogglePlayback);
		_previousButton = CreateButton("◀|", "コマ戻し (← または ,)", () => Step(-1));
		_nextButton = CreateButton("|▶", "コマ送り (→ または .)", () => Step(1));
		buttons.Children.Add(_previousButton);
		buttons.Children.Add(_playButton);
		buttons.Children.Add(_nextButton);
		controls.Children.Add(buttons);
		_time = new TextBlock { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
		_time.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);
		controls.Children.Add(_time);
		footer.Children.Add(controls);

		_frameRateText = new TextBlock
		{
			Text = "動画のFPSを取得中…",
			Margin = new Thickness(0, 6, 0, 0)
		};
		_frameRateText.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.MutedTextBrushKey);
		footer.Children.Add(_frameRateText);

		_media = new MediaElement
		{
			LoadedBehavior = MediaState.Manual,
			UnloadedBehavior = MediaState.Manual,
			ScrubbingEnabled = true,
			// 表示矩形は編集画像と共有し、MediaElement側で再計算しない。
			Stretch = Stretch.Fill,
			UseLayoutRounding = false,
			SnapsToDevicePixels = true,
			Focusable = false
		};
		_media.MediaOpened += (_, _) => MediaOpened();
		_media.MediaEnded += (_, _) =>
		{
			if (_disposed)
			{
				return;
			}
			Pause();
			_position = _duration;
			UpdatePosition();
			NotifyPausedFrame();
		};
		_media.MediaFailed += (_, e) => ShowFailure("動画を再生できません。形式・コーデック、またはファイルの状態を確認してください。", e.ErrorException);
		var viewport = _viewport = new Grid { Background = Brushes.Black, UseLayoutRounding = false, ClipToBounds = true };
		viewport.SizeChanged += (_, _) => UpdateVideoLayout();
		// 編集画面では動画へポインターを戻すだけで停止中のコマを編集できる。
		viewport.MouseEnter += (_, _) => PauseForEditing();
		viewport.PreviewMouseLeftButtonDown += (_, _) => PauseForEditing();
		// Canvasに置き、映像の自然サイズや表示矩形で表示領域自体を押し広げない。
		var mediaHost = new Canvas { ClipToBounds = true };
		mediaHost.Children.Add(_media);
		viewport.Children.Add(mediaHost);
		_message = new TextBlock
		{
			Text = "動画を読み込み中…",
			Foreground = Brushes.White,
			TextWrapping = TextWrapping.Wrap,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(16),
			IsHitTestVisible = false
		};
		viewport.Children.Add(_message);
		_editorHost = new Grid
		{
			Visibility = Visibility.Collapsed
		};
		viewport.Children.Add(_editorHost);
		panel.Children.Add(viewport);
		Content = panel;
		SetControlsEnabled(false);
		UpdatePosition();
		_timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
		{
			if (_playing && !_dragging)
			{
				_position = _media.Position;
				UpdatePosition();
			}
		}, Dispatcher);
		_timer.Stop();
		Loaded += OnLoaded;
		Unloaded += (_, _) => Dispose();
	}

	public double FrameRate => _frameRate;

	public event Action<TimeSpan>? PausedFrameChanged;

	public Func<bool>? BeforeNavigate { get; set; }

	public void ShowFrameEditor(ImagePaintEditor? editor)
	{
		if (_frameEditor != null) _frameEditor.VideoImageBoundsChanged -= SetVideoImageBounds;
		_frameEditor = editor;
		_editorHost.Children.Clear();
		if (editor != null)
		{
			if (_videoImageSize != editor.VideoImageSize || _videoImageBounds.IsEmpty)
			{
				_videoImageSize = editor.VideoImageSize;
				UpdateVideoLayout();
			}
			editor.SetVideoViewportBounds(_videoImageBounds);
			editor.VideoImageBoundsChanged += SetVideoImageBounds;
			_editorHost.Children.Add(editor);
		}
		_editorHost.Visibility = editor == null ? Visibility.Collapsed : Visibility.Visible;
		_media.Visibility = editor == null ? Visibility.Visible : Visibility.Hidden;
		SetControlsEnabled(_opened);
	}

	private void UpdateVideoLayout()
	{
		DpiScale dpi = VisualTreeHelper.GetDpi(_viewport);
		if (!ClipboardVideoViewportLayout.TryFit(_videoImageSize.Width, _videoImageSize.Height,
			_viewport.ActualWidth, _viewport.ActualHeight, dpi.DpiScaleX, dpi.DpiScaleY, out var layout)) return;
		SetVideoImageBounds(new Rect(layout.Left, layout.Top, layout.Width, layout.Height));
		_frameEditor?.SetVideoViewportBounds(_videoImageBounds);
	}

	private void SetVideoImageBounds(Rect bounds)
	{
		if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
		_videoImageBounds = bounds;
		_media.Width = bounds.Width;
		_media.Height = bounds.Height;
		Canvas.SetLeft(_media, bounds.Left);
		Canvas.SetTop(_media, bounds.Top);
	}

	protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
	{
		base.OnDpiChanged(oldDpi, newDpi);
		UpdateVideoLayout();
	}

	public void SetEditedFrames(IReadOnlyDictionary<long, BitmapSource> frames) => _frameStrip.SetEditedFrames(frames);

	public void PausePlayback()
	{
		if (!_opened || _disposed) return;
		Pause();
		NotifyPausedFrame();
	}

	public void SelectFrame(TimeSpan position)
	{
		if (_disposed) return;
		if (!_opened)
		{
			// 元動画がなくても保存済みのコマは開ける。
			PausedFrameChanged?.Invoke(position);
			return;
		}
		Pause();
		if (SeekTo(position) && _frameRate <= 0) PausedFrameChanged?.Invoke(position);
	}

	public bool HandleKeyDown(KeyEventArgs e)
	{
		if (Keyboard.Modifiers != ModifierKeys.None || !_opened)
		{
			return false;
		}
		switch (e.Key)
		{
			case Key.Space:
				TogglePlayback();
				return true;
			case Key.Right:
			case Key.OemPeriod:
				Step(1);
				return true;
			case Key.Left:
			case Key.OemComma:
				Step(-1);
				return true;
			case Key.Home:
				SeekTo(TimeSpan.Zero);
				return true;
			default:
				return false;
		}
	}

	private async void OnLoaded(object sender, RoutedEventArgs e)
	{
		Loaded -= OnLoaded;
		if (_disposed)
		{
			return;
		}
		if (!File.Exists(_path))
		{
			ShowFailure("動画の元ファイルが見つかりません。移動・削除されていないか確認してください。");
			return;
		}
		try
		{
			_media.Source = new Uri(_path, UriKind.Absolute);
			_media.Pause();
		}
		catch (Exception ex)
		{
			ShowFailure("動画を開けませんでした。", ex);
			return;
		}

		double? frameRate = null;
		try
		{
			frameRate = await Task.Run(() => ClipboardVideoMetadata.ReadFrameRate(_path));
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardVideoPlayer: 動画の情報を取得できませんでした。");
		}
		if (_disposed)
		{
			return;
		}
		_frameRate = frameRate.GetValueOrDefault();
		_frameRateText.Text = _frameRate > 0
			? $"動画のFPS: {_frameRate.ToString("0.###", CultureInfo.InvariantCulture)}"
			: "動画のFPSを取得できません（コマ送り不可）";
		SetControlsEnabled(_opened);
		UpdatePosition();
		NotifyPausedFrame();
	}

	private void MediaOpened()
	{
		if (_disposed)
		{
			return;
		}
		if (!_media.HasVideo || !_media.NaturalDuration.HasTimeSpan || _media.NaturalDuration.TimeSpan <= TimeSpan.Zero)
		{
			ShowFailure("再生できる映像がありません。");
			return;
		}
		_duration = _media.NaturalDuration.TimeSpan;
		_videoImageSize = new Size(_media.NaturalVideoWidth, _media.NaturalVideoHeight);
		UpdateVideoLayout();
		_opened = true;
		_message.Visibility = Visibility.Collapsed;
		_seek.Maximum = _duration.TotalSeconds;
		SetControlsEnabled(true);
		_timer.Start();
		UpdatePosition();
		if (_autoPlay) TogglePlayback();
		else NotifyPausedFrame();
	}

	private void TogglePlayback()
	{
		if (!_opened || _disposed)
		{
			return;
		}
		if (_playing)
		{
			Pause();
			NotifyPausedFrame();
		}
		else
		{
			if (BeforeNavigate?.Invoke() == false) return;
			_playing = true;
			if (_position >= _duration)
			{
				_position = TimeSpan.Zero;
				_media.Position = _position;
				UpdatePosition();
			}
			_media.Play();
			_playButton.Content = "❚❚";
		}
	}

	private void Pause()
	{
		if (_playing)
		{
			_position = _media.Position;
		}
		_media.Pause();
		_playing = false;
		_playButton.Content = "▶";
		UpdatePosition();
	}

	private void Step(int direction)
	{
		if (!_opened || _disposed || _frameRate <= 0)
		{
			return;
		}
		Pause();
		SeekTo(ClipboardVideoTimeline.Step(_position, _duration, _frameRate, direction));
	}

	private bool SeekTo(TimeSpan position)
	{
		if (BeforeNavigate?.Invoke() == false) return false;
		_position = ClipboardVideoTimeline.ClampPosition(position, _duration);
		_media.Position = _position;
		UpdatePosition();
		NotifyPausedFrame();
		return true;
	}

	private void BeginSeek(object sender, MouseButtonEventArgs e)
	{
		if (!_opened || _disposed || _dragging || BeforeNavigate?.Invoke() == false || !_seek.CaptureMouse())
		{
			return;
		}
		_dragging = true;
		_resumeAfterSeek = _playing;
		Pause();
		SeekFromMouse(e);
		e.Handled = true;
	}

	private void ContinueSeek(object sender, MouseEventArgs e)
	{
		if (!_dragging)
		{
			return;
		}
		if (e.LeftButton == MouseButtonState.Pressed)
		{
			SeekFromMouse(e);
		}
		else
		{
			EndSeek();
		}
		e.Handled = true;
	}

	private void SeekFromMouse(MouseEventArgs e)
	{
		if (_seek.Template?.FindName("PART_Track", _seek) is not Track track)
		{
			return;
		}
		// 直前のシークで動いたつまみの位置を反映してから計算する。
		track.UpdateLayout();
		double value = track.ValueFromPoint(e.GetPosition(track));
		if (double.IsFinite(value))
		{
			_seek.Value = Math.Clamp(value, _seek.Minimum, _seek.Maximum);
		}
	}

	private void EndSeek()
	{
		if (!_dragging)
		{
			return;
		}
		_dragging = false;
		if (_seek.IsMouseCaptured)
		{
			_seek.ReleaseMouseCapture();
		}
		if (_resumeAfterSeek && _opened && !_disposed && !_playing)
		{
			TogglePlayback();
		}
		_resumeAfterSeek = false;
		NotifyPausedFrame();
	}

	private void PauseForEditing()
	{
		if (_playing && PausedFrameChanged != null) PausePlayback();
	}

	private void NotifyPausedFrame()
	{
		if (!_opened || _disposed || _playing || _dragging || _frameRate <= 0) return;
		TimeSpan? position = ClipboardVideoTimeline.PreviewPositions(_position, _duration, _frameRate, 1).FirstOrDefault();
		if (position.HasValue) PausedFrameChanged?.Invoke(position.Value);
	}

	private void UpdatePosition()
	{
		// 再生エンジンは終端付近でNaturalDurationを超える位置を返すことがある。
		_position = ClipboardVideoTimeline.ClampPosition(_position, _duration);
		_updatingPosition = true;
		_seek.Value = _position.TotalSeconds;
		_updatingPosition = false;
		_time.Text = $"{FormatTime(_position)} / {FormatTime(_duration)}";
		_frameStrip.UpdatePosition(_position, _duration, _opened ? _frameRate : 0);
	}

	private static string FormatTime(TimeSpan time) => $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";

	private void ShowFailure(string message, Exception? exception = null)
	{
		if (_disposed)
		{
			return;
		}
		_opened = false;
		_playing = false;
		EndSeek();
		_timer.Stop();
		_media.Close();
		SetControlsEnabled(false);
		_frameStrip.UpdatePosition(TimeSpan.Zero, TimeSpan.Zero, 0);
		_message.Text = message;
		_message.Visibility = Visibility.Visible;
		Logger.Error(exception, $"ClipboardVideoPlayer: {message} Path={_path}");
	}

	private void SetControlsEnabled(bool enabled)
	{
		_playButton.IsEnabled = enabled;
		_previousButton.IsEnabled = enabled && _frameRate > 0;
		_nextButton.IsEnabled = enabled && _frameRate > 0;
		_seek.IsEnabled = enabled;
	}

	private static Button CreateButton(string text, string toolTip, Action action)
	{
		var button = new Button
		{
			Content = text,
			ToolTip = toolTip,
			MinWidth = 46,
			MinHeight = 30,
			Padding = new Thickness(8, 4, 8, 4),
			Margin = new Thickness(3, 0, 3, 0),
			Focusable = false
		};
		AppTheme.ApplyButton(button);
		button.Click += (_, _) => action();
		return button;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		EndSeek();
		_playing = false;
		_timer.Stop();
		_frameStrip.Dispose();
		if (_frameEditor != null) _frameEditor.VideoImageBoundsChanged -= SetVideoImageBounds;
		_media.Close();
		_media.Source = null;
	}
}
