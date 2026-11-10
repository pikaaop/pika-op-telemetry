using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PikaOp {
internal class LicenseClaims {
    public string Product {get;set;} public string Id {get;set;} public string Customer {get;set;}
    public long NotBeforeUnix {get;set;} public long ExpiresUnix {get;set;}
    public string InstallationId {get;set;}
}
internal static class Licensing {
    internal static string LicensePath {get{return Path.Combine(Security.DataFolder,"activation.dat");}}
    internal static string InstallationPath {get{return Path.Combine(Security.DataFolder,"installation-id.txt");}}
    internal static string ClockPath {get{return Path.Combine(Security.DataFolder,"license-clock.dat");}}
    internal static string InstallationId {
        get {
            Directory.CreateDirectory(Security.DataFolder);
            if(!File.Exists(InstallationPath)) {
                using(var stream=new FileStream(InstallationPath,FileMode.CreateNew,FileAccess.Write))using(var writer=new StreamWriter(stream))writer.Write(Guid.NewGuid().ToString("N"));
            }
            string value=File.ReadAllText(InstallationPath).Trim();Guid id;
            if(!Guid.TryParseExact(value,"N",out id))throw new InvalidOperationException("Installation identifier is invalid");
            return value;
        }
    }
    internal static string Encode(byte[] bytes){return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');}
    static byte[] Decode(string value){value=value.Replace('-','+').Replace('_','/');switch(value.Length%4){case 2:value+="==";break;case 3:value+="=";break;}return Convert.FromBase64String(value);}
    internal static string Validate(string token,string publicKey,string installation,long now,out LicenseClaims claims) {
        claims=null;
        try {
            if(string.IsNullOrWhiteSpace(token)||token.Length>16384)return "Enter a valid activation key";
            string[] parts=token.Trim().Split('.');if(parts.Length!=3||parts[0]!="PIKA1")return "Activation key format is invalid";
            byte[] payload=Decode(parts[1]),signature=Decode(parts[2]);
            if(payload.Length>8192||signature.Length>1024||!Security.VerifySignature(payload,signature,publicKey))return "Activation key signature is invalid";
            var value=Json.Serializer().Deserialize<LicenseClaims>(Encoding.UTF8.GetString(payload));Guid id;
            if(value==null||value.Product!="pika op"||!Guid.TryParse(value.Id,out id)||string.IsNullOrWhiteSpace(value.Customer)||value.Customer.Length>160)return "Activation key claims are invalid";
            if(value.ExpiresUnix<=value.NotBeforeUnix)return "Activation key dates are invalid";
            DateTimeOffset.FromUnixTimeSeconds(value.NotBeforeUnix);DateTimeOffset.FromUnixTimeSeconds(value.ExpiresUnix);
            if(now<value.NotBeforeUnix)return "Activation key is not valid yet; check the PC clock";
            if(now>=value.ExpiresUnix)return "Activation key has expired";
            if(!string.IsNullOrEmpty(value.InstallationId)&&!string.Equals(value.InstallationId,installation,StringComparison.Ordinal))return "This key belongs to another installation";
            claims=value;return null;
        }catch(Exception){return "Activation key could not be verified";}
    }
    static void StoreProtected(string path,string text) {
        Directory.CreateDirectory(Security.DataFolder);byte[] bytes=ProtectedData.Protect(Encoding.UTF8.GetBytes(text),null,DataProtectionScope.CurrentUser);
        string temp=path+".tmp";File.WriteAllBytes(temp,bytes);if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
    }
    internal static void Activate(string token) {
        LicenseClaims claims;string failure=Validate(token,LicenseKey.PublicXml,InstallationId,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),out claims);
        if(failure!=null)throw new InvalidOperationException(failure);
        StoreProtected(LicensePath,token.Trim());StoreProtected(ClockPath,DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
    }
    internal static string Check(out LicenseClaims claims) {
        claims=null;
        try {
            if(!File.Exists(LicensePath))return "Activation is required";
            long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if(File.Exists(ClockPath)){long previous;string value=Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(ClockPath),null,DataProtectionScope.CurrentUser));if(!long.TryParse(value,out previous))return "License clock state is invalid";if(now+300<previous)return "PC clock moved backwards; correct it before using the app";}
            string token=Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(LicensePath),null,DataProtectionScope.CurrentUser));
            string failure=Validate(token,LicenseKey.PublicXml,InstallationId,now,out claims);
            if(failure==null)StoreProtected(ClockPath,now.ToString());return failure;
        }catch(Exception){return "Saved activation could not be read; activate this installation again";}
    }
    internal static Window ActivationWindow(string reason,bool smoke) {
        const string markup=@"<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Title='pika op — activation' Width='650' Height='650' MinWidth='580' MinHeight='570' WindowStartupLocation='CenterScreen' Background='#091018' Foreground='#E6EDF5' FontFamily='Segoe UI'>
<ScrollViewer VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled' Background='#091018'><StackPanel Margin='34'><TextBlock Text='pika op' Foreground='#65E9BD' FontSize='31' FontWeight='Bold'/><TextBlock Text='Activate your installation' FontSize='22' Margin='0,22,0,12'/><TextBlock x:Name='Reason' Foreground='#9CB2C3' TextWrapping='Wrap'/><Border Background='#111C28' BorderBrush='#203040' BorderThickness='1' CornerRadius='12' Padding='20' Margin='0,24,0,0'><StackPanel><TextBlock Text='Installation ID (random identifier, not a hardware fingerprint)' Foreground='#9CB2C3' TextWrapping='Wrap'/><TextBox x:Name='Id' IsReadOnly='True' Background='#0B1520' Foreground='#DDE8F0' BorderBrush='#2A3E50' Padding='10' Margin='0,10,0,18'/><TextBlock Text='Paste your signed activation key' Foreground='#DDE8F0'/><TextBox x:Name='Key' AcceptsReturn='True' Height='140' TextWrapping='Wrap' VerticalScrollBarVisibility='Auto' Background='#0B1520' Foreground='#DDE8F0' CaretBrush='#65E9BD' BorderBrush='#2A3E50' Padding='10' Margin='0,10,0,18'/><Button x:Name='Activate' Content='Activate pika op' Background='#1B7459' Foreground='White' Padding='14,12'/><TextBlock x:Name='Result' Foreground='#F1BD7A' TextWrapping='Wrap' Margin='0,14,0,0'/></StackPanel></Border><TextBlock Text='Keys are verified locally. This installation ID can be shared with the owner for an installation-bound key. No hardware details or network request is required for activation.' Foreground='#8193A7' TextWrapping='Wrap' Margin='0,20,0,0'/></StackPanel></ScrollViewer></Window>";
        var window=(Window)XamlReader.Parse(markup);((TextBlock)window.FindName("Reason")).Text=reason;((TextBox)window.FindName("Id")).Text=InstallationId;
        ((Button)window.FindName("Activate")).Click+=delegate{try{Activate(((TextBox)window.FindName("Key")).Text.Trim());window.DialogResult=true;}catch(Exception ex){((TextBlock)window.FindName("Result")).Text=ex.Message;}};
        if(smoke){var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};timer.Tick+=delegate{timer.Stop();window.UpdateLayout();var content=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-Activation.png")))png.Save(file);window.Close();};timer.Start();}
        return window;
    }
}
}
