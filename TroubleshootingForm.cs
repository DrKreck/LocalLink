using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LocalLink
{
    public sealed class TroubleshootingForm : Form
    {
        readonly AppTheme theme;
        readonly Action<string> exportStandardReport;
        readonly Func<TroubleshootingState> captureState;
        readonly Func<TroubleshootingCase,IProgress<CheckRow>,CancellationToken,Task<TroubleshootingRun>> run;
        readonly ComboBox scenario=new ThemedComboBox(),outcome=new ThemedComboBox();
        readonly Label instruction=new Label(),progressLabel=new Label();
        readonly TextBox summary=new ThemedTextBox();
        readonly Button primary=new ThemedButton(),export=new ThemedButton(),close=new ThemedButton();
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
        TroubleshootingSession session;
        CancellationTokenSource operation;
        bool running,closePending,finished,disposed;
        int probeCount;
        public string LastReportPath { get; private set; }
        public bool SessionActive { get { return session!=null&&!finished; } }

        public TroubleshootingForm(AppTheme theme,Action<string> exportStandardReport,Func<TroubleshootingState> captureState)
            :this(theme,exportStandardReport,captureState,Troubleshooting.RunAsync) { }
        internal TroubleshootingForm(AppTheme theme,Action<string> exportStandardReport,Func<TroubleshootingState> captureState,
            Func<TroubleshootingCase,IProgress<CheckRow>,CancellationToken,Task<TroubleshootingRun>> run)
        {
            if(theme==null)throw new ArgumentNullException("theme");if(exportStandardReport==null)throw new ArgumentNullException("exportStandardReport");
            if(captureState==null)throw new ArgumentNullException("captureState");if(run==null)throw new ArgumentNullException("run");
            this.theme=theme;this.exportStandardReport=exportStandardReport;this.captureState=captureState;this.run=run;
            Text="Что не работает? — LocalLink";Font=AppTheme.Font(10.5f,FontStyle.Regular);Tag="root";
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
            ClientSize=new Size(690,530);MinimumSize=new Size(620,520);StartPosition=FormStartPosition.CenterParent;
            MaximizeBox=false;MinimizeBox=false;ShowInTaskbar=false;
            var grid=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(24),ColumnCount=1,RowCount=8,Margin=Padding.Empty,Tag="root"};
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach(float height in new[]{38f,48f,36f,90f,32f,44f})grid.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent,100));grid.RowStyles.Add(new RowStyle(SizeType.Absolute,52));
            var title=TextLabel("Что не работает?",true);grid.Controls.Add(title,0,0);
            scenario.DropDownStyle=ComboBoxStyle.DropDownList;scenario.Dock=DockStyle.Top;scenario.Margin=new Padding(0,8,0,8);scenario.AccessibleName="Сценарий проблемы";
            foreach(var item in Troubleshooting.Cases())scenario.Items.Add(item);scenario.SelectedIndex=0;grid.Controls.Add(scenario,0,1);
            var consent=TextLabel("Проверим публичные узлы; фильтр и настройки не меняем.",false);consent.Tag="muted";grid.Controls.Add(consent,0,2);
            instruction.Dock=DockStyle.Fill;instruction.Margin=new Padding(0,6,0,4);instruction.AccessibleName="Следующий шаг";grid.Controls.Add(instruction,0,3);
            progressLabel.Dock=DockStyle.Fill;progressLabel.Tag="muted";progressLabel.AccessibleName="Ход проверки";grid.Controls.Add(progressLabel,0,4);
            outcome.DropDownStyle=ComboBoxStyle.DropDownList;outcome.Dock=DockStyle.Top;outcome.Margin=new Padding(0,4,0,8);outcome.AccessibleName="Результат в приложении";
            outcome.Items.AddRange(new object[]{"Результат ещё не проверял","Проблема осталась","Заработало"});outcome.SelectedIndex=0;outcome.Enabled=false;grid.Controls.Add(outcome,0,5);
            summary.Dock=DockStyle.Fill;summary.Multiline=true;summary.ReadOnly=true;summary.ScrollBars=ScrollBars.Vertical;summary.WordWrap=true;summary.BorderStyle=BorderStyle.FixedSingle;
            summary.Margin=new Padding(0,6,0,12);summary.AccessibleName="Итог проверки";grid.Controls.Add(summary,0,6);
            var buttons=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=Padding.Empty};
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,43));buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,34));buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,23));buttons.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            Style(primary,"Начать",true);Style(export,"Сохранить отчёт",false);Style(close,"Закрыть",false);
            primary.Margin=new Padding(0,6,8,0);export.Margin=new Padding(0,6,8,0);close.Margin=new Padding(0,6,0,0);export.Enabled=false;
            buttons.Controls.Add(primary,0,0);buttons.Controls.Add(export,1,0);buttons.Controls.Add(close,2,0);grid.Controls.Add(buttons,0,7);Controls.Add(grid);
            scenario.SelectedIndexChanged+=delegate{if(session==null)InstructionBefore();};
            primary.Click+=async delegate{await AdvanceAsync();};export.Click+=delegate{Export();};
            close.Click+=delegate { if(running){closePending=true;if(operation!=null)operation.Cancel();}else Close(); };
            FormClosing+=delegate(object sender,FormClosingEventArgs e)
            {
                if(running&&e.CloseReason==CloseReason.UserClosing){closePending=true;if(operation!=null)operation.Cancel();e.Cancel=true;}
            };
            timer.Interval=1000;timer.Tick+=delegate
            {
                if(session!=null&&!finished&&DateTime.UtcNow>=session.StartedUtc.AddMinutes(TroubleshootingSession.MaximumDurationMinutes))
                {
                    if(operation!=null)operation.Cancel();
                    if(!running)CompleteCancelled();
                    instruction.Text="Достигнут лимит времени. Можно сохранить собранный отчёт.";
                }
            };
            InstructionBefore();theme.Apply(this);HandleCreated+=delegate{theme.Apply(this);};
            Shown+=delegate{scenario.Focus();};
        }
        static Label TextLabel(string text,bool title)
        { return new Label {Text=text,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Margin=Padding.Empty,Font=AppTheme.Font(title?16f:10.5f,title?FontStyle.Bold:FontStyle.Regular)}; }
        static void Style(Button button,string text,bool prominent)
        { button.Text=text;button.AccessibleName=text;button.Dock=DockStyle.Fill;button.FlatStyle=FlatStyle.Flat;button.Cursor=Cursors.Hand;button.Tag=prominent?"primary":null; }
        void InstructionBefore()
        {
            var item=scenario.SelectedItem as TroubleshootingCase;
            instruction.Text=item==null?"Выберите сценарий.":"1. Нажмите «Начать». После проверки узлов:\r\n"+item.Instruction;
            progressLabel.Text="Содержимое трафика и данные аккаунтов не собираются.";
        }
        async Task AdvanceAsync()
        {
            if(running||disposed)return;
            if(finished)
            {
                session=null;finished=false;scenario.Enabled=true;outcome.Enabled=false;outcome.SelectedIndex=0;export.Enabled=false;summary.Clear();primary.Text="Начать";InstructionBefore();return;
            }
            if(session==null)
            {
                var item=scenario.SelectedItem as TroubleshootingCase;if(item==null)return;
                session=new TroubleshootingSession(item.Id,captureState());scenario.Enabled=false;timer.Start();
                var result=await ProbeAsync("Начальная проверка узлов…");
                if(result==null||disposed||closePending)return;
                session.SetBaseline(result.Rows,result.TimedOut);session.BeginReproduction();
                instruction.Text="2. "+session.Case.Instruction+"\r\nЗатем выберите результат и нажмите «Завершить».";
                progressLabel.Text="Воспроизведите проблему. Фильтр остаётся в прежнем состоянии.";outcome.Enabled=true;primary.Text="Завершить";outcome.Focus();
            }
            else
            {
                string resultCode=outcome.SelectedIndex==1?"still-fails":outcome.SelectedIndex==2?"works":"not-checked";
                outcome.Enabled=false;
                var result=await ProbeAsync("Повторная проверка узлов…");if(result==null||disposed||closePending)return;
                session.Finish(resultCode,result.Rows,result.TimedOut,captureState());Complete();
            }
        }
        async Task<TroubleshootingRun> ProbeAsync(string message)
        {
            running=true;primary.Enabled=false;probeCount=0;instruction.Text=message;close.Text="Отменить";
            var current=new CancellationTokenSource();operation=current;
            var progress=new Progress<CheckRow>(delegate(CheckRow row)
            { if(!disposed&&running&&ReferenceEquals(operation,current))progressLabel.Text="Получено результатов: "+(++probeCount).ToString(CultureInfo.InvariantCulture); });
            try { return await run(session.Case,progress,current.Token); }
            catch(OperationCanceledException){if(!disposed)CompleteCancelled();return null;}
            catch(Exception){if(!disposed){CompleteCancelled();summary.Text="Проверка узлов завершилась ошибкой. Сохраните отчёт; причина пока не установлена.";}return null;}
            finally
            {
                operation=null;current.Dispose();running=false;
                if(!disposed){primary.Enabled=true;close.Text="Закрыть";if(closePending)Close();}
            }
        }
        void CompleteCancelled()
        {
            if(session!=null&&!session.EndedUtc.HasValue)session.Finish("cancelled",null,false,captureState());Complete();
        }
        void Complete()
        {
            timer.Stop();finished=true;outcome.Enabled=false;export.Enabled=session!=null&&session.EndedUtc.HasValue;primary.Text="Новая проверка";
            if(session!=null)summary.Text=session.Summary();instruction.Text="3. Сохраните ZIP и отправьте его автору.";progressLabel.Text="Отчёт сохраняется только на вашем компьютере.";
        }
        void Export()
        {
            if(running||session==null||!session.EndedUtc.HasValue)return;
            using(var dialog=new SaveFileDialog {Filter="ZIP-отчёт (*.zip)|*.zip",DefaultExt="zip",AddExtension=true,OverwritePrompt=true,
                FileName="LocalLink-case-"+session.Case.Id+"-"+DateTime.Now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture)+".zip"})
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                try { LastReportPath=Troubleshooting.Export(dialog.FileName,session,exportStandardReport);progressLabel.Text="Отчёт сохранён. Отправьте ZIP автору."; }
                catch(Exception) { MessageBox.Show(this,"Не удалось сохранить отчёт. Выберите новое имя в доступной папке.","LocalLink",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing&&!disposed){disposed=true;timer.Stop();timer.Dispose();if(operation!=null)operation.Cancel();}
            base.Dispose(disposing);
        }
    }
}
