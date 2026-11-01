using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clipboard;

internal sealed class ClipboardVideoFrameStrip : Grid, IDisposable
{
	private readonly string _path;
	private readonly Action<TimeSpan> _seek;
	private readonly StackPanel _frames;
	private readonly TextBlock _message;
	private readonly DispatcherTimer _refreshTimer;
	private readonly List<FrameTile> _tiles = new();
	private readonly Dictionary<long, BitmapSource> _cache = new();
	private readonly Queue<long> _cacheOrder = new();
	private IReadOnlyDictionary<long, BitmapSource> _editedFrames = new Dictionary<long, BitmapSource>();
	private ClipboardVideoFrameReader? _reader;
	private CancellationTokenSource? _cancellation;
	private TimeSpan?[] _positions = [];
	private TimeSpan[] _readingPositions = [];
	private TimeSpan _position;
	private TimeSpan _duration;
	private double _frameRate;
	private int _generation;
	private bool _loading;
	private bool _failed;
	private bool _disposed;

	public ClipboardVideoFrameStrip(string path, Action<TimeSpan> seek)
	{
		_path = path;
		_seek = seek;
		Height = 124;
		Margin = new Thickness(0, 0, 0, 6);
		ClipToBounds = true;
		Visibility = Visibility.Collapsed;
		_frames = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
		Children.Add(_frames);
		_message = new TextBlock
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(6),
			Visibility = Visibility.Collapsed
		};
		_message.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.MutedTextBrushKey);
		Children.Add(_message);
		_refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
		_refreshTimer.Tick += (_, _) => RefreshFrames();
		SizeChanged += (_, _) => UpdatePosition(_position, _duration, _frameRate);
	}

	public void SetEditedFrames(IReadOnlyDictionary<long, BitmapSource> frames)
	{
		if (_disposed) return;
		_editedFrames = frames;
		PaintFrames();
	}

	public void UpdatePosition(TimeSpan position, TimeSpan duration, double frameRate)
	{
		if (_disposed) return;
		_position = position;
		_duration = duration;
		_frameRate = frameRate;
		Visibility = frameRate > 0 && duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
		if (_failed) return;
		TimeSpan?[] positions = ClipboardVideoTimeline.PreviewPositions(position, duration, frameRate, ActualWidth);
		if (_positions.SequenceEqual(positions)) return;
		_positions = positions;
		_generation++;
		// 大きくシークした場合は古い位置の読み込みを中止する。
		if (_loading && !positions.Any(value => value.HasValue && _readingPositions.Contains(value.Value)))
			_cancellation?.Cancel();
		PaintFrames();
		ScheduleRefresh();
	}

	private void PaintFrames()
	{
		if (_tiles.Count != _positions.Length)
		{
			_tiles.Clear();
			_frames.Children.Clear();
			for (int index = 0; index < _positions.Length; index++)
			{
				var image = new Image { Height = 76, Stretch = Stretch.Uniform };
				var imageHost = new Grid { Height = 76, Width = ClipboardVideoTimeline.PreviewFrameWidth - 24 };
				imageHost.Children.Add(image);
				var badge = ImageEditBadgeFactory.Create(VerticalAlignment.Top);
				imageHost.Children.Add(badge);
				var time = new TextBlock { FontSize = 12, TextAlignment = TextAlignment.Center };
				time.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.MutedTextBrushKey);
				var content = new StackPanel();
				content.Children.Add(imageHost);
				content.Children.Add(time);
				var button = new Button
				{
					Width = ClipboardVideoTimeline.PreviewFrameWidth - 8,
					Height = 120,
					Margin = new Thickness(4, 0, 4, 0),
					Padding = new Thickness(2),
					Focusable = false,
					Content = content
				};
				AppTheme.ApplyButton(button);
				bool center = index == _positions.Length / 2;
				button.BorderThickness = new Thickness(center ? 4 : 2);
				button.SetResourceReference(Control.BorderBrushProperty, center ? AppTheme.AccentBorderBrushKey : AppTheme.BorderBrushKey);
				button.SetResourceReference(Control.BackgroundProperty, center ? AppTheme.SurfaceSelectedBrushKey : AppTheme.ThumbnailBackgroundBrushKey);
				button.Click += (_, _) =>
				{
					if (button.Tag is TimeSpan target) _seek(target);
				};
				_tiles.Add(new FrameTile(button, image, time, badge));
				_frames.Children.Add(button);
			}
		}
		for (int index = 0; index < _positions.Length; index++)
		{
			FrameTile tile = _tiles[index];
			TimeSpan? position = _positions[index];
			tile.Button.Tag = position;
			tile.Button.IsEnabled = position.HasValue;
			tile.Button.Opacity = position.HasValue ? 1 : 0;
			BitmapSource? edited = null;
			bool isEdited = position.HasValue && _editedFrames.TryGetValue(position.Value.Ticks, out edited);
			tile.Image.Source = isEdited ? edited
				: position.HasValue && _cache.TryGetValue(position.Value.Ticks, out BitmapSource? image) ? image : null;
			tile.EditBadge.Visibility = isEdited ? Visibility.Visible : Visibility.Collapsed;
			tile.Time.Text = position.HasValue ? FormatTime(position.Value) : "";
			tile.Button.ToolTip = position.HasValue ? $"{FormatTime(position.Value)}（クリックして移動）" : null;
		}
	}

	private void ScheduleRefresh()
	{
		if (!_loading && !_disposed && !_failed && _positions.Any(value => value.HasValue && !_cache.ContainsKey(value.Value.Ticks)))
			_refreshTimer.Start();
	}

	private async void RefreshFrames()
	{
		_refreshTimer.Stop();
		if (_disposed || _failed || _loading) return;
		TimeSpan[] positions = _positions.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
		if (positions.Length == 0) return;
		_loading = true;
		_readingPositions = positions;
		int generation = _generation;
		using var cancellation = new CancellationTokenSource();
		_cancellation = cancellation;
		try
		{
			_reader ??= new ClipboardVideoFrameReader(_path);
			IReadOnlyDictionary<long, BitmapSource> images = await _reader.ReadAsync(positions, _frameRate, cancellation.Token);
			if (_disposed) return;
			foreach (var (ticks, image) in images)
			{
				if (_cache.TryAdd(ticks, image)) _cacheOrder.Enqueue(ticks);
			}
			while (_cacheOrder.Count > 192) _cache.Remove(_cacheOrder.Dequeue());
			PaintFrames();
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			if (_disposed) return;
			_failed = true;
			_frames.Visibility = Visibility.Collapsed;
			_message.Text = "コマ画像を取得できません（通常の再生・シークは使用できます）";
			_message.Visibility = Visibility.Visible;
			_reader?.Dispose();
			_reader = null;
			Logger.Error(ex, $"ClipboardVideoFrameStrip: コマ画像を取得できませんでした。Path={_path}");
		}
		finally
		{
			_loading = false;
			_cancellation = null;
			if (generation != _generation) ScheduleRefresh();
		}
	}

	private static string FormatTime(TimeSpan time) => $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_refreshTimer.Stop();
		_cancellation?.Cancel();
		_reader?.Dispose();
		_reader = null;
		_cache.Clear();
		foreach (FrameTile tile in _tiles) tile.Image.Source = null;
	}

	private sealed record FrameTile(Button Button, Image Image, TextBlock Time, Border EditBadge);
}
