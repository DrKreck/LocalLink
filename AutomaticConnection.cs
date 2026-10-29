using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LocalLink
{
    public sealed partial class MainForm
    {
        readonly CheckBox automaticConnection=new ThemedCheckBox(),connectOnOpen=new ThemedCheckBox();
        AutomaticSetupCache automaticMethods;
        AppUpdateStartupGate updateStartupGate;
        CancellationTokenSource automaticLifetime;
        bool automaticConnecting,automaticStartupHandled,directConnectionAvailable;

        void InitializeAutomaticConnection()
        {
            automaticConnection.Checked=settings.AutomaticSetup;connectOnOpen.Checked=settings.ConnectOnOpen;
            try{automaticMethods=new AutomaticSetupCache(preview||!persistPreferences?
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"automatic-methods-preview.json"):AutomaticSetupCache.DefaultPath);}
            catch(Exception error){RecordFailure("automatic",error);}
            automaticConnection.CheckedChanged+=delegate{if(!loadingPreset){Save();RefreshControls();}};
            connectOnOpen.CheckedChanged+=delegate{if(!loadingPreset){Save();RefreshStartup();}};
            if(!preview&&persistPreferences)Shown+=async delegate{await HandleAutomaticOpeningAsync();};
        }
        void BuildAutomaticSettings(TableLayoutPanel grid,int row)
        {
            var options=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=Padding.Empty};
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            options.RowStyles.Add(new RowStyle(SizeType.Percent,35));options.RowStyles.Add(new RowStyle(SizeType.Percent,35));options.RowStyles.Add(new RowStyle(SizeType.Percent,30));
            automaticConnection.Text="Автоматически подбирать подключение";automaticConnection.Name="AutomaticConnection";
            connectOnOpen.Text="Подключаться при открытии LocalLink";connectOnOpen.Name="ConnectOnOpen";
            foreach(var box in new[]{automaticConnection,connectOnOpen}){box.Dock=DockStyle.Fill;box.AutoSize=true;box.Margin=Padding.Empty;options.Controls.Add(box);}
            options.Controls.Add(new Label{Text="Компоненты и метод настраиваются автоматически.",Dock=DockStyle.Fill,Tag="muted",AutoEllipsis=true,
                Font=AppTheme.Font(9.5f,FontStyle.Regular),TextAlign=ContentAlignment.MiddleLeft,Margin=Padding.Empty});
            var card=Field(grid,"Подключение",options,0,row,140);if(grid.ColumnCount>1)grid.SetColumnSpan(card,grid.ColumnCount);
            tips.SetToolTip(automaticConnection,"При подключении проверяет сохранённый метод и при необходимости подбирает другой. Во время работающего соединения методы не переключаются.");
            tips.SetToolTip(connectOnOpen,"После включения этой опции подключение запускается при следующем открытии, в том числе из автозапуска. Windows может запросить права.");
        }
        async Task HandleAutomaticOpeningAsync()
        {
            if(automaticStartupHandled||preview||!persistPreferences||closing||updateStartupGate!=null)return;
            automaticStartupHandled=true;
            if(resumeStartAfterElevation||connectOnOpen.Checked)
            {resumeStartAfterElevation=false;await StartAsync();}
            else if(await EnsureDependenciesAsync()&&connectOnOpen.Checked&&!closing&&!IsDisposed)await StartAsync();
        }
        void TickAutomaticOpening()
        {
            if(updateStartupGate!=null&&!closing&&!IsDisposed)
            {
                if(!updateStartupGate.CanResume())return;
                updateStartupGate=null;RefreshControls();
                HandleAutomaticOpeningAsync().ContinueWith(task=>{var ignored=task.Exception;},TaskContinuationOptions.OnlyOnFaulted);
                return;
            }
            if(preview||!persistPreferences||closing||automaticStartupHandled||!startInTray||!connectOnOpen.Checked||!IsHandleCreated||busy||featureDialogOpen)return;
            HandleAutomaticOpeningAsync().ContinueWith(task=>{var ignored=task.Exception;},TaskContinuationOptions.OnlyOnFaulted);
        }
        void RefreshAutomaticControls()
        {
            if(updateStartupGate!=null)
            {primary.Enabled=false;primary.Text="Завершение…";status.Text="Завершение обновления";automaticConnection.Enabled=connectOnOpen.Enabled=false;return;}
            primary.Enabled=true;
            bool editable=!preview&&!closing&&!busy&&!automaticConnecting&&!engine.IsRunning&&!recoveryPolicy.Pending;
            automaticConnection.Enabled=editable&&mode.SelectedIndex==0;connectOnOpen.Enabled=editable;
            if(automaticConnection.Checked&&mode.SelectedIndex==0){strategy.Enabled=false;discordStrategy.Enabled=false;}
            if(automaticConnecting&&!engine.IsRunning){status.Text=busy?workState+"…":"Подготовка…";primary.Text="Отменить";}
            else if(directConnectionAvailable&&!busy&&!engine.IsRunning){status.Text="Проверенные узлы доступны";primary.Text="Проверить снова";}
            tips.SetToolTip(strategy,automaticConnection.Checked?"Для ручного выбора отключите автоматическую настройку в разделе «Основное».":"Метод обработки выбранных сервисов.");
        }
        void CancelAutomaticConnection()
        {directConnectionAvailable=false;if(automaticLifetime!=null)automaticLifetime.Cancel();}
        public void PrepareUpdateStartup(string[] args)
        {updateStartupGate=AppUpdateHealth.CreateStartupGate(args);RefreshControls();}

        async Task StartAutomaticConnectionAsync()
        {
            // Hidden native fixtures never inspect routes, download, elevate,
            // run public probes or start a component.
            if(preview||!persistPreferences||automaticConnecting||busy||closing||updateStartupGate!=null||engine.IsRunning)return;
            automaticConnecting=true;directConnectionAvailable=false;DisarmRecovery();
            automaticLifetime=CancellationTokenSource.CreateLinkedTokenSource(featureLifetime.Token);
            bool handoffToElevation=false;
            string originalStrategy=methods[Math.Max(0,strategy.SelectedIndex)];
            string originalDiscord=discordStrategy.SelectedIndex>0?methods[discordStrategy.SelectedIndex-1]:"";
            bool accepted=false;
            try
            {
                ResetEngineFailureForNewStart();Record("automatic_setup_started");
                if(!await EnsureDependenciesAsync())return;
                automaticLifetime.Token.ThrowIfCancellationRequested();
                await PrepareSelectedListAsync();automaticLifetime.Token.ThrowIfCancellationRequested();
                SavePreferencesRequired();
                // Elevation remains a standard UAC request; the replacement
                // window resumes this workflow, not a raw unverified start.
                if(!Engine.IsAdmin())
                {
                    automaticConnecting=false;RefreshControls();
                    if(!Visible)RestoreWindow();
                    Permit(true);handoffToElevation=exitRequested||closing;return;
                }
                Begin("Автонастройка");operation.CancelAfter(TimeSpan.FromMinutes(8));
                var token=operation.Token;var selected=Selected();
                if(selected.Count==0){summary.Text="Выберите приложения в настройках.";return;}
                string networkKey=currentNetwork!=null&&currentNetwork.Available?currentNetwork.Key:"";
                string environment=AutomaticEnvironmentFingerprint();
                var value=CaptureSettings();
                bool overrideVpn=ignoreVpn.Checked;ignoreVpn.Checked=false;
                bool vpnHints=NetworkInfo.HasLikelyVpn();bool canLearn=!vpnHints&&!overrideVpn;
                AutomaticMethodChoice cached=!canLearn||automaticMethods==null?null:automaticMethods.Find(networkKey,value,DateTime.UtcNow,environment);
                if(cached!=null)ApplyAutomaticMethods(cached.Strategy,cached.DiscordStrategy);
                var spec=Spec();Profiles.Build(spec);ResetRows();
                summary.Text=cached==null?"Проверяем подключение…":"Проверяем сохранённый метод…";
                await StartAutomaticProfileAsync(spec,token);
                var observations=await ProbeAutomaticServicesAsync(selected,token);
                if(AutomaticSetupPolicy.PublicReachabilityReady(CaptureSettings(),observations))
                {
                    FinishAutomaticConnection(spec,observations,null,networkKey,environment,canLearn);accepted=true;
                    await PrepareAutomaticApplicationsAsync(token);
                    return;
                }
                engine.Stop();
                if(cached!=null)
                {
                    try{automaticMethods.Forget(networkKey,value,environment);}catch(Exception error){RecordFailure("automatic",error);}
                    ApplyAutomaticMethods(originalStrategy,originalDiscord);spec=Spec();
                }
                // A running VPN is never disabled or silently ignored. A raw
                // saved profile can still be enabled without adapting its method.
                if(!overrideVpn&&vpnHints)
                {
                    await StartAutomaticProfileAsync(spec,token);SavePreferencesRequired();ArmRecovery(spec);accepted=true;
                    summary.Text="Фильтр включён. Автоподбор при VPN пропущен.";await PrepareAutomaticApplicationsAsync(token);return;
                }
                var tls=selected.Where(id=>id!="telegram").ToList();TuningResult result=null;
                if(tls.Count>0)
                {
                    summary.Text="Подбираем подключение…";
                    result=await Tuning.RunAsync(engine,spec,tls,new Progress<CheckRow>(row=>{if(!token.IsCancellationRequested)AddRow(row);}),
                        phase=>{if(!closing&&!IsDisposed){summary.Text="Подбираем подключение…";Write(phase);}},token,overrideVpn);
                    lastTuning=result;token.ThrowIfCancellationRequested();
                    if(result.Verified)ApplyAutomaticMethods(result.Strategy,originalDiscord);
                    spec=Spec();
                }
                if(!engine.IsRunning)
                {
                    // No evidence for a method change may mean that public nodes
                    // already work without a filter. Recheck rather than invent a win.
                    observations=await ProbeAutomaticServicesAsync(selected,token);
                    if(AutomaticSetupPolicy.PublicReachabilityReady(CaptureSettings(),observations))
                    {summary.Text="Проверенные узлы доступны без фильтра.";SavePreferencesRequired();directConnectionAvailable=true;accepted=true;await PrepareAutomaticApplicationsAsync(token);return;}
                    await StartAutomaticProfileAsync(spec,token);
                }
                observations=await ProbeAutomaticServicesAsync(selected,token);
                if(AutomaticSetupPolicy.ShouldTryDiscordMethod(CaptureSettings(),observations))
                {
                    engine.Stop();summary.Text="Настраиваем Discord…";
                    var discord=await Tuning.RunDiscordAsync(engine,spec,selected,new Progress<CheckRow>(row=>{if(!token.IsCancellationRequested)AddRow(row);}),
                        phase=>{if(!closing&&!IsDisposed)Write(phase);},token,overrideVpn);
                    token.ThrowIfCancellationRequested();lastTuning=discord;
                    if(discord.Verified){ApplyAutomaticMethods(spec.Strategy,discord.Strategy);result=discord;}
                    spec=Spec();if(!engine.IsRunning)await StartAutomaticProfileAsync(spec,token);
                    observations=await ProbeAutomaticServicesAsync(selected,token);
                }
                FinishAutomaticConnection(spec,observations,result,networkKey,environment,canLearn);accepted=true;
                await PrepareAutomaticApplicationsAsync(token);
            }
            catch(OperationCanceledException){DisarmRecovery();TryStop();if(!closing&&!IsDisposed)summary.Text="Автонастройка отменена.";}
            catch(Exception error)
            {DisarmRecovery();RecordFailure("automatic",error);TryStop();if(!closing&&!IsDisposed){engineFailure=true;summary.Text="Не удалось настроить подключение. Откройте «Что не работает?».";}}
            finally
            {
                if(!accepted&&!handoffToElevation&&!closing&&!IsDisposed)ApplyAutomaticMethods(originalStrategy,originalDiscord);
                if(automaticLifetime!=null){automaticLifetime.Dispose();automaticLifetime=null;}
                automaticConnecting=false;End();
            }
        }
        void ApplyAutomaticMethods(string method,string discord)
        {
            if(!Profiles.IsTlsStrategy(method)||!String.IsNullOrEmpty(discord)&&!Profiles.IsTlsStrategy(discord))throw new ArgumentException("Неизвестный метод.");
            strategy.SelectedIndex=methods.IndexOf(method);discordStrategy.SelectedIndex=String.IsNullOrEmpty(discord)?0:methods.IndexOf(discord)+1;
        }
        async Task StartAutomaticProfileAsync(ProfileSpec spec,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using(var prepared=await engine.PrepareStartAsync(spec,token)){token.ThrowIfCancellationRequested();engine.StartPrepared(prepared);}
            await Task.Delay(600,token);if(!engine.IsRunning)throw new IOException("Сетевой модуль завершился.");
        }
        async Task<List<CheckRow>> ProbeAutomaticServicesAsync(List<string> ids,CancellationToken token)
        {
            var rows=await ServiceDiagnostics.RunAsync(ids,new Progress<CheckRow>(row=>{if(automaticConnecting&&!token.IsCancellationRequested)AddRow(row);}),token);
            token.ThrowIfCancellationRequested();return rows;
        }
        void FinishAutomaticConnection(ProfileSpec spec,List<CheckRow> observations,TuningResult result,string networkKey,string environment,bool canLearn)
        {
            bool reachable=AutomaticSetupPolicy.PublicReachabilityReady(CaptureSettings(),observations);
            SavePreferencesRequired();ArmRecovery(spec);
            try
            {
                canLearn=canLearn&&!NetworkInfo.HasLikelyVpn()&&currentNetwork!=null&&currentNetwork.Key==networkKey;
                if(canLearn&&automaticMethods!=null&&!String.IsNullOrEmpty(networkKey))
                {
                    var value=CaptureSettings();
                    if(reachable)automaticMethods.RememberReachable(networkKey,value,observations,DateTime.UtcNow,environment);
                    else if(result!=null)automaticMethods.RememberVerified(networkKey,value,result,DateTime.UtcNow,environment);
                }
            }
            catch(Exception error){RecordFailure("automatic",error);}
            if(canLearn&&reachable)RememberWorkingSettings(CaptureSettings());
            summary.Text=reachable?"Подключение настроено.":"Фильтр включён. Доступ сервисов не подтверждён — «Что не работает?».";
            Record("automatic_setup_finished","enabled",engine.IsRunning,"passed",reachable);
        }
        string AutomaticEnvironmentFingerprint()
        {
            string engineVersion=Path.GetFileName(dependencies.ComponentsRoot);
            if(!publicLists.Checked)return ApplicationVersion()+":"+engineVersion+":builtin-v1";
            try{return ApplicationVersion()+":"+engineVersion+":list:"+Path.GetFileName(PublicLists.GetActivePath(CancellationToken.None));}
            catch(IOException){return ApplicationVersion()+":list:unavailable";}
        }
        Task PrepareAutomaticApplicationsAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try{if(Selected().Contains("telegram")&&TelegramDesktopInstalled())LaunchTelegramBridge(true);}
            catch(Exception error){RecordFailure("telegram",error);Write("Фильтр работает; модуль Telegram можно повторно запустить в разделе «Приложения».");}
            return Task.FromResult(0);
        }
        static bool TelegramDesktopInstalled()
        {
            if(File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Telegram Desktop","Telegram.exe"))||
                File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Telegram Desktop","Telegram.exe"))||
                File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Telegram Desktop","Telegram.exe")))return true;
            // A registered URI covers Store/portable clients without reading
            // their executable command, account data or proxy preferences.
            try
            {
                using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\tg"))if(key!=null&&key.GetValue("URL Protocol")!=null)return true;
                using(var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Classes\tg"))return key!=null&&key.GetValue("URL Protocol")!=null;
            }
            catch(System.Security.SecurityException){return false;}
            catch(UnauthorizedAccessException){return false;}
        }
    }
}
