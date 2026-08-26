using System.Text;

namespace Clipboard;

internal enum ClipboardTableTextFormat
{
	Tsv,
	Csv,
	Markdown
}

internal static class ClipboardTableTextConverter
{
	private enum SourceFormat
	{
		PlainText,
		Tsv,
		Csv,
		Markdown
	}

	internal static string Convert(string text, ClipboardTableTextFormat outputFormat)
	{
		if (text.Length == 0)
		{
			return string.Empty;
		}

		string normalizedText = NormalizeNewLines(ClipboardTextNormalizer.NormalizeForPaste(text));
		List<List<string>> rows = ParseRows(normalizedText, DetectSourceFormat(normalizedText));
		return outputFormat switch
		{
			ClipboardTableTextFormat.Tsv => SerializeDelimited(rows, '\t'),
			ClipboardTableTextFormat.Csv => SerializeDelimited(rows, ','),
			ClipboardTableTextFormat.Markdown => SerializeMarkdown(rows),
			_ => normalizedText
		};
	}

	internal static bool IsTableText(string text)
	{
		if (text.Length == 0)
		{
			return false;
		}

		string normalizedText = NormalizeNewLines(ClipboardTextNormalizer.NormalizeForPaste(text));
		return DetectSourceFormat(normalizedText) != SourceFormat.PlainText;
	}

	private static SourceFormat DetectSourceFormat(string text)
	{
		string[] lines = text.Split('\n');
		if (HasConsistentDelimitedColumns(text, '\t', minimumRowCount: 1))
		{
			return SourceFormat.Tsv;
		}

		if (HasConsistentMarkdownColumns(lines))
		{
			return SourceFormat.Markdown;
		}

		if (HasConsistentDelimitedColumns(text, ',', minimumRowCount: 2))
		{
			return SourceFormat.Csv;
		}

		return SourceFormat.PlainText;
	}

	private static List<List<string>> ParseRows(string text, SourceFormat sourceFormat)
	{
		return sourceFormat switch
		{
			SourceFormat.Tsv => ParseDelimited(text, '\t'),
			SourceFormat.Csv => ParseDelimited(text, ','),
			SourceFormat.Markdown => ParseMarkdown(text.Split('\n')),
			_ => ParsePlainText(text)
		};
	}

	private static List<List<string>> ParsePlainText(string text)
	{
		var rows = text
			.Split('\n')
			.Select(line => new List<string> { line })
			.ToList();
		RemoveTrailingEmptyRows(rows);
		return rows;
	}

	private static bool HasConsistentDelimitedColumns(string text, char delimiter, int minimumRowCount)
	{
		if (!TryParseDelimited(text, delimiter, out List<List<string>> rows))
		{
			return false;
		}

		List<List<string>> meaningfulRows = rows
			.Where(row => !IsBlankRow(row))
			.ToList();
		if (meaningfulRows.Count < minimumRowCount)
		{
			return false;
		}

		int columnCount = meaningfulRows[0].Count;
		return columnCount >= 2 && meaningfulRows.All(row => row.Count == columnCount);
	}

	private static bool HasConsistentMarkdownColumns(string[] lines)
	{
		List<string> meaningfulLines = lines
			.Where(line => !string.IsNullOrWhiteSpace(line))
			.ToList();
		if (meaningfulLines.Count < 2 || !meaningfulLines.Any(line => CountUnescapedPipes(line) > 0))
		{
			return false;
		}

		List<List<string>> rows = meaningfulLines
			.Select(ParseMarkdownRow)
			.ToList();
		int columnCount = rows[0].Count;
		bool hasSeparatorRow = IsMarkdownSeparatorRow(rows[1]);
		if (hasSeparatorRow)
		{
			return rows[1].Count == columnCount &&
				rows.Skip(2).All(row => row.Count == columnCount);
		}

		return columnCount >= 2 && rows.All(row => row.Count == columnCount);
	}

	private static List<List<string>> ParseDelimited(string text, char delimiter)
	{
		TryParseDelimited(text, delimiter, out List<List<string>> rows);
		return rows
			.Where(row => !IsBlankRow(row))
			.ToList();
	}

	private static bool TryParseDelimited(string text, char delimiter, out List<List<string>> rows)
	{
		rows = new List<List<string>>();
		var row = new List<string>();
		var cell = new StringBuilder();
		bool inQuotes = false;
		for (int index = 0; index < text.Length; index++)
		{
			char character = text[index];
			if (character == '"')
			{
				if (inQuotes && index + 1 < text.Length && text[index + 1] == '"')
				{
					cell.Append('"');
					index++;
				}
				else if (inQuotes)
				{
					inQuotes = false;
				}
				else if (cell.Length == 0)
				{
					inQuotes = true;
				}
				else
				{
					cell.Append(character);
				}
			}
			else if (!inQuotes && character == delimiter)
			{
				row.Add(cell.ToString());
				cell.Clear();
			}
			else if (!inQuotes && character == '\n')
			{
				row.Add(cell.ToString());
				cell.Clear();
				rows.Add(row);
				row = new List<string>();
			}
			else
			{
				cell.Append(character);
			}
		}

		row.Add(cell.ToString());
		rows.Add(row);
		RemoveTrailingEmptyRows(rows);
		return !inQuotes;
	}

	private static List<List<string>> ParseMarkdown(string[] lines)
	{
		var rows = lines
			.Where(line => !string.IsNullOrWhiteSpace(line))
			.Select(ParseMarkdownRow)
			.ToList();
		if (rows.Count >= 2 && IsMarkdownSeparatorRow(rows[1]))
		{
			rows.RemoveAt(1);
		}

		return rows;
	}

	private static List<string> ParseMarkdownRow(string line)
	{
		string value = line.Trim();
		if (value.StartsWith('|'))
		{
			value = value[1..];
		}
		if (EndsWithUnescapedPipe(value))
		{
			value = value[..^1];
		}

		var cells = new List<string>();
		var cell = new StringBuilder();
		for (int index = 0; index < value.Length; index++)
		{
			char character = value[index];
			if (character == '\\' && index + 1 < value.Length &&
				(value[index + 1] == '|' || value[index + 1] == '\\'))
			{
				cell.Append(value[index + 1]);
				index++;
			}
			else if (character == '|')
			{
				cells.Add(cell.ToString().Trim());
				cell.Clear();
			}
			else
			{
				cell.Append(character);
			}
		}

		cells.Add(cell.ToString().Trim());
		return cells;
	}

	private static bool IsMarkdownSeparatorRow(List<string> row)
	{
		return row.Count > 0 && row.All(IsMarkdownSeparatorCell);
	}

	private static bool IsMarkdownSeparatorCell(string cell)
	{
		string value = cell.Trim();
		if (value.StartsWith(':'))
		{
			value = value[1..];
		}
		if (value.EndsWith(':'))
		{
			value = value[..^1];
		}

		return value.Length >= 3 && value.All(character => character == '-');
	}

	private static string SerializeDelimited(List<List<string>> rows, char delimiter)
	{
		return string.Join(
			"\r\n",
			rows.Select(row => string.Join(delimiter, row.Select(cell => EscapeDelimitedCell(cell, delimiter)))));
	}

	private static string EscapeDelimitedCell(string cell, char delimiter)
	{
		cell = cell.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
		if (!cell.Contains(delimiter) && !cell.Contains('"') && !cell.Contains('\r') && !cell.Contains('\n'))
		{
			return cell;
		}

		return '"' + cell.Replace("\"", "\"\"") + '"';
	}

	private static string SerializeMarkdown(List<List<string>> rows)
	{
		if (rows.Count == 0)
		{
			return string.Empty;
		}

		int columnCount = Math.Max(1, rows.Max(row => row.Count));
		var outputLines = new List<string>(rows.Count + 1)
		{
			SerializeMarkdownRow(rows[0], columnCount),
			"| " + string.Join(" | ", Enumerable.Repeat("---", columnCount)) + " |"
		};
		for (int rowIndex = 1; rowIndex < rows.Count; rowIndex++)
		{
			outputLines.Add(SerializeMarkdownRow(rows[rowIndex], columnCount));
		}

		return string.Join("\r\n", outputLines);
	}

	private static string SerializeMarkdownRow(List<string> row, int columnCount)
	{
		return "| " + string.Join(
			" | ",
			Enumerable.Range(0, columnCount)
				.Select(index => index < row.Count ? EscapeMarkdownCell(row[index]) : string.Empty)) + " |";
	}

	private static string EscapeMarkdownCell(string cell)
	{
		return cell
			.Replace("\\", "\\\\")
			.Replace("|", "\\|")
			.Replace("\r\n", "<br>")
			.Replace("\r", "<br>")
			.Replace("\n", "<br>");
	}

	private static int CountUnescapedPipes(string text)
	{
		int count = 0;
		for (int index = 0; index < text.Length; index++)
		{
			if (text[index] == '\\')
			{
				index++;
			}
			else if (text[index] == '|')
			{
				count++;
			}
		}

		return count;
	}

	private static bool EndsWithUnescapedPipe(string text)
	{
		if (!text.EndsWith('|'))
		{
			return false;
		}

		int slashCount = 0;
		for (int index = text.Length - 2; index >= 0 && text[index] == '\\'; index--)
		{
			slashCount++;
		}
		return slashCount % 2 == 0;
	}

	private static bool IsBlankRow(List<string> row)
	{
		return row.Count == 1 && string.IsNullOrWhiteSpace(row[0]);
	}

	private static void RemoveTrailingEmptyRows(List<List<string>> rows)
	{
		while (rows.Count > 1 && rows[^1].All(string.IsNullOrEmpty))
		{
			rows.RemoveAt(rows.Count - 1);
		}
	}

	private static string NormalizeNewLines(string text)
	{
		return text.Replace("\r\n", "\n").Replace('\r', '\n');
	}
}
