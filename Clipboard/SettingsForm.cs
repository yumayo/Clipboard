using System.Drawing;
using System.Windows.Forms;

namespace Clipboard;

internal sealed class SettingsForm : Form
{
	private readonly TextBox _separatorTextBox;

	public SettingsForm()
	{
		Text = "設定";
		StartPosition = FormStartPosition.CenterScreen;
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		Icon = new Icon("Clipboard.ico");
		ClientSize = new Size(420, 230);

		var label = new Label
		{
			Text = "連結文字",
			AutoSize = true,
			Location = new Point(16, 18)
		};

		_separatorTextBox = new TextBox
		{
			AcceptsReturn = true,
			AcceptsTab = true,
			Multiline = true,
			ScrollBars = ScrollBars.Vertical,
			Location = new Point(16, 44),
			Size = new Size(388, 112),
			Text = ToTextBoxNewLines(ClipboardSettings.GetCopy().ConcatenationSeparator)
		};

		var saveButton = new Button
		{
			Text = "保存",
			DialogResult = DialogResult.OK,
			Location = new Point(248, 178),
			Size = new Size(75, 28)
		};
		saveButton.Click += SaveButton_Click;

		var cancelButton = new Button
		{
			Text = "キャンセル",
			DialogResult = DialogResult.Cancel,
			Location = new Point(329, 178),
			Size = new Size(75, 28)
		};

		Controls.Add(label);
		Controls.Add(_separatorTextBox);
		Controls.Add(saveButton);
		Controls.Add(cancelButton);

		AcceptButton = saveButton;
		CancelButton = cancelButton;
	}

	private void SaveButton_Click(object? sender, EventArgs e)
	{
		try
		{
			ClipboardSettings.Save(new ClipboardSettingsData
			{
				ConcatenationSeparator = _separatorTextBox.Text
			});
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "設定の保存に失敗しました。");
			DialogResult = DialogResult.None;
			MessageBox.Show("設定の保存に失敗しました。", "Clipboard", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
	}

	private static string ToTextBoxNewLines(string text)
	{
		return text.Replace("\n", "\r\n");
	}
}
