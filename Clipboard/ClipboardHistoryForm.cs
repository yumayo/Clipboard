using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Clipboard;

internal sealed class ClipboardHistoryForm : Form
{
	private const int MaxHistoryItems = 100;
	private const int PreviewTextLength = 180;
	private readonly FlowLayoutPanel _listPanel;
	private CancellationTokenSource? _loadHistoryCancellation;
	private IntPtr _targetWindow;
	private bool _isLoadingHistory;
	private bool _hasLoadedHistory;

	public ClipboardHistoryForm()
	{
		Text = "クリップボード履歴";
		StartPosition = FormStartPosition.Manual;
		Size = new Size(520, 640);
		MinimumSize = new Size(380, 320);
		MaximizeBox = false;
		MinimizeBox = false;
		ShowInTaskbar = false;
		KeyPreview = true;

		if (File.Exists("Clipboard.ico"))
		{
			Icon = new Icon("Clipboard.ico");
		}

		_listPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			AutoScroll = true,
			FlowDirection = FlowDirection.TopDown,
			WrapContents = false,
			Padding = new Padding(10),
			BackColor = Color.FromArgb(245, 246, 248)
		};
		_listPanel.Resize += (_, _) => ResizeHistoryItems();

		Controls.Add(_listPanel);
	}

	public void ShowHistory(IntPtr targetWindow)
	{
		_targetWindow = targetWindow;
		MoveNearTextInput(targetWindow);

		TopMost = true;
		Show();
		Activate();
		BringToFront();
		TopMost = false;
		StopWindowFlash();
		BeginInvoke((Action)StopWindowFlash);

		if (_isLoadingHistory && _loadHistoryCancellation?.IsCancellationRequested == true)
		{
			_isLoadingHistory = false;
		}

		if (!_isLoadingHistory)
		{
			BeginLoadHistory(_hasLoadedHistory && _listPanel.Controls.Count > 0);
		}
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		if (e.KeyCode == Keys.Escape)
		{
			Hide();
			e.Handled = true;
			return;
		}

		base.OnKeyDown(e);
	}

	protected override void OnFormClosing(FormClosingEventArgs e)
	{
		if (e.CloseReason == CloseReason.UserClosing)
		{
			e.Cancel = true;
			Hide();
			return;
		}

		base.OnFormClosing(e);
	}

	protected override void OnVisibleChanged(EventArgs e)
	{
		base.OnVisibleChanged(e);

		if (!Visible)
		{
			CancelHistoryLoad();
		}
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			CancelHistoryLoad();
			_loadHistoryCancellation?.Dispose();
		}

		base.Dispose(disposing);
	}

	private async void BeginLoadHistory(bool preserveExistingItems)
	{
		CancelHistoryLoad();
		var cancellationTokenSource = new CancellationTokenSource();
		_loadHistoryCancellation = cancellationTokenSource;
		_isLoadingHistory = true;

		if (!preserveExistingItems)
		{
			ClearHistoryControls();
			AddMessage("読み込み中...");
		}

		List<ClipboardHistoryEntry> entries;
		try
		{
			entries = await Task.Run(() => LoadHistoryEntries(cancellationTokenSource.Token), cancellationTokenSource.Token);
		}
		catch (OperationCanceledException)
		{
			if (_loadHistoryCancellation == cancellationTokenSource)
			{
				_isLoadingHistory = false;
			}

			return;
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardHistoryForm: 履歴の読み込みに失敗しました。");
			if (!IsDisposed && _loadHistoryCancellation == cancellationTokenSource)
			{
				_isLoadingHistory = false;
				if (!preserveExistingItems)
				{
					ClearHistoryControls();
					AddMessage("履歴を読み込めませんでした");
				}
			}

			return;
		}

		if (IsDisposed || cancellationTokenSource.IsCancellationRequested || _loadHistoryCancellation != cancellationTokenSource)
		{
			if (_loadHistoryCancellation == cancellationTokenSource)
			{
				_isLoadingHistory = false;
			}

			DisposeEntryImages(entries);
			return;
		}

		_isLoadingHistory = false;
		_hasLoadedHistory = true;
		PopulateHistory(entries);
	}

	private void PopulateHistory(List<ClipboardHistoryEntry> entries)
	{
		_listPanel.SuspendLayout();
		try
		{
			ClearHistoryControls();
			if (entries.Count == 0)
			{
				AddMessage("履歴がありません");
				return;
			}

			foreach (var entry in entries)
			{
				var item = new HistoryItemControl(entry)
				{
					Width = GetItemWidth()
				};
				item.Activated += (_, _) => PasteEntry(entry);
				_listPanel.Controls.Add(item);
			}
		}
		finally
		{
			_listPanel.ResumeLayout();
		}
	}

	private void PasteEntry(ClipboardHistoryEntry entry)
	{
		Hide();
		ClipboardManager.PasteHistoryFile(entry.FilePath, _targetWindow);
	}

	private void AddMessage(string text)
	{
		_listPanel.Controls.Add(new Label
		{
			Text = text,
			AutoSize = false,
			TextAlign = ContentAlignment.MiddleCenter,
			ForeColor = Color.FromArgb(96, 96, 96),
			Width = GetItemWidth(),
			Height = 80
		});
	}

	private void ClearHistoryControls()
	{
		foreach (Control control in _listPanel.Controls.Cast<Control>().ToArray())
		{
			control.Dispose();
		}

		_listPanel.Controls.Clear();
	}

	private void CancelHistoryLoad()
	{
		if (_loadHistoryCancellation is { IsCancellationRequested: false })
		{
			_loadHistoryCancellation.Cancel();
		}
	}

	private void ResizeHistoryItems()
	{
		int width = GetItemWidth();
		foreach (Control control in _listPanel.Controls)
		{
			control.Width = width;
		}
	}

	private int GetItemWidth()
	{
		int scrollbarWidth = _listPanel.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
		return Math.Max(260, _listPanel.ClientSize.Width - _listPanel.Padding.Horizontal - scrollbarWidth - 4);
	}

	private void MoveNearTextInput(IntPtr targetWindow)
	{
		bool hasTextInputBounds = TryGetTextInputBounds(targetWindow, out var textInputBounds);
		Rectangle anchorBounds;
		if (hasTextInputBounds)
		{
			anchorBounds = textInputBounds;
			Logger.Debug($"ClipboardHistoryForm: テキスト入力座標を取得しました。TargetWindow={FormatHandle(targetWindow)} Bounds={anchorBounds}");
		}
		else
		{
			anchorBounds = new Rectangle(Cursor.Position, Size.Empty);
			Logger.Debug($"ClipboardHistoryForm: テキスト入力座標を取得できなかったためマウス座標にフォールバックします。TargetWindow={FormatHandle(targetWindow)} Cursor={Cursor.Position}");
		}

		Rectangle workingArea = Screen.FromPoint(anchorBounds.Location).WorkingArea;
		int x = anchorBounds.Left;
		int y = anchorBounds.Bottom + 8;

		x = Math.Max(workingArea.Left, Math.Min(x, workingArea.Right - Width));
		if (y + Height > workingArea.Bottom && anchorBounds.Top - Height - 8 >= workingArea.Top)
		{
			y = anchorBounds.Top - Height - 8;
		}

		y = Math.Max(workingArea.Top, Math.Min(y, workingArea.Bottom - Height));
		Location = new Point(x, y);
		Logger.Debug($"ClipboardHistoryForm: 履歴画面の表示位置を決定しました。Anchor={anchorBounds} WorkingArea={workingArea} Location={Location} Size={Size}");
	}

	private static bool TryGetTextInputBounds(IntPtr targetWindow, out Rectangle bounds)
	{
		bounds = Rectangle.Empty;
		if (targetWindow == IntPtr.Zero || !NativeMethods.IsWindow(targetWindow))
		{
			Logger.Debug($"ClipboardHistoryForm: テキスト入力座標の取得に失敗しました。対象ウィンドウが無効です。TargetWindow={FormatHandle(targetWindow)}");
			return false;
		}

		uint threadId = NativeMethods.GetWindowThreadProcessId(targetWindow, out _);
		if (threadId == 0)
		{
			Logger.Debug($"ClipboardHistoryForm: テキスト入力座標の取得に失敗しました。GetWindowThreadProcessId が失敗しました。TargetWindow={FormatHandle(targetWindow)} Win32Error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
			return false;
		}

		var guiThreadInfo = new NativeMethods.GuiThreadInfo
		{
			CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
		};
		if (!NativeMethods.GetGUIThreadInfo(threadId, ref guiThreadInfo))
		{
			Logger.Debug($"ClipboardHistoryForm: テキスト入力座標の取得に失敗しました。GetGUIThreadInfo が失敗しました。TargetWindow={FormatHandle(targetWindow)} ThreadId={threadId} Win32Error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
			return false;
		}

		Logger.Debug($"ClipboardHistoryForm: GUI thread 情報を取得しました。TargetWindow={FormatHandle(targetWindow)} ThreadId={threadId} Active={FormatHandle(guiThreadInfo.HWndActive)} Focus={FormatHandle(guiThreadInfo.HWndFocus)} Caret={FormatHandle(guiThreadInfo.HWndCaret)} CaretRect=({guiThreadInfo.RcCaret.Left},{guiThreadInfo.RcCaret.Top})-({guiThreadInfo.RcCaret.Right},{guiThreadInfo.RcCaret.Bottom})");
		if (guiThreadInfo.HWndCaret == IntPtr.Zero)
		{
			Logger.Debug($"ClipboardHistoryForm: テキスト入力座標の取得に失敗しました。キャレット HWND が 0 です。TargetWindow={FormatHandle(targetWindow)} ThreadId={threadId} Focus={FormatHandle(guiThreadInfo.HWndFocus)}");
			return false;
		}

		var topLeft = new NativeMethods.NativePoint
		{
			X = guiThreadInfo.RcCaret.Left,
			Y = guiThreadInfo.RcCaret.Top
		};
		var bottomRight = new NativeMethods.NativePoint
		{
			X = guiThreadInfo.RcCaret.Right,
			Y = guiThreadInfo.RcCaret.Bottom
		};

		if (!NativeMethods.ClientToScreen(guiThreadInfo.HWndCaret, ref topLeft))
		{
			Logger.Debug($"ClipboardHistoryForm: テキスト入力座標の取得に失敗しました。ClientToScreen(topLeft) が失敗しました。Caret={FormatHandle(guiThreadInfo.HWndCaret)} Win32Error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
			return false;
		}

		if (!NativeMethods.ClientToScreen(guiThreadInfo.HWndCaret, ref bottomRight))
		{
			Logger.Debug($"ClipboardHistoryForm: テキスト入力座標の取得に失敗しました。ClientToScreen(bottomRight) が失敗しました。Caret={FormatHandle(guiThreadInfo.HWndCaret)} Win32Error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
			return false;
		}

		bounds = Rectangle.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
		if (bounds.IsEmpty)
		{
			Logger.Debug($"ClipboardHistoryForm: テキスト入力座標の取得に失敗しました。画面座標変換後のキャレット矩形が空です。Caret={FormatHandle(guiThreadInfo.HWndCaret)} Bounds={bounds}");
			return false;
		}

		return true;
	}

	private static string FormatHandle(IntPtr handle)
	{
		return $"0x{handle.ToInt64():X}";
	}

	private void StopWindowFlash()
	{
		if (!IsHandleCreated)
		{
			return;
		}

		var flashWindowInfo = new NativeMethods.FlashWindowInfo
		{
			CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FlashWindowInfo>(),
			HWnd = Handle,
			DwFlags = NativeMethods.FLASHW_STOP
		};
		NativeMethods.FlashWindowEx(ref flashWindowInfo);
	}

	private static List<ClipboardHistoryEntry> LoadHistoryEntries(CancellationToken cancellationToken)
	{
		try
		{
			if (!Directory.Exists(ClipboardSettings.BaseDirectoryPath))
			{
				return new List<ClipboardHistoryEntry>();
			}

			var candidates = LoadHistoryCandidates(cancellationToken);

			var entries = new List<ClipboardHistoryEntry>(candidates.Count);
			foreach (var candidate in candidates)
			{
				cancellationToken.ThrowIfCancellationRequested();
				entries.Add(CreateHistoryEntry(candidate));
			}

			return entries;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardHistoryForm: 履歴の読み込みに失敗しました。");
			return new List<ClipboardHistoryEntry>();
		}
	}

	private static List<ClipboardHistoryCandidate> LoadHistoryCandidates(CancellationToken cancellationToken)
	{
		var baseDirectory = new DirectoryInfo(ClipboardSettings.BaseDirectoryPath);
		var candidates = new List<ClipboardHistoryCandidate>(MaxHistoryItems);

		AddDirectoryCandidates(baseDirectory, SearchOption.TopDirectoryOnly, candidates, cancellationToken);

		var datedDirectories = baseDirectory
			.EnumerateDirectories()
			.Select(directory => new
			{
				Directory = directory,
				Date = TryGetHistoryDirectoryDate(directory.Name, out var date) ? date : (DateTime?)null
			})
			.Where(item => item.Date.HasValue)
			.OrderByDescending(item => item.Date.GetValueOrDefault())
			.ToList();

		var datedDirectoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in datedDirectories)
		{
			cancellationToken.ThrowIfCancellationRequested();
			datedDirectoryPaths.Add(item.Directory.FullName);
			AddDirectoryCandidates(item.Directory, SearchOption.TopDirectoryOnly, candidates, cancellationToken);
			if (candidates.Count >= MaxHistoryItems)
			{
				break;
			}
		}

		if (candidates.Count < MaxHistoryItems)
		{
			var fallbackDirectories = baseDirectory
				.EnumerateDirectories()
				.Where(directory => !datedDirectoryPaths.Contains(directory.FullName))
				.OrderByDescending(directory => directory.LastWriteTime);

			foreach (var directory in fallbackDirectories)
			{
				cancellationToken.ThrowIfCancellationRequested();
				AddDirectoryCandidates(directory, SearchOption.AllDirectories, candidates, cancellationToken);
				if (candidates.Count >= MaxHistoryItems)
				{
					break;
				}
			}
		}

		return candidates
			.OrderByDescending(candidate => candidate.LastWriteTime)
			.ThenByDescending(candidate => candidate.FilePath)
			.Take(MaxHistoryItems)
			.ToList();
	}

	private static void AddDirectoryCandidates(
		DirectoryInfo directory,
		SearchOption searchOption,
		List<ClipboardHistoryCandidate> candidates,
		CancellationToken cancellationToken)
	{
		foreach (var fileInfo in directory.EnumerateFiles("*.*", searchOption))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var candidate = CreateHistoryCandidate(fileInfo, cancellationToken);
			if (candidate != null)
			{
				candidates.Add(candidate);
			}
		}
	}

	private static bool TryGetHistoryDirectoryDate(string directoryName, out DateTime date)
	{
		return DateTime.TryParseExact(
			directoryName,
			"yyyyMMdd",
			CultureInfo.InvariantCulture,
			DateTimeStyles.None,
			out date);
	}

	private static ClipboardHistoryEntry CreateHistoryEntry(ClipboardHistoryCandidate candidate)
	{
		string previewText;
		Image? thumbnail = null;

		if (candidate.Kind == ClipboardHistoryKind.Image)
		{
			(previewText, thumbnail) = CreateImagePreview(candidate.FilePath);
		}
		else
		{
			previewText = CreatePreviewText(candidate.FilePath, candidate.Kind);
		}

		return new ClipboardHistoryEntry
		{
			FilePath = candidate.FilePath,
			Kind = candidate.Kind,
			LastWriteTime = candidate.LastWriteTime,
			PreviewText = previewText,
			Thumbnail = thumbnail
		};
	}

	private static ClipboardHistoryCandidate? CreateHistoryCandidate(FileInfo fileInfo, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		string extension = fileInfo.Extension.ToLowerInvariant();
		ClipboardHistoryKind kind = extension switch
		{
			".png" => ClipboardHistoryKind.Image,
			".html" => ClipboardHistoryKind.Html,
			".rtf" => ClipboardHistoryKind.Rtf,
			".txt" => ClipboardHistoryKind.Text,
			_ => ClipboardHistoryKind.Unknown
		};

		if (kind == ClipboardHistoryKind.Unknown)
		{
			return null;
		}

		return new ClipboardHistoryCandidate
		{
			FilePath = fileInfo.FullName,
			Kind = kind,
			LastWriteTime = fileInfo.LastWriteTime
		};
	}

	private static void DisposeEntryImages(IEnumerable<ClipboardHistoryEntry> entries)
	{
		foreach (var entry in entries)
		{
			entry.Thumbnail?.Dispose();
		}
	}

	private static string CreatePreviewText(string filePath, ClipboardHistoryKind kind)
	{
		try
		{
			string text = ReadTextStart(filePath, 4096);
			if (kind == ClipboardHistoryKind.Html)
			{
				text = ConvertHtmlToPlainText(text);
			}
			else if (kind == ClipboardHistoryKind.Rtf)
			{
				text = ConvertRtfToPlainText(text);
			}

			text = NormalizePreviewText(text);
			if (text.Length > PreviewTextLength)
			{
				text = text[..PreviewTextLength] + "...";
			}

			return string.IsNullOrWhiteSpace(text) ? Path.GetFileName(filePath) : text;
		}
		catch
		{
			return Path.GetFileName(filePath);
		}
	}

	private static string ReadTextStart(string filePath, int maxChars)
	{
		using var reader = new StreamReader(filePath, Encoding.UTF8, true);
		char[] buffer = new char[maxChars];
		int read = reader.ReadBlock(buffer, 0, buffer.Length);
		return new string(buffer, 0, read);
	}

	private static string NormalizePreviewText(string text)
	{
		return Regex.Replace(text, @"\s+", " ").Trim();
	}

	private static string ConvertHtmlToPlainText(string html)
	{
		string fragment = html;
		const string startMarker = "<!--StartFragment-->";
		const string endMarker = "<!--EndFragment-->";
		int start = html.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
		int end = html.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
		if (start >= 0 && end > start)
		{
			start += startMarker.Length;
			fragment = html[start..end];
		}

		string noTags = Regex.Replace(fragment, "<[^>]+>", " ");
		return WebUtility.HtmlDecode(noTags);
	}

	private static string ConvertRtfToPlainText(string rtf)
	{
		string text = Regex.Replace(rtf, @"\\'[0-9a-fA-F]{2}", " ");
		text = Regex.Replace(text, @"\\[a-zA-Z]+\d* ?", " ");
		text = text.Replace(@"\par", " ");
		text = text.Replace(@"\tab", " ");
		text = Regex.Replace(text, @"[{}]", " ");
		return text;
	}

	private static (string PreviewText, Image? Thumbnail) CreateImagePreview(string filePath)
	{
		try
		{
			using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var image = Image.FromStream(stream);
			var thumbnail = new Bitmap(86, 66);
			using var graphics = Graphics.FromImage(thumbnail);
			graphics.Clear(Color.FromArgb(238, 238, 238));
			graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

			Rectangle bounds = GetContainBounds(image.Size, thumbnail.Size);
			graphics.DrawImage(image, bounds);
			return ($"{Path.GetFileName(filePath)} / {image.Width} x {image.Height}", thumbnail);
		}
		catch
		{
			return (Path.GetFileName(filePath), null);
		}
	}

	private static Rectangle GetContainBounds(Size sourceSize, Size targetSize)
	{
		double scale = Math.Min(
			(double)targetSize.Width / sourceSize.Width,
			(double)targetSize.Height / sourceSize.Height);
		int width = Math.Max(1, (int)Math.Round(sourceSize.Width * scale));
		int height = Math.Max(1, (int)Math.Round(sourceSize.Height * scale));
		int x = (targetSize.Width - width) / 2;
		int y = (targetSize.Height - height) / 2;
		return new Rectangle(x, y, width, height);
	}

	private sealed class ClipboardHistoryCandidate
	{
		public required string FilePath { get; init; }
		public required ClipboardHistoryKind Kind { get; init; }
		public required DateTime LastWriteTime { get; init; }
	}

	private sealed class ClipboardHistoryEntry
	{
		public required string FilePath { get; init; }
		public required ClipboardHistoryKind Kind { get; init; }
		public required DateTime LastWriteTime { get; init; }
		public required string PreviewText { get; init; }
		public Image? Thumbnail { get; init; }
	}

	private enum ClipboardHistoryKind
	{
		Image,
		Html,
		Rtf,
		Text,
		Unknown
	}

	private sealed class HistoryItemControl : UserControl
	{
		private readonly Label _kindLabel;
		private readonly Label _dateLabel;
		private readonly Label _previewLabel;
		private readonly PictureBox? _thumbnailBox;

		public event EventHandler? Activated;

		public HistoryItemControl(ClipboardHistoryEntry entry)
		{
			Margin = new Padding(0, 0, 0, 8);
			Padding = new Padding(10);
			BackColor = Color.White;
			BorderStyle = BorderStyle.FixedSingle;
			Cursor = Cursors.Hand;

			_kindLabel = new Label
			{
				AutoSize = false,
				Font = new Font(Font, FontStyle.Bold),
				ForeColor = Color.FromArgb(32, 32, 32)
			};

			_dateLabel = new Label
			{
				AutoSize = false,
				Text = entry.LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss"),
				TextAlign = ContentAlignment.MiddleRight,
				ForeColor = Color.FromArgb(112, 112, 112)
			};

			_previewLabel = new Label
			{
				AutoSize = false,
				Text = entry.PreviewText,
				ForeColor = Color.FromArgb(48, 48, 48)
			};

			if (entry.Kind == ClipboardHistoryKind.Image)
			{
				_thumbnailBox = new PictureBox
				{
					SizeMode = PictureBoxSizeMode.Zoom,
					BackColor = Color.FromArgb(238, 238, 238),
					Image = entry.Thumbnail
				};
				Controls.Add(_thumbnailBox);
			}

			_kindLabel.Text = entry.Kind switch
			{
				ClipboardHistoryKind.Image => "画像",
				ClipboardHistoryKind.Html => "HTML",
				ClipboardHistoryKind.Rtf => "RTF",
				_ => "文字列"
			};

			Controls.Add(_kindLabel);
			Controls.Add(_dateLabel);
			Controls.Add(_previewLabel);

			Height = entry.Kind == ClipboardHistoryKind.Image ? 92 : 78;
			WireMouseEvents(this);
			LayoutChildren();
		}

		protected override void OnResize(EventArgs e)
		{
			base.OnResize(e);
			LayoutChildren();
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				_thumbnailBox?.Image?.Dispose();
			}

			base.Dispose(disposing);
		}

		private void LayoutChildren()
		{
			if (_kindLabel == null || _dateLabel == null || _previewLabel == null)
			{
				return;
			}

			int x = Padding.Left;
			if (_thumbnailBox != null)
			{
				_thumbnailBox.Bounds = new Rectangle(Padding.Left, Padding.Top, 86, 66);
				x = _thumbnailBox.Right + 10;
			}

			int contentWidth = Math.Max(40, ClientSize.Width - x - Padding.Right);
			_kindLabel.Bounds = new Rectangle(x, Padding.Top, 80, 22);
			_dateLabel.Bounds = new Rectangle(x + 84, Padding.Top, Math.Max(40, contentWidth - 84), 22);
			_previewLabel.Bounds = new Rectangle(x, Padding.Top + 28, contentWidth, ClientSize.Height - Padding.Vertical - 28);
		}

		private void WireMouseEvents(Control control)
		{
			control.Click += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
			control.MouseEnter += (_, _) => BackColor = Color.FromArgb(232, 240, 254);
			control.MouseLeave += (_, _) => BackColor = Color.White;

			foreach (Control child in control.Controls)
			{
				WireMouseEvents(child);
			}
		}

	}
}
