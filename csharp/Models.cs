using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Web.Script.Serialization;

namespace PikaOp {
public struct Vec3 {
    public double X,Y,Z;
    public Vec3(double x,double y,double z){X=x;Y=y;Z=z;}
    public double Distance(Vec3 p){return Math.Sqrt((X-p.X)*(X-p.X)+(Y-p.Y)*(Y-p.Y)+(Z-p.Z)*(Z-p.Z));}
}
public class Player {
    public long Address {get;set;} public int Team {get;set;} public string Name {get;set;}
    public int Health {get;set;} public int? Armor {get;set;} public string Weapon {get;set;}
    public int? Ammo {get;set;} public bool Scoped {get;set;} public int Shots {get;set;}
    public bool? Defusing {get;set;} public bool? Immune {get;set;}
    public string StatusFlags {get{return (Scoped?"SCOPED ":"")+(Defusing==true?"DEFUSING ":"")+(Immune==true?"IMMUNE":"");}}
    public Vec3 Position {get;set;} public Vec3 Minimum {get;set;} public Vec3 Maximum {get;set;}
    public bool BoundsValid {get;set;} public double Distance {get;set;}
    public Dictionary<string,Vec3> Bones {get;set;}
    public bool? SpottedByLocal {get;set;}
    public Vec3? EyeAngles {get;set;}
    public uint EntityHandle {get;set;}
    public string Review {get;set;}
    public int RapidTurns {get;set;}
    public string SpottedLabel {get{return SpottedByLocal.HasValue?(SpottedByLocal.Value?"Spotted":"Not spotted"):"Unknown";}}
}
public class Frame {
    public string TimestampUtc {get;set;} public long Sequence {get;set;} public int Build {get;set;}
    public long SampleTimestamp {get;set;}
    public Vec3 Origin {get;set;} public int Team {get;set;} public Player Local {get;set;}
    public float[] Matrix {get;set;} public List<Player> Players {get;set;}
    public double Sensitivity {get;set;} public double ScopeScale {get;set;} public double ReadMilliseconds {get;set;}
    public int Stride {get;set;} public long WindowHandle {get;set;} public int Width {get;set;} public int Height {get;set;}
    public List<string[]> SkeletonEdges {get;set;}
}
internal static class Json {
    internal static JavaScriptSerializer Serializer(){return new JavaScriptSerializer {MaxJsonLength=64*1024*1024};}
    internal static T Read<T>(string path){return Serializer().Deserialize<T>(File.ReadAllText(path));}
    internal static string Encode(object value){return Serializer().Serialize(value);}
    internal static void AtomicWrite(string path,object value) {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp=path+".tmp"; File.WriteAllText(temp,Encode(value));
        if(File.Exists(path))File.Replace(temp,path,null); else File.Move(temp,path);
    }
}
public class TrackingProfile {
    public double Radius {get;set;} public double Tau {get;set;} public double Gain {get;set;}
    public double Deadzone {get;set;} public double MaxSpeed {get;set;}
    public string AimPoint {get;set;} public string Priority {get;set;}
    public bool LockTarget {get;set;} public bool ScopedOnly {get;set;}
}
public class Options:INotifyPropertyChanged {
    public event PropertyChangedEventHandler PropertyChanged;
    readonly Dictionary<string,object> values=new Dictionary<string,object>();
    T Get<T>(T fallback,[CallerMemberName]string name=null){object v;return values.TryGetValue(name,out v)?(T)v:fallback;}
    void Set<T>(T value,[CallerMemberName]string name=null){values[name]=value;var h=PropertyChanged;if(h!=null)h(this,new PropertyChangedEventArgs(name));}
    public bool Esp {get{return Get(true);}set{Set(value);}}
    public bool AdaptiveEsp {get{return Get(true);}set{Set(value);}}
    public bool ShowStatus {get{return Get(true);}set{Set(value);}}
    public bool HealthColors {get{return Get(true);}set{Set(value);}}
    public bool SpottedColors {get{return Get(false);}set{Set(value);}}
    public bool HeadMarker {get{return Get(false);}set{Set(value);}}
    public bool Teammates {get{return Get(false);}set{Set(value);}}
    public bool AimTeammates {get{return Get(false);}set{Set(value);}}
    public bool Aim {get{return Get(false);}set{Set(value);}}
    public bool Paused {get{return Get(false);}set{Set(value);}}
    public bool Skeleton {get{return Get(true);}set{Set(value);}}
    public bool Names {get{return Get(true);}set{Set(value);}}
    public bool Weapons {get{return Get(true);}set{Set(value);}}
    public bool Armor {get{return Get(true);}set{Set(value);}}
    public bool Distances {get{return Get(true);}set{Set(value);}}
    public bool Corners {get{return Get(true);}set{Set(value);}}
    public bool Snaplines {get{return Get(false);}set{Set(value);}}
    public bool Crosshair {get{return Get(false);}set{Set(value);}}
    public bool Preaim {get{return Get(true);}set{Set(value);}}
    public bool ShowRadius {get{return Get(true);}set{Set(value);}}
    public bool AutoPreset {get{return Get(false);}set{Set(value);}}
    public bool SpottedOnly {get{return Get(true);}set{Set(value);}}
    public bool ReviewIndicators {get{return Get(true);}set{Set(value);}}
    public bool LockTarget {get{return Get(true);}set{Set(value);}}
    public bool ScopedOnly {get{return Get(false);}set{Set(value);}}
    public string ActivationKey {get{return Get("Left mouse");}set{Set(new[]{"Left mouse","Right mouse","Mouse 4","Mouse 5","Alt"}.Contains(value)?value:"Left mouse");}}
    public string Priority {get{return Get("Crosshair");}set{Set(new[]{"Crosshair","Distance","Lowest health"}.Contains(value)?value:"Crosshair");}}
    public double Deadzone {get{return Get(.5);}set{Set(Clamp(value,0,10));}}
    public double MaxSpeed {get{return Get(800.0);}set{Set(Clamp(value,100,6000));}}
    public Dictionary<string,TrackingProfile> Profiles {get;set;}
    public Options(){Profiles=new Dictionary<string,TrackingProfile>();}
    public double Radius {get{return Get(180.0);}set{Set(Clamp(value,30,500));}}
    public double Tau {get{return Get(.18);}set{Set(Clamp(value,.04,.6));}}
    public double Gain {get{return Get(1.0);}set{Set(Clamp(value,.25,2));}}
    public double Height {get{return Get(64.0);}set{Set(Clamp(value,40,80));}}
    public double MaxDistance {get{return Get(0.0);}set{Set(Clamp(value,0,10000));}}
    public double Opacity {get{return Get(1.0);}set{Set(Clamp(value,.45,1));}}
    public double FontSize {get{return Get(10.0);}set{Set(Clamp(value,8,18));}}
    public double Stroke {get{return Get(1.5);}set{Set(Clamp(value,1,4));}}
    public double RadarRange {get{return Get(2000.0);}set{Set(Clamp(value,500,6000));}}
    public string Palette {get{return Get("Emerald");}set{Set(new[]{"Emerald","Blue","Rose","Amber"}.Contains(value)?value:"Emerald");}}
    public string Preset {get{return Get("Rifle");}set{Set(new[]{"Rifle","Sniper","Desert Eagle / Tap","Pistol","SMG","Shotgun","Aggressive"}.Contains(value)?value:"Rifle");}}
    public string AimPoint {get{return Get("Chest");}set{Set(value=="Head"?"Head":"Chest");}}
    public string SourceFolder {get{return Get("embedded");}set{Set(value??"embedded");}}
    public static double Clamp(double v,double min,double max){return double.IsNaN(v)||double.IsInfinity(v)?min:Math.Max(min,Math.Min(max,v));}
    internal static string PresetForWeapon(string weapon){
        if(new[]{"awp","ssg08","scar20","g3sg1"}.Contains(weapon))return "Sniper";
        if(new[]{"deagle","revolver"}.Contains(weapon))return "Desert Eagle / Tap";
        if(new[]{"glock","hkp2000","usp_silencer","p250","fiveseven","tec9","cz75a","elite"}.Contains(weapon))return "Pistol";
        if(new[]{"mac10","mp9","mp7","mp5sd","ump45","p90","bizon"}.Contains(weapon))return "SMG";
        return new[]{"nova","xm1014","mag7","sawedoff"}.Contains(weapon)?"Shotgun":"Rifle";
    }
    internal void SaveProfile(){if(Profiles==null)Profiles=new Dictionary<string,TrackingProfile>();Profiles[Preset]=new TrackingProfile{Radius=Radius,Tau=Tau,Gain=Gain,AimPoint=AimPoint,Deadzone=Deadzone,MaxSpeed=MaxSpeed,Priority=Priority,LockTarget=LockTarget,ScopedOnly=ScopedOnly};}
    internal void ApplyPreset(){if(Preset=="Aggressive"){AutoPreset=false;SpottedOnly=true;}TrackingProfile p;if(Profiles!=null&&Profiles.TryGetValue(Preset,out p)&&p!=null){Radius=p.Radius;Tau=p.Tau;Gain=p.Gain;AimPoint=p.AimPoint;Deadzone=p.Deadzone;MaxSpeed=p.MaxSpeed;Priority=p.Priority;LockTarget=p.LockTarget;ScopedOnly=p.ScopedOnly;return;}ResetPreset();}
    internal void ResetPreset(){
        if(Preset=="Aggressive"){Radius=220;Tau=.04;AimPoint="Head";Gain=1;Deadzone=.25;MaxSpeed=3200;Priority="Crosshair";LockTarget=true;AutoPreset=false;SpottedOnly=true;return;}
        Radius=Preset=="Sniper"?90:Preset=="SMG"?210:Preset=="Shotgun"?240:Preset=="Rifle"?180:110;Tau=Preset=="Sniper"?.075:Preset=="Desert Eagle / Tap"?.085:.14;AimPoint=Preset=="Desert Eagle / Tap"?"Head":"Chest";Gain=1;Deadzone=.5;MaxSpeed=800;Priority="Crosshair";LockTarget=true;ScopedOnly=false;
    }
}
}
