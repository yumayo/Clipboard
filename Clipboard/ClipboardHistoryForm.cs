using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Clipboard;

internal sealed class ClipboardHistoryForm : Form
{
	private const int MaxHistoryItems = 100;
	private const int PreviewTextLength = 180;
	private readonly FlowLayoutPanel _listPanel;
	private IntPtr _targetWindow;

	public ClipboardHistoryForm()
	{
		Text = "クリップボード履歴";
		StartPosition = FormStartPosition.Manual;
		Size = new Size(520, 640);
		MinimumSize = new Size(380, 320);
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
		LoadHistory();
		MoveNearCursor();

		TopMost = true;
		Show();
		Activate();
		BringToFront();
		TopMost = false;
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

	private void LoadHistory()
	{
		ClearHistoryControls();

		var entries = LoadHistoryEntries();
		if (entries.Count == 0)
		{
			AddEmptyMessage();
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

	private void PasteEntry(ClipboardHistoryEntry entry)
	{
		Hide();
		ClipboardManager.PasteHistoryFile(entry.FilePath, _targetWindow);
	}

	private void AddEmptyMessage()
	{
		_listPanel.Controls.Add(new Label
		{
			Text = "履歴がありません",
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

	private void MoveNearCursor()
	{
		Rectangle workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
		int x = Cursor.Position.X - Width / 2;
		int y = Cursor.Position.Y - 40;

		x = Math.Max(workingArea.Left, Math.Min(x, workingArea.Right - Width));
		y = Math.Max(workingArea.Top, Math.Min(y, workingArea.Bottom - Height));
		Location = new Point(x, y);
	}

	private static List<ClipboardHistoryEntry> LoadHistoryEntries()
	{
		try
		{
			if (!Directory.Exists(ClipboardSettings.BaseDirectoryPath))
			{
				return new List<ClipboardHistoryEntry>();
			}

			return Directory
				.EnumerateFiles(ClipboardSettings.BaseDirectoryPath, "*.*", SearchOption.AllDirectories)
				.Select(CreateHistoryEntry)
				.Where(entry => entry != null)
				.Cast<ClipboardHistoryEntry>()
				.OrderByDescending(entry => entry.LastWriteTime)
				.ThenByDescending(entry => entry.FilePath)
				.Take(MaxHistoryItems)
				.ToList();
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ClipboardHistoryForm: 履歴の読み込みに失敗しました。");
			return new List<ClipboardHistoryEntry>();
		}
	}

	private static ClipboardHistoryEntry? CreateHistoryEntry(string filePath)
	{
		string extension = Path.GetExtension(filePath).ToLowerInvariant();
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

		return new ClipboardHistoryEntry
		{
			FilePath = filePath,
			Kind = kind,
			LastWriteTime = File.GetLastWriteTime(filePath),
			PreviewText = CreatePreviewText(filePath, kind)
		};
	}

	private static string CreatePreviewText(string filePath, ClipboardHistoryKind kind)
	{
		try
		{
			if (kind == ClipboardHistoryKind.Image)
			{
				using var stream = new MemoryStream(File.ReadAllBytes(filePath));
				using var image = Image.FromStream(stream);
				return $"{Path.GetFileName(filePath)} / {image.Width} x {image.Height}";
			}

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
		try
		{
			using var richTextBox = new RichTextBox();
			richTextBox.Rtf = rtf;
			return richTextBox.Text;
		}
		catch
		{
			return string.Empty;
		}
	}

	private sealed class ClipboardHistoryEntry
	{
		public required string FilePath { get; init; }
		public required ClipboardHistoryKind Kind { get; init; }
		public required DateTime LastWriteTime { get; init; }
		public required string PreviewText { get; init; }
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
					Image = LoadThumbnail(entry.FilePath)
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

		private static Image? LoadThumbnail(string filePath)
		{
			try
			{
				using var stream = new MemoryStream(File.ReadAllBytes(filePath));
				using var image = Image.FromStream(stream);
				var thumbnail = new Bitmap(86, 66);
				using var graphics = Graphics.FromImage(thumbnail);
				graphics.Clear(Color.FromArgb(238, 238, 238));
				graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

				Rectangle bounds = GetContainBounds(image.Size, thumbnail.Size);
				graphics.DrawImage(image, bounds);
				return thumbnail;
			}
			catch
			{
				return null;
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
	}
}
