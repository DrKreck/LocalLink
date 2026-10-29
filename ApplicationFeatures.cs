using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LocalLink
{
    public sealed partial class MainForm
    {
        readonly Button troubleshoot=new ThemedButton(),rememberNetwork=new ThemedButton(),forgetNetwork=new ThemedButton(),applyNetwork=new ThemedButton();
        readonly Button checkUpdate=new ThemedButton(),rollbackUpdate=new ThemedButton(),networkHintApply=new ThemedButton();
        readonly Label networkStatus=new Label(),updateStatus=new Label(),networkHintText=new Label();
        readonly CheckBox autoRecovery=new ThemedCheckBox();
        readonly RecoveryPolicy recoveryPolicy=new RecoveryPolicy();
        NetworkPresetStore networkPresetStore;
        NetworkSnapshot currentNetwork;
        ProfileSpec recoverySpec,lastOwnedSpec;
        Panel networkHint;
        TableLayoutPanel diagnosticsLayout;
        string suggestedNetworkPreset="",suggestedNetworkPresetName="";
        bool featureDialogOpen,updatingApplication,recovering,featureEventsAttached;
        int networkObservationGeneration;
        CancellationTokenSource featureLifetime=new CancellationTokenSource();

        void InitializeApplicationFeatures()
        {
            string path=preview||!persistPreferences?Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"network-presets-preview.json"):NetworkPresetStore.DefaultPath;
            networkPresetStore=new NetworkPresetStore(path);
            troubleshoot.Click+=delegate{OpenTroubleshooting();};
            rememberNetwork.Click+=delegate{RememberCurrentNetwork();};
            forgetNetwork.Click+=delegate{ForgetCurrentNetwork();};
            applyNetwork.Click+=delegate{ApplyNetworkPreset();};networkHintApply.Click+=delegate{ApplyNetworkPreset();};
            checkUpdate.Click+=async delegate{await CheckApplicationUpdateAsync();};
            rollbackUpdate.Click+=async delegate{await RollbackApplicationUpdateAsync();};
            InitializeUpdateExperience();
            autoRecovery.Checked=settings.AutomaticRecovery;
            autoRecovery.CheckedChanged+=delegate
            {
                if(!autoRecovery.Checked)DisarmRecovery();
                else if(engine.IsRunning&&!preview&&!busy&&lastOwnedSpec!=null)ArmRecovery(lastOwnedSpec);
                Save();
            };
            if(preview||!persistPreferences)return;
            SystemEvents.PowerModeChanged+=ApplicationPowerModeChanged;
            NetworkChange.NetworkAddressChanged+=ApplicationNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged+=ApplicationNetworkAvailabilityChanged;
            featureEventsAttached=true;
            Shown+=delegate{RequestNetworkObservation();};
        }

        Control BuildNetworkSuggestion()
        {
            networkHint=new ThemedPanel{Dock=DockStyle.Fill,Padding=new Padding(12,6,12,6),Margin=new Padding(0,0,0,8),Visible=false};
            var row=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,160));
            row.RowStyles.Add(new RowStyle(SizeType.Percent,100));networkHint.Controls.Add(row);
            networkHintText.Dock=DockStyle.Fill;networkHintText.Margin=new Padding(0,0,10,0);networkHintText.TextAlign=ContentAlignment.MiddleLeft;
            networkHintText.AutoEllipsis=true;networkHintText.Font=AppTheme.Font(9.5f,FontStyle.Regular);
            Button(networkHintApply,"Применить пресет",false);row.Controls.Add(networkHintText);row.Controls.Add(networkHintApply);
            return networkHint;
        }

        void BuildNetworkSettings(TableLayoutPanel basic)
        {
            var actions=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=Padding.Empty};
            actions.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,48));actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,28));actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,24));
            Button(rememberNetwork,"Запомнить для сети",false);Button(applyNetwork,"Применить",false);Button(forgetNetwork,"Забыть",false);
            applyNetwork.Margin=forgetNetwork.Margin=new Padding(8,0,0,0);
            actions.Controls.Add(rememberNetwork);actions.Controls.Add(applyNetwork);actions.Controls.Add(forgetNetwork);
            basic.SetColumnSpan(Field(basic,"Пресет для текущей сети",actions,0,5,92),2);
            networkStatus.Dock=DockStyle.Fill;networkStatus.Margin=new Padding(12,0,12,6);networkStatus.Tag="muted";
            networkStatus.Font=AppTheme.Font(9.5f,FontStyle.Regular);networkStatus.TextAlign=ContentAlignment.MiddleLeft;networkStatus.AutoEllipsis=true;
            basic.Controls.Add(networkStatus,0,6);basic.SetColumnSpan(networkStatus,2);basic.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
            tips.SetToolTip(rememberNetwork,"Привязать выбранный сохранённый пресет к этой сети. При смене сети LocalLink предложит его, но не переключит соединения сам.");
            tips.SetToolTip(forgetNetwork,"Убрать только привязку к сети. Пресет останется.");
        }

        void BuildUpdateSettings(TableLayoutPanel everyday)
        {
            var actions=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
            actions.RowStyles.Add(new RowStyle(SizeType.Percent,100));actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,60));actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,40));
            Button(checkUpdate,"Проверить обновления",false);Button(rollbackUpdate,"Предыдущая версия",false);rollbackUpdate.Margin=new Padding(8,0,0,0);
            actions.Controls.Add(checkUpdate);actions.Controls.Add(rollbackUpdate);Field(everyday,"LocalLink "+ApplicationVersion(),actions,0,3,92);
            updateStatus.Dock=DockStyle.Fill;updateStatus.Tag="muted";updateStatus.Margin=new Padding(12,0,12,8);updateStatus.AutoSize=true;
            updateStatus.Font=AppTheme.Font(9.5f,FontStyle.Regular);everyday.Controls.Add(updateStatus,0,4);everyday.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            autoRecovery.Text="Восстанавливать фильтр после сбоя и сна";autoRecovery.AutoSize=true;autoRecovery.Dock=DockStyle.Fill;
            Field(everyday,"Соединение",autoRecovery,0,5,88);
            automaticUpdateCheck.Text="Предлагать новые версии";automaticUpdateCheck.AutoSize=true;automaticUpdateCheck.Dock=DockStyle.Fill;
            Field(everyday,"Обновления",automaticUpdateCheck,0,6,88);
            tips.SetToolTip(autoRecovery,"До трёх попыток с паузами, только прежний метод. Ручное выключение отменяет восстановление; во время диагностики и обновления повторов нет.");
            tips.SetToolTip(checkUpdate,"Проверить стабильный релиз DrKreck/LocalLink на GitHub. Установка — только после подтверждения.");
            tips.SetToolTip(automaticUpdateCheck,"Проверка при запуске, не чаще раза в сутки. Ничего не устанавливается без согласия. Ручная проверка доступна всегда.");
        }

        static string ApplicationVersion()
        {var value=typeof(MainForm).Assembly.GetName().Version;return value.Major+"."+value.Minor+"."+value.Build;}

        void RefreshApplicationFeatureControls()
        {
            bool allowed=!preview&&!closing&&!busy&&!automaticConnecting&&updateStartupGate==null&&!exitPending&&!featureDialogOpen&&!updatingApplication;
            troubleshoot.Enabled=allowed;checkUpdate.Enabled=allowed&&!checkingUpdateInBackground;
            // Availability of the previous verified install is checked on click,
            // not by repeatedly scanning disk from the 350 ms UI timer.
            rollbackUpdate.Enabled=allowed&&!checkingUpdateInBackground;
            bool known=currentNetwork!=null&&currentNetwork.Available&&!String.IsNullOrEmpty(currentNetwork.Key);
            rememberNetwork.Enabled=allowed&&!recoveryPolicy.Pending&&known&&!String.IsNullOrEmpty(ChosenPresetId());
            forgetNetwork.Enabled=allowed&&known&&!String.IsNullOrEmpty(suggestedNetworkPreset);
            bool canApply=allowed&&!engine.IsRunning&&!recoveryPolicy.Pending&&!String.IsNullOrEmpty(suggestedNetworkPreset);
            applyNetwork.Enabled=networkHintApply.Enabled=canApply;
            autoRecovery.Enabled=!busy&&!automaticConnecting&&updateStartupGate==null&&!featureDialogOpen&&!updatingApplication;
            automaticUpdateCheck.Enabled=!preview&&!closing&&updateStartupGate==null&&!featureDialogOpen&&!updatingApplication;
            networkStatus.Text=!known?"Сеть ещё не определена.":String.IsNullOrEmpty(suggestedNetworkPreset)?"Для этой сети пресет не сохранён.":"Для этой сети: "+suggestedNetworkPresetName;
            bool showHint=known&&!String.IsNullOrEmpty(suggestedNetworkPreset)&&ChosenPresetId()!=suggestedNetworkPreset;
            if(networkHint!=null)
            {
                networkHint.Visible=showHint;
                if(diagnosticsLayout!=null)diagnosticsLayout.RowStyles[0].Height=showHint?(float)Math.Ceiling(68*DeviceDpi/96.0):0;
                networkHintText.Text="Для этой сети: "+suggestedNetworkPresetName+(engine.IsRunning?". Доступен после выключения фильтра.":"");
            }
        }

        void RememberCurrentNetwork()
        {
            if(preview||busy||automaticConnecting||closing||featureDialogOpen||recoveryPolicy.Pending||currentNetwork==null||!currentNetwork.Available||String.IsNullOrEmpty(ChosenPresetId()))return;
            try
            {
                string id=ChosenPresetId();var saved=presetStore.Load(id);var current=CaptureSettings();
                if(!PresetSettingsEqual(saved,current))
                {
                    var entry=presetChoice.SelectedItem as PresetEntry;
                    if(MessageBox.Show(this,"Сохранить текущие настройки в пресет «"+(entry==null?"":entry.Name)+"» и привязать его к сети?","Пресет сети",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
                    presetStore.Save(entry.Name,current,id);
                }
                networkPresetStore.Remember(currentNetwork.Key,ChosenPresetId());RefreshNetworkBinding();
                Record("network_preset_saved");Write("Пресет привязан к текущей сети.");
            }
            catch(Exception error){RecordFailure("network_profile",error);Write("Не удалось сохранить привязку к сети.");}
        }

        static bool PresetSettingsEqual(AppSettings left,AppSettings right)
        {
            var serializer=new System.Web.Script.Serialization.JavaScriptSerializer();
            return serializer.Serialize(AppSettings.Normalize(left))==serializer.Serialize(AppSettings.Normalize(right));
        }

        void ForgetCurrentNetwork()
        {
            if(preview||busy||automaticConnecting||closing||currentNetwork==null||String.IsNullOrEmpty(currentNetwork.Key))return;
            try{networkPresetStore.Forget(currentNetwork.Key);RefreshNetworkBinding();Record("network_preset_forgotten");}
            catch(Exception error){RecordFailure("network_profile",error);Write("Не удалось убрать привязку к сети.");}
        }

        void ApplyNetworkPreset()
        {
            if(preview||busy||automaticConnecting||engine.IsRunning||recoveryPolicy.Pending||closing||featureDialogOpen||String.IsNullOrEmpty(suggestedNetworkPreset))return;
            for(int index=1;index<presetChoice.Items.Count;index++)
                if(((PresetEntry)presetChoice.Items[index]).Id==suggestedNetworkPreset)
                {if(presetChoice.SelectedIndex==index)LoadChosenPreset();else presetChoice.SelectedIndex=index;Record("network_preset_applied");RefreshApplicationFeatureControls();return;}
            Write("Пресет этой сети не найден. Сохраните новый или уберите привязку.");
        }

        void RefreshNetworkBinding()
        {
            suggestedNetworkPreset="";suggestedNetworkPresetName="";
            if(currentNetwork!=null&&currentNetwork.Available&&!String.IsNullOrEmpty(currentNetwork.Key))
            {
                string id=networkPresetStore.Find(currentNetwork.Key);
                var entry=presetStore.List().FirstOrDefault(x=>x.Id==id);
                if(entry!=null){suggestedNetworkPreset=id;suggestedNetworkPresetName=entry.Name;}
            }
            RefreshApplicationFeatureControls();
        }

        void ApplicationNetworkChanged(object sender,EventArgs args){PostFeatureAction(RequestNetworkObservation);}
        void ApplicationNetworkAvailabilityChanged(object sender,NetworkAvailabilityEventArgs args)
        {PostFeatureAction(delegate{recoveryPolicy.SetNetworkAvailable(args.IsAvailable,DateTime.UtcNow);RequestNetworkObservation();});}
        void ApplicationPowerModeChanged(object sender,PowerModeChangedEventArgs args)
        {
            PostFeatureAction(delegate
            {
                if(args.Mode==PowerModes.Suspend){recoveryPolicy.Suspend();if(recovering&&operation!=null)operation.Cancel();Record("system_suspended");}
                else if(args.Mode==PowerModes.Resume)
                {recoveryPolicy.Resume(DateTime.UtcNow);if(engine.IsRunning)recoveryPolicy.MarkRunning(DateTime.UtcNow);Record("system_resumed");RequestNetworkObservation();}
            });
        }
        void PostFeatureAction(Action action)
        {
            if(closing||IsDisposed||!IsHandleCreated)return;
            try{BeginInvoke((Action)delegate{if(!closing&&!IsDisposed)action();});}catch(InvalidOperationException){}
        }
        async void RequestNetworkObservation()
        {
            if(preview||!persistPreferences||closing||featureLifetime.IsCancellationRequested)return;
            int generation=++networkObservationGeneration;var token=featureLifetime.Token;
            try
            {
                await Task.Delay(1800,token);
                if(generation!=networkObservationGeneration)return;
                var observed=await Task.Run(()=>NetworkIdentityObserver.Capture(),token);
                if(closing||IsDisposed||generation!=networkObservationGeneration||token.IsCancellationRequested)return;
                recoveryPolicy.SetNetworkAvailable(NetworkInterface.GetIsNetworkAvailable(),DateTime.UtcNow);
                AcceptNetworkObservation(observed);
            }
            catch(OperationCanceledException){}
            catch(Exception error){RecordFailure("network_profile",error);}
        }
        void AcceptNetworkObservation(NetworkSnapshot observed)
        {
            if(observed==null)throw new ArgumentNullException("observed");
            bool changed=currentNetwork==null||currentNetwork.Key!=observed.Key||currentNetwork.Available!=observed.Available;
            currentNetwork=observed;
            if(changed){RefreshNetworkBinding();Record("network_observed","enabled",observed.Available);}
        }

        static ProfileSpec CopyProfile(ProfileSpec source)
        {
            return new ProfileSpec{Mode=source.Mode,Strategy=source.Strategy,DiscordStrategy=source.DiscordStrategy,Domains=source.Domains,
                Endpoint=source.Endpoint,Port=source.Port,Quic=source.Quic,DiscordVoice=source.DiscordVoice,TelegramNative=source.TelegramNative,
                HostListFile=source.HostListFile,HostListExcludeFile=source.HostListExcludeFile};
        }
        void ArmRecovery(ProfileSpec spec)
        {
            lastOwnedSpec=CopyProfile(spec);
            if(preview||!autoRecovery.Checked)return;
            recoverySpec=CopyProfile(spec);recoveryPolicy.UserStarted(DateTime.UtcNow);recoveryPolicy.MarkRunning(DateTime.UtcNow);
            // Unknown NLM identity does not prevent using an otherwise available
            // adapter for recovery; it only disables per-network preset binding.
            recoveryPolicy.SetNetworkAvailable(NetworkInterface.GetIsNetworkAvailable(),DateTime.UtcNow);
        }
        void DisarmRecovery(){recoveryPolicy.UserStopped();recoverySpec=null;}
        void TickApplicationFeatures()
        {
            TickAutomaticOpening();
            TickUpdateExperience();
            if(preview||!persistPreferences||closing||automaticConnecting||recovering||featureDialogOpen||updatingApplication)return;
            if(engine.IsRunning){recoveryPolicy.ObserveHealthy(DateTime.UtcNow);return;}
            if(!busy&&recoverySpec!=null&&autoRecovery.Checked&&recoveryPolicy.TryBeginAttempt(DateTime.UtcNow))RecoverFilterAsync();
        }
        async void RecoverFilterAsync()
        {
            if(closing||busy||recovering||recoverySpec==null)return;
            recovering=true;var sameProfile=CopyProfile(recoverySpec);Begin("Восстановление");
            Record("recovery_started","count",recoveryPolicy.Attempts);
            try
            {
                if(!Engine.IsAdmin())throw new InvalidOperationException("Для восстановления нужны прежние права запуска.");
                var token=operation.Token;
                using(var prepared=await engine.PrepareStartAsync(sameProfile,token))
                {token.ThrowIfCancellationRequested();if(closing||!recoveryPolicy.DesiredRunning||recoveryPolicy.IsSuspended)return;engine.StartPrepared(prepared);}
                await Task.Delay(600,token);token.ThrowIfCancellationRequested();
                if(!engine.IsRunning)throw new IOException("Повторный запуск не завершён.");
                engineFailure=false;recoveryPolicy.MarkRunning(DateTime.UtcNow);Record("recovery_succeeded");
                summary.Text="Фильтр восстановлен. Метод не менялся.";
            }
            catch(OperationCanceledException){CompleteCancelledRecovery();}
            catch(Exception error)
            {
                RecordFailure("recovery",error);engine.TakeUnexpectedExit();TryStop();recoveryPolicy.AttemptFailed(DateTime.UtcNow);
                if(!closing)summary.Text=recoveryPolicy.Exhausted?"Не удалось восстановить фильтр. Нажмите «Включить» или «Что не работает?». Таймер остановлен.":"Повторный запуск не удался. Следующая попытка — после паузы.";
            }
            finally{recovering=false;End();}
        }
        void CompleteCancelledRecovery()
        {
            if(closing)return;
            TryStop();
            // Resume can arrive before the cancelled preflight continuation.
            // The owned stop is expected, so it won't raise a child-exit event.
            if(recoveryPolicy.DesiredRunning)recoveryPolicy.UnexpectedExit(DateTime.UtcNow);
        }

        void OpenTroubleshooting()
        {
            if(preview||busy||automaticConnecting||closing||featureDialogOpen||updatingApplication)return;
            featureDialogOpen=true;recoveryPolicy.Pause(true);RefreshControls();Record("troubleshooting_opened");
            try
            {
                using(var wizard=new TroubleshootingForm(theme,delegate(string destination){BugReport.Export(destination,history,CaptureBugReport());},
                    delegate{return new TroubleshootingState{FilterRunning=engine.IsRunning,Strategy=methods[Math.Max(0,strategy.SelectedIndex)],DiscordStrategy=discordStrategy.SelectedIndex>0?methods[discordStrategy.SelectedIndex-1]:"",Mode=Math.Max(0,mode.SelectedIndex)};}))
                    wizard.ShowDialog(this);
            }
            catch(Exception error){RecordFailure("troubleshooting",error);Write("Не удалось открыть мастер диагностики.");}
            finally{featureDialogOpen=false;recoveryPolicy.Pause(false);RefreshControls();}
        }

        void DisposeApplicationFeatures()
        {
            CancelAutomaticConnection();
            DisarmRecovery();networkObservationGeneration++;featureLifetime.Cancel();
            DisposeUpdateExperience();
            if(featureEventsAttached)
            {SystemEvents.PowerModeChanged-=ApplicationPowerModeChanged;NetworkChange.NetworkAddressChanged-=ApplicationNetworkChanged;NetworkChange.NetworkAvailabilityChanged-=ApplicationNetworkAvailabilityChanged;featureEventsAttached=false;}
            featureLifetime.Dispose();
        }
    }
}
