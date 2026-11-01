using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
	private readonly TextBlock _time;
	private readonly TextBlock _message;
	private readonly TextBlock _frameRateText;
	private readonly ClipboardVideoFrameStrip _frameStrip;
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

	public ClipboardVideoPlayer(string path)
	{
		_path = path;
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

		var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
		_playButton = CreateButton("再生", "再生／一時停止 (Space)", TogglePlayback);
		_previousButton = CreateButton("◀|", "コマ戻し (← または ,)", () => Step(-1));
		_nextButton = CreateButton("|▶", "コマ送り (→ または .)", () => Step(1));
		buttons.Children.Add(_playButton);
		buttons.Children.Add(_previousButton);
		buttons.Children.Add(_nextButton);
		_time = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
		_time.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);
		buttons.Children.Add(_time);
		footer.Children.Add(buttons);

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
			Stretch = Stretch.Uniform,
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
		};
		_media.MediaFailed += (_, e) => ShowFailure("動画を再生できません。形式・コーデック、またはファイルの状態を確認してください。", e.ErrorException);
		_media.MouseLeftButtonDown += (_, e) =>
		{
			TogglePlayback();
			Focus();
			e.Handled = true;
		};
		var viewport = new Grid { Background = Brushes.Black };
		viewport.Children.Add(_media);
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
		_opened = true;
		_message.Visibility = Visibility.Collapsed;
		_seek.Maximum = _duration.TotalSeconds;
		SetControlsEnabled(true);
		_timer.Start();
		UpdatePosition();
		TogglePlayback();
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
		}
		else
		{
			if (_position >= _duration)
			{
				SeekTo(TimeSpan.Zero);
			}
			_media.Play();
			_playing = true;
			_playButton.Content = "一時停止";
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
		_playButton.Content = "再生";
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

	private void SeekTo(TimeSpan position)
	{
		_position = ClipboardVideoTimeline.ClampPosition(position, _duration);
		_media.Position = _position;
		UpdatePosition();
	}

	private void BeginSeek(object sender, MouseButtonEventArgs e)
	{
		if (!_opened || _disposed || _dragging || !_seek.CaptureMouse())
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
			Margin = new Thickness(0, 0, 6, 0),
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
		_media.Close();
		_media.Source = null;
	}
}
