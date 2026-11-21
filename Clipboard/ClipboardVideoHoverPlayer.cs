using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Clipboard;

/// <summary>
/// 履歴一覧・ギャラリーでマウスオーバー中に動画を再生する軽量なプレビュー。
/// 再生していない間はサムネイルだけを表示し、MediaElementとファイルを保持しない。
/// </summary>
internal sealed class ClipboardVideoHoverPlayer : Grid, IDisposable
{
	private readonly string? _path;
	private readonly Image _poster;
	private readonly Grid _mediaHost;
	private readonly Border _seekHost;
	private readonly Slider _seek;
	private readonly TextBlock _time;
	private readonly TextBlock _message;
	private readonly DispatcherTimer _timer;
	private readonly double _maxDisplayWidth;
	private readonly double _maxDisplayHeight;
	private MediaElement? _media;
	private TimeSpan _duration;
	private bool _active;
	private bool _opened;
	private bool _updatingPosition;
	private bool _dragging;
	private bool _disposed;
	private bool _startWhenLoaded;

	/// <param name="path">動画ファイルのパス。</param>
	/// <param name="poster">再生前に表示するサムネイル。</param>
	/// <param name="maxDisplayWidth">0より大きい場合は、この幅に収まるよう動画の縦横比で自身のサイズを決める。</param>
	/// <param name="maxDisplayHeight">0より大きい場合は、この高さに収まるよう動画の縦横比で自身のサイズを決める。</param>
	public ClipboardVideoHoverPlayer(string? path, ImageSource? poster, double maxDisplayWidth = 0, double maxDisplayHeight = 0)
	{
		_path = path;
		_maxDisplayWidth = maxDisplayWidth;
		_maxDisplayHeight = maxDisplayHeight;
		ClipToBounds = true;
		SnapsToDevicePixels = true;

		_poster = new Image
		{
			Source = poster,
			Stretch = Stretch.Uniform,
			SnapsToDevicePixels = true,
			IsHitTestVisible = false
		};
		RenderOptions.SetBitmapScalingMode(_poster, BitmapScalingMode.HighQuality);
		Children.Add(_poster);

		_mediaHost = new Grid { IsHitTestVisible = false };
		Children.Add(_mediaHost);

		_message = new TextBlock
		{
			Foreground = Brushes.White,
			Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
			Padding = new Thickness(6, 3, 6, 3),
			FontSize = 11,
			TextWrapping = TextWrapping.Wrap,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Visibility = Visibility.Collapsed,
			IsHitTestVisible = false
		};
		Children.Add(_message);

		_seek = new Slider
		{
			Minimum = 0,
			Maximum = 1,
			Focusable = false,
			VerticalAlignment = VerticalAlignment.Center,
			Cursor = Cursors.Hand,
			IsEnabled = false
		};
		_seek.ValueChanged += (_, _) =>
		{
			if (!_updatingPosition && _opened && _media != null)
			{
				_media.Position = TimeSpan.FromSeconds(_seek.Value);
				UpdateTimeText();
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
			}
			// シークバーの操作で履歴の貼り付けを発生させない。
			e.Handled = true;
		};
		_seek.LostMouseCapture += (_, _) => EndSeek();

		_time = new TextBlock
		{
			Foreground = Brushes.White,
			FontSize = 10,
			Margin = new Thickness(6, 0, 0, 0),
			VerticalAlignment = VerticalAlignment.Center,
			IsHitTestVisible = false
		};

		var seekGrid = new Grid();
		seekGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		seekGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		Grid.SetColumn(_seek, 0);
		Grid.SetColumn(_time, 1);
		seekGrid.Children.Add(_seek);
		seekGrid.Children.Add(_time);

		_seekHost = new Border
		{
			Child = seekGrid,
			Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
			Padding = new Thickness(6, 2, 6, 2),
			VerticalAlignment = VerticalAlignment.Bottom,
			Visibility = Visibility.Collapsed
		};
		// シークバー周辺のクリックも貼り付けとして扱わない。
		_seekHost.MouseLeftButtonUp += (_, e) => e.Handled = true;
		_seekHost.MouseLeftButtonDown += (_, e) => e.Handled = true;
		Children.Add(_seekHost);

		_timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => UpdatePosition(), Dispatcher);
		_timer.Stop();

		if (poster is { Width: > 0, Height: > 0 })
		{
			ApplyDisplaySize(poster.Width, poster.Height);
		}

		Loaded += (_, _) =>
		{
			if (_startWhenLoaded)
			{
				_startWhenLoaded = false;
				Start();
			}
		};
		Unloaded += (_, _) => Stop();
	}

	public bool IsActive => _active;

	/// <summary>先頭から再生を開始する。音声はミュートする。</summary>
	public void Start()
	{
		if (_disposed || _active)
		{
			return;
		}

		if (!IsLoaded)
		{
			// ポップアップなど表示前に呼ばれた場合は、表示されてから再生する。
			_startWhenLoaded = true;
			return;
		}

		_active = true;
		_seekHost.Visibility = Visibility.Visible;
		if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
		{
			ShowMessage("元ファイルが見つかりません");
			return;
		}

		ShowMessage("読み込み中…");
		try
		{
			_media = new MediaElement
			{
				LoadedBehavior = MediaState.Manual,
				UnloadedBehavior = MediaState.Manual,
				ScrubbingEnabled = true,
				IsMuted = true,
				Stretch = Stretch.Uniform,
				SnapsToDevicePixels = true,
				Focusable = false,
				IsHitTestVisible = false
			};
			_media.MediaOpened += OnMediaOpened;
			_media.MediaEnded += OnMediaEnded;
			_media.MediaFailed += OnMediaFailed;
			_mediaHost.Children.Add(_media);
			_media.Source = new Uri(_path, UriKind.Absolute);
			_media.Play();
		}
		catch (Exception ex)
		{
			Logger.Error(ex, $"ClipboardVideoHoverPlayer: 動画を開けませんでした。Path={_path}");
			CloseMedia();
			ShowMessage("再生できません");
		}
	}

	/// <summary>再生を止めてファイルを解放し、サムネイル表示に戻す。</summary>
	public void Stop()
	{
		_startWhenLoaded = false;
		if (!_active)
		{
			return;
		}

		_active = false;
		EndSeek();
		CloseMedia();
		_seekHost.Visibility = Visibility.Collapsed;
		_message.Visibility = Visibility.Collapsed;
		_poster.Visibility = Visibility.Visible;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		Stop();
		_disposed = true;
	}

	private void OnMediaOpened(object? sender, RoutedEventArgs e)
	{
		if (!_active || _media == null || !ReferenceEquals(sender, _media))
		{
			return;
		}

		if (!_media.HasVideo || !_media.NaturalDuration.HasTimeSpan || _media.NaturalDuration.TimeSpan <= TimeSpan.Zero)
		{
			CloseMedia();
			ShowMessage("再生できる映像がありません");
			return;
		}

		_duration = _media.NaturalDuration.TimeSpan;
		_opened = true;
		_seek.Maximum = _duration.TotalSeconds;
		_seek.IsEnabled = true;
		_message.Visibility = Visibility.Collapsed;
		_poster.Visibility = Visibility.Hidden;
		ApplyDisplaySize(_media.NaturalVideoWidth, _media.NaturalVideoHeight);
		UpdatePosition();
		_timer.Start();
	}

	private void OnMediaEnded(object? sender, RoutedEventArgs e)
	{
		if (!_active || _media == null || !ReferenceEquals(sender, _media))
		{
			return;
		}

		// マウスオーバー中は繰り返し再生する。
		_media.Position = TimeSpan.Zero;
		if (!_dragging)
		{
			_media.Play();
		}
		UpdatePosition();
	}

	private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
	{
		if (!ReferenceEquals(sender, _media))
		{
			return;
		}

		Logger.Error(e.ErrorException, $"ClipboardVideoHoverPlayer: 動画を再生できません。Path={_path}");
		CloseMedia();
		if (_active)
		{
			ShowMessage("再生できません");
		}
	}

	private void CloseMedia()
	{
		_timer.Stop();
		_opened = false;
		_seek.IsEnabled = false;
		_updatingPosition = true;
		_seek.Value = 0;
		_updatingPosition = false;
		_time.Text = string.Empty;
		if (_media == null)
		{
			return;
		}

		MediaElement media = _media;
		_media = null;
		media.MediaOpened -= OnMediaOpened;
		media.MediaEnded -= OnMediaEnded;
		media.MediaFailed -= OnMediaFailed;
		try
		{
			media.Close();
			media.Source = null;
		}
		catch (Exception ex)
		{
			Logger.Debug($"ClipboardVideoHoverPlayer: 動画を閉じられませんでした。Path={_path} Error={ex.Message}");
		}
		_mediaHost.Children.Remove(media);
	}

	private void ShowMessage(string text)
	{
		_message.Text = text;
		_message.Visibility = Visibility.Visible;
	}

	private void ApplyDisplaySize(double sourceWidth, double sourceHeight)
	{
		if (_maxDisplayWidth <= 0 || _maxDisplayHeight <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
		{
			return;
		}

		double scale = Math.Min(_maxDisplayWidth / sourceWidth, _maxDisplayHeight / sourceHeight);
		Width = Math.Max(1, Math.Round(sourceWidth * scale));
		Height = Math.Max(1, Math.Round(sourceHeight * scale));
	}

	private void UpdatePosition()
	{
		if (!_opened || _media == null || _dragging)
		{
			return;
		}

		TimeSpan position = ClipboardVideoTimeline.ClampPosition(_media.Position, _duration);
		_updatingPosition = true;
		_seek.Value = position.TotalSeconds;
		_updatingPosition = false;
		UpdateTimeText();
	}

	private void UpdateTimeText()
	{
		TimeSpan position = TimeSpan.FromSeconds(Math.Clamp(_seek.Value, 0, _duration.TotalSeconds));
		_time.Text = $"{FormatTime(position)} / {FormatTime(_duration)}";
	}

	private void BeginSeek(object sender, MouseButtonEventArgs e)
	{
		e.Handled = true;
		if (!_opened || _media == null || _dragging || !_seek.CaptureMouse())
		{
			return;
		}

		_dragging = true;
		_media.Pause();
		SeekFromMouse(e);
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

		if (_active && _opened && _media != null)
		{
			_media.Play();
		}
	}

	private static string FormatTime(TimeSpan time)
	{
		return time.TotalHours >= 1
			? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
			: $"{time.Minutes:00}:{time.Seconds:00}";
	}
}
