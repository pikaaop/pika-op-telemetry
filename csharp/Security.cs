using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace PikaOp {
internal class MailOptions {
    public bool Enabled {get;set;} public bool IncludeDevice {get;set;}
    public string Recipient {get;set;} public string Sender {get;set;} public string Host {get;set;}
    public int Port {get;set;} public string PasswordProtected {get;set;}
    public MailOptions(){Recipient="spidermanpikaop@gmail.com";Sender="";Host="smtp.gmail.com";Port=587;PasswordProtected="";}
}
internal static class Security {
    internal static readonly string DataFolder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PikaOp");
    internal static string MailPath {get{return Path.Combine(DataFolder,"mail.json");}}
    internal static string LogPath {get{return Path.Combine(DataFolder,"security.log");}}
    internal static string Hex(byte[] b){return BitConverter.ToString(b).Replace("-","").ToLowerInvariant();}
    internal static bool VerifySignature(byte[] data,byte[] signature,string key) {
        using(var rsa=new RSACryptoServiceProvider()){rsa.PersistKeyInCsp=false;rsa.FromXmlString(key);return rsa.VerifyData(data,CryptoConfig.MapNameToOID("SHA256"),signature);}
    }
    internal static string VerifyRelease(string folder,string exe) {
        try {
            byte[] manifest,signature;long payloadLength;ReadRelease(exe,out manifest,out signature,out payloadLength);
            if(!VerifySignature(manifest,signature,ReleaseKey.PublicXml))return "Release manifest signature is invalid";
            var info=Json.Serializer().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(manifest));
            string expected=Convert.ToString(info["sha256"]),actual;
            using(var stream=File.OpenRead(exe))using(var sha=SHA256.Create()){
                var buffer=new byte[65536];long remaining=payloadLength;
                while(remaining>0){int count=stream.Read(buffer,0,(int)Math.Min(buffer.Length,remaining));if(count==0)throw new EndOfStreamException();sha.TransformBlock(buffer,0,count,buffer,0);remaining-=count;}
                sha.TransformFinalBlock(new byte[0],0,0);actual=Hex(sha.Hash);
            }
            if(!string.Equals(expected,actual,StringComparison.Ordinal))return "Executable integrity check failed";
            return null;
        }catch(Exception ex){return "Release verification failed: "+ex.Message;}
    }
    internal static void ReadRelease(string exe,out byte[] manifest,out byte[] signature,out long payloadLength){
        using(var stream=File.OpenRead(exe))using(var reader=new BinaryReader(stream)){
            if(stream.Length<16)throw new InvalidDataException("Missing signed release trailer");
            stream.Position=stream.Length-16;int manifestSize=reader.ReadInt32(),signatureSize=reader.ReadInt32();
            if(Encoding.ASCII.GetString(reader.ReadBytes(8))!="PIKAEND1"||manifestSize<1||manifestSize>65536||signatureSize<1||signatureSize>1024)throw new InvalidDataException("Invalid signed release trailer");
            payloadLength=stream.Length-16-manifestSize-signatureSize;if(payloadLength<64)throw new InvalidDataException("Invalid release length");
            stream.Position=payloadLength;manifest=reader.ReadBytes(manifestSize);signature=reader.ReadBytes(signatureSize);
            if(manifest.Length!=manifestSize||signature.Length!=signatureSize)throw new EndOfStreamException();
        }
    }
    internal static MailOptions LoadMail(){try{return File.Exists(MailPath)?Json.Read<MailOptions>(MailPath):new MailOptions();}catch{return new MailOptions();}}
    internal static void SaveMail(MailOptions options,string password) {
        if(password.Length>0)options.PasswordProtected=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password),null,DataProtectionScope.CurrentUser));
        Json.AtomicWrite(MailPath,options);
    }
    internal static string EventBody(string reason,MailOptions settings) {
        var body=new StringBuilder("pika op integrity event\nUTC: "+DateTime.UtcNow.ToString("o")+"\nEvent: "+reason+"\n");
        if(settings.IncludeDevice){body.AppendLine("Device information included with this installation's explicit opt-in.");body.AppendLine("PC name: "+Environment.MachineName);
            var ips=Dns.GetHostAddresses(Environment.MachineName).Where(a=>a.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(a));body.AppendLine("Local IPv4: "+string.Join(", ",ips.Select(a=>a.ToString())));}
        body.AppendLine("This detects an integrity mismatch, not a person's identity or proof of reverse engineering.");return body.ToString();
    }
    internal static void Log(string text){try{Directory.CreateDirectory(DataFolder);File.AppendAllText(LogPath,DateTime.UtcNow.ToString("o")+" "+text+Environment.NewLine);}catch(IOException){}catch(UnauthorizedAccessException){}}
    internal static void Send(MailOptions options,string reason) {
        if(!options.Enabled)throw new InvalidOperationException("Email alerts are disabled");
        if(string.IsNullOrWhiteSpace(options.Host)||options.Port<1||options.Port>65535)throw new InvalidOperationException("Invalid SMTP server");
        var sender=new MailAddress(options.Sender);var recipient=new MailAddress(options.Recipient);
        if(string.IsNullOrEmpty(options.PasswordProtected))throw new InvalidOperationException("Configure a sender app password locally first");
        string password=Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(options.PasswordProtected),null,DataProtectionScope.CurrentUser));
        ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
        using(var smtp=new SmtpClient(options.Host,options.Port)){smtp.EnableSsl=true;smtp.Timeout=10000;smtp.Credentials=new NetworkCredential(options.Sender,password);
            using(var mail=new MailMessage(sender,recipient)){mail.Subject="pika op integrity notification";mail.Body=EventBody(reason,options);smtp.Send(mail);}}
    }
    internal static void Report(string reason,bool popup) {
        Log(reason);
        var mail=LoadMail();Task delivery=null;
        if(mail.Enabled)delivery=Task.Run(delegate{try{Send(mail,reason);}catch(Exception ex){Log("Email delivery failed: "+ex.Message);}});
        if(popup)System.Windows.MessageBox.Show(reason+"\n\nThe application will not start. See "+LogPath,"pika op — integrity alert",System.Windows.MessageBoxButton.OK,System.Windows.MessageBoxImage.Warning);
        if(delivery!=null)delivery.Wait(TimeSpan.FromSeconds(15));
    }
}
}
