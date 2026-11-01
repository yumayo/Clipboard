namespace Clipboard;

internal enum ClipboardHistoryKind
{
	Image,
	Html,
	Rtf,
	Text,
	Unknown,
	// SQLiteに保存済みの種別番号を変えないよう、末尾に追加する。
	Video
}
