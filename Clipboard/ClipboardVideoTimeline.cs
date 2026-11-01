namespace Clipboard;

internal static class ClipboardVideoTimeline
{
	public const double PreviewFrameWidth = 144;

	public static TimeSpan?[] PreviewPositions(TimeSpan position, TimeSpan duration, double frameRate, double width)
	{
		if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(frameRate) || frameRate <= 0 || duration <= TimeSpan.Zero)
		{
			return [];
		}
		int count = Math.Max(1, (int)Math.Min(1001, Math.Floor(width / PreviewFrameWidth)));
		if (count % 2 == 0)
		{
			count--;
		}
		var positions = new TimeSpan?[count];
		double tolerance = frameRate / TimeSpan.TicksPerSecond;
		double last = Math.Max(0, Math.Ceiling(duration.TotalSeconds * frameRate - tolerance) - 1);
		double center = Math.Clamp(Math.Floor(ClampPosition(position, duration).TotalSeconds * frameRate + tolerance), 0, last);
		for (int index = 0; index < count; index++)
		{
			double frame = center + index - count / 2;
			if (frame >= 0 && frame <= last)
			{
				long ticks = (long)Math.Round(frame / frameRate * TimeSpan.TicksPerSecond);
				positions[index] = TimeSpan.FromTicks(Math.Clamp(ticks, 0, duration.Ticks - 1));
			}
		}
		return positions;
	}

	public static TimeSpan ClampPosition(TimeSpan position, TimeSpan duration)
	{
		return TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, Math.Max(0, duration.Ticks)));
	}

	public static TimeSpan Step(TimeSpan position, TimeSpan duration, double frameRate, int direction)
	{
		if (!double.IsFinite(frameRate) || frameRate <= 0 || duration <= TimeSpan.Zero)
		{
			return TimeSpan.Zero;
		}

		double frame = Math.Max(0, position.TotalSeconds) * frameRate;
		// TimeSpanの100ns丸め誤差によって同じコマに留まらないようにする。
		double tolerance = frameRate / TimeSpan.TicksPerSecond;
		double next = direction > 0 ? Math.Floor(frame + tolerance) + 1 : Math.Ceiling(frame - tolerance) - 1;
		double last = Math.Max(0, Math.Ceiling(duration.TotalSeconds * frameRate - tolerance) - 1);
		long ticks = (long)Math.Round(Math.Clamp(next, 0, last) / frameRate * TimeSpan.TicksPerSecond);
		return TimeSpan.FromTicks(Math.Clamp(ticks, 0, duration.Ticks - 1));
	}
}
