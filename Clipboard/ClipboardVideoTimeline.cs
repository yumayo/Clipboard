namespace Clipboard;

internal static class ClipboardVideoTimeline
{
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
