using System;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace LocalLink
{
    public sealed class SetupPrompt : Form
    {
        readonly CheckBox automaticStart=new ThemedCheckBox();
        readonly bool automaticOffered;
        public bool AutomaticStart {get{return automaticOffered&&automaticStart.Checked;}}
        public SetupPrompt(AppTheme theme, bool retry = false,bool automatic = false)
        {
            automaticOffered=automatic;
            Text = "Компоненты LocalLink";
            Font = AppTheme.Font(10.5f, FontStyle.Regular);
            ClientSize = new Size(500, 232); AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            BackColor = theme.Surface; ForeColor = theme.Text;
            var message = new Label { Text = retry ? "Не удалось скачать компоненты.\r\nПовторить загрузку?" :
                "Скачать компоненты LocalLink?",
                Location = new Point(24, 18), Size = new Size(452, 44), TextAlign = ContentAlignment.MiddleLeft };
            var purpose = new Label { Text = "Сетевой модуль нужен для всей работы LocalLink.\r\nМодуль Telegram — дополнительный.",
                Location = new Point(24, 64), Size = new Size(452, 56), TextAlign = ContentAlignment.MiddleLeft };
            var caption = new Label { Text = "Подписал специально для Kenkyusha", Tag = "muted",
                Location = new Point(24, 124), Size = new Size(452, 26), TextAlign = ContentAlignment.MiddleLeft };
            var install = new ThemedButton { Text = retry ? "Повторить" : "Установить", AccessibleName = "Установить компоненты",
                DialogResult = DialogResult.Yes, Location = new Point(218, 168), Size = new Size(124, 40), Tag = "primary" };
            var later = new ThemedButton { Text = "Не сейчас", AccessibleName = "Не сейчас", DialogResult = DialogResult.No,
                Location = new Point(354, 168), Size = new Size(122, 40) };
            Controls.AddRange(new Control[] { message, purpose, caption, install, later }); AcceptButton = install; CancelButton = later;
            if(automatic)
            {
                ClientSize=new Size(500,274);
                automaticStart.Text="Подключаться при открытии LocalLink";automaticStart.Checked=true;automaticStart.AutoSize=true;
                automaticStart.Location=new Point(24,120);automaticStart.AccessibleName=automaticStart.Text;
                caption.Top=156;install.Top=later.Top=210;Controls.Add(automaticStart);
            }
            HandleCreated += delegate { theme.Apply(this); };
            theme.Apply(this);
        }
    }

    public static class BootstrapPayload
    {
        public static string Ensure(string folder)
        {
            return PrepareResources(folder, ReadResource("LocalLink.TelegramLauncher"),
                ReadResource("LocalLink.TelegramScript"), ReadResource("LocalLink.TelegramConfig"));
        }
        // Content-addressed launchers let an old bridge keep running during an
        // application update. Never replace its executable or configuration.
        private static string PrepareResources(string folder, byte[] launcher, byte[] script, byte[] config)
        {
            folder = Path.GetFullPath(folder); CheckPath(folder); Directory.CreateDirectory(folder); CheckPath(folder);
            string identity;
            using (var framed = new MemoryStream())
            {
                using (var writer = new BinaryWriter(framed, System.Text.Encoding.UTF8, true))
                { writer.Write(launcher.Length); writer.Write(launcher); writer.Write(config.Length); writer.Write(config); }
                framed.Position = 0;
                using (var hash = SHA256.Create())
                    identity = BitConverter.ToString(hash.ComputeHash(framed)).Replace("-", "").ToLowerInvariant().Substring(0, 16);
            }
            string name = "LocalLink.Telegram-" + identity + ".exe";
            WriteResource(folder, name, launcher);
            WriteResource(folder, "bridge.py", script);
            WriteResource(folder, name + ".config", config);
            return Path.Combine(folder, name);
        }
        private static byte[] ReadResource(string resource)
        {
            using (var stream = typeof(BootstrapPayload).Assembly.GetManifestResourceStream(resource))
            {
                if (stream == null) throw new IOException("Не удалось подготовить Telegram. Скачайте LocalLink заново.");
                using (var memory = new MemoryStream()) { stream.CopyTo(memory); return memory.ToArray(); }
            }
        }
        private static void WriteResource(string folder, string name, byte[] data)
        {
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
