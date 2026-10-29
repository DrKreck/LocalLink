using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LocalLink
{
    internal sealed class DesktopIdentity
    {
        internal int ProcessId,SessionId;
        internal string ImagePath,UserSid;
        internal bool Elevated;
    }

    // Documented desktop automation handoff, not token editing or elevation:
    // https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643
    // The live desktop's Shell.Application is used; creating a fresh
    // Shell.Application COM instance in this process would not drop rights.
    internal static class DesktopProcess
    {
        const uint QueryLimitedInformation=0x1000,TokenQuery=8;
        const int ErrorInsufficientBuffer=122;
        const int TokenUser=1,TokenSessionId=12,TokenElevation=20;
        static readonly Guid ShellWindowsClass=new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        static readonly Guid TopLevelBrowser=new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        static readonly Guid ShellBrowserId=new Guid("000214E2-0000-0000-C000-000000000046");
        static readonly Guid DispatchId=new Guid("00020400-0000-0000-C000-000000000046");

        internal static void LaunchVerifiedHelper(string helperPath,string planPath,string planSha256)
        {
            ValidateHelperArguments(helperPath,planPath,planSha256);
            if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)
                throw new InvalidOperationException("Передача обновления рабочему столу требует потока интерфейса.");
            DesktopIdentity expected=InspectDesktop();RequireTrustedDesktop(expected);
            long expectedStart=QueryStartTimeUtcTicks(expected.ProcessId);
            object windows=null,desktop=null,browserObject=null,viewObject=null,folder=null,application=null;
            try
            {
                windows=Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClass,true));
                object[] args={0,null,8,0,1};
                var modifiers=new ParameterModifier(5);modifiers[0]=true;modifiers[1]=true;modifiers[3]=true;
                desktop=windows.GetType().InvokeMember("FindWindowSW",BindingFlags.InvokeMethod,null,windows,args,new[]{modifiers},CultureInfo.InvariantCulture,null);
                if(desktop==null)throw new IOException("Окно рабочего стола недоступно для обновления.");
                var provider=(IComServiceProvider)desktop;Guid service=TopLevelBrowser,iid=ShellBrowserId;
                Marshal.ThrowExceptionForHR(provider.QueryService(ref service,ref iid,out browserObject));
                var browser=(IShellBrowser)browserObject;IntPtr browserWindow;
                Marshal.ThrowExceptionForHR(browser.GetWindow(out browserWindow));
                uint browserPid;GetWindowThreadProcessId(browserWindow,out browserPid);
                if(browserPid!=(uint)expected.ProcessId)throw new IOException("Изменился процесс рабочего стола.");
                IShellView view;Marshal.ThrowExceptionForHR(browser.QueryActiveShellView(out view));viewObject=view;
                iid=DispatchId;Marshal.ThrowExceptionForHR(view.GetItemObject(0,ref iid,out folder));
                application=folder.GetType().InvokeMember("Application",BindingFlags.GetProperty,null,folder,null,CultureInfo.InvariantCulture);
                if(application==null)throw new IOException("Автоматизация рабочего стола недоступна.");
                // Re-check the desktop's identity after acquiring the COM chain,
                // before asking it to run the exact verified helper.
                DesktopIdentity current=InspectDesktop();RequireTrustedDesktop(current);
                if(current.ProcessId!=expected.ProcessId||QueryStartTimeUtcTicks(current.ProcessId)!=expectedStart)
                    throw new IOException("Рабочий стол изменился во время подготовки обновления.");
                ValidateHelperArguments(helperPath,planPath,planSha256);
                application.GetType().InvokeMember("ShellExecute",BindingFlags.InvokeMethod,null,application,
                    new object[]{Path.GetFullPath(helperPath),"--apply "+Quote(Path.GetFullPath(planPath))+" "+planSha256,
                        Path.GetDirectoryName(Path.GetFullPath(helperPath)),"open",0},CultureInfo.InvariantCulture);
            }
            catch(TargetInvocationException error)
            {throw new IOException("Не удалось передать обновление обычному процессу рабочего стола.",error.InnerException??error);}
            catch(COMException error)
            {throw new IOException("Рабочий стол не подтвердил запуск обновления.",error);}
            finally{ReleaseUnique(application,folder,viewObject,browserObject,desktop,windows);}
        }

        internal static DesktopIdentity InspectDesktop()
        {
            IntPtr window=GetShellWindow();uint pid;
            if(window==IntPtr.Zero||GetWindowThreadProcessId(window,out pid)==0||pid<1||pid>Int32.MaxValue)
                throw new IOException("Не найден обычный рабочий стол Windows.");
            using(var process=OpenLimited((int)pid))
            {
                IntPtr token;
                if(!OpenProcessToken(process.Handle,TokenQuery,out token))throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    return new DesktopIdentity{ProcessId=(int)pid,ImagePath=QueryImagePath(process.Handle),UserSid=ReadSid(token),
                        SessionId=ReadTokenInt(token,TokenSessionId),Elevated=ReadTokenInt(token,TokenElevation)!=0};
                }
                finally{CloseHandle(token);}
            }
        }
        internal static void RequireTrustedDesktop(DesktopIdentity identity)
        {
            using(var current=WindowsIdentity.GetCurrent())
                if(identity==null||!IsTrustedDesktop(identity,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),
                    current.User.Value,Process.GetCurrentProcess().SessionId))
                    throw new IOException("Для обновления нужен обычный рабочий стол того же пользователя Windows.");
        }
        internal static bool IsTrustedDesktop(DesktopIdentity identity,string explorerPath,string userSid,int sessionId)
        {
            if(identity==null||identity.ProcessId<1||identity.Elevated||identity.SessionId!=sessionId||identity.UserSid!=userSid||String.IsNullOrEmpty(identity.ImagePath))return false;
            try{return String.Equals(Path.GetFullPath(identity.ImagePath),Path.GetFullPath(explorerPath),StringComparison.OrdinalIgnoreCase);}
            catch(ArgumentException){return false;}catch(NotSupportedException){return false;}catch(PathTooLongException){return false;}
        }

        internal static void ValidateHelperArguments(string helperPath,string planPath,string planSha256)
        {
            if(!Regex.IsMatch(planSha256??"",@"\A[a-f0-9]{64}\z"))throw new IOException("Недопустимая контрольная сумма плана.");
            string helper=OrdinaryPath(helperPath),plan=OrdinaryPath(planPath);
            if(!String.Equals(Path.GetFileName(helper),"LocalLink.UpdateHelper.exe",StringComparison.Ordinal)||
                !String.Equals(Path.GetFileName(plan),"plan.json",StringComparison.Ordinal)||
                !String.Equals(Path.GetDirectoryName(helper),Path.GetDirectoryName(plan),StringComparison.OrdinalIgnoreCase))
                throw new IOException("Модуль и план обновления должны находиться в одной отдельной папке.");
            if(!File.Exists(helper)||new FileInfo(helper).Length<1||new FileInfo(helper).Length>8*1024*1024)
                throw new IOException("Модуль обновления не найден или слишком большой.");
            using(var input=new FileStream(plan,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(input.Length<2||input.Length>32768)throw new IOException("Недопустимый размер плана обновления.");
                using(var hash=SHA256.Create())
                    if(BitConverter.ToString(hash.ComputeHash(input)).Replace("-","").ToLowerInvariant()!=planSha256)
                        throw new IOException("План обновления изменился.");
            }
        }
        static string OrdinaryPath(string path)
        {
            if(String.IsNullOrWhiteSpace(path)||!Regex.IsMatch(path,@"\A[A-Za-z]:[\\/]")||path.IndexOf(':',2)>=0||path.IndexOfAny(new[]{'"','\r','\n','\0'})>=0)
                throw new IOException("Нужен обычный локальный путь обновления.");
            string full=Path.GetFullPath(path);
            for(string part=full;!String.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))
            {
                try{if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("Перенаправления в пути обновления не поддерживаются.");}
                catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
            }
            return full;
        }
        static string Quote(string value){return "\""+value+"\"";}

        internal static string QueryImagePath(int processId)
        {using(var process=OpenLimited(processId))return QueryImagePath(process.Handle);}
        static string QueryImagePath(IntPtr process)
        {
            var buffer=new StringBuilder(32768);uint length=(uint)buffer.Capacity;
            if(!QueryFullProcessImageName(process,0,buffer,ref length))throw new Win32Exception(Marshal.GetLastWin32Error());
            return buffer.ToString();
        }
        internal static long QueryStartTimeUtcTicks(int processId)
        {using(var process=OpenLimited(processId))return QueryStartTimeUtcTicks(process.Handle);}
        static long QueryStartTimeUtcTicks(IntPtr process)
        {
            FileTime created,exit,kernel,user;
            if(!GetProcessTimes(process,out created,out exit,out kernel,out user))throw new Win32Exception(Marshal.GetLastWin32Error());
            return DateTime.FromFileTimeUtc(((long)created.High<<32)|created.Low).Ticks;
        }
        internal static ObservedProcess Observe(int processId){return new ObservedProcess(processId);}
        internal sealed class ObservedProcess:IDisposable
        {
            readonly LimitedProcess process;
            internal ObservedProcess(int id){process=OpenLimited(id);}
            internal string ImagePath{get{return QueryImagePath(process.Handle);}}
            internal long StartedUtcTicks{get{return QueryStartTimeUtcTicks(process.Handle);}}
            internal bool HasExited
            {
                get
                {
                    uint code;
                    if(!GetExitCodeProcess(process.Handle,out code))throw new Win32Exception(Marshal.GetLastWin32Error());
                    return code!=259; // LocalLink never uses STILL_ACTIVE as an exit code.
                }
            }
            public void Dispose(){process.Dispose();}
        }
        sealed class LimitedProcess:IDisposable
        {
            internal IntPtr Handle;internal LimitedProcess(IntPtr handle){Handle=handle;}
            public void Dispose(){if(Handle!=IntPtr.Zero){CloseHandle(Handle);Handle=IntPtr.Zero;}}
        }
        static LimitedProcess OpenLimited(int pid)
        {
            if(pid<1)throw new ArgumentOutOfRangeException("pid");
            IntPtr handle=OpenProcess(QueryLimitedInformation,false,pid);
            if(handle==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            return new LimitedProcess(handle);
        }
        static int ReadTokenInt(IntPtr token,int kind)
        {
            int value,length;
            if(!GetTokenInformationInt(token,kind,out value,sizeof(int),out length))throw new Win32Exception(Marshal.GetLastWin32Error());
            if(length!=sizeof(int))throw new IOException("Недопустимые данные токена рабочего стола.");return value;
        }
        static string ReadSid(IntPtr token)
        {
            int length;GetTokenInformation(token,TokenUser,IntPtr.Zero,0,out length);
            if(Marshal.GetLastWin32Error()!=ErrorInsufficientBuffer||length<1||length>4096)throw new IOException("Не удалось проверить пользователя рабочего стола.");
            IntPtr buffer=Marshal.AllocHGlobal(length);
            try
            {if(!GetTokenInformation(token,TokenUser,buffer,length,out length))throw new Win32Exception(Marshal.GetLastWin32Error());return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;}
            finally{Marshal.FreeHGlobal(buffer);}
        }
        static void ReleaseUnique(params object[] values)
        {
            var released=new List<object>();
            foreach(object value in values)
            {
                if(value==null||!Marshal.IsComObject(value)||released.Exists(x=>Object.ReferenceEquals(x,value)))continue;
                released.Add(value);try{Marshal.FinalReleaseComObject(value);}catch(InvalidComObjectException){}
            }
        }

        [StructLayout(LayoutKind.Sequential)]struct FileTime{internal uint Low,High;}
        [DllImport("user32.dll")]static extern IntPtr GetShellWindow();
        [DllImport("user32.dll",SetLastError=true)]static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
        [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="QueryFullProcessImageNameW")]
        static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder buffer,ref uint length);
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetProcessTimes(IntPtr process,out FileTime creation,out FileTime exit,out FileTime kernel,out FileTime user);
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetExitCodeProcess(IntPtr process,out uint exitCode);
        [DllImport("advapi32.dll",SetLastError=true)]static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
        [DllImport("advapi32.dll",SetLastError=true,EntryPoint="GetTokenInformation")]
        static extern bool GetTokenInformation(IntPtr token,int kind,IntPtr information,int length,out int returned);
        [DllImport("advapi32.dll",SetLastError=true,EntryPoint="GetTokenInformation")]
        static extern bool GetTokenInformationInt(IntPtr token,int kind,out int information,int length,out int returned);

        [ComImport,Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IComServiceProvider
        {[PreserveSig]int QueryService(ref Guid service,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out object result);}
        [ComImport,Guid("000214E2-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellBrowser
        {
            [PreserveSig]int GetWindow(out IntPtr window);
            [PreserveSig]int ContextSensitiveHelp(bool enter);
            [PreserveSig]int InsertMenusSB(IntPtr menu,IntPtr widths);
            [PreserveSig]int SetMenuSB(IntPtr menu,IntPtr oleMenu,IntPtr window);
            [PreserveSig]int RemoveMenusSB(IntPtr menu);
            [PreserveSig]int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)]string text);
            [PreserveSig]int EnableModelessSB(bool enable);
            [PreserveSig]int TranslateAcceleratorSB(IntPtr message,ushort id);
            [PreserveSig]int BrowseObject(IntPtr pidl,uint flags);
            [PreserveSig]int GetViewStateStream(uint mode,out IntPtr stream);
            [PreserveSig]int GetControlWindow(uint id,out IntPtr window);
            [PreserveSig]int SendControlMsg(uint id,uint message,IntPtr wparam,IntPtr lparam,out IntPtr result);
            [PreserveSig]int QueryActiveShellView(out IShellView view);
        }
        [ComImport,Guid("000214E3-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellView
        {
            [PreserveSig]int GetWindow(out IntPtr window);
            [PreserveSig]int ContextSensitiveHelp(bool enter);
            [PreserveSig]int TranslateAccelerator(IntPtr message);
            [PreserveSig]int EnableModeless(bool enable);
            [PreserveSig]int UIActivate(uint state);
            [PreserveSig]int Refresh();
            [PreserveSig]int CreateViewWindow(IntPtr previous,IntPtr settings,IntPtr browser,IntPtr rect,out IntPtr window);
            [PreserveSig]int DestroyViewWindow();
            [PreserveSig]int GetCurrentInfo(IntPtr settings);
            [PreserveSig]int AddPropertySheetPages(uint reserved,IntPtr callback,IntPtr lparam);
            [PreserveSig]int SaveViewState();
            [PreserveSig]int SelectItem(IntPtr pidl,uint flags);
            [PreserveSig]int GetItemObject(uint item,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out object result);
        }
    }
}
