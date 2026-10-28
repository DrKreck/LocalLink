using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security;
using LocalLink;

// Fixed-link semantics only. The injected launcher never opens a browser,
// Telegram, a mail application, a network connection, report file or any real application.
internal static class ContactLinkTests
{
    static int assertions;
    static readonly Dictionary<short,OpCode> Codes=ReadCodes();

    static int Main()
    {
        try
        {
            FixedAddress();LaunchContract();FailureContract();EmailContract();PublicApi();CallAudit();
            Console.WriteLine("Contact link: "+assertions+" offline assertions passed. No real URL, browser, Telegram, file, network or filter action.");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static void Check(bool condition,string description)
    {
        if(!condition)throw new InvalidOperationException(description);
        assertions++;Console.WriteLine("PASS: "+description);
    }
    static void FixedAddress()
    {
        Check(ContactLink.Url=="https://t.me/AresC404","exact contact destination");
        var field=typeof(ContactLink).GetField("Url",BindingFlags.Public|BindingFlags.Static);
        Check(field!=null&&field.IsLiteral&&field.FieldType==typeof(string),"destination is a fixed constant");
        var uri=new Uri(ContactLink.Url,UriKind.Absolute);
        Check(uri.Scheme==Uri.UriSchemeHttps,"contact uses HTTPS");
        Check(uri.Host=="t.me"&&uri.AbsolutePath=="/AresC404","contact targets the supplied Telegram username");
        Check(uri.IsDefaultPort,"no alternate destination port");
        Check(uri.Query.Length==0&&uri.Fragment.Length==0&&uri.UserInfo.Length==0,"no payload, authentication, attachment or forwarding parameters");
    }
    static void LaunchContract()
    {
        int calls=0;string received=null;
        Check(ContactLink.TryOpen(delegate(string address){calls++;received=address;}),"injected successful handler returns success");
        Check(calls==1&&received==ContactLink.Url,"handler receives only the fixed URL once");
        calls=0;Exception error;
        bool opened=ContactLink.TryOpen(delegate(string address){calls++;received=address;},out error);
        Check(opened&&error==null,"successful handler has no error metadata");
        Check(calls==1&&received=="https://t.me/AresC404","error-aware overload keeps the same URL contract");
    }
    static void FailureContract()
    {
        foreach(Exception failure in new Exception[]{new Win32Exception(1155,"SENTINEL_PRIVATE_HANDLER_MESSAGE"),
            new InvalidOperationException("SENTINEL_PRIVATE_HANDLER_MESSAGE"),new SecurityException("SENTINEL_PRIVATE_HANDLER_MESSAGE"),
            new NotSupportedException("SENTINEL_PRIVATE_HANDLER_MESSAGE"),new IOException("SENTINEL_PRIVATE_HANDLER_MESSAGE"),
            new UnauthorizedAccessException("SENTINEL_PRIVATE_HANDLER_MESSAGE"),new UriFormatException("SENTINEL_PRIVATE_HANDLER_MESSAGE")})
        {
            int calls=0;string received=null;Exception error;
            bool opened=ContactLink.TryOpen(delegate(string address){calls++;received=address;throw failure;},out error);
            Check(!opened,"handler failure is reported without escaping: "+failure.GetType().Name);
            Check(Object.ReferenceEquals(error,failure),"original error is available for sanitized diagnostics: "+failure.GetType().Name);
            Check(calls==1&&received==ContactLink.Url,"handler failure never retries or changes URL: "+failure.GetType().Name);
            calls=0;
            Check(!ContactLink.TryOpen(delegate(string address){calls++;throw failure;}),"simple overload safely reports failure: "+failure.GetType().Name);
            Check(calls==1,"simple overload invokes handler once: "+failure.GetType().Name);
        }
        Check(!ContactLink.TryOpen((Action<string>)null),"null test handler cannot fall back to a real browser");
        Exception nullError;
        Check(!ContactLink.TryOpen((Action<string>)null,out nullError),"error-aware null handler remains non-launching");
        Check(nullError==null||nullError is ArgumentNullException,"null test handler cannot invent an OS-handler failure");
    }
    static void PublicApi()
    {
        MethodInfo systemLauncher=typeof(ContactLink).GetMethod("TryOpen",Type.EmptyTypes);
        Check(systemLauncher!=null&&systemLauncher.ReturnType==typeof(bool),"system launcher exists but is not executed in tests");
        foreach(var method in typeof(ContactLink).GetMethods(BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly))
        {
            Check((method.Name=="TryOpen"||method.Name=="TryOpenEmail")&&method.ReturnType==typeof(bool),"public API exposes only fixed-destination bool launch overloads");
            foreach(var parameter in method.GetParameters())
                Check(parameter.ParameterType==typeof(Action<string>)||(parameter.IsOut&&parameter.ParameterType==typeof(Exception).MakeByRefType()),
                    "public API accepts no arbitrary URL, file, report, process or filter object");
        }
    }
    static void EmailContract()
    {
        Check(ContactLink.Email=="kirill0063@gmail.com","exact supplied email address");
        Check(ContactLink.EmailUrl=="mailto:"+ContactLink.Email,"email destination is mailto only");
        foreach(string name in new[]{"Email","EmailUrl"})
        {
            var field=typeof(ContactLink).GetField(name,BindingFlags.Public|BindingFlags.Static);
            Check(field!=null&&field.IsLiteral&&field.FieldType==typeof(string),"email destination is a fixed constant: "+name);
        }
        var uri=new Uri(ContactLink.EmailUrl,UriKind.Absolute);
        Check(uri.Scheme==Uri.UriSchemeMailto&&uri.Query.Length==0&&uri.Fragment.Length==0,"email has no body, attachment or forwarding parameters");
        int calls=0;string received=null;Exception error;
        Check(ContactLink.TryOpenEmail(delegate(string address){calls++;received=address;},out error),"email launcher returns success");
        Check(calls==1&&received==ContactLink.EmailUrl&&error==null,"email handler receives the fixed mailto once");
        calls=0;
        Check(ContactLink.TryOpenEmail(delegate(string address){calls++;received=address;})&&calls==1&&received==ContactLink.EmailUrl,"simple email overload keeps fixed mailto contract");
        foreach(Exception failure in new Exception[]{new Win32Exception(1155,"NO_MAIL_CLIENT_SENTINEL"),
            new InvalidOperationException("MAIL_HANDLER_SENTINEL"),new SecurityException("MAIL_HANDLER_SENTINEL"),
            new NotSupportedException("MAIL_HANDLER_SENTINEL"),new IOException("MAIL_HANDLER_SENTINEL"),
            new UnauthorizedAccessException("MAIL_HANDLER_SENTINEL"),new UriFormatException("MAIL_HANDLER_SENTINEL")})
        {
            calls=0;received=null;
            bool opened=ContactLink.TryOpenEmail(delegate(string address){calls++;received=address;throw failure;},out error);
            Check(!opened&&Object.ReferenceEquals(error,failure),"email failure remains available without escaping: "+failure.GetType().Name);
            Check(calls==1&&received==ContactLink.EmailUrl,"email failure does not retry or change destination: "+failure.GetType().Name);
            calls=0;
            Check(!ContactLink.TryOpenEmail(delegate(string address){calls++;throw failure;})&&calls==1,"simple email failure is safe: "+failure.GetType().Name);
        }
        Check(!ContactLink.TryOpenEmail((Action<string>)null),"null email test handler cannot launch real mail client");
        Check(!ContactLink.TryOpenEmail((Action<string>)null,out error)&&error==null,"error-aware null email handler stays non-launching");
        Check(typeof(ContactLink).GetMethod("TryOpenEmail",Type.EmptyTypes)!=null,"system email launcher exists but is not executed in tests");
    }
    static Dictionary<short,OpCode> ReadCodes()
    {
        var result=new Dictionary<short,OpCode>();
        foreach(var field in typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static))
            if(field.FieldType==typeof(OpCode)){var code=(OpCode)field.GetValue(null);result[code.Value]=code;}
        return result;
    }
    static void CallAudit()
    {
        int inspected=0;
        foreach(var type in SelfTypes(typeof(ContactLink)))
            foreach(var method in type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly))
            {
                var body=method.GetMethodBody();if(body==null)continue;byte[] il=body.GetILAsByteArray();int position=0;
                while(position<il.Length)
                {
                    short value=il[position++];if(value==0xfe)value=unchecked((short)(0xfe00|il[position++]));
                    OpCode code;if(!Codes.TryGetValue(value,out code))throw new InvalidOperationException("Unrecognized launcher IL instruction.");
                    if(code.OperandType==OperandType.InlineMethod)
                    {
                        int token=BitConverter.ToInt32(il,position);var target=method.Module.ResolveMethod(token);
                        string name=target.DeclaringType==null?"":target.DeclaringType.FullName;
                        Check(!name.StartsWith("System.IO.",StringComparison.Ordinal)&&!name.StartsWith("System.Net.",StringComparison.Ordinal),"launcher never reads files or uploads through a network API");
                        Check(!name.StartsWith("LocalLink.Engine",StringComparison.Ordinal)&&!name.StartsWith("LocalLink.BugReport",StringComparison.Ordinal),"launcher cannot stop a filter or create/send a report");
                        inspected++;
                    }
                    position+=OperandBytes(code.OperandType,il,position);
                }
                Check(position==il.Length,"launcher method IL is fully inspected");
            }
        Check(inspected>0,"launcher dependency audit inspected actual method calls");
    }
    static IEnumerable<Type> SelfTypes(Type type)
    {
        yield return type;
        foreach(var nested in type.GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic))
            foreach(var item in SelfTypes(nested))yield return item;
    }
    static int OperandBytes(OperandType type,byte[] il,int position)
    {
        switch(type)
        {
            case OperandType.InlineNone:return 0;
            case OperandType.ShortInlineBrTarget:case OperandType.ShortInlineI:case OperandType.ShortInlineVar:return 1;
            case OperandType.InlineVar:return 2;
            case OperandType.InlineI8:case OperandType.InlineR:return 8;
            case OperandType.InlineSwitch:return 4+BitConverter.ToInt32(il,position)*4;
            default:return 4;
        }
    }
}
