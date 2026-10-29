using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LocalLink
{
    static class Program
    {
        const string RestoreEventName="Local\\LocalLink.Restore.v1";
        static bool RestoreExisting()
        {
            try{using(var signal=EventWaitHandle.OpenExisting(RestoreEventName,EventWaitHandleRights.Modify)){signal.Set();return true;}}
            catch(WaitHandleCannotBeOpenedException){return false;}
            catch(UnauthorizedAccessException){return false;}
            catch(System.Security.SecurityException){return false;}
        }
        static EventWaitHandle CreateRestoreSignal()
        {
            try
            {
                var security=new EventWaitHandleSecurity();security.SetAccessRuleProtection(true,false);
                using(var identity=WindowsIdentity.GetCurrent())
                    security.AddAccessRule(new EventWaitHandleAccessRule(identity.User,EventWaitHandleRights.FullControl,AccessControlType.Allow));
                bool created;return new EventWaitHandle(false,EventResetMode.AutoReset,RestoreEventName,out created,security);
            }
            catch(UnauthorizedAccessException){return null;}
            catch(System.Security.SecurityException){return null;}
            catch(WaitHandleCannotBeOpenedException){return null;}
            catch(IOException){return null;}
        }
        [STAThread] static void Main(string[] args)
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string[] previews={"--render","--render-checks","--render-checks-dark","--render-checks-light","--render-settings","--render-dark","--render-light","--render-settings-dark","--render-settings-light","--render-dark-settings","--render-light-settings","--render-startup-dark","--render-startup-light","--render-small"};
            bool render=args.Length==2&&previews.Contains(args[0]),owned;
            bool elevatedStart=args.Length==1&&args[0]=="--elevated-start";
            bool elevatedRestart=elevatedStart||(args.Length==1&&args[0]=="--elevated-restart");
            bool startup=args.Length==1&&args[0]==StartupRegistration.StartupArgument;
            bool updateHealth=args.Length==3&&args[0]=="--update-health";
            using(var mutex=new Mutex(true,render?"Local\\LocalLink.Preview.v3":"Local\\LocalLink.UI.v1",out owned))
            {
                if(!owned)
                {
                    // Login startup must never raise an already running window.
                    if(startup)return;
                    // Ordinary double-launch only asks the existing window to show itself.
                    // Elevation waits for the previous owner instead of restoring it.
                    if(!render&&!elevatedRestart&&RestoreExisting())return;
                    try{owned=mutex.WaitOne(elevatedRestart||render?8000:350);}catch(AbandonedMutexException){owned=true;}
                    if(!owned&&!render&&!elevatedRestart&&RestoreExisting())return;
                    if(!owned){MessageBox.Show("LocalLink уже запущен. Откройте его через значок в трее возле часов (в том числе в скрытых значках). Для смены версии выберите «Выход» в меню значка.");return;}
                }
                try
                {
                    using(var diagnostics=DiagnosticHistory.CreateDefault(!render))
                    {
                    UnhandledExceptionEventHandler domainError=delegate(object sender,UnhandledExceptionEventArgs e){diagnostics.RecordException("unhandled_domain",e.ExceptionObject as Exception);};
                    EventHandler<UnobservedTaskExceptionEventArgs> taskError=delegate(object sender,UnobservedTaskExceptionEventArgs e){diagnostics.RecordException("unobserved_task",e.Exception);};
                    if(!render){Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);AppDomain.CurrentDomain.UnhandledException+=domainError;TaskScheduler.UnobservedTaskException+=taskError;}
                    try
                    {
                    using(var restore=render?null:CreateRestoreSignal())
                    using(var form=new MainForm(render,args.Length>0&&args[0].IndexOf("-dark",StringComparison.Ordinal)>=0,args.Length>0&&args[0].IndexOf("-light",StringComparison.Ordinal)>=0,restore,true,startup,diagnostics))
                    {
                        if(elevatedRestart&&!render)form.ShowElevatedRestartNotice(elevatedStart);
                        if(updateHealth&&!render){form.PrepareUpdateStartup(args);form.Shown+=delegate{AppUpdateHealth.Acknowledge(args);};}
                        if(render)
                        {
                            form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);
                            form.Show();form.SetPreview(args[0]);Application.DoEvents();
                            // DrawToBitmap cannot reproduce DWM window composition; render
                            // only our client area rather than a misleading classic caption.
                            using(var bitmap=new Bitmap(form.ClientSize.Width,form.ClientSize.Height))
                            {form.Controls[0].DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.ClientSize));bitmap.Save(Path.GetFullPath(args[1]));}
                            form.Close();
                        }
                        else Application.Run(form);
                    }
                    }
                    catch(Exception error)
                    {
                        diagnostics.RecordException("unhandled_ui",error);
                        if(render)throw;
                        Environment.ExitCode=1;
                        MessageBox.Show("LocalLink завершает работу из-за ошибки. После повторного запуска нажмите «Баг-репорт» и сохраните ZIP для разбора.","Ошибка LocalLink",MessageBoxButtons.OK,MessageBoxIcon.Error);
                    }
                    finally{if(!render){AppDomain.CurrentDomain.UnhandledException-=domainError;TaskScheduler.UnobservedTaskException-=taskError;}}
                    }
                }
                finally{mutex.ReleaseMutex();}
            }
        }
    }

    public sealed partial class MainForm : Form
    {
        readonly bool preview,forceDark,forceLight,persistPreferences,startInTray;
        readonly DiagnosticHistory history;readonly bool ownsHistory;
        readonly EventWaitHandle restoreSignal;
        readonly Icon applicationIcon=AppTheme.CreateApplicationIcon();
        readonly Engine engine;
        readonly DependencyInstaller dependencies;
        readonly Label status=new Label(),summary=new Label(),detail=new Label(),listStatus=new Label();
        readonly Button primary=new ThemedButton(),check=new ThemedButton(),tune=new ThemedButton(),exportTuning=new ThemedButton(),find=new ThemedButton(),elevate=new ThemedButton(),updateLists=new ThemedButton(),telegramBridge=new ThemedButton(),startupToggle=new ThemedButton(),startupRemove=new ThemedButton();
        readonly Label startupStatus=new Label();
        readonly Label settingsNotice=new Label(),diagnosticsNotice=new Label();
        readonly Button advancedToggle=new ThemedButton();
        readonly Button manageDomains=new ThemedButton();
        readonly CheckBox ignoreVpn=new ThemedCheckBox();
        readonly Panel advancedSettings=new Panel();readonly TableLayoutPanel advancedGrid=new TableLayoutPanel();
        readonly CheckBox showDetails=new ThemedCheckBox();
        readonly ComboBox mode=new ThemedComboBox(),strategy=new ThemedComboBox(),discordStrategy=new ThemedComboBox(),endpoints=new ThemedComboBox();
        readonly Button tuneDiscord=new ThemedButton();
        readonly Button bugReport=new ThemedButton();
        readonly Button contact=new ThemedButton();
        readonly Button emailContact=new ThemedButton();
        readonly ComboBox presetChoice=new ThemedComboBox();
        readonly Button savePreset=new ThemedButton(),savePresetAs=new ThemedButton();
        readonly PresetStore presetStore;
        bool loadingPreset;
        readonly TextBox endpoint=new ThemedTextBox(),custom=new ThemedTextBox(),log=new ThemedTextBox();
        readonly NumericUpDown port=new ThemedNumericUpDown();
        readonly CheckBox quic=new ThemedCheckBox(),voice=new ThemedCheckBox(),telegram=new ThemedCheckBox(),publicLists=new ThemedCheckBox();
        readonly ListView results=new ListView();readonly AppShell shell=new AppShell();
        readonly Dictionary<string,CheckBox> selections=new Dictionary<string,CheckBox>();
        readonly Dictionary<string,ListViewItem> rows=new Dictionary<string,ListViewItem>();
        readonly List<string> rowOrder=new List<string>();
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
        readonly ToolTip tips=new ToolTip{AutoPopDelay=25000,InitialDelay=450};
        readonly List<string> methods=new List<string>(Profiles.TlsStrategies());
        readonly string[] appearances={"system","light","dark"};
        AppTheme theme;AppSettings settings;CancellationTokenSource operation;TuningResult lastTuning;
        NotifyIcon tray;ContextMenuStrip trayMenu;ToolStripMenuItem trayStop;
        bool busy,closing,lastRunning,exitRequested,exitPending,trayNoticeShown,resourcesDisposed,engineFailure,startupHidden,advancedExpanded,resumeStartAfterElevation;
        string workState="Проверка";
        string activeOperation="none";bool exportingReport;
        StartupRegistrationState? startupState;
        bool useCustomDomains=true;
        List<string> disabledDomains=new List<string>();
        string preparedExclusions="";

        public MainForm(bool isPreview):this(isPreview,false){}
        public MainForm(bool isPreview,bool darkPreview,bool lightPreview=false,EventWaitHandle windowRestoreSignal=null,bool savePreferences=true,bool startHidden=false,DiagnosticHistory diagnosticHistory=null)
        {
            preview=isPreview;forceDark=darkPreview;forceLight=lightPreview;persistPreferences=savePreferences;startInTray=!preview&&startHidden;restoreSignal=preview?null:windowRestoreSignal;
            dependencies=new DependencyInstaller(DependencyInstaller.DefaultComponentsRoot);
            engine=new Engine(preview||!persistPreferences?AppDomain.CurrentDomain.BaseDirectory:dependencies.ComponentsRoot);
            ownsHistory=diagnosticHistory==null;history=diagnosticHistory??DiagnosticHistory.CreateDefault(!preview&&persistPreferences);
            settings=preview||!persistPreferences?new AppSettings():AppSettings.Load();
            presetStore=new PresetStore(preview||!persistPreferences?Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"presets-preview"):PresetStore.DefaultFolder);
            InitializeApplicationFeatures();
            InitializeAutomaticConnection();InitializePresetExchange();
            useCustomDomains=settings.UseCustomDomains;disabledDomains=new List<string>(settings.DisabledDomains);
            Text="LocalLink";Icon=applicationIcon;ClientSize=new Size(980,720);MinimumSize=new Size(820,620);StartPosition=FormStartPosition.CenterScreen;
            Font=AppTheme.Font(10.5f,FontStyle.Regular);AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;KeyPreview=true;Tag="root";
            var root=new TableLayoutPanel{Dock=DockStyle.Fill,Margin=Padding.Empty,ColumnCount=1,RowCount=1,Tag="root"};
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100));Controls.Add(root);
            shell.Dock=DockStyle.Fill;shell.Margin=Padding.Empty;
            root.Controls.Add(shell);BuildDiagnostics(shell.MainContent);BuildSettings(shell.SettingsContent);
            shell.TrayAction.Click+=delegate{HideToTray();};shell.TrayAction.Enabled=!preview;
            tips.SetToolTip(shell.TrayAction,"Скрыть окно и оставить фильтр работающим. Открыть или завершить LocalLink можно через значок возле часов.");
            mode.SelectedIndex=settings.Mode;strategy.SelectedIndex=Math.Max(0,methods.IndexOf(settings.Strategy));quic.Checked=settings.Quic;
            discordStrategy.SelectedIndex=String.IsNullOrEmpty(settings.DiscordStrategy)?0:methods.IndexOf(settings.DiscordStrategy)+1;
            voice.Checked=settings.DiscordVoice;telegram.Checked=settings.TelegramNative;publicLists.Checked=settings.UsePublicLists;
            endpoint.Text=settings.Endpoint;port.Value=settings.Port;custom.Text=settings.CustomDomains;
            shell.Appearance.SelectedIndex=preview&&forceDark?2:preview&&forceLight?1:Math.Max(0,Array.IndexOf(appearances,settings.Appearance));
            shell.Appearance.SelectedIndexChanged+=delegate
            {
                settings.Appearance=SelectedAppearance();Record("theme_changed","appearance",SelectedAppearance());ApplyTheme();SaveAppearance();
            };
            tips.SetToolTip(shell.Appearance,"Оформление только LocalLink. Применяется сразу, не меняет тему Windows и не перезапускает обработку.");
            mode.SelectedIndexChanged+=delegate{if(mode.SelectedIndex!=0)SetAdvancedSettings(true);RefreshControls();};
            primary.Click+=async delegate{if(busy||automaticConnecting||engine.IsRunning||recoveryPolicy.Pending)StopOperation();else await StartAsync();};
            check.Click+=async delegate{await CheckAsync();};tune.Click+=async delegate{shell.CloseSettings();await TuneAsync();};exportTuning.Click+=delegate{ExportTuning();};
            bugReport.Click+=async delegate{await ExportBugReportAsync();};
            contact.Click+=delegate{OpenContact();};
            emailContact.Click+=delegate{OpenEmailContact();};
            savePreset.Click+=delegate{SavePreset(false);};savePresetAs.Click+=delegate{SavePreset(true);};
            presetChoice.SelectedIndexChanged+=delegate{if(!loadingPreset)LoadChosenPreset();};
            tuneDiscord.Click+=async delegate{shell.CloseSettings();await TuneDiscordAsync();};
            shell.SettingsOpenChanged+=delegate{Record(shell.SettingsOpen?"settings_opened":"settings_closed");if(!shell.SettingsOpen)Save();};
            manageDomains.Click+=delegate{OpenDomainManager();};
            showDetails.CheckedChanged+=delegate{RebuildVisibleRows();};
            elevate.Click+=delegate{Elevate();};find.Click+=delegate{Discover();};updateLists.Click+=async delegate{await UpdateListsAsync();};
            telegramBridge.Click+=async delegate{await OpenTelegramBridgeAsync();};
            startupToggle.Click+=delegate{ChangeStartup(true);};startupRemove.Click+=delegate{ChangeStartup(false);};
            endpoints.SelectedIndexChanged+=delegate{var value=endpoints.SelectedItem as VpnConnection;if(value!=null){endpoint.Text=value.Address;port.Value=value.Port;}};
            results.SelectedIndexChanged+=delegate{detail.Text=results.SelectedItems.Count==0?"Выберите строку для подробностей.":results.SelectedItems[0].ToolTipText;};
            results.Resize+=delegate{ResizeColumns();};
            results.VisibleChanged+=delegate
            {if(results.Visible&&IsHandleCreated&&!closing)BeginInvoke((Action)ResizeColumns);};
            timer.Interval=350;timer.Tick+=delegate{if(!closing&&restoreSignal!=null&&restoreSignal.WaitOne(0))RestoreWindow();InspectEngineExit();TickApplicationFeatures();RefreshControls();};timer.Start();
            // Escape must not silently disconnect applications or cancel voice traffic.
            KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Escape&&shell.SettingsOpen){shell.CloseSettings();e.Handled=true;e.SuppressKeyPress=true;}else if(e.KeyCode==Keys.Escape&&(busy||engine.IsRunning)){e.Handled=true;e.SuppressKeyPress=true;}};
            FormClosing+=OnClosing;FormClosed+=delegate{DisposeOwnedResources();};
            if(!preview)SystemEvents.UserPreferenceChanged+=ThemeChanged;
            if(!preview)CreateTray();
            summary.Text="";Write("Фильтр выключен.");
            if(!preview&&settings.RecoveredFromBackup)Write("Настройки восстановлены из резервного варианта. Повреждённый файл не удалялся.");
            SetAdvancedSettings(settings.Mode!=0);ApplyTheme();RefreshLists();RefreshPresets(settings.SelectedPresetId);RefreshControls();RefreshStartup();RefreshDiagnosticsNotice();
            RecordConfiguration();
        }

        protected override void SetVisibleCore(bool value)
        {
            if(startInTray&&!startupHidden&&value)
            {
                startupHidden=true;ShowInTaskbar=false;
                // Keep the normal message loop and restore-event handle without
                // flashing a window at login. No filter or bridge starts here.
                if(!IsHandleCreated)CreateHandle();
                base.SetVisibleCore(false);return;
            }
            base.SetVisibleCore(value);
        }

        Label Label(string text,float size,bool bold,bool muted)
        {return new Label{Text=text,Dock=DockStyle.Fill,Margin=Padding.Empty,TextAlign=ContentAlignment.MiddleLeft,UseCompatibleTextRendering=false,Font=AppTheme.Font(size,bold?FontStyle.Bold:FontStyle.Regular),Tag=muted?"muted":null};}
        void Button(Button button,string text,bool prominent)
        {button.Text=text;button.Height=42;button.Dock=DockStyle.Fill;button.Margin=Padding.Empty;button.FlatStyle=FlatStyle.Flat;button.Cursor=Cursors.Hand;button.Tag=prominent?"primary":null;button.Font=AppTheme.Font(10.5f,FontStyle.Regular);}
        void Combo(ComboBox box){box.DropDownStyle=ComboBoxStyle.DropDownList;box.FlatStyle=FlatStyle.Flat;box.Dock=DockStyle.Top;box.Margin=Padding.Empty;box.Font=AppTheme.Font(10.5f,FontStyle.Regular);}
        Control BuildWorkflow()
        {
            var card=new ThemedPanel{Dock=DockStyle.Fill,Padding=new Padding(18),Margin=Padding.Empty};
            var grid=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,270));grid.RowStyles.Add(new RowStyle(SizeType.Percent,100));card.Controls.Add(grid);
            var text=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=new Padding(0,0,16,0)};
            text.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));text.RowStyles.Add(new RowStyle(SizeType.Absolute,32));text.RowStyles.Add(new RowStyle(SizeType.Percent,100));grid.Controls.Add(text,0,0);
            status.Font=AppTheme.Font(16,FontStyle.Bold);status.Dock=DockStyle.Fill;status.Margin=Padding.Empty;status.TextAlign=ContentAlignment.MiddleLeft;text.Controls.Add(status);
            summary.Dock=DockStyle.Fill;summary.Margin=Padding.Empty;summary.TextAlign=ContentAlignment.TopLeft;summary.Tag="muted";summary.Font=AppTheme.Font(9.5f,FontStyle.Regular);summary.AutoEllipsis=true;text.Controls.Add(summary);
            var actions=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty,Padding=new Padding(0,12,0,12)};
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));actions.RowStyles.Add(new RowStyle(SizeType.Percent,100));grid.Controls.Add(actions,1,0);
            Button(primary,"Включить",true);primary.Margin=new Padding(0,0,8,0);actions.Controls.Add(primary);
            Button(check,"Проверить",false);actions.Controls.Add(check);
            primary.AccessibleName="Включить или выключить LocalLink";check.AccessibleName="Проверить подключение";
            return card;
        }
        void BuildDiagnostics(Panel page)
        {
            page.Padding=new Padding(24,20,24,20);
            var grid=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Margin=Padding.Empty};diagnosticsLayout=grid;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute,0));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute,132));grid.RowStyles.Add(new RowStyle(SizeType.Absolute,48));grid.RowStyles.Add(new RowStyle(SizeType.Percent,100));grid.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
            page.Controls.Add(grid);grid.Controls.Add(BuildNetworkSuggestion(),0,0);grid.Controls.Add(BuildWorkflow(),0,1);
            var heading=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=4,RowCount=1,Margin=Padding.Empty};
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,150));heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,150));heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));heading.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            Button(troubleshoot,"Что не работает?",false);troubleshoot.Margin=new Padding(0,6,8,6);troubleshoot.AccessibleName="Мастер диагностики проблемы";heading.Controls.Add(troubleshoot);Button(bugReport,"Баг-репорт",false);bugReport.Margin=new Padding(0,6,8,6);bugReport.AccessibleName="Создать баг-репорт";heading.Controls.Add(bugReport);
            Button(contact,"@AresC404",false);contact.Margin=new Padding(0,6,0,6);contact.AccessibleName="Открыть Telegram @AresC404";heading.Controls.Add(contact);grid.Controls.Add(heading,0,2);
            Button(emailContact,"Почта",false);emailContact.Margin=new Padding(8,6,0,6);emailContact.AccessibleName="Написать на почту kirill0063@gmail.com";tips.SetToolTip(emailContact,ContactLink.Email);heading.Controls.Add(emailContact);
            tips.SetToolTip(bugReport,"Сохранить ZIP с историей LocalLink и результатами проверок. Ничего не отправляется; фильтр продолжит работать.");
            tips.SetToolTip(contact,"Открыть t.me/AresC404. Прикрепите ZIP вручную — приложение ничего не отправляет.");
            results.Dock=DockStyle.Fill;results.Margin=Padding.Empty;results.Font=AppTheme.Font(10.5f,FontStyle.Regular);results.View=View.Details;results.FullRowSelect=true;results.MultiSelect=false;results.HideSelection=false;results.ShowItemToolTips=true;results.BorderStyle=BorderStyle.FixedSingle;results.HeaderStyle=ColumnHeaderStyle.Nonclickable;
            results.OwnerDraw=true;
            results.DrawItem+=delegate(object sender,DrawListViewItemEventArgs e){e.DrawDefault=true;};
            results.DrawSubItem+=delegate(object sender,DrawListViewSubItemEventArgs e){e.DrawDefault=true;};
            results.DrawColumnHeader+=delegate(object sender,DrawListViewColumnHeaderEventArgs e)
            {
                if(theme==null||theme.HighContrast){e.DrawDefault=true;return;}
                using(var brush=new SolidBrush(theme.Input))e.Graphics.FillRectangle(brush,e.Bounds);
                using(var pen=new Pen(theme.Border))e.Graphics.DrawLine(pen,e.Bounds.Left,e.Bounds.Bottom-1,e.Bounds.Right,e.Bounds.Bottom-1);
                int inset=Math.Max(7,(int)Math.Round(e.Graphics.DpiX/96f*7));
                TextRenderer.DrawText(e.Graphics,e.Header.Text,results.Font,new Rectangle(e.Bounds.X+inset,e.Bounds.Y,Math.Max(1,e.Bounds.Width-2*inset),e.Bounds.Height),theme.Muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix);
            };
            results.Columns.Add("Сервис",425);results.Columns.Add("Результат",160);results.Columns.Add("Время",80);grid.Controls.Add(results,0,3);
            detail.Text="";detail.Dock=DockStyle.Fill;detail.Margin=Padding.Empty;detail.TextAlign=ContentAlignment.MiddleLeft;detail.Font=AppTheme.Font(9.5f,FontStyle.Regular);detail.Tag="muted";detail.AutoEllipsis=true;grid.Controls.Add(detail,0,4);
        }
        void BuildSettings(Panel page)
        {BuildOrganizedSettings(page);}
        void SetAdvancedSettings(bool expanded)
        {SelectSettingsSection(expanded?"advanced":"basic");}
        Control Field(TableLayoutPanel grid,string title,Control control,int column,int row,int height)
        {
            while(grid.RowStyles.Count<=row)grid.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            var card=new ThemedPanel{Dock=DockStyle.Fill,Padding=new Padding(12,8,12,8),Margin=new Padding(0,0,grid.ColumnCount>1&&column==0?12:0,8)};
            var cell=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};card.Controls.Add(cell);
            cell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            cell.RowStyles.Add(new RowStyle(SizeType.AutoSize));cell.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var caption=Label(title,9.5f,false,true);caption.AutoSize=true;caption.Dock=DockStyle.Top;caption.Margin=new Padding(0,0,0,8);
            control.Margin=Padding.Empty;cell.Controls.Add(caption);cell.Controls.Add(control);grid.Controls.Add(card,column,row);return card;
        }
        void ThemeChanged(object sender,UserPreferenceChangedEventArgs e)
        {if(!closing&&IsHandleCreated&&!IsDisposed)try{BeginInvoke((Action)ApplyTheme);}catch(InvalidOperationException){}}
        protected override void OnHandleCreated(EventArgs e)
        {base.OnHandleCreated(e);if(settings!=null)ApplyTheme();}
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if((message.Msg==0x31A||message.Msg==0x320||message.Msg==0x1A)&&IsHandleCreated&&!closing&&theme!=null)
                try{BeginInvoke((Action)ApplyTheme);}catch(InvalidOperationException){}
        }
        void ApplyTheme()
        {
            if(closing||IsDisposed)return;theme=AppTheme.Read(SelectedAppearance(),forceDark,forceLight);theme.Apply(this);
            foreach(var item in rows.Values){var row=item.Tag as CheckRow;if(row!=null)item.ForeColor=RowColor(row);}
            status.ForeColor=engine.IsRunning?theme.Success:engineFailure?theme.Error:theme.Text;shell.Invalidate();
            if(trayMenu!=null)theme.ApplyMenu(trayMenu);
        }
        Color RowColor(CheckRow row){return row.Passed==true?theme.Success:row.Passed==false?theme.Error:theme.Muted;}
        void ResizeColumns()
        {
            if(results.IsDisposed||results.Columns.Count<3||results.ClientSize.Width<=400)return;
            float scale=results.DeviceDpi/96f;
            int stateWidth=Math.Max((int)Math.Round(160*scale),TextRenderer.MeasureText("Результат",results.Font).Width+(int)Math.Round(24*scale));
            int timeWidth=Math.Max((int)Math.Round(70*scale),TextRenderer.MeasureText("Время",results.Font).Width+(int)Math.Round(24*scale));
            int available=Math.Max(1,results.ClientSize.Width-SystemInformation.VerticalScrollBarWidth-4);
            results.Columns[0].Width=Math.Max(120,available-stateWidth-timeWidth);results.Columns[1].Width=stateWidth;results.Columns[2].Width=timeWidth;
        }
        List<string> Selected(){return selections.Where(x=>x.Value.Checked).Select(x=>x.Key).ToList();}
        AppSettings CaptureSettings()
        {return new AppSettings{Services=Selected(),Mode=Math.Max(0,mode.SelectedIndex),Strategy=methods[Math.Max(0,strategy.SelectedIndex)],DiscordStrategy=discordStrategy.SelectedIndex>0?methods[discordStrategy.SelectedIndex-1]:"",Quic=quic.Checked,DiscordVoice=voice.Checked,TelegramNative=telegram.Checked,UsePublicLists=publicLists.Checked,UseCustomDomains=useCustomDomains,DisabledDomains=new List<string>(disabledDomains),CustomDomains=custom.Text,Endpoint=endpoint.Text.Trim(),Port=(int)port.Value,Appearance=SelectedAppearance(),SelectedPresetId=ChosenPresetId(),AutomaticRecovery=autoRecovery.Checked,AutomaticSetup=automaticConnection.Checked,ConnectOnOpen=connectOnOpen.Checked};}
        string ChosenPresetId(){var value=presetChoice.SelectedItem as PresetEntry;return value==null?"":value.Id;}
        void RefreshPresets(string selectedId)
        {
            loadingPreset=true;
            try
            {
                presetChoice.Items.Clear();presetChoice.Items.Add(new PresetEntry{Id="",Name="Свои настройки"});
                foreach(var value in presetStore.List())presetChoice.Items.Add(value);
                presetChoice.SelectedIndex=0;
                for(int i=1;i<presetChoice.Items.Count;i++)if(((PresetEntry)presetChoice.Items[i]).Id==selectedId){presetChoice.SelectedIndex=i;break;}
            }
            catch(Exception error){Write("Не удалось прочитать пресеты: "+error.Message);}
            finally{loadingPreset=false;}
        }
        void SavePreset(bool saveAs)
        {
            if(preview||busy||automaticConnecting||engine.IsRunning||recoveryPolicy.Pending)return;
            try
            {
                var chosen=presetChoice.SelectedItem as PresetEntry;string id=saveAs?"":ChosenPresetId();
                string name=chosen==null||String.IsNullOrEmpty(chosen.Id)?"Мой пресет":chosen.Name;
                if(String.IsNullOrEmpty(id))
                {
                    using(var prompt=new PresetNamePrompt(theme,name)){if(prompt.ShowDialog(this)!=DialogResult.OK)return;name=prompt.PresetName;}
                }
                string saved=presetStore.Save(name,CaptureSettings(),String.IsNullOrEmpty(id)?null:id);
                RefreshPresets(saved);settings=CaptureSettings();Save();RefreshNetworkBinding();Write("Пресет сохранён.");
            }
            catch(Exception error){RecordFailure("preset_save",error);Write("Не удалось сохранить пресет: "+error.Message);}
        }
        void LoadChosenPreset()
        {
            if(preview||busy||automaticConnecting||engine.IsRunning||recoveryPolicy.Pending||String.IsNullOrEmpty(ChosenPresetId()))return;
            loadingPreset=true;
            try
            {
                string id=ChosenPresetId();var value=presetStore.Load(id);
                settings=value;useCustomDomains=value.UseCustomDomains;disabledDomains=new List<string>(value.DisabledDomains);
                foreach(var pair in selections)pair.Value.Checked=value.Services.Contains(pair.Key);
                mode.SelectedIndex=value.Mode;strategy.SelectedIndex=Math.Max(0,methods.IndexOf(value.Strategy));
                discordStrategy.SelectedIndex=String.IsNullOrEmpty(value.DiscordStrategy)?0:methods.IndexOf(value.DiscordStrategy)+1;
                quic.Checked=value.Quic;voice.Checked=value.DiscordVoice;telegram.Checked=value.TelegramNative;publicLists.Checked=value.UsePublicLists;
                endpoint.Text=value.Endpoint;port.Value=value.Port;custom.Text=value.CustomDomains;
                shell.Appearance.SelectedIndex=Math.Max(0,Array.IndexOf(appearances,value.Appearance));
                autoRecovery.Checked=value.AutomaticRecovery;
                automaticConnection.Checked=value.AutomaticSetup;
            preparedExclusions="";ignoreVpn.Checked=false;Save();RefreshLists();RefreshControls();Write("Пресет загружен.");
            }
            catch(Exception error){RefreshPresets(settings.SelectedPresetId);RecordFailure("preset_load",error);Write("Не удалось загрузить пресет: "+error.Message);}
            finally{loadingPreset=false;}
        }
        void OpenDomainManager()
        {
            if(busy||automaticConnecting||engine.IsRunning)return;
            string path="";
            try{path=PublicLists.GetActivePath(CancellationToken.None);}catch(IOException){}
            try
            {
                using(var dialog=new DomainManagerForm(CaptureSettings(),path,theme))
                {
                    if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                    var selection=dialog.Selection;
                    foreach(var pair in selections)pair.Value.Checked=selection.Services.Contains(pair.Key);
                    publicLists.Checked=selection.UsePublicLists;useCustomDomains=selection.UseCustomDomains;
                    disabledDomains=new List<string>(selection.DisabledDomains);preparedExclusions="";
                    Save();RefreshLists();RefreshControls();
                }
            }
            catch(Exception error){Write("Не удалось открыть домены: "+error.Message);}
        }
        string SelectedAppearance(){return shell.Appearance.SelectedIndex>=0&&shell.Appearance.SelectedIndex<appearances.Length?appearances[shell.Appearance.SelectedIndex]:"system";}
        void SaveAppearance()
        {
            if(preview||!persistPreferences)return;
            // Theme-only save preserves network drafts and never restarts the engine.
            try{settings.Save();}catch(Exception error){RecordFailure("theme",error);Write("Оформление применено, но сохранить выбор не удалось.");}
        }
        void Save(){try{SavePreferencesRequired();}catch(Exception error){RecordFailure("settings",error);Write("Не удалось сохранить настройки. Текущие значения остались в окне.");}}
        void SavePreferencesRequired()
        {
            if(preview||!persistPreferences)return;
            SavePreferencesTo(delegate(AppSettings value){value.Save();});
        }
        void SavePreferencesTo(Action<AppSettings> save)
        {var captured=CaptureSettings();save(captured);settings=captured;RecordConfiguration("settings_saved");}
        ProfileSpec Spec()
        {
            bool direct=mode.SelectedIndex==0;string list="";
            // Read only the small snapshot passport here; full hash/content
            // validation belongs to cancellable background preparation.
            if(direct&&publicLists.Checked){list=PublicLists.GetActivePath(CancellationToken.None);if(String.IsNullOrEmpty(list))throw new InvalidOperationException("Список доменов ещё не загружен. Обновите его в настройках или снимите флажок.");}
            var selected=Selected();
            string customText=useCustomDomains?custom.Text:"";
            string domains=direct&&(selected.Count>0||!String.IsNullOrWhiteSpace(customText)||list.Length==0)?ServiceCatalog.DomainsFor(selected,customText):"";
            return new ProfileSpec{Mode=(HelperMode)Math.Max(0,mode.SelectedIndex),Domains=domains,Endpoint=endpoint.Text.Trim(),Port=(int)port.Value,
                Strategy=mode.SelectedIndex==2?"wireguard":methods[Math.Max(0,strategy.SelectedIndex)],Quic=direct&&quic.Checked,HostListFile=list,HostListExcludeFile=direct?preparedExclusions:"",
                DiscordStrategy=direct&&selected.Contains("discord")&&discordStrategy.SelectedIndex>0?methods[discordStrategy.SelectedIndex-1]:"",
                DiscordVoice=direct&&voice.Checked&&selected.Contains("discord"),TelegramNative=direct&&telegram.Checked&&selected.Contains("telegram")};
        }
        void RefreshControls()
        {
            if(closing)return;bool running=engine.IsRunning,working=busy||automaticConnecting||updateStartupGate!=null,editable=!working&&!running&&!recoveryPolicy.Pending,direct=mode.SelectedIndex==0;
            status.Text=working?workState+"…":running?"Фильтр включён":recoveryPolicy.Pending?"Ожидание повтора":engineFailure?"Обработка остановлена":"Фильтр выключен";
            if(theme!=null)status.ForeColor=busy?theme.Text:running?theme.Success:engineFailure?theme.Error:theme.Text;
            primary.Text=!working&&!running&&recoveryPolicy.Pending?"Отменить повтор":PrimaryCaption(working,running);check.Enabled=!working;
            bugReport.Enabled=!preview&&!closing&&!exportingReport;bugReport.Text=exportingReport?"Сохранение…":"Баг-репорт";
            contact.Enabled=!preview&&!closing;
            emailContact.Enabled=!preview&&!closing;
            tips.SetToolTip(primary,busy&&running?"Остановить подбор и выключить фильтр. Соединения могут прерваться.":busy?"Отменить текущую операцию.":running?"Выключить фильтр, не закрывая окно.":"Включить фильтр. Если Windows запросит права, подтвердите и нажмите кнопку ещё раз.");
            tips.SetToolTip(summary,summary.Text);summary.AccessibleDescription=summary.Text;
            tune.Enabled=editable&&direct;exportTuning.Enabled=!working&&lastTuning!=null;mode.Enabled=editable;strategy.Enabled=editable&&mode.SelectedIndex!=2;
            manageDomains.Enabled=editable&&direct;ignoreVpn.Enabled=!working&&direct;
            presetChoice.Enabled=editable;savePreset.Enabled=savePresetAs.Enabled=editable&&!preview;
            discordStrategy.Enabled=editable&&direct&&Selected().Contains("discord");tuneDiscord.Enabled=editable&&direct&&Selected().Contains("discord");
            foreach(var control in selections.Values)control.Enabled=editable;
            custom.Enabled=quic.Enabled=voice.Enabled=telegram.Enabled=publicLists.Enabled=editable&&direct;
            endpoint.Enabled=port.Enabled=find.Enabled=endpoints.Enabled=editable&&!direct;
            elevate.Enabled=editable&&!Engine.IsAdmin();updateLists.Enabled=editable;
            telegramBridge.Enabled=!preview&&!working&&!exitPending;
            startupToggle.Enabled=!preview&&!working&&!exitPending&&startupState.HasValue&&startupState.Value!=StartupRegistrationState.Enabled&&startupState.Value!=StartupRegistrationState.ForeignEntry;
            startupRemove.Enabled=!preview&&!working&&!exitPending&&startupState.HasValue&&(startupState.Value==StartupRegistrationState.Enabled||startupState.Value==StartupRegistrationState.DifferentLocation);
            settingsNotice.Text=busy?"Параметры недоступны во время операции.":running?"Для изменения параметров выключите фильтр.":"Изменения применяются при включении фильтра.";
            for(int row=7;row<=8&&row<advancedGrid.RowStyles.Count;row++)
            {
                advancedGrid.RowStyles[row].Height=direct?0:row==7?92:88;
                foreach(Control control in advancedGrid.Controls)if(advancedGrid.GetRow(control)==row)control.Visible=!direct;
            }
            if(tray!=null){tray.Text=running?"LocalLink — фильтр включён":working?"LocalLink — выполняется проверка":recoveryPolicy.Pending?"LocalLink — ожидается повтор":"LocalLink — фильтр выключен";trayStop.Enabled=running||working||recoveryPolicy.Pending;}
            if(lastRunning&&!running&&!busy&&!engineFailure)Write("Обработка остановлена.");lastRunning=running;
            RefreshApplicationFeatureControls();
            RefreshAutomaticControls();RefreshPresetExchangeControls();
        }
        static string HumanMethod(string method)
        {
            switch(method){case "split":return "Мягкий";case "sni-split":return "По SNI";case "disorder":return "Перестановка";case "fake-split":return "Фейк + разбиение";case "fake-disorder":return "Фейк + перестановка";case "wireguard":return "WireGuard";default:return "не определён";}
        }
        static string PrimaryCaption(bool isBusy,bool isRunning)
        {return isBusy?(isRunning?"Остановить":"Отменить"):isRunning?"Выключить":"Включить";}
        void InspectEngineExit()
        {
            if(closing||IsDisposed||preview)return;
            var info=engine.TakeUnexpectedExit();if(info==null)return;
            engineFailure=true;
            Record("filter_unexpected_exit","exitCode",info.ExitCode);
            recoveryPolicy.UnexpectedExit(DateTime.UtcNow);
            string message="Движок неожиданно завершился"+(info.ExitCode.HasValue?" · код "+info.ExitCode.Value:" · код недоступен")+(recoveryPolicy.Pending?". Повторим запуск прежнего метода.":".");
            summary.Text=message;Write(message);
            if(operation!=null)operation.Cancel();
            if(!busy)TryStop();
        }
        void ResetEngineFailureForNewStart()
        {
            // Drain the previous child's event BEFORE a new operation exists;
            // it must never cancel an unrelated retry's background preflight.
            InspectEngineExit();engineFailure=false;
        }
        void RefreshLists()
        {
            string details=PublicLists.Summary();
            listStatus.Text=details=="Список не загружен"?details:publicLists.Checked?"Список включён":"Список выключен · выбор в категориях";
            tips.SetToolTip(listStatus,details);
        }
        void RefreshStartup()
        {
            if(preview){startupState=null;startupStatus.Text="Пример: автозапуск выключен. Реестр не меняется.";RefreshControls();return;}
            try
            {
                startupState=new StartupRegistration(Application.ExecutablePath).GetState();
                startupStatus.Text=startupState==StartupRegistrationState.Enabled?"Автозапуск включён. "+(connectOnOpen.Checked?"Подключение — автоматически.":"Подключение — кнопкой на главном экране."):startupState==StartupRegistrationState.DifferentLocation?"Автозапуск указывает на другую папку LocalLink.":startupState==StartupRegistrationState.ForeignEntry?"Найдена незнакомая запись LocalLink. Она сохранена без изменений.":"Автозапуск выключен.";
                startupToggle.Text=startupState==StartupRegistrationState.DifferentLocation?"Обновить путь автозапуска":"Добавить в автозапуск";
            }
            catch(Exception error){RecordFailure("startup",error);startupState=null;startupStatus.Text="Не удалось проверить автозапуск. Реестр не изменён.";}
            RefreshControls();
        }
        void ChangeStartup(bool enable)
        {
            if(preview||busy||closing||exitPending)return;
            try
            {
                var registration=new StartupRegistration(Application.ExecutablePath);
                if(enable)registration.Enable();else registration.Disable();
                Record("startup_changed","enabled",enable);
                Write(enable?"Автозапуск приложения добавлен. Режим подключения — в разделе «Основное».":"Автозапуск LocalLink убран. Другие записи Windows не изменены.");
            }
            catch(Exception error){RecordFailure("startup",error);MessageBox.Show(this,"Не удалось изменить автозапуск. Чужие записи не меняются; проверьте доступ и длину пути к папке LocalLink.","Автозапуск",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            RefreshStartup();
        }
        void Write(string text){if(!closing){if(log.TextLength>22000)log.Clear();log.AppendText(DateTime.Now.ToString("HH:mm:ss")+"  "+text+Environment.NewLine);}}
        void Record(string code,params object[] fields)
        {
            var values=new Dictionary<string,object>();for(int i=0;i+1<fields.Length;i+=2)values[(string)fields[i]]=fields[i+1];
            history.Record(code,values);
        }
        void RecordFailure(string stage,Exception error)
        {history.RecordException("caught_error",error,new Dictionary<string,object>{{"operation",stage}});}
        void OpenContact()
        {
            if(preview||closing)return;
            Record("support_open_requested");Exception failure;
            if(ContactLink.TryOpen(out failure)){Record("support_opened");return;}
            if(failure!=null)RecordFailure("contact",failure);Record("support_open_failed");
            MessageBox.Show(this,"Не удалось открыть Telegram. Откройте ссылку вручную:\r\n"+ContactLink.Url,"Связь",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
        void OpenEmailContact()
        {
            if(preview||closing)return;
            Exception failure;if(ContactLink.TryOpenEmail(out failure))return;
            if(failure!=null)RecordFailure("email_contact",failure);
            MessageBox.Show(this,"Напишите на "+ContactLink.Email+" через свою почту.","Связь",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
        void RecordConfiguration(string code="settings_snapshot")
        {
            Record(code,"mode",Math.Max(0,mode.SelectedIndex),"strategy",methods[Math.Max(0,strategy.SelectedIndex)],
                "discordStrategy",discordStrategy.SelectedIndex>0?methods[discordStrategy.SelectedIndex-1]:"", "services",Selected(),
                "discordVoice",voice.Checked,"telegramNative",telegram.Checked,"quic",quic.Checked,"publicLists",publicLists.Checked,
                "customDomainCount",CountCustomDomains(),"endpointConfigured",!String.IsNullOrWhiteSpace(endpoint.Text));
        }
        int CountCustomDomains()
        {return Math.Min(2000000,custom.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Length);}
        BugReportSnapshot CaptureBugReport()
        {
            var state=new BugReportSnapshot{AppVersion=typeof(MainForm).Assembly.GetName().Version.ToString(),Busy=busy,FilterRunning=engine.IsRunning,
                Operation=activeOperation,IsElevated=Engine.IsAdmin(),Mode=Math.Max(0,mode.SelectedIndex),Strategy=methods[Math.Max(0,strategy.SelectedIndex)],
                DiscordStrategy=discordStrategy.SelectedIndex>0?methods[discordStrategy.SelectedIndex-1]:"",DiscordVoice=voice.Checked,
                TelegramNative=telegram.Checked,Quic=quic.Checked,UsePublicLists=publicLists.Checked,SelectedServices=Selected(),
                CustomDomainCount=CountCustomDomains(),EndpointConfigured=!String.IsNullOrWhiteSpace(endpoint.Text),ProfileEvidence=engine.LastProfileEvidence,
                Appearance=SelectedAppearance(),Dpi=DeviceDpi,ClientWidth=ClientSize.Width,ClientHeight=ClientSize.Height};
            foreach(var item in rows.Values)
            {
                var row=item.Tag as CheckRow;if(row==null)continue;
                state.Checks.Add(new BugReportCheck{ServiceId=row.ServiceId,Stage=row.Key,Code=row.DiagnosticCode,Passed=row.Passed,Milliseconds=(int)Math.Max(0,Math.Min(32000,row.Milliseconds))});
            }
            if(lastTuning!=null)state.TuningJson=TuningReport.ToJson(lastTuning,engine.LastProfileEvidence);
            return state;
        }
        async Task ExportBugReportAsync()
        {
            if(preview||closing||exportingReport)return;
            using(var dialog=new SaveFileDialog{Title="Сохранить баг-репорт",Filter="Баг-репорт LocalLink (*.zip)|*.zip",DefaultExt="zip",AddExtension=true,
                FileName="LocalLink-bugreport-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6)+".zip",OverwritePrompt=false,RestoreDirectory=true})
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK){Record("report_cancelled");return;}
                if(File.Exists(dialog.FileName))
                {MessageBox.Show(this,"Выберите новое имя файла. Существующий архив не будет перезаписан.","Баг-репорт",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
                exportingReport=true;RefreshControls();Record("report_requested");
                try
                {
                    var snapshot=CaptureBugReport();string destination=dialog.FileName;
                    await Task.Run(delegate{BugReport.Export(destination,history,snapshot);});Record("report_created");
                    if(!closing&&!IsDisposed)MessageBox.Show(this,"Баг-репорт сохранён:\r\n"+destination+"\r\n\r\nОтправьте этот ZIP для разбора. Он содержит только диагностику LocalLink и не отправляется автоматически.","Баг-репорт",MessageBoxButtons.OK,MessageBoxIcon.Information);
                }
                catch(Exception error)
                {
                    RecordFailure("report",error);Record("report_failed");
                    if(!closing&&!IsDisposed)MessageBox.Show(this,"Не удалось сохранить баг-репорт. Выберите другую обычную локальную папку и новое имя файла.","Баг-репорт",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                }
                finally
                {
                    exportingReport=false;
                    if(!closing&&!IsDisposed){if(exitPending&&!busy){exitPending=false;Close();}else RefreshControls();}
                }
            }
        }
        bool Permit(bool resumeStart=false)
        {
            if(!Engine.IsAdmin()){Elevate(resumeStart);return false;}
            return true;
        }
        async Task<bool> EnsureDependenciesAsync(bool requireTelegram=false)
        {
            if(preview||busy||updateStartupGate!=null||closing||exitPending)return false;
            // Hidden fixtures never write components or contact download servers.
            if(!persistPreferences)return true;
            Begin("Подготовка");var token=operation.Token;
            bool includeTelegram=requireTelegram||Selected().Contains("telegram")&&(!automaticConnection.Checked||TelegramDesktopInstalled());
            try
            {
                var current=await Task.Run(()=>dependencies.Inspect(includeTelegram,token),token);
                if(!current.Ready)
                {
                    summary.Text="Проверка установленных компонентов…";
                    await dependencies.ReuseInstalledAsync(includeTelegram,token);
                    current=await Task.Run(()=>dependencies.Inspect(includeTelegram,token),token);
                }
                token.ThrowIfCancellationRequested();if(closing||IsDisposed)return false;
                bool retry=false;
                while(!current.Ready)
                {
                    using(var prompt=new SetupPrompt(theme,retry,automaticConnection.Checked&&mode.SelectedIndex==0))
                    {
                        if(prompt.ShowDialog(this)!=DialogResult.Yes){summary.Text="Установка отложена.";return false;}
                        if(prompt.AutomaticStart){connectOnOpen.Checked=true;SavePreferencesRequired();}
                    }
                    try
                    {
                        Record("components_install_started");workState="Установка";RefreshControls();
                        await dependencies.InstallAsync(includeTelegram,new Progress<DependencyProgress>(p=>
                        {if(!closing&&!IsDisposed){summary.Text=p.Message+" "+p.Percent+"%";}}),token);
                        token.ThrowIfCancellationRequested();current=await Task.Run(()=>dependencies.Inspect(includeTelegram,token),token);
                    }
                    catch(OperationCanceledException){if(token.IsCancellationRequested)throw;retry=true;Record("components_download_failed");}
                    catch(Exception error){RecordFailure("components",error);retry=true;}
                    token.ThrowIfCancellationRequested();if(closing||IsDisposed)return false;
                }
                if(includeTelegram)await Task.Run(()=>BootstrapPayload.Ensure(Path.Combine(dependencies.ComponentsRoot,"telegram")),token);
                token.ThrowIfCancellationRequested();
                summary.Text="Готово к подключению.";Record("components_ready");return true;
            }
            catch(OperationCanceledException){if(!closing&&!IsDisposed)summary.Text="Установка отменена.";return false;}
            catch(Exception error){RecordFailure("components",error);if(!closing&&!IsDisposed)summary.Text="Не удалось подготовить компоненты. Нажмите «Включить», чтобы повторить.";return false;}
            finally{End();}
        }
        async Task PrepareSelectedListAsync()
        {
            preparedExclusions="";
            if(mode.SelectedIndex!=0)return;
            bool download=publicLists.Checked;
            try{if(download&&!String.IsNullOrEmpty(PublicLists.GetActivePath(CancellationToken.None)))download=false;}
            catch(IOException){}
            var captured=CaptureSettings();
            Begin(download?"Загрузка списка":"Подготовка доменов");
            try
            {
                if(download)await PublicLists.UpdateAsync(new Progress<string>(Write),operation.Token);
                string folder=Path.Combine(preview||!persistPreferences?AppDomain.CurrentDomain.BaseDirectory:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LocalLink"),"lists");
                var token=operation.Token;
                preparedExclusions=await Task.Run(()=>DomainSelection.WriteExclusions(captured,folder,token),token);
                operation.Token.ThrowIfCancellationRequested();RefreshLists();
            }
            catch(OperationCanceledException){throw;}
            catch(Exception error)
            {
                RecordFailure("lists",error);
                throw new IOException("Не удалось подготовить домены. Повторите включение.",error);
            }
            finally{End();}
        }
        void Begin(string title)
        {
            recoveryPolicy.Pause(true);
            busy=true;workState=title;activeOperation=title=="Подготовка"?"start":title=="Проверка"?"check":title=="Подбор Discord"?"discord_tune":title=="Подбор метода"?"tune":title=="Загрузка списка"?"lists":title=="Восстановление"?"recovery":title=="Обновление"?"app_update":title=="Автонастройка"?"automatic":"none";
            Record("operation_started","operation",activeOperation,"filterRunning",engine.IsRunning);operation=automaticConnecting&&automaticLifetime!=null?CancellationTokenSource.CreateLinkedTokenSource(automaticLifetime.Token):new CancellationTokenSource();RefreshControls();
        }
        void End()
        {
            if(busy)Record("operation_finished","operation",activeOperation,"filterRunning",engine.IsRunning);
            activeOperation="none";
            if(operation!=null){operation.Dispose();operation=null;}busy=false;
            recoveryPolicy.Pause(featureDialogOpen||updatingApplication||automaticConnecting);
            if(closing||IsDisposed)return;
            if(exitPending){exitPending=false;Close();}else RefreshControls();
        }
        void StopOperation()
        {
            if(closing||IsDisposed)return;
            CancelAutomaticConnection();
            DisarmRecovery();
            engineFailure=false;
            Record("operation_cancelled","operation",activeOperation);Record("filter_stop_requested");
            if(operation!=null)operation.Cancel();
            // An explicit Stop dismisses an already pending failure instead of
            // redisplaying it on the next timer tick as a new crash.
            engine.TakeUnexpectedExit();
            try{engine.Stop();Record("filter_stopped");summary.Text="Остановлено.";Write("Остановлен только компонент LocalLink.");}
            catch(Exception e){RecordFailure("stop",e);Write(e.Message);summary.Text="Остановка не подтверждена. См. проверки.";}RefreshControls();
        }
        async Task StartAsync()
        {
            if(busy||preview||!persistPreferences||automaticConnecting||updateStartupGate!=null)return;
            if(automaticConnection.Checked&&mode.SelectedIndex==0){await StartAutomaticConnectionAsync();return;}
            DisarmRecovery();
            Record("filter_start_requested");RecordConfiguration();
            try
            {
                ResetEngineFailureForNewStart();
                if(!await EnsureDependenciesAsync())return;
                await PrepareSelectedListAsync();
                var spec=Spec();Profiles.Build(spec);Save();if(!Permit(true))return;
                engineFailure=false;Begin("Подготовка");summary.Text="Проверяем компоненты и список доменов…";
                var token=operation.Token;
                using(var prepared=await engine.PrepareStartAsync(spec,token))
                {
                    Record("filter_prepared");
                    token.ThrowIfCancellationRequested();if(closing||IsDisposed)return;
                    workState="Запуск";RefreshControls();engine.StartPrepared(prepared);
                }
                await Task.Delay(500,token);token.ThrowIfCancellationRequested();
                if(closing||IsDisposed)return;
                if(!engine.IsRunning){InspectEngineExit();throw new IOException("Движок завершился при запуске. Не отключайте защиту Windows.");}
                Record("filter_started");
                ArmRecovery(spec);
                var evidence=engine.LastProfileEvidence;if(evidence!=null)Write(evidence.ToDisplayString());
                summary.Text=mode.SelectedIndex==0?"Фильтр запущен.":"Фильтр запущен для указанного сервера.";Write(summary.Text);
            }
            catch(OperationCanceledException){Record("operation_cancelled","operation","start");TryStop();if(!closing&&!IsDisposed&&!engineFailure)summary.Text="Отменено.";}
            catch(Exception e){RecordFailure("start",e);InspectEngineExit();TryStop();if(!closing&&!IsDisposed){if(!engineFailure)summary.Text="Не удалось включить: "+e.Message;engineFailure=true;Write(e.Message);}}
            finally{End();}
        }
        void TryStop(){if(closing||IsDisposed)return;try{engine.Stop();Record("filter_stopped");}catch(Exception e){RecordFailure("stop",e);Write(e.Message);}}
        void AddRow(CheckRow row)
        {
            if(closing||IsDisposed)return;ListViewItem item;
            bool newResult=!rows.TryGetValue(row.Key,out item);var previous=newResult?null:item.Tag as CheckRow;
            if(newResult||previous==null||previous.Passed!=row.Passed||previous.Milliseconds!=row.Milliseconds||previous.DiagnosticCode!=row.DiagnosticCode)
                Record("check_result","service",row.ServiceId,"stage",row.Key,"code",row.DiagnosticCode,"passed",row.Passed,"milliseconds",row.Milliseconds);
            if(newResult){item=new ListViewItem(new[]{row.Name,row.State,""});rows.Add(row.Key,item);rowOrder.Add(row.Key);if(ShowDiagnosticRow(row))results.Items.Add(item);}
            item.SubItems[0].Text=row.Name;item.SubItems[1].Text=row.State;item.SubItems[2].Text=row.Milliseconds>0?row.Milliseconds+" мс":"—";
            item.ToolTipText=row.Detail;item.Tag=row;if(theme!=null)item.ForeColor=RowColor(row);
            RefreshDiagnosticsNotice();
        }
        bool ShowDiagnosticRow(CheckRow row)
        {
            if(showDetails.Checked||row.Key==null||!row.Key.StartsWith("discord.desktop.",StringComparison.Ordinal))return true;
            if(row.Key.EndsWith(".dns",StringComparison.Ordinal)||row.Key.EndsWith(".tcp",StringComparison.Ordinal))return false;
            return !row.Key.EndsWith(".tls",StringComparison.Ordinal)||row.Key.StartsWith("discord.desktop.cdn.",StringComparison.Ordinal);
        }
        void RebuildVisibleRows()
        {
            if(closing||IsDisposed)return;results.BeginUpdate();
            try{results.Items.Clear();foreach(string key in rowOrder){var item=rows[key];if(ShowDiagnosticRow((CheckRow)item.Tag))results.Items.Add(item);}}
            finally{results.EndUpdate();}RefreshDiagnosticsNotice();
        }
        void RefreshDiagnosticsNotice()
        {
            diagnosticsNotice.Text="Проверка публичных узлов. Голос и игровые сессии проверяются в приложениях.";
        }
        void ResetRows(){Record("checks_reset");results.Items.Clear();rows.Clear();rowOrder.Clear();detail.Text="Выберите строку для подробностей.";RefreshDiagnosticsNotice();}
        async Task CheckAsync()
        {
            if(busy||automaticConnecting||preview||!persistPreferences)return;var ids=Selected();if(ids.Count==0){summary.Text="Выберите хотя бы один сервис.";return;}
            Save();Begin("Проверка");Record("checks_started","services",ids);ResetRows();summary.Text="Проверяем сайты и публичные узлы приложений";
            try
            {
                Write(NetworkInfo.Summary());var token=operation.Token;
                var result=await ServiceDiagnostics.RunAsync(ids,new Progress<CheckRow>(AddRow),token);token.ThrowIfCancellationRequested();
                if(closing||IsDisposed)return;
                foreach(var row in result)AddRow(row);
                Record("checks_finished","count",result.Count);
                summary.Text="Проверки: "+result.Count(x=>x.Passed==true)+" успешных, "+result.Count(x=>x.Passed==false)+" с проблемами.";
                Write(summary.Text+" Голос и игровые сессии проверяются отдельно. VPN не переключался.");
            }
            catch(OperationCanceledException){Record("operation_cancelled","operation","check");if(!closing&&!IsDisposed&&!engineFailure)summary.Text="Проверка отменена.";}
            catch(Exception e){RecordFailure("check",e);if(!closing&&!IsDisposed){summary.Text="Проверка не завершена. См. журнал.";Write(e.Message);}}
            finally{End();}
        }
        async Task TuneDiscordAsync()
        {
            if(busy||automaticConnecting||preview||!persistPreferences||mode.SelectedIndex!=0||!Selected().Contains("discord"))return;
            DisarmRecovery();
            bool ignoreVpnThisRun=ignoreVpn.Checked;ignoreVpn.Checked=false;
            try
            {
                if(!await EnsureDependenciesAsync())return;
                await PrepareSelectedListAsync();
                var spec=Spec();Profiles.Build(spec);Save();if(!Permit())return;
                if(MessageBox.Show(this,"Подобрать метод Discord? Это займёт несколько минут.","Подбор Discord",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
                engine.Stop();ResetEngineFailureForNewStart();engineFailure=false;
                lastTuning=null;Begin("Подбор Discord");ResetRows();var token=operation.Token;
                Record("tuning_started","operation","discord_tune","vpnOverride",ignoreVpnThisRun);
                var result=await Tuning.RunDiscordAsync(engine,spec,Selected(),new Progress<CheckRow>(AddRow),delegate(string phase){if(!closing){summary.Text=phase;Write(phase);}},token,ignoreVpnThisRun);
                lastTuning=result;if(closing||IsDisposed)return;token.ThrowIfCancellationRequested();
                Record("tuning_finished","operation","discord_tune","strategy",result.Strategy,"enabled",result.Verified);
                if(result.Verified)discordStrategy.SelectedIndex=methods.IndexOf(result.Strategy)+1;
                summary.Text=result.Verified?"Discord: "+HumanMethod(result.Strategy)+". Фильтр включён; голос проверьте в звонке.":result.Summary;
                Write(summary.Text);var evidence=engine.LastProfileEvidence;if(evidence!=null)Write(evidence.ToDisplayString());Save();if(result.Verified&&engine.IsRunning){spec.DiscordStrategy=result.Strategy;ArmRecovery(spec);}
            }
            catch(OperationCanceledException){Record("operation_cancelled","operation","discord_tune");TryStop();if(!closing&&!IsDisposed&&!engineFailure)summary.Text="Подбор Discord отменён; исходные методы сохранены, фильтр выключен.";}
            catch(Exception e){RecordFailure("discord_tune",e);InspectEngineExit();TryStop();if(!closing&&!IsDisposed){if(!engineFailure)summary.Text="Подбор Discord не завершён. Исходные методы сохранены; см. журнал.";Write(e.Message);}}
            finally{End();}
        }
        async Task TuneAsync()
        {
            if(busy||automaticConnecting||preview||!persistPreferences||engine.IsRunning||mode.SelectedIndex!=0)return;
            DisarmRecovery();
            bool ignoreVpnThisRun=ignoreVpn.Checked;ignoreVpn.Checked=false;
            try
            {
                ResetEngineFailureForNewStart();
                if(!await EnsureDependenciesAsync())return;
                await PrepareSelectedListAsync();
                var spec=Spec();Profiles.Build(spec);var checkedServices=Selected().Where(id=>id!="telegram").ToList();
                if(checkedServices.Count==0){summary.Text="Для подбора выберите сервис с HTTPS-проверкой, например Discord или YouTube.";return;}
                Save();if(!Permit())return;engineFailure=false;lastTuning=null;Begin("Подбор метода");ResetRows();
                Record("tuning_started","operation","tune","vpnOverride",ignoreVpnThisRun);
                var token=operation.Token;
                var result=await Tuning.RunAsync(engine,spec,checkedServices,new Progress<CheckRow>(AddRow),delegate(string phase){if(!closing){summary.Text=phase;Write(phase);}},token,ignoreVpnThisRun);
                lastTuning=result;
                Record("tuning_finished","operation","tune","strategy",result.Strategy,"enabled",result.Verified);
                if(closing||IsDisposed)return;
                token.ThrowIfCancellationRequested();strategy.SelectedIndex=Math.Max(0,methods.IndexOf(result.Strategy));summary.Text=result.Summary;Write(result.Summary);
                var evidence=engine.LastProfileEvidence;if(evidence!=null)Write(evidence.ToDisplayString());Save();if(result.Verified&&engine.IsRunning){spec.Strategy=result.Strategy;ArmRecovery(spec);}
            }
            catch(OperationCanceledException){Record("operation_cancelled","operation","tune");TryStop();if(!closing&&!IsDisposed&&!engineFailure)summary.Text="Подбор отменён.";}
            catch(Exception e){RecordFailure("tune",e);InspectEngineExit();TryStop();if(!closing&&!IsDisposed){if(!engineFailure)summary.Text="Подбор не завершён. См. журнал.";Write(e.Message);}}
            finally{End();}
        }
        void ExportTuning()
        {
            if(preview||busy||lastTuning==null)return;
            try
            {
                string report=TuningReport.Export(AppDomain.CurrentDomain.BaseDirectory,lastTuning,engine.LastProfileEvidence);
                summary.Text="Отчёт сохранён в папку reports рядом с LocalLink.";Write(summary.Text);
                MessageBox.Show(this,"Отчёт подбора сохранён:\r\n"+report+"\r\nМожно отправить этот JSON для разбора.","Отчёт",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }
            catch(Exception error){RecordFailure("report",error);summary.Text="Не удалось сохранить отчёт. Результат подбора остаётся в памяти до выхода.";Write(summary.Text);}
        }
        async Task UpdateListsAsync()
        {
            if(busy||preview||engine.IsRunning)return;
            Begin("Загрузка списка");Record("lists_update_started");summary.Text="Загружаем публичный список доменов…";
            try
            {
                var token=operation.Token;var result=await PublicLists.UpdateAsync(new Progress<string>(Write),token);token.ThrowIfCancellationRequested();
                if(closing||IsDisposed)return;
                RefreshLists();summary.Text=result.Message;Write(result.Message);Save();
                Record("lists_update_finished");
            }
            catch(OperationCanceledException){Record("operation_cancelled","operation","lists");if(!closing&&!IsDisposed)summary.Text="Загрузка отменена.";}
            catch(Exception error){RecordFailure("lists",error);if(!closing&&!IsDisposed){summary.Text="Не удалось обновить список. Предыдущая версия сохранена.";Write(summary.Text);}}
            finally{End();}
        }
        void Discover()
        {
            if(preview)return;
            try{endpoints.Items.Clear();foreach(var item in NetworkInfo.FindVpnConnections())endpoints.Items.Add(item);Write("TCP-кандидатов: "+endpoints.Items.Count+". Адрес и протокол нужно сверить вручную.");}
            catch(Exception error){RecordFailure("discover",error);Write("Не удалось получить кандидатов VPN.");}
        }
        void Elevate(bool resumeStart=false)
        {
            if(preview||!persistPreferences||Engine.IsAdmin()||busy||automaticConnecting||updateStartupGate!=null||engine.IsRunning)return;
            Record("elevation_requested");Save();try{using(Process child=Process.Start(new ProcessStartInfo(Application.ExecutablePath,resumeStart?"--elevated-start":"--elevated-restart"){UseShellExecute=true,Verb="runas"})){}exitRequested=true;Close();}
            catch(System.ComponentModel.Win32Exception error){RecordFailure("elevate",error);Record("elevation_cancelled");summary.Text="Повышение прав отменено.";}
        }
        public void ShowElevatedRestartNotice(bool resumeStart=false)
        {
            resumeStartAfterElevation=resumeStart;
            if(resumeStart){summary.Text="Подготовка подключения…";RefreshControls();return;}
            summary.Text="Окно перезапущено для запуска фильтра. Нажмите «Включить» ещё раз; автоматически он не включался.";
            Write(summary.Text);RefreshControls();
        }
        void CreateTray()
        {
            trayMenu=new ContextMenuStrip();
            var open=new ToolStripMenuItem("Открыть LocalLink");open.Click+=delegate{RestoreWindow();};trayMenu.Items.Add(open);
            trayStop=new ToolStripMenuItem("Остановить обработку");trayStop.Click+=delegate{StopOperation();};trayMenu.Items.Add(trayStop);
            trayMenu.Items.Add(new ToolStripSeparator());
            var exit=new ToolStripMenuItem("Выход");exit.Click+=delegate{if(!closing){exitRequested=true;Close();}};trayMenu.Items.Add(exit);
            tray=new NotifyIcon{Icon=applicationIcon,Text="LocalLink — фильтр выключен",ContextMenuStrip=trayMenu,Visible=true};
            tray.DoubleClick+=delegate{RestoreWindow();};tray.BalloonTipClicked+=delegate{RestoreWindow();};
        }
        void RestoreWindow()
        {
            if(closing||IsDisposed)return;
            ShowInTaskbar=true;Show();if(WindowState==FormWindowState.Minimized)WindowState=FormWindowState.Normal;Activate();
            Record("window_restored");
        }
        void HideToTray()
        {
            if(preview||closing||tray==null)return;
            Record("window_hidden");Save();Hide();ShowInTaskbar=false;
            if(!trayNoticeShown)
            {
                trayNoticeShown=true;
                try{tray.ShowBalloonTip(3000,"LocalLink остаётся в трее","Закрытие окна не останавливает фильтр. Открыть, остановить или выйти можно через значок возле часов.",ToolTipIcon.Info);}
                catch(System.ComponentModel.Win32Exception){Write("Окно свёрнуто в трей; уведомление Windows недоступно.");}
            }
        }
        async Task OpenTelegramBridgeAsync()
        {
            if(preview||!persistPreferences||busy||automaticConnecting||closing||exitPending)return;
            Record("telegram_open_requested");
            try
            {
                if(!await EnsureDependenciesAsync(true))return;
                LaunchTelegramBridge(false);
            }
            catch(Exception e)
            {
                RecordFailure("telegram",e);Write("Модуль Telegram не запущен: "+e.Message);
                MessageBox.Show(this,"Не удалось открыть модуль Telegram.","Telegram",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            }
        }
        void LaunchTelegramBridge(bool automatic)
        {
                string folder=Path.Combine(dependencies.ComponentsRoot,"telegram");
                string executable=BootstrapPayload.Ensure(folder);
                if(!File.Exists(executable))throw new FileNotFoundException("Не удалось подготовить Telegram. Повторите запуск.");
                // Verified adjacent resource only: no PATH search, URL, shell verb, or user-supplied arguments.
                if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0||(File.GetAttributes(executable)&FileAttributes.ReparsePoint)!=0)
                    throw new IOException("Модуль Telegram должен находиться в папке сборки, а не по символической ссылке.");
                using(Process child=Process.Start(new ProcessStartInfo(executable){UseShellExecute=false,WorkingDirectory=folder,Arguments=automatic?"--auto-setup":""}))
                {if(child==null)throw new IOException("Windows не подтвердила запуск модуля.");}
                Write("Открыт модуль Telegram.");
                Record("telegram_opened");
        }
        void OnClosing(object sender,FormClosingEventArgs args)
        {
            if(closing)return;
            if(!preview&&!exitRequested&&args.CloseReason==CloseReason.UserClosing)
            {args.Cancel=true;HideToTray();return;}
            if(exportingReport&&args.CloseReason==CloseReason.UserClosing){args.Cancel=true;exitPending=true;return;}
            CancelAutomaticConnection();
            DisarmRecovery();
            if(operation!=null)operation.Cancel();
            // Explicit exit waits for cancelled UI work to finish, so async callbacks cannot
            // race disposal. Session shutdown must never wait for this cancellation path.
            if((busy||automaticConnecting)&&args.CloseReason==CloseReason.UserClosing)
            {
                args.Cancel=true;exitPending=true;TryStop();workState="Завершение";summary.Text="Завершаем текущую проверку и выходим…";RefreshControls();return;
            }
            try{engine.Stop();}
            catch(Exception e)
            {
                RecordFailure("close",e);
                if(args.CloseReason==CloseReason.UserClosing)
                {args.Cancel=true;exitRequested=false;exitPending=false;RestoreWindow();Write(e.Message);summary.Text="Остановка не подтверждена. Повторите остановку перед выходом.";return;}
                // Shutdown/Application.Exit still disposes the owned job; never block Windows.
                Write(e.Message);
            }
            Save();closing=true;timer.Stop();
        }
        void DisposeOwnedResources()
        {
            if(resourcesDisposed)return;resourcesDisposed=true;closing=true;
            DisposeApplicationFeatures();
            SystemEvents.UserPreferenceChanged-=ThemeChanged;timer.Stop();timer.Dispose();
            if(tray!=null){tray.Visible=false;tray.Dispose();tray=null;}
            if(trayMenu!=null){trayMenu.Dispose();trayMenu=null;}
            applicationIcon.Dispose();
            tips.Dispose();
            // Engine owns only its own filter job. Telegram bridge has an independent UI
            // and lifecycle; exiting this window must not silently kill Telegram's proxy.
            try{engine.Dispose();}catch(Exception error){RecordFailure("dispose",error);}
            if(!busy&&operation!=null){operation.Dispose();operation=null;}
            if(ownsHistory)history.Dispose();
        }
        public void SetPreview(string name)
        {
            if(!preview)return;
            if(name.IndexOf("startup",StringComparison.Ordinal)>=0)
            {
                shell.OpenSettings();
                for(Control parent=startupToggle.Parent;parent!=null;parent=parent.Parent)
                {var scroll=parent as ScrollableControl;if(scroll!=null&&scroll.AutoScroll)scroll.ScrollControlIntoView(startupToggle);}
            }
            else if(name.IndexOf("settings",StringComparison.Ordinal)>=0)shell.OpenSettings();
            else if(name.IndexOf("--render-checks",StringComparison.Ordinal)==0)PreviewChecks();
            else if(name=="--render-small")Size=MinimumSize;
        }
        public void PreviewChecks()
        {
            summary.Text="Пример отображения — не результаты проверки";
            AddRow(new CheckRow{Key="demo1",Name="Discord — Gateway WSS Hello",State="Доступен",Passed=true,Milliseconds=184,Detail="Демонстрационные данные."});
            AddRow(new CheckRow{Key="demo2",Name="Discord — голос",State="Вручную",Passed=null,Detail="Демонстрационные данные. Не подтверждает работу голоса."});
            AddRow(new CheckRow{Key="demo3",Name="YouTube — HTTPS/TLS",State="Доступен",Passed=true,Milliseconds=96,Detail="Демонстрационные данные."});
            AddRow(new CheckRow{Key="demo4",Name="Telegram Desktop — MTProto",State="Вручную",Passed=null,Detail="Демонстрационные данные."});
            detail.Text="Демонстрационные данные. Сеть и фильтр не запускались.";
        }
    }
}
