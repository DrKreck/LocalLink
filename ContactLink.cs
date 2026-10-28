using System;
using System.Diagnostics;

namespace LocalLink
{
    public static class ContactLink
    {
        public const string Url="https://t.me/AresC404";
        public const string Email="kirill0063@gmail.com";
        public const string EmailUrl="mailto:kirill0063@gmail.com";
        public static bool TryOpen(){Exception ignored;return TryOpen(out ignored);}
        public static bool TryOpen(out Exception error){return TryOpen(OpenSystemLink,out error);}
        public static bool TryOpen(Action<string> openLink){Exception ignored;return TryOpen(openLink,out ignored);}
        public static bool TryOpen(Action<string> openLink,out Exception error)
        {
            error=null;if(openLink==null)return false;
            try{openLink(Url);return true;}
            catch(Exception failure){error=failure;return false;}
        }
        static void OpenSystemLink(string ignored)
        {
            using(Process process=Process.Start(new ProcessStartInfo(Url){UseShellExecute=true})){}
        }
        public static bool TryOpenEmail(){Exception ignored;return TryOpenEmail(out ignored);}
        public static bool TryOpenEmail(out Exception error){return TryOpenEmail(OpenSystemEmail,out error);}
        public static bool TryOpenEmail(Action<string> openLink){Exception ignored;return TryOpenEmail(openLink,out ignored);}
        public static bool TryOpenEmail(Action<string> openLink,out Exception error)
        {
            error=null;if(openLink==null)return false;
            try{openLink(EmailUrl);return true;}
            catch(Exception failure){error=failure;return false;}
        }
        static void OpenSystemEmail(string ignored)
        {
            using(Process process=Process.Start(new ProcessStartInfo(EmailUrl){UseShellExecute=true})){}
        }
    }
}
