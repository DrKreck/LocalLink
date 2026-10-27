using System;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace LocalLink
{
    public sealed class SetupPrompt : Form
    {
        public SetupPrompt(AppTheme theme, bool retry = false)
        {
            Text = "Компоненты LocalLink";
            Font = AppTheme.Font(10.5f, FontStyle.Regular);
            ClientSize = new Size(460, 170); AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            BackColor = theme.Surface; ForeColor = theme.Text;
            var message = new Label { Text = retry ? "Не удалось скачать компоненты.\r\nПовторить загрузку?" :
                "Скачать сетевой модуль и Telegram?\r\nОни нужны для подключения.",
                Location = new Point(24, 22), Size = new Size(412, 66), TextAlign = ContentAlignment.MiddleLeft };
            var install = new ThemedButton { Text = retry ? "Повторить" : "Установить", AccessibleName = "Установить компоненты",
                DialogResult = DialogResult.Yes, Location = new Point(178, 108), Size = new Size(124, 40), Tag = "primary" };
            var later = new ThemedButton { Text = "Не сейчас", AccessibleName = "Не сейчас", DialogResult = DialogResult.No,
                Location = new Point(314, 108), Size = new Size(122, 40) };
            Controls.AddRange(new Control[] { message, install, later }); AcceptButton = install; CancelButton = later;
            theme.Apply(this);
        }
    }

    public static class BootstrapPayload
    {
        public static void Ensure(string folder)
        {
            folder = Path.GetFullPath(folder); CheckPath(folder); Directory.CreateDirectory(folder); CheckPath(folder);
            WriteResource(folder, "LocalLink.Telegram.exe", "LocalLink.TelegramLauncher");
            WriteResource(folder, "bridge.py", "LocalLink.TelegramScript");
            WriteResource(folder, "LocalLink.Telegram.exe.config", "LocalLink.TelegramConfig");
        }
        private static void WriteResource(string folder, string name, string resource)
        {
            byte[] data;
            using (var stream = typeof(BootstrapPayload).Assembly.GetManifestResourceStream(resource))
            {
                if (stream == null) throw new IOException("Не удалось подготовить Telegram. Скачайте LocalLink заново.");
                using (var memory = new MemoryStream()) { stream.CopyTo(memory); data = memory.ToArray(); }
            }
            string path = Path.Combine(folder, name); CheckPath(path);
            if (File.Exists(path))
            {
                using (var hash = SHA256.Create())
                using (var file = File.OpenRead(path))
                    if (Convert.ToBase64String(hash.ComputeHash(file)) == Convert.ToBase64String(hash.ComputeHash(data))) return;
            }
            string temporary = path + ".new-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(data, 0, data.Length); file.Flush(true); }
                CheckPath(path);
                if (File.Exists(path)) File.Replace(temporary, path, path + ".previous-" + Guid.NewGuid().ToString("N"));
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static void CheckPath(string path)
        {
            for (string current = Path.GetFullPath(path); !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Папка компонентов недоступна. Выберите обычную локальную папку.");
        }
    }
}
