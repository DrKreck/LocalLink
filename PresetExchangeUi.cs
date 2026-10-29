using System;
using System.IO;
using System.Windows.Forms;

namespace LocalLink
{
    public sealed partial class MainForm
    {
        readonly Button importPreset=new ThemedButton(),exportPreset=new ThemedButton(),restoreWorkingPreset=new ThemedButton(),rememberWorkingPreset=new ThemedButton();
        WorkingSettingsStore workingSettingsStore;
        bool hasWorkingSettings;

        void InitializePresetExchange()
        {
            try
            {
                workingSettingsStore=new WorkingSettingsStore(preview||!persistPreferences?
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"working-preview"):WorkingSettingsStore.DefaultFolder);
                hasWorkingSettings=workingSettingsStore.Load()!=null;
            }
            catch(Exception error){RecordFailure("preset_load",error);}
            importPreset.Click+=delegate{ImportPortablePreset();};
            exportPreset.Click+=delegate{ExportPortablePreset();};
            restoreWorkingPreset.Click+=delegate{RestoreWorkingSettings();};
            rememberWorkingPreset.Click+=delegate{RememberWorkingSettings(CaptureSettings());};
        }
        void BuildPresetExchangeSettings(TableLayoutPanel grid,int row)
        {
            var actions=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Margin=Padding.Empty};
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            actions.RowStyles.Add(new RowStyle(SizeType.Percent,50));actions.RowStyles.Add(new RowStyle(SizeType.Percent,50));
            Button(importPreset,"Импортировать",false);Button(exportPreset,"Экспортировать",false);
            Button(restoreWorkingPreset,"Вернуть рабочие",false);Button(rememberWorkingPreset,"Запомнить рабочие",false);
            importPreset.Margin=new Padding(0,0,8,6);exportPreset.Margin=new Padding(0,0,0,6);
            restoreWorkingPreset.Margin=new Padding(0,0,8,0);rememberWorkingPreset.Margin=Padding.Empty;
            actions.Controls.Add(importPreset,0,0);actions.Controls.Add(exportPreset,1,0);
            actions.Controls.Add(restoreWorkingPreset,0,1);actions.Controls.Add(rememberWorkingPreset,1,1);
            var card=Field(grid,"Пресеты и восстановление",actions,0,row,152);
            if(grid.ColumnCount>1)grid.SetColumnSpan(card,grid.ColumnCount);
            tips.SetToolTip(importPreset,"Прочитать настройки из JSON и сохранить отдельный пресет. Фильтр не запускается.");
            tips.SetToolTip(exportPreset,"Сохранить выбранные категории, домены и параметры подключения одним файлом. Привязки сетей и настройки обновлений не включаются.");
            tips.SetToolTip(restoreWorkingPreset,"Вернуть отмеченные рабочими настройки. Текущие сохранятся отдельным пресетом.");
            tips.SetToolTip(rememberWorkingPreset,"Нажмите, когда проверили, что нужные приложения работают с текущими настройками.");
        }
        void RefreshPresetExchangeControls()
        {
            bool available=!preview&&!closing&&!busy&&updateStartupGate==null&&!exitPending&&!featureDialogOpen&&!updatingApplication&&!automaticConnecting&&!recoveryPolicy.Pending;
            exportPreset.Enabled=rememberWorkingPreset.Enabled=available;
            importPreset.Enabled=available&&!engine.IsRunning;
            restoreWorkingPreset.Enabled=available&&!engine.IsRunning&&hasWorkingSettings;
        }
        bool CanChangePortablePreset()
        {return !preview&&!closing&&!busy&&updateStartupGate==null&&!exitPending&&!featureDialogOpen&&!updatingApplication&&!automaticConnecting&&!engine.IsRunning&&!recoveryPolicy.Pending;}
        void ImportPortablePreset()
        {
            if(!CanChangePortablePreset())return;
            BeginPresetFileDialog();
            try
            {
                using(var dialog=new OpenFileDialog{Title="Импорт пресета LocalLink",Filter="Пресет LocalLink (*.json)|*.json",CheckFileExists=true,Multiselect=false})
                {
                    if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                    var imported=PresetExchange.Read(dialog.FileName);
                    presetStore.Save("До импорта",CaptureSettings());
                    string id=presetStore.Save(imported.Name,imported.Settings);
                    RefreshPresets(id);LoadChosenPreset();Write("Пресет импортирован. Для применения включите соединение.");
                }
            }
            catch(Exception error){RecordFailure("preset_load",error);Write("Не удалось импортировать пресет: "+error.Message);}
            finally{EndPresetFileDialog();}
        }
        void ExportPortablePreset()
        {
            if(preview||closing||busy||featureDialogOpen||updatingApplication||automaticConnecting)return;
            BeginPresetFileDialog();
            try
            {
                using(var dialog=new SaveFileDialog{Title="Экспорт пресета LocalLink",Filter="Пресет LocalLink (*.json)|*.json",DefaultExt="json",AddExtension=true,
                    FileName="LocalLink-preset.json",OverwritePrompt=true})
                {
                    if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                    var entry=presetChoice.SelectedItem as PresetEntry;
                    string name=entry==null||String.IsNullOrEmpty(entry.Id)?"Мой пресет":entry.Name;
                    PresetExchange.Write(dialog.FileName,name,CaptureSettings(),true);Write("Пресет экспортирован.");
                }
            }
            catch(Exception error){RecordFailure("preset_save",error);Write("Не удалось экспортировать пресет: "+error.Message);}
            finally{EndPresetFileDialog();}
        }
        void BeginPresetFileDialog(){featureDialogOpen=true;recoveryPolicy.Pause(true);RefreshControls();}
        void EndPresetFileDialog(){featureDialogOpen=false;recoveryPolicy.Pause(false);if(!closing&&!IsDisposed)RefreshControls();}
        void RememberWorkingSettings(AppSettings value)
        {
            if(preview||closing||workingSettingsStore==null)return;
            try{workingSettingsStore.Remember(value);hasWorkingSettings=true;RefreshPresetExchangeControls();Write("Рабочие настройки сохранены.");}
            catch(Exception error){RecordFailure("preset_save",error);Write("Не удалось сохранить рабочие настройки: "+error.Message);}
        }
        void RestoreWorkingSettings()
        {
            if(!CanChangePortablePreset()||workingSettingsStore==null)return;
            try
            {
                var value=workingSettingsStore.Load();
                if(value==null){hasWorkingSettings=false;RefreshPresetExchangeControls();Write("Рабочие настройки ещё не сохранены.");return;}
                presetStore.Save("До восстановления",CaptureSettings());
                string id=presetStore.Save("Рабочая конфигурация",value);
                RefreshPresets(id);LoadChosenPreset();Write("Рабочие настройки восстановлены. Для применения включите соединение.");
            }
            catch(Exception error){RecordFailure("preset_load",error);Write("Не удалось восстановить рабочие настройки: "+error.Message);}
        }
    }
}
