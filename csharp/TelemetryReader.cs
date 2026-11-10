using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace PikaOp {
internal sealed class TelemetryReader:IDisposable {
    readonly Process process; readonly IntPtr handle; readonly long client;
    readonly Dictionary<string,object> offsets,classes;
    readonly Dictionary<string,object> skeleton;
    readonly Dictionary<long,long> chunks=new Dictionary<long,long>();
    sealed class ExtraSnapshot {internal long Controller,Expires;internal Player Player;}
    readonly Dictionary<long,ExtraSnapshot> extras=new Dictionary<long,ExtraSnapshot>();
    int stride; int build; long sequence; IntPtr window;int localSlot;
    readonly ReviewMonitor review=new ReviewMonitor();
    internal int Build {get{return build;}}
    static Dictionary<string,object> Map(object o){return (Dictionary<string,object>)o;}
    static long Number(object o){return Convert.ToInt64(o);}
    internal static bool Bundled(string folder){return string.Equals(folder,"embedded",StringComparison.OrdinalIgnoreCase)||!Directory.Exists(folder);}
    internal static Dictionary<string,object> ReadDump(string folder,string name){
        if(!Bundled(folder))return Json.Read<Dictionary<string,object>>(Path.Combine(Resolve(folder),name));
        using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("PikaOp.Dump."+name)){
            if(stream==null)throw new InvalidOperationException("Bundled dump is missing: "+name);
            using(var text=new StreamReader(stream))return Json.Serializer().Deserialize<Dictionary<string,object>>(text.ReadToEnd());
        }
    }
    internal static IEnumerable<string> DumpNames(string folder){return Bundled(folder)?new[]{"offsets.json","client_dll.json","info.json"}:Directory.GetFiles(Resolve(folder),"*.json").Select(Path.GetFileName);}
    internal static string Resolve(string folder) {
        if(File.Exists(Path.Combine(folder,"offsets.json"))&&File.Exists(Path.Combine(folder,"client_dll.json")))return folder;
        string output=Path.Combine(folder,"output");
        if(Directory.Exists(output))return Resolve(output);
        if(Directory.Exists(folder))foreach(string child in Directory.GetDirectories(folder)) {
            output=Path.Combine(child,"output");if(File.Exists(Path.Combine(output,"offsets.json")))return Resolve(output);
        }
        throw new InvalidOperationException("Select the dump's output folder");
    }
    internal TelemetryReader(string folder) {
        if(!Bundled(folder))folder=Resolve(folder);
        var all=ReadDump(folder,"offsets.json");
        offsets=Map(all["client.dll"]);
        classes=Map(Map(ReadDump(folder,"client_dll.json")["client.dll"])["classes"]);
        var processes=Process.GetProcessesByName("cs2");
        if(processes.Length!=1)throw new InvalidOperationException("Start exactly one CS2 local session");
        process=processes[0];handle=Native.OpenProcess(0x1010,false,process.Id);
        if(handle==IntPtr.Zero)throw new InvalidOperationException("Cannot open CS2 for read-only telemetry");
        try {
            long engine=0;foreach(ProcessModule module in process.Modules) {
                if(module.ModuleName.Equals("client.dll",StringComparison.OrdinalIgnoreCase))client=module.BaseAddress.ToInt64();
                if(module.ModuleName.Equals("engine2.dll",StringComparison.OrdinalIgnoreCase))engine=module.BaseAddress.ToInt64();
            }
            if(client==0||engine==0)throw new InvalidOperationException("Waiting for engine modules");
            build=I32(engine+Number(Map(all["engine2.dll"])["dwBuildNumber"]));
            string info=Path.Combine(folder,"info.json");
            if(Bundled(folder)||File.Exists(info)) {
                int expected=Convert.ToInt32(ReadDump(folder,"info.json")["build_number"]);
                if(expected!=build)throw new InvalidOperationException("Dump build "+expected+" does not match game build "+build);
            }
            string profile=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"skeleton_profile.json");
            if(File.Exists(profile)) {
                skeleton=Json.Read<Dictionary<string,object>>(profile);
                int size=Convert.ToInt32(skeleton["stride"]),pos=Convert.ToInt32(skeleton["position_offset"]);
                long pointer=Number(skeleton["pointer_offset"]);
                var joints=Map(skeleton["joints"]);
                if(size<12||size>256||pos<0||pos+12>size||pointer<0||pointer>65536||joints.Count==0||joints.Values.Any(v=>Number(v)<0||Number(v)>255))throw new InvalidOperationException("Invalid skeleton profile");
            }
        } catch {Dispose();throw;}
    }
    internal byte[] Read(long address,int length) {
        if(address<=0||length<0||length>1048576)throw new InvalidOperationException("Invalid memory request");
        var buffer=new byte[length];IntPtr count;
        if(!Native.ReadProcessMemory(handle,new IntPtr(address),buffer,new IntPtr(length),out count)||count.ToInt64()!=length)throw new IOException("Object changed during memory read");
        return buffer;
    }
    internal long Pointer(long a){if(a==0)return 0;try{return BitConverter.ToInt64(Read(a,8),0);}catch(IOException){return 0;}}
    int I32(long a){return BitConverter.ToInt32(Read(a,4),0);}
    uint U32(long a){return BitConverter.ToUInt32(Read(a,4),0);}
    float Float(long a){return BitConverter.ToSingle(Read(a,4),0);}
    long Field(string cls,string name){return Number(Map(Map(classes[cls])["fields"])[name]);}
    long Global(string name){return Pointer(client+Number(offsets[name]));}
    Vec3 Vector(long address){var b=Read(address,12);var p=new Vec3(BitConverter.ToSingle(b,0),BitConverter.ToSingle(b,4),BitConverter.ToSingle(b,8));if(!MathTools.Finite(p.X)||!MathTools.Finite(p.Y)||!MathTools.Finite(p.Z)||Math.Abs(p.X)>1e7||Math.Abs(p.Y)>1e7||Math.Abs(p.Z)>1e7)throw new IOException("Invalid vector");return p;}
    Vec3 Position(long pawn){long node=Pointer(pawn+Field("C_BaseEntity","m_pGameSceneNode"));if(node==0)throw new IOException("Missing scene node");return Vector(node+Field("CGameSceneNode","m_vecAbsOrigin"));}
    long Entity(long root,int index,int size){if(index<0||index>0x7fff)return 0;long key=root+0x10+8*(index>>9),chunk;if(!chunks.TryGetValue(key,out chunk)){chunk=Pointer(key);chunks[key]=chunk;}return chunk==0?0:Pointer(chunk+size*(index&0x1ff));}
    string Text(long address,string fallback) {
        if(address==0)return fallback;var bytes=new List<byte>();
        for(int i=0;i<128;i+=8){var part=Read(address+i,8);bool end=false;foreach(byte b in part){if(b==0){end=true;break;}bytes.Add(b);}if(end)break;}
        string text=new string(Encoding.UTF8.GetString(bytes.ToArray()).Where(c=>!char.IsControl(c)).Take(64).ToArray());return text.Length==0?fallback:text;
    }
    void Extras(Player p,long controller,long root) {
        try {p.Name=Text(Pointer(controller+Field("CCSPlayerController","m_sSanitizedPlayerName")),p.Name);}catch(IOException){}
        try {int v=I32(p.Address+Field("C_CSPlayerPawn","m_ArmorValue"));p.Armor=v>=0&&v<=1000?(int?)v:null;}catch(IOException){}
        try {p.Scoped=Read(p.Address+Field("C_CSPlayerPawn","m_bIsScoped"),1)[0]!=0;p.Shots=I32(p.Address+Field("C_CSPlayerPawn","m_iShotsFired"));}catch(IOException){}
        try {p.Defusing=Read(p.Address+Field("C_CSPlayerPawn","m_bIsDefusing"),1)[0]!=0;}catch(IOException){}
        try {p.Immune=Read(p.Address+Field("C_CSPlayerPawn","m_bGunGameImmunity"),1)[0]!=0;}catch(IOException){}
        try {
            long services=Pointer(p.Address+Field("C_BasePlayerPawn","m_pWeaponServices"));
            long weapon=services==0?0:Entity(root,(int)(U32(services+Field("CPlayer_WeaponServices","m_hActiveWeapon"))&0x7fff),stride);
            if(weapon!=0){long identity=Pointer(weapon+Field("CEntityInstance","m_pEntity"));p.Weapon=Text(identity==0?0:Pointer(identity+Field("CEntityIdentity","m_designerName")),"Unknown");if(p.Weapon.StartsWith("weapon_"))p.Weapon=p.Weapon.Substring(7);int ammo=I32(weapon+Field("C_BasePlayerWeapon","m_iClip1"));p.Ammo=ammo>=-1&&ammo<=1000?(int?)ammo:null;}
        }catch(IOException){}
    }
    void CachedExtras(Player p,long controller,long root){
        long now=Stopwatch.GetTimestamp();ExtraSnapshot saved;
        if(extras.TryGetValue(p.Address,out saved)&&saved.Controller==controller&&now<saved.Expires){var old=saved.Player;p.Name=old.Name;p.Armor=old.Armor;p.Scoped=old.Scoped;p.Shots=old.Shots;p.Weapon=old.Weapon;p.Ammo=old.Ammo;p.Defusing=old.Defusing;p.Immune=old.Immune;return;}
        Extras(p,controller,root);if(extras.Count>128)extras.Clear();extras[p.Address]=new ExtraSnapshot{Controller=controller,Expires=now+Stopwatch.Frequency/10,Player=p};
    }
    Dictionary<string,Vec3> Bones(Player p) {
        var result=new Dictionary<string,Vec3>();if(skeleton==null)return result;
        try {
            long node=Pointer(p.Address+Field("C_BaseEntity","m_pGameSceneNode"));
            long buffer=Pointer(node+Field("CSkeletonInstance","m_modelState")+Number(skeleton["pointer_offset"]));if(buffer==0)return result;
            foreach(var joint in Map(skeleton["joints"])) {
                Vec3 v=Vector(buffer+Number(joint.Value)*Number(skeleton["stride"])+Number(skeleton["position_offset"]));if(v.Distance(p.Position)>256)return new Dictionary<string,Vec3>();result[joint.Key]=v;
            }
        }catch(IOException){result.Clear();}return result;
    }
    internal IEnumerable<string[]> BoneEdges {
        get {
            if(skeleton==null||!skeleton.ContainsKey("edges"))yield break;
            foreach(object edge in (object[])skeleton["edges"]){var a=(object[])edge;if(a.Length==2)yield return new[]{Convert.ToString(a[0]),Convert.ToString(a[1])};}
        }
    }
    internal Frame Sample() {
        long sampleStarted=Stopwatch.GetTimestamp();var timer=Stopwatch.StartNew();
        chunks.Clear();
        long local=Global("dwLocalPlayerPawn"),controller=Global("dwLocalPlayerController"),root=Global("dwEntityList");
        if(local==0||controller==0||root==0){stride=0;throw new InvalidOperationException("Enter a local map to begin telemetry");}
        long handleOffset=Field("CCSPlayerController","m_hPlayerPawn");uint localHandle=U32(controller+handleOffset);
        if(stride==0)foreach(int size in new[]{112,120}) {
            for(int i=1;i<=64;i++)if(Entity(root,i,size)==controller && Entity(root,(int)(localHandle&0x7fff),size)==local){stride=size;break;}if(stride!=0)break;
        }
        if(stride==0)throw new InvalidOperationException("Entity layout could not be verified");
        if(Entity(root,(int)(localHandle&0x7fff),stride)!=local){stride=0;throw new IOException("Local entity changed");}
        if(localSlot==0||Entity(root,localSlot,stride)!=controller){localSlot=0;for(int i=1;i<=64;i++)if(Entity(root,i,stride)==controller){localSlot=i;break;}}
        if(window==IntPtr.Zero||!Native.IsWindow(window))window=Native.FindWindow(process.Id);
        if(window==IntPtr.Zero)throw new InvalidOperationException("No visible CS2 window");
        var frame=new Frame {Build=build,Stride=stride,Sequence=++sequence,TimestampUtc=DateTime.UtcNow.ToString("o"),
            Origin=Position(local),Team=Read(local+Field("C_BaseEntity","m_iTeamNum"),1)[0],Players=new List<Player>(),WindowHandle=window.ToInt64()};
        frame.Local=new Player {Address=local,Name="Local",Weapon="Unknown",Position=frame.Origin};Extras(frame.Local,controller,root);
        try {long sensitivity=Global("dwSensitivity");frame.Sensitivity=Float(sensitivity+Number(offsets["dwSensitivity_sensitivity"]));frame.ScopeScale=Float(local+Field("C_BasePlayerPawn","m_flFOVSensitivityAdjust"));}catch(IOException){}
        var seen=new HashSet<long>();
        for(int i=1;i<=64;i++)try {
            long c=Entity(root,i,stride);if(c==0)continue;uint h=U32(c+handleOffset);if(h==0||h==uint.MaxValue)continue;
            long pawn=Entity(root,(int)(h&0x7fff),stride);if(pawn==0||pawn==local||!seen.Add(pawn))continue;
            int hp=I32(pawn+Field("C_BaseEntity","m_iHealth")),team=Read(pawn+Field("C_BaseEntity","m_iTeamNum"),1)[0];
            if(hp<=0||hp>1000||Read(pawn+Field("C_BaseEntity","m_lifeState"),1)[0]!=0||(team!=2&&team!=3))continue;
            var p=new Player {Address=pawn,EntityHandle=h,Name="Player "+i,Health=hp,Team=team,Position=Position(pawn),Weapon="Unknown",Minimum=new Vec3(-16,-16,0),Maximum=new Vec3(16,16,64)};
            try{byte[] mask=Read(pawn+Field("C_CSPlayerPawn","m_entitySpottedState")+Field("EntitySpottedState_t","m_bSpottedByMask"),8);p.SpottedByLocal=MathTools.Spotted(BitConverter.ToUInt64(mask,0),localSlot);}catch(IOException){}
            if(team!=frame.Team){try{p.EyeAngles=Vector(pawn+Field("C_CSPlayerPawn","m_angEyeAngles"));}catch(IOException){}review.Observe(p,sampleStarted/(double)Stopwatch.Frequency);}else p.Review="Teammate — not evaluated";
            p.Distance=p.Position.Distance(frame.Origin);CachedExtras(p,c,root);
            try {long collision=Pointer(pawn+Field("C_BaseEntity","m_pCollision"));if(collision!=0){Vec3 min=Vector(collision+Field("CCollisionProperty","m_vecMins")),max=Vector(collision+Field("CCollisionProperty","m_vecMaxs"));if(max.X>min.X&&max.Y>min.Y&&max.Z>min.Z&&max.Distance(min)<300){p.Minimum=min;p.Maximum=max;p.BoundsValid=true;}}}catch(IOException){}
            p.Bones=Bones(p);frame.Players.Add(p);
        }catch(IOException){}
        Native.Rect r;Native.GetClientRect(window,out r);frame.Width=r.Right-r.Left;frame.Height=r.Bottom-r.Top;
        var bytes=Read(client+Number(offsets["dwViewMatrix"]),64);frame.Matrix=new float[16];Buffer.BlockCopy(bytes,0,frame.Matrix,0,64);
        if(frame.Matrix.Any(v=>!MathTools.Finite(v)))throw new IOException("Invalid view matrix");
        frame.SampleTimestamp=sampleStarted;
        frame.SkeletonEdges=BoneEdges.ToList();frame.ReadMilliseconds=timer.Elapsed.TotalMilliseconds;return frame;
    }
    public void Dispose(){if(handle!=IntPtr.Zero)Native.CloseHandle(handle);if(process!=null)process.Dispose();}
}
}
