using System;
using System.Drawing;
using System.Windows.Forms;

namespace LocalLink
{
    public sealed class UpdatePromptForm : Form
    {
        public UpdatePromptChoice Choice {get;private set;}
        public string OfferedVersion {get;private set;}
        readonly Button update=new ThemedButton(),later=new ThemedButton(),ignore=new ThemedButton();

        public UpdatePromptForm(AppTheme theme,string version)
        {
            if(theme==null)throw new ArgumentNullException("theme");UpdatePreferences.RequireVersion(version);
            Choice=UpdatePromptChoice.Later;OfferedVersion=version;
            Text="Обновление LocalLink";Tag="root";Font=AppTheme.Font(10.5f,FontStyle.Regular);
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
            ClientSize=new Size(610,222);MinimumSize=new Size(580,250);FormBorderStyle=FormBorderStyle.FixedDialog;
            MaximizeBox=MinimizeBox=false;ShowInTaskbar=false;StartPosition=FormStartPosition.CenterParent;
            var grid=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(24),Margin=Padding.Empty,ColumnCount=1,RowCount=4};
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute,42));grid.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute,30));grid.RowStyles.Add(new RowStyle(SizeType.Absolute,46));
            var title=new Label{Text="Доступен LocalLink "+version,Dock=DockStyle.Fill,Margin=Padding.Empty,TextAlign=ContentAlignment.MiddleLeft,Font=AppTheme.Font(15,FontStyle.Bold)};
            var description=new Label{Text="Установить обновление? Настройки сохранятся.",Dock=DockStyle.Fill,Margin=Padding.Empty,TextAlign=ContentAlignment.MiddleLeft};
            var note=new Label{Text="«Не напоминать» — только об этой версии.",Dock=DockStyle.Fill,Margin=Padding.Empty,Tag="muted",TextAlign=ContentAlignment.MiddleLeft,Font=AppTheme.Font(9.5f,FontStyle.Regular)};
            grid.Controls.Add(title,0,0);grid.Controls.Add(description,0,1);grid.Controls.Add(note,0,2);
            var buttons=new TableLayoutPanel{Dock=DockStyle.Fill,Margin=Padding.Empty,ColumnCount=3,RowCount=1};
            buttons.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,34));buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,26));buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,40));
            Configure(update,"Обновить","UpdateNow");Configure(later,"Позже","UpdateLater");Configure(ignore,"Не напоминать","IgnoreVersion");
            update.Tag="primary";update.Margin=later.Margin=new Padding(0,0,8,0);
            update.Click+=delegate{Choice=UpdatePromptChoice.Update;DialogResult=DialogResult.OK;};
            later.Click+=delegate{Choice=UpdatePromptChoice.Later;DialogResult=DialogResult.Cancel;};
            ignore.Click+=delegate{Choice=UpdatePromptChoice.IgnoreVersion;DialogResult=DialogResult.Ignore;};
            buttons.Controls.Add(update,0,0);buttons.Controls.Add(later,1,0);buttons.Controls.Add(ignore,2,0);grid.Controls.Add(buttons,0,3);Controls.Add(grid);
            AcceptButton=later;CancelButton=later;Shown+=delegate{later.Focus();};
            HandleCreated+=delegate{theme.Apply(this);};theme.Apply(this);
        }
        static void Configure(Button button,string text,string name)
        {button.Text=text;button.Name=name;button.AccessibleName=text;button.Dock=DockStyle.Fill;button.Margin=Padding.Empty;button.FlatStyle=FlatStyle.Flat;button.Cursor=Cursors.Hand;button.Font=AppTheme.Font(10.5f,FontStyle.Regular);}
    }
}
