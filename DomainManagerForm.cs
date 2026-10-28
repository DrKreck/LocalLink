using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LocalLink
{
    public sealed class DomainManagerForm : Form
    {
        readonly AppSettings draft;
        readonly string publicPath;
        readonly AppTheme theme;
        readonly Dictionary<string, CheckBox> categories = new Dictionary<string, CheckBox>();
        readonly TextBox search = new ThemedTextBox();
        readonly ComboBox categoryFilter = new ThemedComboBox();
        readonly ListView domains = new ListView();
        readonly Label count = new Label();
        readonly Button enable = new ThemedButton(), disable = new ThemedButton(), previous = new ThemedButton(), next = new ThemedButton();
        readonly Button enableAllCategories = new ThemedButton(), disableAllCategories = new ThemedButton();
        readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer();
        CancellationTokenSource pending;
        int offset, revision;
        bool closing, loading, resourcesDisposed;

        public AppSettings Selection { get { return Clone(draft); } }
        public DomainManagerForm(AppSettings settings, string publicListPath, AppTheme palette)
        {
            draft = Clone(settings); publicPath = publicListPath ?? ""; theme = palette;
            Text = "Домены и категории"; Font = AppTheme.Font(10.5f, FontStyle.Regular);
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(900, 650); MinimumSize = new Size(780, 600);
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(20), Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 44, 100, 66, 0, 34, 44, 48 })
                layout.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute, height == 0 ? 100 : height));
            Controls.Add(layout);
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            heading.Controls.Add(new Label { Text = "Категории", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = AppTheme.Font(11, FontStyle.Bold) }, 0, 0);
            StyleButton(enableAllCategories, "Включить всё"); StyleButton(disableAllCategories, "Выключить всё");
            enableAllCategories.AccessibleName = "Включить все категории"; disableAllCategories.AccessibleName = "Выключить все категории";
            enableAllCategories.Click += delegate { SetAllCategories(true); }; disableAllCategories.Click += delegate { SetAllCategories(false); };
            heading.Controls.Add(enableAllCategories, 1, 0); heading.Controls.Add(disableAllCategories, 2, 0); layout.Controls.Add(heading, 0, 0);
            var categoryGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Margin = Padding.Empty };
            for (int i = 0; i < 3; i++) { categoryGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3)); categoryGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3)); }
            int index = 0;
            foreach (var category in DomainSelection.Categories())
            {
                string id = category.Id;
                var box = new ThemedCheckBox { Text = id == "public" ? "Остальной список" : category.Name, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 4), Checked = CategoryEnabled(id), AccessibleName = "Категория " + category.Name };
                categories.Add(id, box); categoryGrid.Controls.Add(box, index % 3, index / 3); index++;
                box.CheckedChanged += delegate { SetCategory(id, box.Checked); ScheduleSearch(); };
            }
            layout.Controls.Add(categoryGrid, 0, 1);
            var find = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            find.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); find.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 236));
            find.RowStyles.Add(new RowStyle(SizeType.Absolute,20));find.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            find.Controls.Add(new Label{Text="Поиск домена",Dock=DockStyle.Fill,Tag="muted"},0,0);
            find.Controls.Add(new Label{Text="Показывать",Dock=DockStyle.Fill,Tag="muted"},1,0);
            search.Dock = DockStyle.Fill; search.Margin = new Padding(0, 4, 10, 6); search.MaxLength = 253; search.AccessibleName = "Поиск домена";
            categoryFilter.Dock = DockStyle.Fill; categoryFilter.Margin = new Padding(0, 4, 0, 6); categoryFilter.DropDownStyle = ComboBoxStyle.DropDownList; categoryFilter.AccessibleName = "Категория поиска";
            categoryFilter.DisplayMember = "Name"; categoryFilter.Items.Add(new CategoryItem { Id = "", Name = "Все категории" });
            foreach (var category in DomainSelection.Categories()) categoryFilter.Items.Add(new CategoryItem { Id = category.Id, Name = category.Name });
            categoryFilter.SelectedIndex = 0; find.Controls.Add(search, 0, 1); find.Controls.Add(categoryFilter, 1, 1); layout.Controls.Add(find, 0, 2);
            search.TextChanged += delegate { ScheduleSearch(); }; categoryFilter.SelectedIndexChanged += delegate { ScheduleSearch(); };
            domains.Dock = DockStyle.Fill; domains.View = View.Details; domains.FullRowSelect = true; domains.HideSelection = false;
            domains.MultiSelect = true; domains.BorderStyle = BorderStyle.FixedSingle; domains.AccessibleName = "Найденные домены";
            domains.Columns.Add("Домен", 420); domains.Columns.Add("Категория", 210); domains.Columns.Add("Обработка", 150);
            domains.OwnerDraw = true;
            domains.DrawItem += delegate(object sender, DrawListViewItemEventArgs e) { e.DrawDefault = true; };
            domains.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs e) { e.DrawDefault = true; };
            domains.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e)
            {
                if (theme.HighContrast) { e.DrawDefault = true; return; }
                using (var brush = new SolidBrush(theme.Input)) e.Graphics.FillRectangle(brush, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, domains.Font, Rectangle.Inflate(e.Bounds, -8, 0), theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            domains.Resize += delegate { if (domains.Columns.Count == 3) domains.Columns[0].Width = Math.Max(180, domains.ClientSize.Width - 360 - SystemInformation.VerticalScrollBarWidth - 4); };
            domains.SelectedIndexChanged += delegate { RefreshActions(); }; layout.Controls.Add(domains, 0, 3);
            count.Dock = DockStyle.Fill; count.Tag = "muted"; count.TextAlign = ContentAlignment.MiddleLeft; count.AutoEllipsis = true; layout.Controls.Add(count, 0, 4);
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            StyleButton(enable, "Включить"); StyleButton(disable, "Отключить"); StyleButton(previous, "Назад"); StyleButton(next, "Далее");
            actions.Controls.Add(enable, 0, 0); actions.Controls.Add(disable, 1, 0); actions.Controls.Add(previous, 3, 0); actions.Controls.Add(next, 4, 0); layout.Controls.Add(actions, 0, 5);
            enable.Click += delegate { SetSelected(true); }; disable.Click += delegate { SetSelected(false); };
            previous.Click += delegate { offset = Math.Max(0, offset - DomainSelection.MaximumPageSize); StartSearch(); };
            next.Click += delegate { offset += DomainSelection.MaximumPageSize; StartSearch(); };
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
            var hint = new Label { Text = "Отключение домена действует и на его поддомены.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Tag = "muted", Font = AppTheme.Font(9, FontStyle.Regular) };
            var apply = new ThemedButton { Tag = "primary" }; var cancel = new ThemedButton(); StyleButton(apply, "Применить"); StyleButton(cancel, "Отмена");
            apply.DialogResult = DialogResult.OK; cancel.DialogResult = DialogResult.Cancel; CancelButton = cancel;
            footer.Controls.Add(hint, 0, 0); footer.Controls.Add(apply, 1, 0); footer.Controls.Add(cancel, 2, 0); layout.Controls.Add(footer, 0, 6);
            debounce.Interval = 300; debounce.Tick += delegate { debounce.Stop(); StartSearch(); };
            HandleCreated += delegate { theme.Apply(this); };
            theme.Apply(this); RefreshActions(); Shown += delegate { StartSearch(); };
            FormClosed += delegate { ReleaseResources(); };
        }
        sealed class CategoryItem { public string Id { get; set; } public string Name { get; set; } }
        static void StyleButton(Button button, string text)
        { button.Text = text; button.Dock = DockStyle.Fill; button.Margin = new Padding(0, 4, 8, 4); button.Cursor = Cursors.Hand; }
        static AppSettings Clone(AppSettings value)
        {
            return new AppSettings { Services = new List<string>(value.Services), CustomDomains = value.CustomDomains,
                UsePublicLists = value.UsePublicLists, UseCustomDomains = value.UseCustomDomains, DisabledDomains = new List<string>(value.DisabledDomains) };
        }
        bool CategoryEnabled(string id)
        { return id == "public" ? draft.UsePublicLists : id == "custom" ? draft.UseCustomDomains : draft.Services.Contains(id); }
        void SetCategory(string id, bool value)
        {
            if (id == "public") draft.UsePublicLists = value;
            else if (id == "custom") draft.UseCustomDomains = value;
            else if (value && !draft.Services.Contains(id)) draft.Services.Add(id);
            else if (!value) draft.Services.Remove(id);
        }
        void SetAllCategories(bool enabled)
        { foreach (var box in categories.Values) box.Checked = enabled; ScheduleSearch(); }
        void CancelSearch()
        { if (pending != null) { pending.Cancel(); pending.Dispose(); pending = null; } }
        void ScheduleSearch()
        { offset = 0; revision++; CancelSearch(); loading = true; RefreshActions(); debounce.Stop(); debounce.Start(); }
        async void StartSearch()
        {
            if (closing || IsDisposed) return;
            CancelSearch(); pending = new CancellationTokenSource(); var token = pending.Token; int current = ++revision;
            var snapshot = Clone(draft); string query = search.Text; string id = ((CategoryItem)categoryFilter.SelectedItem).Id; int skip = offset;
            loading = true; count.Text = "Поиск…"; RefreshActions();
            try
            {
                var result = await Task.Run(() => DomainSelection.Search(snapshot, publicPath, id, query, skip, DomainSelection.MaximumPageSize, token), token);
                if (closing || IsDisposed || current != revision) return;
                domains.BeginUpdate();
                try
                {
                    domains.SelectedIndices.Clear();domains.Items.Clear();
                    foreach (var row in result.Rows)
                    {
                        string state = row.Enabled ? "Включена" : !row.CategoryEnabled ? "Категория выкл." : row.InheritedExclusion ? "Родитель выкл." : "Отключена";
                        var item = new ListViewItem(new[] { row.Domain, row.CategoryName, state }) { Tag = row };
                        item.ForeColor = row.Enabled ? theme.Text : theme.Muted; domains.Items.Add(item);
                    }
                }
                finally{domains.EndUpdate();}
                loading = false; previous.Enabled = skip > 0; next.Enabled = result.HasMore;
                count.Text = result.Rows.Count == 0 ? "Ничего не найдено" : "Показано " + (skip + 1) + "–" + (skip + result.Rows.Count) + (result.HasMore ? " · есть ещё" : "");
                if (String.IsNullOrEmpty(publicPath)) count.Text += " · публичный список ещё не загружен";
                RefreshActions();
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            { if (!closing && !IsDisposed && current == revision) { loading = false; count.Text = "Поиск не выполнен: " + error.Message; previous.Enabled = next.Enabled = false; RefreshActions(); } }
        }
        void RefreshActions()
        {
            if(loading||closing||IsDisposed){enable.Enabled=disable.Enabled=false;previous.Enabled=next.Enabled=false;return;}
            var selected = domains.SelectedItems.Cast<ListViewItem>().Select(x => x.Tag as DomainSearchRow).Where(x => x != null).ToList();
            enable.Enabled = !loading && selected.Count > 0 && selected.All(x => x.CategoryEnabled && !x.InheritedExclusion) && selected.Any(x => !x.Enabled);
            disable.Enabled = !loading && selected.Any(x => x.Enabled);
            if (loading) previous.Enabled = next.Enabled = false;
        }
        void SetSelected(bool enabled)
        {
            try
            {
                foreach (ListViewItem item in domains.SelectedItems)
                { var row = (DomainSearchRow)item.Tag; if (row.CategoryEnabled && (!enabled || !row.InheritedExclusion)) DomainSelection.SetEnabled(draft, row.Domain, enabled); }
                StartSearch();
            }
            catch (Exception error) { count.Text = error.Message; }
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing)ReleaseResources();
            base.Dispose(disposing);
        }
        void ReleaseResources()
        {if(resourcesDisposed)return;resourcesDisposed=true;closing=true;debounce.Stop();debounce.Dispose();CancelSearch();}
        protected override void OnFormClosing(FormClosingEventArgs e)
        {base.OnFormClosing(e);if(!e.Cancel)ReleaseResources();}
    }
}
