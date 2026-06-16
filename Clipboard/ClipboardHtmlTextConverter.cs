using System.Net;
using System.Text.RegularExpressions;

namespace Clipboard;

internal static class ClipboardHtmlTextConverter
{
	public static string ConvertToPlainText(string html)
	{
		string fragment = GetHtmlFragment(html);
		fragment = Regex.Replace(
			fragment,
			@"<(script|style)\b[^>]*>.*?</\1>",
			" ",
			RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
		fragment = Regex.Replace(
			fragment,
			@"<\s*br\s*/?\s*>",
			"\n",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		fragment = Regex.Replace(
			fragment,
			@"</\s*(p|div|section|article|header|footer|aside|main|li|ul|ol|tr|table|thead|tbody|tfoot|h[1-6]|blockquote|pre)\s*>",
			"\n",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		fragment = Regex.Replace(
			fragment,
			@"</\s*(td|th)\s*>",
			"\t",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

		string noTags = Regex.Replace(fragment, "<[^>]+>", string.Empty);
		string decoded = WebUtility.HtmlDecode(noTags);
		return NormalizePlainTextWhitespace(decoded);
	}

	private static string GetHtmlFragment(string html)
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

		return fragment;
	}

	private static string NormalizePlainTextWhitespace(string text)
	{
		text = text.Replace("\r\n", "\n").Replace("\r", "\n");
		text = Regex.Replace(text, @"[ \t\f\v]+", " ");
		text = Regex.Replace(text, @" *\n *", "\n");
		text = Regex.Replace(text, @"\n{3,}", "\n\n");
		return text.Trim().Replace("\n", "\r\n");
	}
}
