using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LocalLink
{
    public sealed partial class MainForm
    {
        readonly Dictionary<string,Panel> settingsSections=new Dictionary<string,Panel>(StringComparer.Ordinal);
        readonly Dictionary<string,Button> settingsSectionButtons=new Dictionary<string,Button>(StringComparer.Ordinal);
        readonly string[] settingsSectionOrder={"basic","applications","profiles","updates","advanced"};
        string selectedSettingsSection="basic";

        void BuildOrganizedSettings(Panel page)
        {
            page.AutoScroll=false;page.Padding=Padding.Empty;
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty,Padding=new Padding(18,0,18,12)};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,54));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var tabs=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=5,RowCount=1,Margin=Padding.Empty,AccessibleName="Разделы настроек"};
            tabs.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var host=new Panel{Dock=DockStyle.Fill,Margin=Padding.Empty,TabStop=false,Name="SettingsSectionHost"};
            layout.Controls.Add(tabs,0,0);layout.Controls.Add(host,0,1);page.Controls.Add(layout);
            string[] titles={"Основное","Приложения","Профили","Обновления","Дополнительно"};
            for(int index=0;index<settingsSectionOrder.Length;index++)
            {
                string id=settingsSectionOrder[index];
                tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,20));
                Button action=id=="advanced"?advancedToggle:new ThemedButton();
                Button(action,titles[index],false);action.Name="SettingsTab_"+id;action.AccessibleName="Настройки: "+titles[index];
                action.Font=AppTheme.Font(10,FontStyle.Regular);action.Margin=new Padding(0,0,index==4?0:6,8);action.TabIndex=index;
                action.Click+=delegate{SelectSettingsSection(id);};
                action.KeyDown+=SettingsNavigationKeyDown;
                tabs.Controls.Add(action,index,0);settingsSectionButtons.Add(id,action);
                var section=new Panel{Dock=DockStyle.Fill,AutoScroll=true,Margin=Padding.Empty,Padding=new Padding(0,4,0,0),
                    Name="SettingsSection_"+id,AccessibleName=titles[index],TabStop=false,Visible=false,Enabled=false};
                settingsSections.Add(id,section);host.Controls.Add(section);
            }

            mode.Items.AddRange(new object[]{"Прямое подключение","Помощь VPN · TLS / TCP","Помощь VPN · WireGuard"});
            strategy.Items.AddRange(new object[]{"Мягкий","По SNI","Перестановка","Фейк + разбиение","Фейк + перестановка"});
            discordStrategy.Items.Add("Как общий метод");foreach(var label in strategy.Items)discordStrategy.Items.Add(label);
            Combo(mode);Combo(strategy);Combo(discordStrategy);Combo(endpoints);

            var basic=SettingsSectionGrid("basic",2);
            BuildAutomaticSettings(basic,0);
            var startupActions=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
            startupActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));startupActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            startupActions.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            Button(startupToggle,"Добавить в автозапуск",false);Button(startupRemove,"Убрать из автозапуска",false);startupRemove.Margin=new Padding(8,0,0,0);
            startupActions.Controls.Add(startupToggle);startupActions.Controls.Add(startupRemove);
            basic.SetColumnSpan(Field(basic,"При входе в Windows",startupActions,0,1,96),2);
            startupStatus.Dock=DockStyle.Fill;startupStatus.Tag="muted";startupStatus.Font=AppTheme.Font(9.5f,FontStyle.Regular);
            startupStatus.Margin=new Padding(12,0,12,6);startupStatus.AutoSize=true;
            AddSettingsRow(basic,startupStatus,2,36,2);
            tips.SetToolTip(startupToggle,"Открывать LocalLink в трее при входе в Windows. Автоподключение выбирается отдельно выше.");

            var applications=SettingsSectionGrid("applications",2);
            var catalog=ServiceCatalog.All();int serviceRows=(catalog.Count+1)/2;
            var services=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=serviceRows,Margin=Padding.Empty};
            services.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));services.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            for(int row=0;row<serviceRows;row++)services.RowStyles.Add(new RowStyle(SizeType.Percent,100f/serviceRows));
            int serviceIndex=0;
            foreach(var service in catalog)
            {
                var box=new ThemedCheckBox{Text=service.Name,Checked=settings.Services.Contains(service.Id),AutoSize=true,Dock=DockStyle.Fill,
                    Margin=new Padding(0,0,8,4),AccessibleName="Обрабатывать: "+service.Name};
                // User clicks persist immediately; model/preset assignments do
                // not trigger Click and cannot write partially loaded settings.
                box.Click+=delegate
                {if(!preview&&!loadingPreset&&!busy&&!automaticConnecting&&!engine.IsRunning&&!closing){Save();RefreshControls();}};
                tips.SetToolTip(box,service.Scope);selections.Add(service.Id,box);
                services.Controls.Add(box,serviceIndex%2,serviceIndex/2);serviceIndex++;
            }
            applications.SetColumnSpan(Field(applications,"Что обрабатывать",services,0,0,64+32*serviceRows),2);
            publicLists.Text="Остальной список";publicLists.Visible=false;applications.Controls.Add(publicLists,0,3);
            Button(manageDomains,"Домены и категории",false);Field(applications,"Сайты и исключения",manageDomains,0,1,92);
            Button(telegramBridge,"Модуль Telegram",false);Field(applications,"Telegram Desktop",telegramBridge,1,1,92);

            var profiles=SettingsSectionGrid("profiles",2);
            var presetActions=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=Padding.Empty};
            presetActions.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            presetActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));presetActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,132));presetActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,156));
            Combo(presetChoice);presetChoice.DisplayMember="Name";presetChoice.AccessibleName="Выбранный пресет";
            Button(savePreset,"Сохранить",false);savePreset.Margin=new Padding(8,0,0,0);Button(savePresetAs,"Сохранить как…",false);savePresetAs.Margin=new Padding(8,0,0,0);
            presetActions.Controls.Add(presetChoice);presetActions.Controls.Add(savePreset);presetActions.Controls.Add(savePresetAs);
            profiles.SetColumnSpan(Field(profiles,"Сохранённые настройки",presetActions,0,0,96),2);
            BuildPresetExchangeSettings(profiles,1);
            using(var network=new TableLayoutPanel{ColumnCount=2})
            {
                BuildNetworkSettings(network);
                MoveSettingsRow(network,5,profiles,2,92,2);
                MoveSettingsRow(network,6,profiles,3,40,2);
            }

            var updates=SettingsSectionGrid("updates",1);
            using(var updateRows=new TableLayoutPanel{ColumnCount=1})
            {
                BuildUpdateSettings(updateRows);
                MoveSettingsRow(updateRows,3,updates,0,92,1);
                MoveSettingsRow(updateRows,4,updates,1,48,1);
                MoveSettingsRow(updateRows,6,updates,2,88,1);
                MoveSettingsRow(updateRows,5,basic,3,88,2);
            }

            advancedSettings.Dock=DockStyle.Top;advancedSettings.AutoSize=true;advancedSettings.Margin=Padding.Empty;
            FindSettingsSection("advanced").Controls.Add(advancedSettings);
            var grid=advancedGrid;grid.Dock=DockStyle.Top;grid.AutoSize=true;grid.Margin=Padding.Empty;grid.ColumnCount=2;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));advancedSettings.Controls.Add(grid);
            settingsNotice.Dock=DockStyle.Fill;settingsNotice.Margin=new Padding(12,0,12,4);settingsNotice.Tag="muted";
            settingsNotice.Font=AppTheme.Font(9.5f,FontStyle.Regular);settingsNotice.TextAlign=ContentAlignment.MiddleLeft;
            AddSettingsRow(grid,settingsNotice,0,40,2);
            Field(grid,"Режим подключения",mode,0,1,88);Field(grid,"Общий метод",strategy,1,1,88);
            Field(grid,"Метод Discord",discordStrategy,0,2,88);Button(tuneDiscord,"Подобрать для Discord",false);Field(grid,"Повторный подбор Discord",tuneDiscord,1,2,88);
            Button(tune,"Подобрать метод заново",false);Field(grid,"Повторный подбор",tune,0,3,88);
            ignoreVpn.Text="Игнорировать VPN · один подбор";ignoreVpn.Dock=DockStyle.Fill;ignoreVpn.AutoSize=true;
            ignoreVpn.AccessibleName="Игнорировать VPN при следующем подборе";Field(grid,"Подбор при работающем VPN",ignoreVpn,1,3,88);
            tips.SetToolTip(ignoreVpn,"Разрешает следующий автоподбор с VPN. Сам VPN не отключается; после запуска флажок сбрасывается.");
            var protocols=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Margin=Padding.Empty};
            voice.Text="Голос Discord";telegram.Text="Telegram Desktop";quic.Text="QUIC / HTTP3";
            foreach(var box in new[]{voice,telegram,quic}){box.AutoSize=true;box.Margin=new Padding(0,0,0,8);protocols.Controls.Add(box);}
            grid.SetColumnSpan(Field(grid,"Протоколы",protocols,0,4,170),2);
            tips.SetToolTip(voice,"UDP-профиль голоса. Результат проверяется в звонке.");tips.SetToolTip(telegram,"Обработка TCP Telegram. Для отдельного прокси — модуль Telegram.");tips.SetToolTip(quic,"Обработка QUIC / HTTP3.");
            custom.Multiline=true;custom.ScrollBars=ScrollBars.Vertical;custom.Dock=DockStyle.Fill;custom.BorderStyle=BorderStyle.FixedSingle;
            grid.SetColumnSpan(Field(grid,"Свои домены · по одному в строке",custom,0,5,170),2);
            Button(updateLists,"Обновить список доменов",false);Field(grid,"Публичные домены",updateLists,0,6,92);
            listStatus.Dock=DockStyle.Fill;listStatus.Tag="muted";listStatus.Font=AppTheme.Font(9.5f,FontStyle.Regular);listStatus.Margin=Padding.Empty;Field(grid,"Состояние списка",listStatus,1,6,92);
            var address=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
            address.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));address.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,78));
            endpoint.Dock=DockStyle.Fill;port.Dock=DockStyle.Fill;port.Minimum=1;port.Maximum=65535;port.Value=443;port.Margin=new Padding(8,0,0,0);address.Controls.Add(endpoint);address.Controls.Add(port);
            grid.SetColumnSpan(Field(grid,"VPN · IP и порт",address,0,7,92),2);
            Button(find,"Найти соединения VPN",false);Field(grid,"Соединения VPN",find,0,8,88);Field(grid,"Выбрать соединение",endpoints,1,8,88);
            Button(elevate,"Права администратора",false);Field(grid,"Сетевой компонент",elevate,0,9,88);
            showDetails.Text="Технические этапы проверки";showDetails.AutoSize=true;showDetails.Margin=Padding.Empty;Field(grid,"Диагностика",showDetails,1,9,88);
            Button(exportTuning,"Сохранить отчёт подбора",false);grid.SetColumnSpan(Field(grid,"Отчёт",exportTuning,0,10,88),2);
            log.Multiline=true;log.ReadOnly=true;log.ScrollBars=ScrollBars.Vertical;log.Dock=DockStyle.Fill;log.BorderStyle=BorderStyle.FixedSingle;log.Font=AppTheme.Font(9,FontStyle.Regular);
            grid.SetColumnSpan(Field(grid,"Журнал",log,0,11,160),2);
            diagnosticsNotice.Dock=DockStyle.Fill;diagnosticsNotice.Tag="muted";diagnosticsNotice.Font=AppTheme.Font(9,FontStyle.Regular);diagnosticsNotice.AutoSize=true;
            AddSettingsRow(grid,diagnosticsNotice,12,44,2);
            SelectSettingsSection("basic");
        }

        TableLayoutPanel SettingsSectionGrid(string id,int columns)
        {
            var grid=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=columns,Margin=Padding.Empty,TabStop=false};
            for(int column=0;column<columns;column++)grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100f/columns));
            FindSettingsSection(id).Controls.Add(grid);return grid;
        }
        static void AddSettingsRow(TableLayoutPanel target,Control control,int row,int height,int span)
        {
            while(target.RowStyles.Count<=row)target.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            target.Controls.Add(control,0,row);target.SetColumnSpan(control,span);
        }
        static void MoveSettingsRow(TableLayoutPanel source,int oldRow,TableLayoutPanel target,int row,int height,int span)
        {
            Control control=source.GetControlFromPosition(0,oldRow);
            if(control==null)throw new InvalidOperationException("Не найдена строка настроек.");
            source.Controls.Remove(control);AddSettingsRow(target,control,row,height,span);
        }
        Panel FindSettingsSection(string id)
        {
            Panel section;if(!settingsSections.TryGetValue(id,out section))throw new ArgumentException("Неизвестный раздел настроек.","id");
            return section;
        }
        void SelectSettingsSection(string id)
        {
            FindSettingsSection(id);
            foreach(var pair in settingsSections){pair.Value.Visible=pair.Key==id;pair.Value.Enabled=pair.Key==id;}
            FindSettingsSection(id).BringToFront();selectedSettingsSection=id;
            advancedExpanded=id=="advanced";advancedSettings.Visible=advancedExpanded;
            foreach(var pair in settingsSectionButtons)
            {
                pair.Value.Tag=pair.Key==id?"nav_selected":"nav";
                pair.Value.AccessibleDescription=pair.Key==id?"Выбранный раздел":"Открыть раздел";
                if(theme!=null)theme.Apply(pair.Value);
            }
            advancedToggle.Text="Дополнительно";advancedToggle.AccessibleName="Настройки: Дополнительно";
        }
        void SettingsNavigationKeyDown(object sender,KeyEventArgs args)
        {
            string focused=selectedSettingsSection;
            foreach(var pair in settingsSectionButtons)if(Object.ReferenceEquals(pair.Value,sender)){focused=pair.Key;break;}
            int index=Array.IndexOf(settingsSectionOrder,focused),next=index;
            if(args.KeyCode==Keys.Right)next=(index+1)%settingsSectionOrder.Length;
            else if(args.KeyCode==Keys.Left)next=(index+settingsSectionOrder.Length-1)%settingsSectionOrder.Length;
            else if(args.KeyCode==Keys.Home)next=0;
            else if(args.KeyCode==Keys.End)next=settingsSectionOrder.Length-1;
            else return;
            SelectSettingsSection(settingsSectionOrder[next]);settingsSectionButtons[settingsSectionOrder[next]].Focus();
            args.Handled=true;args.SuppressKeyPress=true;
        }
    }
}
