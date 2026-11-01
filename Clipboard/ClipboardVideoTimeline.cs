namespace Clipboard;

internal static class ClipboardVideoTimeline
{
	// シェルが小数第3位までに丸めたNTSCのFPSを、元の比率へ戻す。
	public static double NormalizeFrameRate(double frameRate)
	{
		foreach (double rate in new[] { 24000.0 / 1001, 30000.0 / 1001, 60000.0 / 1001, 120000.0 / 1001 })
		{
			if (Math.Abs(frameRate - rate) < 0.001)
			{
				return rate;
			}
		}
		return frameRate;
	}

	public static TimeSpan Step(TimeSpan position, TimeSpan duration, double frameRate, int direction)
	{
		if (!double.IsFinite(frameRate) || frameRate <= 0 || duration <= TimeSpan.Zero)
		{
			return TimeSpan.Zero;
		}

		frameRate = NormalizeFrameRate(frameRate);
		double frame = Math.Max(0, position.TotalSeconds) * frameRate;
		// TimeSpanの100ns丸め誤差によって同じコマに留まらないようにする。
		double tolerance = frameRate / TimeSpan.TicksPerSecond;
		double next = direction > 0 ? Math.Floor(frame + tolerance) + 1 : Math.Ceiling(frame - tolerance) - 1;
		double last = Math.Max(0, Math.Ceiling(duration.TotalSeconds * frameRate - tolerance) - 1);
		long ticks = (long)Math.Round(Math.Clamp(next, 0, last) / frameRate * TimeSpan.TicksPerSecond);
		return TimeSpan.FromTicks(Math.Clamp(ticks, 0, duration.Ticks - 1));
	}
}
