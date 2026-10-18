// SPDX-License-Identifier: CC-BY-NC-SA-4.0
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MiTVSwitch
{
    /// <summary>WinForms 没有内置输入框，这里自己搭两个小对话框</summary>
    internal static class Prompt
    {
        public static string? AskText(string title, string label, string value)
        {
            using var f = new Form();
            f.Text = title;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.MinimizeBox = false;
            f.MaximizeBox = false;
            f.TopMost = true;
            f.ClientSize = new Size(380, 140);

            var lbl = new Label { Text = label, Left = 14, Top = 14, Width = 352, Height = 40 };
            var tb = new TextBox { Left = 14, Top = 58, Width = 352, Text = value };
            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Left = 214, Top = 96, Width = 72 };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 294, Top = 96, Width = 72 };

            f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            f.ActiveControl = tb;

            return f.ShowDialog() == DialogResult.OK ? tb.Text.Trim() : null;
        }

        public static int AskList(string title, string label, IReadOnlyList<string> items)
        {
            using var f = new Form();
            f.Text = title;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.MinimizeBox = false;
            f.MaximizeBox = false;
            f.TopMost = true;
            f.ClientSize = new Size(440, 320);

            var lbl = new Label { Text = label, Left = 14, Top = 12, Width = 412, Height = 20 };
            var list = new ListBox { Left = 14, Top = 38, Width = 412, Height = 218 };
            list.Items.AddRange(items.Cast<object>().ToArray());
            if (list.Items.Count > 0) list.SelectedIndex = 0;

            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Left = 274, Top = 268, Width = 72 };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 354, Top = 268, Width = 72 };

            f.Controls.AddRange(new Control[] { lbl, list, ok, cancel });
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            list.DoubleClick += (_, _) => { f.DialogResult = DialogResult.OK; f.Close(); };

            return f.ShowDialog() == DialogResult.OK ? list.SelectedIndex : -1;
        }
    }
}
