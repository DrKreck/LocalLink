using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LocalLink
{
    public sealed partial class MainForm
    {
        readonly CheckBox automaticUpdateCheck=new ThemedCheckBox();
        UpdatePreferenceStore updatePreferenceStore;
        UpdatePreferences updatePreferences;
        CancellationTokenSource backgroundUpdate;
        AppUpdateOffer pendingUpdateOffer;
        DateTime automaticUpdateAfterUtc;
        bool automaticUpdateAttempted,checkingUpdateInBackground;

        void InitializeUpdateExperience()
        {
            try
            {
                updatePreferenceStore=new UpdatePreferenceStore(preview||!persistPreferences?
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"update-preferences-preview.json"):UpdatePreferenceStore.DefaultPath);
                updatePreferences=updatePreferenceStore.Load();
            }
            catch(Exception error)
            {updatePreferences=new UpdatePreferences{AutoCheck=false};RecordFailure("app_update",error);}
            automaticUpdateCheck.Checked=updatePreferences.AutoCheck;
            automaticUpdateCheck.CheckedChanged+=delegate
            {
                updatePreferences.AutoCheck=automaticUpdateCheck.Checked;
                if(!updatePreferences.AutoCheck)
                {pendingUpdateOffer=null;if(backgroundUpdate!=null)backgroundUpdate.Cancel();}
                SaveUpdatePreferences();
            };
            automaticUpdateAfterUtc=DateTime.MaxValue;
            if(!preview&&persistPreferences)Shown+=delegate{automaticUpdateAfterUtc=DateTime.UtcNow.AddSeconds(2);};
        }

        bool SaveUpdatePreferences()
        {
            try
            {
                if(updatePreferenceStore==null)throw new IOException("Хранилище обновлений недоступно.");
                updatePreferenceStore.Save(updatePreferences);return true;
            }
            catch(Exception error)
            {RecordFailure("app_update",error);if(!closing&&!IsDisposed)updateStatus.Text="Выбор обновлений не сохранён. Проверьте доступ к папке настроек.";return false;}
        }

        bool CanOfferAutomaticUpdate()
        {
            return !preview&&persistPreferences&&!closing&&!IsDisposed&&Visible&&WindowState!=FormWindowState.Minimized&&
                !busy&&!automaticConnecting&&updateStartupGate==null&&!exitPending&&!exportingReport&&!featureDialogOpen&&!updatingApplication&&!recovering&&!recoveryPolicy.Pending;
        }
        void TickUpdateExperience()
        {
            if(!CanOfferAutomaticUpdate()||checkingUpdateInBackground||!updatePreferences.AutoCheck)return;
            if(pendingUpdateOffer!=null)
            {
                var offer=pendingUpdateOffer;pendingUpdateOffer=null;
                if(updatePreferences.ShouldPrompt(offer.Version,DateTime.UtcNow))PresentAutomaticUpdate(offer);
                return;
            }
            if(!automaticUpdateAttempted&&DateTime.UtcNow>=automaticUpdateAfterUtc&&updatePreferences.ShouldCheck(DateTime.UtcNow))
                CheckAutomaticUpdate();
        }
        async void CheckAutomaticUpdate()
        {
            automaticUpdateAttempted=true;checkingUpdateInBackground=true;
            updatePreferences.MarkAutomaticCheck(DateTime.UtcNow);
            try
            {
                // Persist the throttle before network activity. A read-only settings
                // directory must not cause a request on every subsequent launch.
                if(!SaveUpdatePreferences())return;
                backgroundUpdate=CancellationTokenSource.CreateLinkedTokenSource(featureLifetime.Token);
                backgroundUpdate.CancelAfter(TimeSpan.FromSeconds(15));
                var token=backgroundUpdate.Token;
                var offer=await new AppUpdateClient().CheckAsync(ApplicationVersion(),token);
                token.ThrowIfCancellationRequested();
                if(!closing&&!IsDisposed&&updatePreferences.AutoCheck&&offer!=null&&updatePreferences.ShouldPrompt(offer.Version,DateTime.UtcNow))
                    pendingUpdateOffer=offer;
            }
            catch(OperationCanceledException){}
            catch(Exception error){if(!closing&&!IsDisposed)RecordFailure("app_update",error);}
            finally
            {
                if(backgroundUpdate!=null){backgroundUpdate.Dispose();backgroundUpdate=null;}
                checkingUpdateInBackground=false;
                if(!closing&&!IsDisposed)RefreshApplicationFeatureControls();
            }
        }
        async void PresentAutomaticUpdate(AppUpdateOffer offer)
        {await RunApplicationUpdateAsync(offer,false);}

        Task CheckApplicationUpdateAsync()
        {return RunApplicationUpdateAsync(null,true);}

        UpdatePromptChoice PromptForUpdate(string version)
        {
            using(var prompt=new UpdatePromptForm(theme,version))
            {
                prompt.ShowDialog(this);
                var choice=prompt.Choice;
                updatePreferences.ApplyChoice(version,choice,DateTime.UtcNow);
                bool saved=SaveUpdatePreferences();
                if(saved)updateStatus.Text=choice==UpdatePromptChoice.IgnoreVersion?"Эту версию больше не предлагаем.":
                    choice==UpdatePromptChoice.Later?"Напомним не раньше чем через сутки.":"Подготавливаем обновление…";
                return choice;
            }
        }

        async Task RunApplicationUpdateAsync(AppUpdateOffer prefetchedOffer,bool manual)
        {
            if(preview||busy||automaticConnecting||updateStartupGate!=null||closing||exitPending||exportingReport||updatingApplication||featureDialogOpen||checkingUpdateInBackground)return;
            updatingApplication=true;Begin("Обновление");Record("app_update_check");
            try
            {
                var client=new AppUpdateClient();var token=operation.Token;
                if(manual)updateStatus.Text="Проверяем обновления…";
                var offer=prefetchedOffer??await client.CheckAsync(ApplicationVersion(),token);
                token.ThrowIfCancellationRequested();if(closing||IsDisposed||exitPending)return;
                if(offer==null){updateStatus.Text="Новых версий нет.";return;}
                if(!updatePreferences.ShouldPrompt(offer.Version,DateTime.UtcNow,manual))return;
                if(PromptForUpdate(offer.Version)!=UpdatePromptChoice.Update)return;
                token.ThrowIfCancellationRequested();
                var staged=await client.StageAsync(offer,new Progress<AppUpdateProgress>(value=>{if(!closing&&!IsDisposed)updateStatus.Text=value.Message;}),token);
                token.ThrowIfCancellationRequested();SavePreferencesRequired();
                using(var handoff=await AppUpdateLauncher.LaunchApplyAsync(staged,token))
                {
                    token.ThrowIfCancellationRequested();
                    CompleteUpdateHandoff(handoff,"app_update_staged");
                }
            }
            catch(OperationCanceledException){if(!closing&&!IsDisposed)updateStatus.Text="Обновление отменено.";}
            catch(Exception error){RecordFailure("app_update",error);if(!closing&&!IsDisposed)updateStatus.Text="Обновление не выполнено. Текущая версия сохранена.";}
            finally{updatingApplication=false;End();}
        }
        void CompleteUpdateHandoff(AppUpdateHandoff handoff,string eventCode)
        {
            // Until the verified helper acknowledges readiness, the old filter
            // remains untouched. A failed/aborted handoff cannot run at a later exit.
            bool closed=false;
            try
            {
                DisarmRecovery();engine.Stop();
                handoff.ConfirmExit();Record(eventCode);exitRequested=true;End();Close();
                if(!IsDisposed)throw new IOException("Закрытие приложения отменено.");
                closed=true;
            }
            finally
            {
                if(!closed)
                {
                    handoff.Cancel();exitRequested=false;
                    if(!IsDisposed){closing=false;timer.Start();}
                }
            }
        }
        async Task RollbackApplicationUpdateAsync()
        {
            if(preview||busy||automaticConnecting||updateStartupGate!=null||closing||exitPending||exportingReport||featureDialogOpen||updatingApplication||checkingUpdateInBackground)return;
            updatingApplication=true;Begin("Обновление");
            try
            {
                var previous=AppUpdateLauncher.FindRollback();
                if(previous==null){updateStatus.Text="Предыдущая установленная версия не найдена.";return;}
                if(MessageBox.Show(this,"Вернуть предыдущую версию LocalLink? Настройки сохранятся.","Откат",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
                var token=operation.Token;token.ThrowIfCancellationRequested();SavePreferencesRequired();
                using(var handoff=await AppUpdateLauncher.LaunchRollbackAsync(previous,token))
                {token.ThrowIfCancellationRequested();CompleteUpdateHandoff(handoff,"app_rollback_started");}
            }
            catch(OperationCanceledException){if(!closing&&!IsDisposed)updateStatus.Text="Откат отменён.";}
            catch(Exception error){RecordFailure("app_update",error);if(!closing&&!IsDisposed)updateStatus.Text="Откат не выполнен. Текущая версия сохранена.";}
            finally{updatingApplication=false;End();}
        }
        void DisposeUpdateExperience()
        {pendingUpdateOffer=null;if(backgroundUpdate!=null)backgroundUpdate.Cancel();}
    }
}
