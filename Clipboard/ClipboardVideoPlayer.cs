using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
	private readonly TextBox _frameRateBox;
	private readonly string _path;
	private TimeSpan _duration;
	private TimeSpan _position;
	private double _frameRate = 30;
	private bool _opened;
	private bool _playing;
	private bool _updatingPosition;
	private bool _dragging;
	private bool _resumeAfterSeek;
	private bool _disposed;
	private bool _frameRateEdited;

	public ClipboardVideoPlayer(string path)
	{
		_path = path;
		Focusable = true;
		var panel = new DockPanel();
		var footer = new StackPanel { Margin = new Thickness(10) };
		DockPanel.SetDock(footer, Dock.Bottom);
		panel.Children.Add(footer);

		var name = new TextBlock
		{
			Text = Path.GetFileName(path),
			ToolTip = path,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(0, 0, 0, 6)
		};
		name.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);
		footer.Children.Add(name);

		_seek = new Slider { Minimum = 0, Maximum = 1, IsEnabled = false, Focusable = false };
		_seek.ValueChanged += (_, _) =>
		{
			if (!_updatingPosition && _opened)
			{
				SeekTo(TimeSpan.FromSeconds(_seek.Value));
			}
		};
		_seek.PreviewMouseLeftButtonDown += (_, _) => BeginSeek();
		_seek.PreviewMouseLeftButtonUp += (_, _) => EndSeek();
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

		var frameControls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
		var fpsLabel = new TextBlock { Text = "コマ送りのFPS", VerticalAlignment = VerticalAlignment.Center };
		fpsLabel.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.MutedTextBrushKey);
		frameControls.Children.Add(fpsLabel);
		_frameRateBox = new TextBox
		{
			Text = "30",
			Width = 70,
			Margin = new Thickness(8, 0, 0, 0),
			ToolTip = "動画のFPSを自動取得します。取得できない場合は30fpsを使用します。必要に応じて変更できます。"
		};
		AppTheme.ApplyTextBox(_frameRateBox);
		_frameRateBox.TextChanged += (_, _) => _frameRateEdited = true;
		_frameRateBox.LostKeyboardFocus += (_, _) => CommitFrameRate();
		frameControls.Children.Add(_frameRateBox);
		footer.Children.Add(frameControls);

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
		if (_frameRateBox.IsKeyboardFocusWithin)
		{
			if (e.Key == Key.Enter)
			{
				CommitFrameRate();
				Focus();
				return true;
			}
			return false;
		}

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

		try
		{
			double? frameRate = await Task.Run(() => ClipboardVideoMetadata.ReadFrameRate(_path));
			if (!_disposed && frameRate.HasValue && !_frameRateEdited && !_frameRateBox.IsKeyboardFocusWithin)
			{
				_frameRate = ClipboardVideoTimeline.NormalizeFrameRate(frameRate.Value);
				_frameRateBox.Text = _frameRate.ToString("0.###", CultureInfo.InvariantCulture);
			}
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardVideoPlayer: 動画の情報を取得できませんでした。");
		}
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
		if (!_opened || _disposed)
		{
			return;
		}
		Pause();
		CommitFrameRate();
		SeekTo(ClipboardVideoTimeline.Step(_position, _duration, _frameRate, direction));
	}

	private void SeekTo(TimeSpan position)
	{
		_position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, _duration.Ticks));
		_media.Position = _position;
		UpdatePosition();
	}

	private void BeginSeek()
	{
		if (!_opened || _dragging)
		{
			return;
		}
		_dragging = true;
		_resumeAfterSeek = _playing;
		Pause();
	}

	private void EndSeek()
	{
		if (!_dragging)
		{
			return;
		}
		_dragging = false;
		if (_resumeAfterSeek && _opened && !_disposed)
		{
			TogglePlayback();
		}
		_resumeAfterSeek = false;
	}

	private void CommitFrameRate()
	{
		if (double.TryParse(_frameRateBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps) &&
			double.IsFinite(fps) && fps > 0 && fps <= 1000)
		{
			_frameRate = ClipboardVideoTimeline.NormalizeFrameRate(fps);
		}
		_frameRateBox.Text = _frameRate.ToString("0.###", CultureInfo.InvariantCulture);
	}

	private void UpdatePosition()
	{
		_updatingPosition = true;
		_seek.Value = _position.TotalSeconds;
		_updatingPosition = false;
		_time.Text = $"{FormatTime(_position)} / {FormatTime(_duration)}";
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
		_timer.Stop();
		_media.Close();
		SetControlsEnabled(false);
		_message.Text = message;
		_message.Visibility = Visibility.Visible;
		Logger.Error(exception, $"ClipboardVideoPlayer: {message} Path={_path}");
	}

	private void SetControlsEnabled(bool enabled)
	{
		_playButton.IsEnabled = enabled;
		_previousButton.IsEnabled = enabled;
		_nextButton.IsEnabled = enabled;
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
		_playing = false;
		_timer.Stop();
		_media.Close();
		_media.Source = null;
	}
}
