using System;
using System.Drawing;
using System.Windows.Forms;

namespace LocalLink
{
    internal sealed class PresetNamePrompt : Form
    {
        readonly TextBox name = new ThemedTextBox();
        internal string PresetName { get { return name.Text.Trim(); } }
        internal PresetNamePrompt(AppTheme theme, string initial)
        {
            Text="Сохранить пресет";Font=AppTheme.Font(10.5f,FontStyle.Regular);
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
            ClientSize=new Size(480,174);FormBorderStyle=FormBorderStyle.FixedDialog;
            MaximizeBox=MinimizeBox=false;ShowInTaskbar=false;StartPosition=FormStartPosition.CenterParent;
            var label=new Label{Text="Название пресета",Location=new Point(20,16),Size=new Size(440,28)};
            name.Location=new Point(20,50);name.Width=440;name.MaxLength=80;name.Text=initial;name.AccessibleName="Название пресета";
            var save=new ThemedButton{Text="Сохранить",Tag="primary",Location=new Point(212,114),Size=new Size(120,40)};
            var cancel=new ThemedButton{Text="Отмена",DialogResult=DialogResult.Cancel,Location=new Point(344,114),Size=new Size(116,40)};
            save.Click+=delegate{if(PresetName.Length>0)DialogResult=DialogResult.OK;else name.Focus();};
            Controls.AddRange(new Control[]{label,name,save,cancel});AcceptButton=save;CancelButton=cancel;
            HandleCreated+=delegate{theme.Apply(this);};
            theme.Apply(this);Shown+=delegate{name.SelectAll();name.Focus();};
        }
    }
}
