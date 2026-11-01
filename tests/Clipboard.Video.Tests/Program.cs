using System.Text;
using System.Text.Json;
using Clipboard;
using Microsoft.Data.Sqlite;

int passed = 0;
int failed = 0;
string first = Path.Combine(Path.GetTempPath(), "動画 #1 [確認] \"a\".MP4");
string second = Path.Combine(Path.GetTempPath(), "another clip.webm");

Run("既存DBの種別番号を維持する", () =>
{
	Equal(0, (int)ClipboardHistoryKind.Image);
	Equal(1, (int)ClipboardHistoryKind.Html);
	Equal(2, (int)ClipboardHistoryKind.Rtf);
	Equal(3, (int)ClipboardHistoryKind.Text);
	Equal(4, (int)ClipboardHistoryKind.Unknown);
	Equal(5, (int)ClipboardHistoryKind.Video);
});

Run("複数動画・日本語・記号を含むパスを復元する", () =>
{
	string[] paths = ClipboardVideoFiles.Decode(ClipboardVideoFiles.Encode(new[] { first, second }));
	Equal(first, paths[0]);
	Equal(second, paths[1]);
	Equal(2, paths.Length);
});

Run("混在したファイルから動画だけを取り出し重複を除く", () =>
{
	string[] paths = ClipboardVideoFiles.Filter(new[]
	{
		first, first.ToLowerInvariant(), Path.ChangeExtension(first, ".png"), second,
		"relative.mp4", "https://example.invalid/movie.mp4", ""
	});
	Equal(2, paths.Length);
	Equal(first, paths[0]);
	Equal(second, paths[1]);
});

Run("破損した履歴は空として扱う", () =>
{
	foreach (string content in new[] { "", "null", "{}", "[", "[42]", "[{}]" })
	{
		Equal(0, ClipboardVideoFiles.Decode(Encoding.UTF8.GetBytes(content)).Length);
	}
	byte[] mixed = JsonSerializer.SerializeToUtf8Bytes(new string?[] { null, first, "relative.mp4" });
	Equal(first, ClipboardVideoFiles.Decode(mixed).Single());
});

Run("元ファイルがなくても履歴の参照を復元できる", () =>
{
	string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mkv");
	Equal(missing, ClipboardVideoFiles.Decode(ClipboardVideoFiles.Encode(new[] { missing })).Single());
});

Run("再生エンジンの位置が総時間を超えても表示位置は末尾に収まる", () =>
{
	TimeSpan duration = TimeSpan.FromSeconds(1);
	Equal(duration, ClipboardVideoTimeline.ClampPosition(TimeSpan.FromMilliseconds(1115), duration));
	Equal(duration, ClipboardVideoTimeline.ClampPosition(TimeSpan.MaxValue, duration));
});

Run("再生位置の補正は範囲内の時刻を保ち、負の位置や長さ不明にも対応する", () =>
{
	TimeSpan duration = TimeSpan.FromSeconds(1);
	foreach (TimeSpan position in new[] { TimeSpan.Zero, TimeSpan.FromTicks(1234567), duration })
	{
		Equal(position, ClipboardVideoTimeline.ClampPosition(position, duration));
	}
	Equal(TimeSpan.Zero, ClipboardVideoTimeline.ClampPosition(TimeSpan.FromMilliseconds(-1), duration));
	Equal(TimeSpan.Zero, ClipboardVideoTimeline.ClampPosition(duration, TimeSpan.Zero));
	Equal(TimeSpan.Zero, ClipboardVideoTimeline.ClampPosition(duration, TimeSpan.FromSeconds(-1)));
});

Run("30fpsの隣接コマへ進み、同じコマへ戻る", () =>
{
	TimeSpan duration = TimeSpan.FromSeconds(10);
	TimeSpan next = ClipboardVideoTimeline.Step(TimeSpan.Zero, duration, 30, 1);
	Equal(333333L, next.Ticks);
	Equal(TimeSpan.Zero, ClipboardVideoTimeline.Step(next, duration, 30, -1));
});

Run("24fpsの連続操作でコマが抜けたり停止したりしない", () =>
{
	TimeSpan position = TimeSpan.Zero;
	TimeSpan duration = TimeSpan.FromMinutes(2);
	for (int index = 1; index <= 2400; index++)
	{
		position = ClipboardVideoTimeline.Step(position, duration, 24, 1);
		Equal((long)Math.Round(index * TimeSpan.TicksPerSecond / 24.0), position.Ticks);
	}
	for (int index = 2399; index >= 0; index--)
	{
		position = ClipboardVideoTimeline.Step(position, duration, 24, -1);
		Equal((long)Math.Round(index * TimeSpan.TicksPerSecond / 24.0), position.Ticks);
	}
});

Run("29.97fpsで長くコマ送りしても丸め誤差が蓄積しない", () =>
{
	TimeSpan position = TimeSpan.Zero;
	for (int index = 0; index < 1800; index++)
	{
		position = ClipboardVideoTimeline.Step(position, TimeSpan.FromHours(1), 29.97, 1);
	}
	Equal(TimeSpan.FromTicks(600600601), position);
});

Run("動画の60・29.94・59.94fpsを補正せずにコマ送りする", () =>
{
	foreach (var (fps, frames, expectedSeconds) in new[]
	{
		(60.0, 600, 10),
		(29.94, 1497, 50),
		(59.94, 2997, 50)
	})
	{
		TimeSpan position = TimeSpan.Zero;
		TimeSpan duration = TimeSpan.FromMinutes(2);
		for (int index = 0; index < frames; index++)
		{
			position = ClipboardVideoTimeline.Step(position, duration, fps, 1);
		}
		Equal(TimeSpan.FromSeconds(expectedSeconds), position);
		for (int index = 0; index < frames; index++)
		{
			position = ClipboardVideoTimeline.Step(position, duration, fps, -1);
		}
		Equal(TimeSpan.Zero, position);
	}
});

Run("コマの途中から前後それぞれのコマへ移動する", () =>
{
	Equal(666667L, ClipboardVideoTimeline.Step(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2), 30, 1).Ticks);
	Equal(333333L, ClipboardVideoTimeline.Step(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2), 30, -1).Ticks);
});

Run("先頭と末尾を越えない", () =>
{
	TimeSpan duration = TimeSpan.FromSeconds(2);
	Equal(TimeSpan.Zero, ClipboardVideoTimeline.Step(TimeSpan.Zero, duration, 30, -1));
	Equal(19666667L, ClipboardVideoTimeline.Step(duration, duration, 30, 1).Ticks);
	Equal(19666667L, ClipboardVideoTimeline.Step(duration, duration, 30, -1).Ticks);
	Equal(TimeSpan.Zero, ClipboardVideoTimeline.Step(TimeSpan.Zero, TimeSpan.FromTicks(1), 30, 1));
});

Run("FPSや長さが不明でも不正な位置を返さない", () =>
{
	foreach (double fps in new[] { 0, -1, double.NaN, double.PositiveInfinity })
	{
		Equal(TimeSpan.Zero, ClipboardVideoTimeline.Step(TimeSpan.Zero, TimeSpan.FromSeconds(1), fps, 1));
	}
	Equal(TimeSpan.Zero, ClipboardVideoTimeline.Step(TimeSpan.Zero, TimeSpan.Zero, 30, 1));
});

using var database = new SqliteConnection("Data Source=:memory:");
database.Open();
using (var command = database.CreateCommand())
{
	command.CommandText = """
		CREATE TABLE clipboard_history (
			id INTEGER PRIMARY KEY, kind INTEGER, created_at_utc_ticks INTEGER,
			preview_text TEXT, thumbnail BLOB, paint_state TEXT
		);
		INSERT INTO clipboard_history VALUES
			(1, 3, 0, 'text', NULL, NULL), (2, 0, 0, 'image', NULL, NULL),
			(3, 1, 0, 'html', NULL, NULL), (4, 5, 0, 'video', NULL, NULL),
			(5, 4, 0, 'unknown', NULL, NULL), (6, 0, 0, 'image', NULL, '{}'),
			(7, 5, 0, 'video', NULL, NULL), (8, 2, 0, 'rtf', NULL, NULL),
			(9, 5, 0, 'video', NULL, NULL), (10, 0, 0, 'image', NULL, NULL);
		""";
	command.ExecuteNonQuery();
}

Run("ギャラリーは画像と動画を新しい順に取得する", () =>
{
	Equal("10,9,7,6,4,2", string.Join(',', LoadPage(null, 100)));
});

Run("画像と動画が混在してもページ間の欠落や重複がない", () =>
{
	long[] page1 = LoadPage(null, 3);
	long[] page2 = LoadPage(page1.Last(), 3);
	Equal("10,9,7", string.Join(',', page1));
	Equal("6,4,2", string.Join(',', page2));
	Equal(0, LoadPage(page2.Last(), 3).Length);
	Equal(0, LoadPage(null, 0).Length);
});

Console.WriteLine($"{passed} checks passed; {failed} failed.");
Environment.ExitCode = failed == 0 ? 0 : 1;

long[] LoadPage(long? beforeId, int count)
{
	using var command = database.CreateCommand();
	command.CommandText = ClipboardGalleryHistoryQuery.Create(beforeId.HasValue);
	command.Parameters.AddWithValue("$image_kind", (int)ClipboardHistoryKind.Image);
	command.Parameters.AddWithValue("$video_kind", (int)ClipboardHistoryKind.Video);
	command.Parameters.AddWithValue("$limit", count);
	if (beforeId.HasValue)
	{
		command.Parameters.AddWithValue("$before_id", beforeId.Value);
	}
	var result = new List<long>();
	using var reader = command.ExecuteReader();
	while (reader.Read())
	{
		result.Add(reader.GetInt64(0));
	}
	return result.ToArray();
}

void Run(string name, Action test)
{
	try
	{
		test();
		passed++;
		Console.WriteLine($"PASS {name}");
	}
	catch (Exception ex)
	{
		failed++;
		Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
	}
}

static void Equal<T>(T expected, T actual)
{
	if (!EqualityComparer<T>.Default.Equals(expected, actual))
	{
		throw new Exception($"Expected {expected}; got {actual}");
	}
}
