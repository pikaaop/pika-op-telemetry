using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PikaOp {
internal static class Entry {
    [STAThread]static int Main(string[] args) {
        string folder=AppDomain.CurrentDomain.BaseDirectory,exe=Assembly.GetExecutingAssembly().Location;
        try {
            string failure=Security.VerifyRelease(folder,exe);
            if(failure!=null){Security.Report(failure,!args.Contains("--self-test"));return 2;}
            if(args.Contains("--self-test")){Checks.Run(folder);return 0;}
            try{Native.SetProcessDpiAwarenessContext(new IntPtr(-4));}catch(EntryPointNotFoundException){}
            if(args.Contains("--live-check")){
                var options=Controller.LoadOptions();using(var reader=new TelemetryReader(options.SourceFolder))Json.AtomicWrite(System.IO.Path.Combine(folder,"live-check.json"),reader.Sample());return 0;
            }
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            var controller=new Controller(args.Contains("--smoke"));app.ShutdownMode=ShutdownMode.OnMainWindowClose;app.MainWindow=controller.Window;app.Run(controller.Window);return 0;
        }catch(Exception error){Security.Log(error.ToString());MessageBox.Show(error.Message,"pika op",MessageBoxButton.OK,MessageBoxImage.Error);return 1;}
    }
}
internal sealed class Controller {
    internal Window Window;
    readonly Options options;readonly Overlay overlay;
    readonly DispatcherTimer timer=new DispatcherTimer(DispatcherPriority.Render);readonly Stopwatch clock=Stopwatch.StartNew();
    CancellationTokenSource cancellation;volatile Frame latest;volatile string sampleError="";
    volatile bool pausedSampling,validationBlocked;int checking;bool precisionTimer;
    int generation;bool closing,f6,f8,compromised;double lastTime,lastUi,lastRecord,lastIntegrity,lastInputTime;long targetAddress,lastSequence;
    Point residual;StreamWriter recorder;int recorded;string recordPath="",lastWeapon="";
    string lastLogged="";MailOptions mail;
    static readonly string SettingsPath=System.IO.Path.Combine(Security.DataFolder,"settings.json");
    static readonly string[] PageNames={"Overview","Visuals","Tracking","Players","Review","Recording","Practice","Diagnostics","Security"};
    T Find<T>(string name)where T:class{return Window.FindName(name)as T;}
    void Status(string text){Find<TextBlock>("StatusText").Text=text;}
    void Log(string text){if(text==lastLogged)return;lastLogged=text;var log=Find<TextBox>("EventLog");log.AppendText(DateTime.UtcNow.ToString("HH:mm:ss 'UTC' ")+text+Environment.NewLine);if(log.LineCount>100)log.Text=string.Join(Environment.NewLine,log.Text.Split(new[]{Environment.NewLine},StringSplitOptions.None).Skip(25));log.ScrollToEnd();}
    void Button(string name,RoutedEventHandler action){Find<Button>(name).Click+=action;}
    internal static Options LoadOptions() {
        var options=new Options();
        try {
            if(File.Exists(SettingsPath))options=Json.Read<Options>(SettingsPath);
            else {
                string legacy=new[]{@"..\pika_op_settings.json",@"..\..\pika_op_settings.json"}.Select(p=>System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,p))).FirstOrDefault(File.Exists);
                if(File.Exists(legacy)) {
                    var old=Json.Read<Dictionary<string,object>>(legacy);
                    var names=new Dictionary<string,string>{{"esp","Esp"},{"teammates","Teammates"},{"aim_teammates","AimTeammates"},{"skeleton","Skeleton"},{"show_names","Names"},{"show_weapons","Weapons"},{"show_armor","Armor"},{"show_distance","Distances"},{"corners","Corners"},{"snaplines","Snaplines"},{"crosshair","Crosshair"},{"preaim","Preaim"},{"show_radius","ShowRadius"},{"automatic_preset","AutoPreset"},{"radius","Radius"},{"tau","Tau"},{"gain","Gain"},{"height","Height"},{"max_distance","MaxDistance"},{"overlay_alpha","Opacity"},{"label_size","FontSize"},{"line_width","Stroke"},{"radar_range","RadarRange"},{"palette","Palette"},{"preset","Preset"},{"aim_joint","AimPoint"},{"source_folder","SourceFolder"}};
                    foreach(var pair in names)if(old.ContainsKey(pair.Key)){var property=typeof(Options).GetProperty(pair.Value);property.SetValue(options,Convert.ChangeType(old[pair.Key],property.PropertyType,CultureInfo.InvariantCulture),null);}
                }
            }
        }catch(Exception ex){Security.Log("Settings could not be imported: "+ex.Message);}
        options.Aim=false;options.Paused=false;return options;
    }
    internal Controller(bool smoke) {
        using(var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("PikaOp.MainWindow.xaml"))Window=(Window)XamlReader.Load(resource);
        Window.MaxHeight=SystemParameters.WorkArea.Height;Window.Width=Math.Min(Window.Width,SystemParameters.WorkArea.Width-40);Window.Height=Math.Min(Window.Height,SystemParameters.WorkArea.Height-40);
        options=LoadOptions();Window.DataContext=options;overlay=new Overlay();mail=Security.LoadMail();
        Find<ListBox>("Navigation").SelectionChanged+=delegate {int index=Find<ListBox>("Navigation").SelectedIndex;if(index<0)return;for(int i=0;i<PageNames.Length;i++)Find<FrameworkElement>(PageNames[i]).Visibility=i==index?Visibility.Visible:Visibility.Collapsed;Find<TextBlock>("PageTitle").Text=index==4?"Enemy review":PageNames[index];};
        Find<ComboBox>("PresetPicker").SelectionChanged+=delegate{if(!Window.IsLoaded)return;var selected=Find<ComboBox>("PresetPicker").SelectedItem as ComboBoxItem;if(selected==null)return;string name=Convert.ToString(selected.Content);if(options.Preset!=name)options.Preset=name;options.ApplyPreset();};
        Button("SaveProfile",delegate{options.SaveProfile();SaveSettings();Status("Saved "+options.Preset+" tracking profile");});
        Button("ResetProfile",delegate{options.ResetPreset();Status("Factory values loaded. Save profile to keep them.");});
        Button("Reconnect",delegate{Restart();});Button("SaveSettings",delegate{SaveSettings();});
        Button("UseBundledDump",delegate{options.SourceFolder="embedded";Restart();});
        Button("BrowseDump",delegate {using(var dialog=new System.Windows.Forms.FolderBrowserDialog()){dialog.Description="Choose the dump or output folder";if(dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK){try{options.SourceFolder=TelemetryReader.Resolve(dialog.SelectedPath);Restart();}catch(Exception ex){Status(ex.Message);}}}});
        Button("SnapshotButton",delegate{Export(false);});Button("CsvButton",delegate{Export(true);});
        Button("InventoryButton",delegate{ExportInventory();});
        Button("StartRecording",delegate{StartRecording();});Button("StopRecording",delegate{StopRecording();});
        Button("CopySpread",delegate{Clipboard.SetText("sv_cheats 1; weapon_accuracy_nospread 1");Status("Copied. Paste into the developer console on your local map.");});
        Button("CheckIntegrity",delegate{CheckIntegrity();});
        Find<TextBlock>("LicenseStatus").Text="Standalone edition — no activation key required";
        FillMail();Button("SaveEmail",delegate{try{SaveMail();Find<TextBlock>("EmailState").Text="Email setup saved locally. No email sent.";}catch(Exception ex){Find<TextBlock>("EmailState").Text=ex.Message;}});
        Find<Button>("TestEmail").Click+=async delegate {
            var button=Find<Button>("TestEmail");button.IsEnabled=false;
            try {SaveMail();var settings=Security.LoadMail();await Task.Run(delegate{Security.Send(settings,"User-requested test alert");});Find<TextBlock>("EmailState").Text="Test email sent to "+settings.Recipient;}
            catch(Exception ex){Find<TextBlock>("EmailState").Text="Email not sent: "+ex.Message;}
            finally{button.IsEnabled=true;}
        };
        precisionTimer=Native.timeBeginPeriod(1)==0;
        Window.Closing+=delegate {closing=true;if(cancellation!=null)cancellation.Cancel();timer.Stop();if(precisionTimer)Native.timeEndPeriod(1);StopRecording();try{Json.AtomicWrite(SettingsPath,options);}catch(Exception ex){Security.Log("Settings save failed: "+ex.Message);}overlay.Close();};
        timer.Interval=TimeSpan.FromMilliseconds(4);timer.Tick+=Tick;timer.Start();Restart();
        if(smoke){var end=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(2000)};end.Tick+=delegate{end.Stop();var tests=new Dictionary<string,object>();for(int index=0;index<PageNames.Length;index++){string page=PageNames[index];tests[page]=Find<FrameworkElement>(page)!=null;Find<ListBox>("Navigation").SelectedIndex=index;PrepareSmokePage(page);Window.UpdateLayout();var content=(FrameworkElement)Window.Content;var bitmap=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-"+page+".png")))encoder.Save(stream);}CheckNativeOverlay(tests);tests["title"]=Window.Title;tests["tracking_enabled"]=options.Aim;tests["players"]=latest==null?0:latest.Players.Count;Json.AtomicWrite(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-smoke.json"),tests);Window.Close();};end.Start();}
    }
    void SaveSettings(){try{Json.AtomicWrite(SettingsPath,options);Status("Settings saved. Tracking always starts disabled.");}catch(Exception ex){Status(ex.Message);}}
    void PrepareSmokePage(string page){if(page!="Review")return;Find<DataGrid>("ReviewTable").ItemsSource=new[]{new Player{Name="Synthetic example A",Review="Review: repeated rapid turns",RapidTurns=4,SpottedByLocal=true},new Player{Name="Synthetic example B",Review="No conclusion",SpottedByLocal=false}};Find<TextBlock>("ReviewSummary").Text="Synthetic UI fixture — these rows are not live players";}
    void CheckNativeOverlay(Dictionary<string,object> tests) {
        var fixture=new Frame{Width=640,Height=360,Sequence=long.MaxValue,SampleTimestamp=Stopwatch.GetTimestamp(),WindowHandle=new System.Windows.Interop.WindowInteropHelper(Window).Handle.ToInt64(),Team=2,Players=new List<Player>(),Matrix=new float[]{0,1,0,0,0,0,1,-64,1,0,0,0,1,0,0,0}};
        fixture.Players.Add(new Player{Address=777,Team=3,Name="Render test",Health=100,Armor=100,Weapon="Test",Ammo=30,Position=new Vec3(200,0,0),Minimum=new Vec3(-16,-16,0),Maximum=new Vec3(16,16,64),BoundsValid=true});
        IntPtr before=Native.GetForegroundWindow();overlay.Present(fixture,options,0,null);
        tests["native_overlay_focus_preserved"]=Native.GetForegroundWindow()==before;
        tests["native_overlay_visible"]=overlay.Visible;tests["native_overlay_backend"]=overlay.Backend;
        long flags=Native.GetWindowLongPtr(overlay.Handle,-20).ToInt64();tests["native_clickthrough_style"]=(flags&0x20)!=0&&(flags&0x08000000)!=0;
        tests["native_colorkey_transparency"]=overlay.TransparencyKey==System.Drawing.Color.Magenta&&(flags&0x80000)!=0;
        tests["native_paint_ms"]=overlay.PaintMilliseconds;
        fixture.Width=800;fixture.Height=450;fixture.Sequence--;overlay.Present(fixture,options,0,null);
        tests["native_resize"]=overlay.ClientSize.Width==800&&overlay.ClientSize.Height==450;
        fixture.Width=1280;fixture.Height=720;fixture.Players.Clear();
        for(int i=0;i<4;i++){double depth=i==0?180:i==3?2400:650;fixture.Players.Add(new Player{Address=800+i,Team=3,Name="Long distance player "+i,Health=80,Armor=70,Weapon="Test rifle",Ammo=25,Distance=depth,Position=new Vec3(depth,i==0?-70:i==3?550:30+(i-1)*5,0),Minimum=new Vec3(-16,-16,0),Maximum=new Vec3(16,16,64),BoundsValid=true});}
        overlay.MeasureDraw(fixture,new Options(),1,System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-distance-esp.png"));
        overlay.Hide();
    }
    void Restart(){if(cancellation!=null)cancellation.Cancel();StopRecording();latest=null;targetAddress=0;residual=new Point();int mine=++generation;var tokenSource=new CancellationTokenSource();cancellation=tokenSource;string folder=options.SourceFolder;
        Task.Run(delegate {
            TelemetryReader reader=null;
            try {while(!tokenSource.IsCancellationRequested){if(pausedSampling){tokenSource.Token.WaitHandle.WaitOne(100);continue;}
                try{var cycle=Stopwatch.StartNew();if(reader==null)reader=new TelemetryReader(folder);Frame frame=reader.Sample();if(mine==generation){latest=frame;sampleError="";}bool foreground=Native.GetForegroundWindow()==new IntPtr(frame.WindowHandle);tokenSource.Token.WaitHandle.WaitOne(MathTools.SampleDelay(cycle.Elapsed.TotalMilliseconds,foreground));}
                catch(Exception ex){if(reader!=null){reader.Dispose();reader=null;}if(mine==generation){latest=null;sampleError=ex.Message;}tokenSource.Token.WaitHandle.WaitOne(2000);}
            }}finally{if(reader!=null)reader.Dispose();}
        });
    }
    void ScheduleChecks(double now){if(now-lastIntegrity<=30||Interlocked.CompareExchange(ref checking,1,0)!=0)return;lastIntegrity=now;
        Task.Run(delegate{try{string failure=Security.VerifyRelease(AppDomain.CurrentDomain.BaseDirectory,Assembly.GetExecutingAssembly().Location);if(failure!=null){validationBlocked=true;Window.Dispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(delegate{if(!closing)CheckIntegrity();}));}}catch(Exception ex){validationBlocked=true;string reason=ex.Message;Window.Dispatcher.BeginInvoke(new Action(delegate{if(!closing){options.Aim=false;overlay.Hide();Status("Validation failed: "+reason);Log(reason);}}));}finally{Interlocked.Exchange(ref checking,0);}});
    }
    void Tick(object sender,EventArgs args) {
        if(closing||compromised)return;if(validationBlocked){overlay.Hide();return;}double now=clock.Elapsed.TotalSeconds,dt=now-lastTime;lastTime=now;
        bool k6=Native.Down(0x75),k8=Native.Down(0x77);if(k6&&!f6)options.Paused=!options.Paused;if(k8&&!f8)options.Aim=false;f6=k6;f8=k8;
        try {
            pausedSampling=options.Paused;ScheduleChecks(now);
            if(options.Paused){timer.Interval=TimeSpan.FromMilliseconds(50);overlay.Hide();targetAddress=0;residual=new Point();Find<TextBlock>("ConnectionBadge").Text="PAUSED";Status("Paused. Press F6 to resume.");return;}
            Frame frame=latest;
            if(frame==null){timer.Interval=TimeSpan.FromMilliseconds(50);overlay.Hide();targetAddress=0;residual=new Point();Find<TextBlock>("ConnectionBadge").Text="WAITING FOR SESSION";Status(sampleError);if(sampleError.Length>0)Log(sampleError);if(recorder!=null)StopRecording();return;}
            Find<TextBlock>("ConnectionBadge").Text="CONNECTED / "+frame.Build;
            if(now-lastUi>.25){lastUi=now;if(Window.IsActive&&Window.WindowState!=WindowState.Minimized)UpdatePanel(frame);}
            if(recorder!=null&&now-lastRecord>=.1){try{recorder.WriteLine(Json.Encode(frame));recorder.Flush();lastRecord=now;recorded++;Find<TextBlock>("RecordingState").Text="Recording · "+recorded+" frames";}catch(Exception ex){StopRecording();Status("Recording stopped: "+ex.Message);}}
            if(options.AutoPreset&&lastWeapon!=frame.Local.Weapon){lastWeapon=frame.Local.Weapon;options.Preset=Options.PresetForWeapon(lastWeapon);options.ApplyPreset();}
            if(!options.AutoPreset)lastWeapon="";
            var hwnd=new IntPtr(frame.WindowHandle);if(Native.IsIconic(hwnd)||Native.GetForegroundWindow()!=hwnd){timer.Interval=TimeSpan.FromMilliseconds(50);overlay.Hide();targetAddress=0;residual=new Point();return;}timer.Interval=TimeSpan.FromMilliseconds(4);
            bool fresh=MathTools.Fresh(frame,Stopwatch.GetTimestamp());
            if(!fresh){overlay.Hide();targetAddress=0;residual=new Point();Status("Stale frame; input stopped");return;}
            var center=new Point(frame.Width/2.0,frame.Height/2.0);Point? selectedPoint=null;
            bool held=TrackingHeld()&&MathTools.CanTrack(frame,options);
            var candidates=new List<Tuple<Player,Point,double>>();
            if(options.Aim&&held)foreach(Player p in frame.Players) {
                if(p.Team==frame.Team&&!options.AimTeammates)continue;if(options.MaxDistance>0&&p.Distance>options.MaxDistance)continue;if(!MathTools.TargetAllowed(p,options))continue;
                Point? point=Overlay.AimPoint(p,frame,options);if(point.HasValue){double distance=MathTools.Distance(center,point.Value);if(distance<=options.Radius)candidates.Add(Tuple.Create(p,point.Value,distance));}
            }
            var target=(options.LockTarget?candidates.FirstOrDefault(c=>c.Item1.Address==targetAddress):null)??candidates.OrderBy(c=>MathTools.TargetScore(c.Item1,c.Item3,options)).ThenBy(c=>c.Item3).FirstOrDefault();
            if(target!=null){if(targetAddress!=target.Item1.Address){residual=new Point();lastInputTime=0;}targetAddress=target.Item1.Address;selectedPoint=target.Item2;
                if(frame.Sequence!=lastSequence){double inputDt=lastInputTime>0?now-lastInputTime:dt;Point move=MathTools.Tracking(target.Item2,center,frame,options,inputDt,ref residual);if(Native.GetForegroundWindow()==hwnd&&TrackingHeld()&&MathTools.CanTrack(frame,options))Native.Move((int)move.X,(int)move.Y);lastInputTime=now;}
            }else{targetAddress=0;residual=new Point();lastInputTime=0;}
            lastSequence=frame.Sequence;overlay.Present(frame,options,targetAddress,selectedPoint);Status("Connected · "+frame.Players.Count+" players · "+candidates.Count+" aim candidates");
        }catch(Exception ex){options.Aim=false;targetAddress=0;residual=new Point();overlay.Hide();Status(ex.Message);Log(ex.Message);}
    }
    bool TrackingHeld(){return Native.Down(MathTools.ActivationCode(options.ActivationKey))||(options.Preaim&&Native.Down(0x12));}
    void UpdatePanel(Frame frame) {
        var enemies=frame.Players.Where(p=>p.Team!=frame.Team).ToList();
        Find<TextBlock>("ReviewSummary").Text=enemies.Count(p=>p.RapidTurns>=3)+" enemies flagged for review · observations are not proof of cheating";
        if(Find<FrameworkElement>("Review").Visibility==Visibility.Visible)Find<DataGrid>("ReviewTable").ItemsSource=enemies;
        Find<TextBlock>("PlayersMetric").Text=frame.Players.Count.ToString();Find<TextBlock>("ReadMetric").Text=frame.ReadMilliseconds.ToString("F1")+" ms";Find<TextBlock>("WeaponMetric").Text=frame.Local.Weapon;
        string summary="Build "+frame.Build+" · verified entity stride "+frame.Stride+"\nOverlay: "+overlay.Backend+"\nPaint rate: "+overlay.PaintRate.ToString("F0")+"/s | CPU paint: "+overlay.PaintMilliseconds.ToString("F2")+" ms | sample age at paint: "+overlay.SampleAgeMilliseconds.ToString("F1")+" ms\nLocal weapon: "+frame.Local.Weapon+" | ammo "+frame.Local.Ammo+"\nScoped: "+frame.Local.Scoped+" | shots: "+frame.Local.Shots+"\nBones: "+frame.Players.Count(p=>p.Bones!=null&&p.Bones.Count>0)+" valid profiles\nPosition: "+frame.Origin.X.ToString("F1")+", "+frame.Origin.Y.ToString("F1")+", "+frame.Origin.Z.ToString("F1");
        Find<TextBlock>("OverviewSummary").Text=summary;Find<TextBlock>("DiagnosticsText").Text=summary;
        if(Find<FrameworkElement>("Players").Visibility==Visibility.Visible){var grid=Find<DataGrid>("PlayerTable");var sort=grid.Items.SortDescriptions.ToList();string q=Find<TextBox>("Search").Text.Trim();
        grid.ItemsSource=frame.Players.Where(p=>(q.Length==0||(p.Name+" "+p.Weapon).IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0)&&(options.MaxDistance==0||p.Distance<=options.MaxDistance)).ToList();
        foreach(var descriptor in sort)grid.Items.SortDescriptions.Add(descriptor);}
        if(Find<FrameworkElement>("Overview").Visibility!=Visibility.Visible)return;
        var radar=Find<Canvas>("Radar");if(radar.ActualWidth<1)return;radar.Children.Clear();double w=radar.ActualWidth,h=radar.ActualHeight,cx=w/2,cy=h/2,r=Math.Min(w,h)/2-20;
        foreach(double k in new[]{.5,1.0}){var circle=new Ellipse{Width=r*2*k,Height=r*2*k,Stroke=new SolidColorBrush(Color.FromRgb(37,57,74)),StrokeThickness=1};Canvas.SetLeft(circle,cx-r*k);Canvas.SetTop(circle,cy-r*k);radar.Children.Add(circle);}
        foreach(Player p in frame.Players){double dx=p.Position.X-frame.Origin.X,dy=p.Position.Y-frame.Origin.Y;if(Math.Sqrt(dx*dx+dy*dy)>options.RadarRange)continue;Dot(radar,cx+dx/options.RadarRange*r,cy-dy/options.RadarRange*r,p.Team==frame.Team?Brushes.LightSkyBlue:Overlay.Accent(options.Palette),4);}
        Dot(radar,cx,cy,Brushes.White,4);
    }
    static void Dot(Canvas canvas,double x,double y,Brush brush,double radius){var e=new Ellipse{Width=radius*2,Height=radius*2,Fill=brush};Canvas.SetLeft(e,x-radius);Canvas.SetTop(e,y-radius);canvas.Children.Add(e);}
    string SavePath(string filter,string name){var dialog=new SaveFileDialog{Filter=filter,FileName=name,OverwritePrompt=true};return dialog.ShowDialog(Window)==true?dialog.FileName:null;}
    void Export(bool csv) {
        Frame frame=latest;if(frame==null){Status("No valid frame to export");return;}
        string path=SavePath(csv?"CSV|*.csv":"JSON|*.json",csv?"pika_op_players.csv":"pika_op_snapshot.json");if(path==null)return;
        try{using(var writer=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write),new UTF8Encoding(true))){if(!csv)writer.Write(Json.Encode(frame));else{writer.WriteLine("timestamp_utc,build,name,health,armor,weapon,ammo,x,y,z,scoped,local_spotted,rapid_turns_5s,review");foreach(Player p in frame.Players)writer.WriteLine(string.Join(",",new object[]{frame.TimestampUtc,frame.Build,Csv(p.Name),p.Health,p.Armor,Csv(p.Weapon),p.Ammo,p.Position.X.ToString(CultureInfo.InvariantCulture),p.Position.Y.ToString(CultureInfo.InvariantCulture),p.Position.Z.ToString(CultureInfo.InvariantCulture),p.Scoped,Csv(p.SpottedLabel),p.RapidTurns,Csv(p.Review??"No conclusion")}));}}Status("Export saved");}catch(Exception ex){Status("Export failed; choose a new filename. "+ex.Message);}
    }
    internal static string Csv(string text){if(text.TrimStart().StartsWith("=")||text.TrimStart().StartsWith("+")||text.TrimStart().StartsWith("-")||text.TrimStart().StartsWith("@"))text="'"+text;return "\""+text.Replace("\"","\"\"")+"\"";}
    void ExportInventory(){try{string folder=options.SourceFolder;var inventory=new Dictionary<string,object>();foreach(string file in TelemetryReader.DumpNames(folder)){var content=TelemetryReader.ReadDump(folder,file);foreach(var pair in content){var module=pair.Value as Dictionary<string,object>;if(module!=null&&module.ContainsKey("classes")){var classes=(Dictionary<string,object>)module["classes"];int fields=classes.Values.Sum(c=>((Dictionary<string,object>)((Dictionary<string,object>)c)["fields"]).Count);inventory[pair.Key]=new{File=System.IO.Path.GetFileName(file),Classes=classes.Count,Fields=fields};}}}string path=SavePath("JSON|*.json","pika_op_inventory.json");if(path!=null){using(var file=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write)))file.Write(Json.Encode(inventory));Status("Inventory saved");}}catch(Exception ex){Status(ex.Message);}}
    void StartRecording(){if(recorder!=null)return;if(latest==null){Status("Connect to a local map first");return;}string path=SavePath("JSON Lines|*.jsonl","pika_op_"+DateTime.UtcNow.ToString("yyyyMMdd_HHmmss")+".jsonl");if(path==null)return;try{recorder=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write));recorded=0;lastRecord=0;recordPath=path;Find<TextBlock>("RecordingState").Text="Recording started";}catch(Exception ex){Status(ex.Message);}}
    void StopRecording(){if(recorder==null)return;try{recorder.Dispose();Find<TextBlock>("RecordingState").Text="Saved "+recorded+" frames to "+System.IO.Path.GetFileName(recordPath);}catch(Exception ex){Security.Log("Recording close: "+ex.Message);}finally{recorder=null;}}
    void FillMail(){Find<CheckBox>("EmailEnabled").IsChecked=mail.Enabled;Find<CheckBox>("IncludeDevice").IsChecked=mail.IncludeDevice;Find<TextBox>("EmailRecipient").Text=mail.Recipient;Find<TextBox>("EmailSender").Text=mail.Sender;Find<TextBox>("EmailHost").Text=mail.Host;Find<TextBox>("EmailPort").Text=mail.Port.ToString();}
    void SaveMail(){mail.Enabled=Find<CheckBox>("EmailEnabled").IsChecked==true;mail.IncludeDevice=Find<CheckBox>("IncludeDevice").IsChecked==true;mail.Recipient=Find<TextBox>("EmailRecipient").Text.Trim();mail.Sender=Find<TextBox>("EmailSender").Text.Trim();mail.Host=Find<TextBox>("EmailHost").Text.Trim();int port;if(!int.TryParse(Find<TextBox>("EmailPort").Text,out port)||port<1||port>65535)throw new InvalidOperationException("Invalid SMTP port");mail.Port=port;if(mail.Enabled){new System.Net.Mail.MailAddress(mail.Recipient);new System.Net.Mail.MailAddress(mail.Sender);}Security.SaveMail(mail,Find<PasswordBox>("EmailPassword").Password);Find<PasswordBox>("EmailPassword").Clear();}
    bool CheckIntegrity(){string failure=Security.VerifyRelease(AppDomain.CurrentDomain.BaseDirectory,Assembly.GetExecutingAssembly().Location);if(failure==null){Find<TextBlock>("IntegrityState").Text="Signed release verification passed";return true;}compromised=true;options.Aim=false;options.Paused=true;overlay.Hide();Find<TextBlock>("IntegrityState").Text=failure;Log(failure);Task.Run(delegate{Security.Report(failure,false);}).ContinueWith(delegate{Window.Dispatcher.BeginInvoke(new Action(delegate{if(!closing)Window.Close();}));});MessageBox.Show(Window,failure+"\nInput has stopped. The app will close after processing the configured alert.","pika op — integrity alert",MessageBoxButton.OK,MessageBoxImage.Warning);return false;}
}
internal static class Checks {
    internal static void Run(string folder) {
        var checks=new Dictionary<string,bool>();
        float[] matrix={1,0,0,0,0,1,0,0,0,0,1,0,0,0,1,0};
        var p=MathTools.Project(new Vec3(0,0,1),matrix,800,600,true);checks["projection_center"]=p.HasValue&&p.Value.X==400&&p.Value.Y==300;
        checks["projection_behind"]=!MathTools.Project(new Vec3(0,0,-1),matrix,800,600,true).HasValue;
        var o=new Options();o.Tau=.5;var f=new Frame{Matrix=matrix,Sensitivity=1,ScopeScale=1};Point residual=new Point();Point a=MathTools.Tracking(new Point(1020,1000),new Point(1000,1000),f,o,.05,ref residual);
        var zoom=(float[])matrix.Clone();zoom[0]=2;zoom[5]=2;f.Matrix=zoom;residual=new Point();Point b=MathTools.Tracking(new Point(1040,1000),new Point(1000,1000),f,o,.05,ref residual);checks["zoom_invariance"]=a==b;
        checks["teammate_default_off"]=!o.AimTeammates;checks["tracking_default_off"]=!o.Aim;
        var flagged=new Player{Scoped=true,Defusing=true,Immune=true};checks["status_flags"]=flagged.StatusFlags.Contains("SCOPED")&&flagged.StatusFlags.Contains("DEFUSING")&&flagged.StatusFlags.Contains("IMMUNE");checks["unknown_status_not_claimed"]=new Player().StatusFlags=="";
        checks["health_colours"]=Overlay.HealthColor(10)==System.Drawing.Color.Tomato&&Overlay.HealthColor(50)==System.Drawing.Color.Gold&&Overlay.HealthColor(100)==System.Drawing.Color.MediumSpringGreen;
        var headPlayer=new Player{Position=new Vec3(10,20,30),Minimum=new Vec3(-16,-16,0),Maximum=new Vec3(16,16,64),BoundsValid=true};checks["head_marker_fallback"]=Math.Abs(Overlay.HeadPosition(headPlayer,70).Z-88.88)<1e-6;
        headPlayer.Bones=new Dictionary<string,Vec3>{{"head",new Vec3(11,22,91)}};checks["head_marker_verified_bone"]=Overlay.HeadPosition(headPlayer,70).Distance(new Vec3(11,22,91))<1e-6;
        var visualSettings=Json.Serializer().Deserialize<Options>(Json.Encode(new Options{HeadMarker=true,SpottedColors=true,ShowStatus=false,HealthColors=false}));checks["visual_settings_round_trip"]=visualSettings.HeadMarker&&visualSettings.SpottedColors&&!visualSettings.ShowStatus&&!visualSettings.HealthColors;
        var aggressive=new Options{Preset="Aggressive",AutoPreset=true,SpottedOnly=false,ScopedOnly=true};aggressive.ApplyPreset();
        checks["aggressive_profile"]=aggressive.Preset=="Aggressive"&&aggressive.AimPoint=="Head"&&aggressive.Tau==.04&&aggressive.MaxSpeed==3200&&aggressive.Deadzone==.25&&aggressive.Radius==220;
        checks["aggressive_preserves_gates"]=!aggressive.Aim&&!aggressive.AutoPreset&&aggressive.SpottedOnly&&aggressive.ScopedOnly&&!aggressive.AimTeammates;
        var rifle=new Options{Preset="Rifle"};rifle.ApplyPreset();Point r1=new Point(),r2=new Point();var fast=MathTools.Tracking(new Point(1200,1000),new Point(1000,1000),f,aggressive,.01,ref r1);var normal=MathTools.Tracking(new Point(1200,1000),new Point(1000,1000),f,rifle,.01,ref r2);
        checks["aggressive_response_bounded"]=fast.X>normal.X&&fast.X<=32&&fast.Y==0;
        aggressive.SaveProfile();var reloaded=Json.Serializer().Deserialize<Options>(Json.Encode(aggressive));reloaded.Preset="Rifle";reloaded.ApplyPreset();reloaded.Preset="Aggressive";reloaded.ApplyPreset();checks["aggressive_profile_persists"]=reloaded.Tau==.04&&reloaded.MaxSpeed==3200&&reloaded.AimPoint=="Head"&&reloaded.SpottedOnly&&!reloaded.AutoPreset;
        checks["activation_code_excluded"]=Assembly.GetExecutingAssembly().GetType("PikaOp.Licensing")==null&&Assembly.GetExecutingAssembly().GetType("PikaOp.LicenseKey")==null;
        checks["bundled_dump_present"]=TelemetryReader.ReadDump("embedded","offsets.json").ContainsKey("client.dll")&&TelemetryReader.ReadDump("embedded","client_dll.json").ContainsKey("client.dll")&&TelemetryReader.ReadDump("embedded","info.json").ContainsKey("build_number");
        checks["missing_dump_uses_bundle"]=TelemetryReader.ReadDump(System.IO.Path.Combine(System.IO.Path.GetTempPath(),Guid.NewGuid().ToString("N")),"offsets.json").ContainsKey("client.dll");
        checks["adaptive_esp_levels"]=Overlay.DetailLevel(20,true)==0&&Overlay.DetailLevel(28,true)==1&&Overlay.DetailLevel(71,true)==1&&Overlay.DetailLevel(72,true)==2&&Overlay.DetailLevel(10,false)==2;
        checks["short_labels"]=Overlay.ShortName("Long player name",10).Length==10&&Overlay.ShortName("Alex",10)=="Alex"&&Overlay.ShortName(null,10)=="Player";
        var occupied=new List<System.Drawing.Rectangle>{new System.Drawing.Rectangle(10,10,50,20)};
        checks["overlapping_labels_hidden"]=!Overlay.LabelFits(new System.Drawing.Rectangle(40,15,50,20),occupied)&&Overlay.LabelFits(new System.Drawing.Rectangle(80,15,50,20),occupied);
        checks["spotted_mask_boundaries"]=MathTools.Spotted(1UL,1)==true&&MathTools.Spotted(1UL<<31,32)==true&&MathTools.Spotted(1UL<<32,33)==true&&MathTools.Spotted(1UL<<63,64)==true&&MathTools.Spotted(1UL,2)==false;
        checks["spotted_unknown_slot"]=MathTools.Spotted(ulong.MaxValue,0)==null&&MathTools.Spotted(ulong.MaxValue,65)==null;
        var visibilityOptions=new Options();var hidden=new Player();checks["spotted_filter_fail_closed"]=visibilityOptions.SpottedOnly&&!MathTools.TargetAllowed(hidden,visibilityOptions);hidden.SpottedByLocal=false;checks["unspotted_target_blocked"]=!MathTools.TargetAllowed(hidden,visibilityOptions);hidden.SpottedByLocal=true;checks["spotted_target_allowed"]=MathTools.TargetAllowed(hidden,visibilityOptions);visibilityOptions.SpottedOnly=false;hidden.SpottedByLocal=null;checks["spotted_filter_optional"]=MathTools.TargetAllowed(hidden,visibilityOptions);
        var monitor=new ReviewMonitor();var observed=new Player{Address=123,EntityHandle=1,EyeAngles=new Vec3(0,0,0)};monitor.Observe(observed,1);
        observed.EyeAngles=new Vec3(0,90,0);monitor.Observe(observed,1.02);observed.EyeAngles=new Vec3(0,180,0);monitor.Observe(observed,1.04);observed.EyeAngles=new Vec3(0,-90,0);monitor.Observe(observed,1.06);
        checks["repeated_turns_flag_review"]=observed.RapidTurns==3&&observed.Review=="Review: repeated rapid turns";
        monitor.Observe(observed,1.07);checks["duplicate_angle_not_counted"]=observed.RapidTurns==3;
        observed.EntityHandle=2;monitor.Observe(observed,1.08);checks["review_resets_on_respawn"]=observed.RapidTurns==0&&observed.Review=="No conclusion";
        checks["angle_wraparound"]=Math.Abs(MathTools.AngleDelta(-179,179)-2)<1e-8;
        observed.EyeAngles=null;monitor.Observe(observed,1.1);checks["missing_angles_not_accused"]=observed.Review=="Angle data unavailable";
        observed.EyeAngles=new Vec3(0,0,0);monitor.Observe(observed,2);observed.EyeAngles=new Vec3(0,90,0);monitor.Observe(observed,3);checks["review_gap_resets"]=observed.RapidTurns==0;
        checks["weapon_classification"]=Options.PresetForWeapon("awp")=="Sniper"&&Options.PresetForWeapon("deagle")=="Desert Eagle / Tap"&&Options.PresetForWeapon("usp_silencer")=="Pistol"&&Options.PresetForWeapon("mp9")=="SMG"&&Options.PresetForWeapon("nova")=="Shotgun"&&Options.PresetForWeapon("ak47")=="Rifle";
        var profile=new Options{Preset="Sniper",Radius=123,Tau=.21,Gain=1.3,Deadzone=2,MaxSpeed=1400,AimPoint="Head",Priority="Lowest health",ScopedOnly=true,LockTarget=false};profile.SaveProfile();
        var restored=Json.Serializer().Deserialize<Options>(Json.Encode(profile));restored.Preset="Rifle";restored.ApplyPreset();restored.Preset="Sniper";restored.ApplyPreset();
        checks["profile_persistence"]=restored.Radius==123&&restored.Tau==.21&&restored.Gain==1.3&&restored.Deadzone==2&&restored.MaxSpeed==1400&&restored.AimPoint=="Head"&&restored.Priority=="Lowest health"&&restored.ScopedOnly&&!restored.LockTarget;
        var gated=new Frame{Local=new Player{Scoped=false}};checks["scoped_gate"]=!MathTools.CanTrack(gated,restored);gated.Local.Scoped=true;checks["scoped_gate_allows_scope"]=MathTools.CanTrack(gated,restored);
        checks["activation_key_mapping"]=MathTools.ActivationCode("Mouse 4")==5&&MathTools.ActivationCode("Mouse 5")==6&&MathTools.ActivationCode("Right mouse")==2&&MathTools.ActivationCode("Alt")==18;
        checks["target_priority"]=MathTools.TargetScore(new Player{Health=12,Distance=500},20,restored)==12;restored.Priority="Distance";checks["distance_priority"]=MathTools.TargetScore(new Player{Health=12,Distance=500},20,restored)==500;
        o.Deadzone=3;residual=new Point(.4,.4);checks["deadzone_clears_residual"]=MathTools.Tracking(new Point(1001,1001),new Point(1000,1000),f,o,.01,ref residual)==new Point()&&residual==new Point();
        o.MaxSpeed=100;residual=new Point();Point limited=MathTools.Tracking(new Point(1800,1800),new Point(1000,1000),f,o,.05,ref residual);checks["mouse_speed_limit"]=Math.Abs(limited.X)<=5&&Math.Abs(limited.Y)<=5;
        residual=new Point();double accumulated=0;for(int i=0;i<100;i++)accumulated+=MathTools.Tracking(new Point(1800,1800),new Point(1000,1000),f,o,.004,ref residual).X;
        checks["fractional_speed_accumulates"]=accumulated>=39&&accumulated<=40;
        byte[] plain=Encoding.UTF8.GetBytes("local-secret-test");var encrypted=ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser);checks["dpapi_round_trip"]=plain.SequenceEqual(ProtectedData.Unprotect(encrypted,null,DataProtectionScope.CurrentUser));
        byte[] manifest,signature;long payloadLength;Security.ReadRelease(Assembly.GetExecutingAssembly().Location,out manifest,out signature,out payloadLength);checks["signed_manifest"]=Security.VerifySignature(manifest,signature,ReleaseKey.PublicXml);manifest[0]^=1;checks["modified_manifest_rejected"]=!Security.VerifySignature(manifest,signature,ReleaseKey.PublicXml);
        string changed=System.IO.Path.GetTempFileName();try{byte[] binary=File.ReadAllBytes(Assembly.GetExecutingAssembly().Location);binary[binary.Length-1]^=1;File.WriteAllBytes(changed,binary);checks["modified_executable_rejected"]=Security.VerifyRelease(folder,changed)!=null;}finally{File.Delete(changed);}
        string corrupt=System.IO.Path.GetTempFileName();try{byte[] binary=File.ReadAllBytes(Assembly.GetExecutingAssembly().Location);binary[64]^=1;File.WriteAllBytes(corrupt,binary);checks["modified_payload_rejected"]=Security.VerifyRelease(folder,corrupt)!=null;File.WriteAllBytes(corrupt,binary.Take(binary.Length-20).ToArray());checks["truncated_trailer_rejected"]=Security.VerifyRelease(folder,corrupt)!=null;}finally{File.Delete(corrupt);}
        var noDevice=new MailOptions();checks["device_details_opt_in_only"]=!Security.EventBody("Test",noDevice).Contains("PC name:");checks["email_disabled_by_default"]=!noDevice.Enabled;
        checks["csv_formula_escape"]=Controller.Csv("=1+1").StartsWith("\"'");
        long tick=Stopwatch.GetTimestamp();var freshFrame=new Frame{SampleTimestamp=tick};
        checks["fresh_frame_accepted"]=MathTools.Fresh(freshFrame,tick+Stopwatch.Frequency/10);
        checks["stale_frame_rejected"]=!MathTools.Fresh(freshFrame,tick+Stopwatch.Frequency/2);
        checks["uninitialized_frame_rejected"]=!MathTools.Fresh(new Frame(),tick);
        checks["sample_wait_bounded"]=MathTools.SampleDelay(100,true)>=1&&MathTools.SampleDelay(0,true)<=5;
        Json.AtomicWrite(System.IO.Path.Combine(folder,"self-test-results.json"),checks);if(checks.Values.Any(v=>!v))throw new InvalidOperationException("A self-test failed");
    }
}
}
