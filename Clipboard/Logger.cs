using System.IO;
using System.Text;

namespace Clipboard;

public class Logger
{
	private static Logger? _singleton;

	private static readonly string LogName = "Clipboard";

	private readonly TextWriter _textWriterSynchronized;

	private Logger(TextWriter textWriterSynchronized)
	{
		_textWriterSynchronized = textWriterSynchronized;
	}

	public static void Setup()
	{
		var logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "log");
		var logName = LogName + ".log";
		var path = Path.Join(logDirectory, logName);

		var directoryName = Path.GetDirectoryName(path);
		if (!Directory.Exists(directoryName))
		{
			Directory.CreateDirectory(directoryName!);
		}

		var streamWriter = File.AppendText(path);
		var textWriterSynchronized = TextWriter.Synchronized(streamWriter);

		_singleton?._textWriterSynchronized.Dispose();
		_singleton = new Logger(textWriterSynchronized);
	}

	public static void Debug(object message)
	{
		_singleton?.InternalLog("DEBUG", message);
	}

	public static void Info(object message)
	{
		_singleton?.InternalLog(" INFO", message);
	}

	public static void Warning(object message)
	{
		_singleton?.InternalLog(" WARN", message);
	}

	public static void Error(Exception? exception, object message)
	{
		_singleton?.InternalLog("ERROR", message, exception);
	}

	private void InternalLog(string type, object message, Exception? exception = null)
	{
		var now = DateTime.Now;
		var dateTimeString = now.ToString("yyyy-MM-dd HH:mm:ss");
		var sb = new StringBuilder();
		sb.Append(dateTimeString).Append(" ").Append(type).Append(" ").Append(message);
		if (exception != null)
		{
			sb.Append(" Exception: ").Append(exception.Message);
			if (!string.IsNullOrEmpty(exception.StackTrace))
			{
				sb.Append(" StackTrace: ").Append(exception.StackTrace);
			}
		}
		_textWriterSynchronized.Write(sb.ToString());
		_textWriterSynchronized.WriteLine();
		_textWriterSynchronized.Flush();
	}

	public static void Close()
	{
		_singleton?._textWriterSynchronized.Dispose();
	}
}
