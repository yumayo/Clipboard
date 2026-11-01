using System.Windows;

namespace Clipboard;

internal sealed class ImagePaintWindow : Window
{
	public ImagePaintWindow(long historyId, byte[] imageBytes)
	{
		InitializeWindow(new ImagePaintEditor(imageBytes, (image, state, _) =>
			ClipboardManager.CopyPaintedImageToClipboard(historyId, image, state)));
	}

	public ImagePaintWindow(long historyId, IReadOnlyList<byte[]> imageBytesList)
	{
		InitializeWindow(new ImagePaintEditor(imageBytesList, (image, state, _) =>
			ClipboardManager.CopyPaintedImageToClipboard(historyId, image, state)));
	}

	public ImagePaintWindow(long historyId, ImagePaintSerializedState savedState)
	{
		InitializeWindow(new ImagePaintEditor(savedState, (image, state, _) =>
			ClipboardManager.CopyPaintedImageToClipboard(historyId, image, state)));
	}

	private void InitializeWindow(ImagePaintEditor editor)
	{
		Title = "ペイント";
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		Width = editor.PreferredWindowSize.Width;
		Height = editor.PreferredWindowSize.Height;
		MinWidth = 420;
		MinHeight = 320;
		Icon = ImagePaintImageFactory.LoadIcon();
		AppTheme.ApplyWindow(this);
		Content = editor;
		editor.Saved += (_, _) => Close();
		Closed += (_, _) => editor.Dispose();
	}
}
