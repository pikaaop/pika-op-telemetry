using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Forms=System.Windows.Forms;
using Media=System.Windows.Media;

namespace PikaOp {
// Keep WPF for the panel; use native color-key transparency for the overlay.
internal sealed class Overlay:Forms.Form {
    static readonly Color KeyColor=Color.Magenta;
    static readonly Dictionary<string,Media.Brush> accents=new Dictionary<string,Media.Brush>();
    readonly Dictionary<Color,SolidBrush> brushes=new Dictionary<Color,SolidBrush>();
    readonly Dictionary<Color,Dictionary<float,Pen>> pens=new Dictionary<Color,Dictionary<float,Pen>>();
    readonly Dictionary<float,Font> fonts=new Dictionary<float,Font>();
    sealed class TextRun {internal string Text;internal float Size;internal Color Color;internal double RefreshAfter;internal Bitmap Image;}
    readonly Dictionary<long,TextRun> texts=new Dictionary<long,TextRun>();
    readonly Queue<double> paintIntervals=new Queue<double>();
    readonly List<Rectangle> labelAreas=new List<Rectangle>();
    readonly List<Player> drawOrder=new List<Player>();
    Frame frame;Options options;long target,sequence=-1;System.Windows.Point? aim;
    int lastX=int.MinValue,lastY,lastWidth,lastHeight;bool dirty=true;
    long previousPaint;double paintRate;
    internal bool IsVisible {get{return Visible;}}
    internal double PaintMilliseconds {get;private set;}
    internal double SampleAgeMilliseconds {get;private set;}
    internal double PaintRate {get{return paintRate;}}
    internal string Backend {get{return "Native GDI / color key";}}
    protected override bool ShowWithoutActivation {get{return true;}}
    protected override Forms.CreateParams CreateParams {get {var p=base.CreateParams;p.ExStyle|=0x08000000|0x20|0x80|0x80000;return p;}}
    internal Overlay() {
        FormBorderStyle=Forms.FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
        StartPosition=Forms.FormStartPosition.Manual;AutoScaleMode=Forms.AutoScaleMode.None;
        BackColor=KeyColor;TransparencyKey=KeyColor;
        SetStyle(Forms.ControlStyles.UserPaint|Forms.ControlStyles.AllPaintingInWmPaint|Forms.ControlStyles.OptimizedDoubleBuffer|Forms.ControlStyles.ResizeRedraw,true);
    }
    protected override void WndProc(ref Forms.Message message) {
        if(message.Msg==0x21){message.Result=new IntPtr(3);return;}
        if(message.Msg==0x84){message.Result=new IntPtr(-1);return;}
        base.WndProc(ref message);
    }
    internal void Present(Frame sample,Options settings,long selected,System.Windows.Point? point) {
        if(!object.ReferenceEquals(options,settings)){if(options!=null)options.PropertyChanged-=OptionsChanged;options=settings;options.PropertyChanged+=OptionsChanged;dirty=true;}
        bool showing=!Visible;Native.Point origin=new Native.Point();Native.ClientToScreen(new IntPtr(sample.WindowHandle),ref origin);
        if(showing||origin.X!=lastX||origin.Y!=lastY||sample.Width!=lastWidth||sample.Height!=lastHeight){Native.SetWindowPos(Handle,new IntPtr(-1),origin.X,origin.Y,sample.Width,sample.Height,0x10);lastX=origin.X;lastY=origin.Y;lastWidth=sample.Width;lastHeight=sample.Height;}
        if(Opacity!=settings.Opacity)Opacity=settings.Opacity;
        if(!showing&&!dirty&&sequence==sample.Sequence&&target==selected&&aim==point)return;
        frame=sample;target=selected;aim=point;sequence=sample.Sequence;dirty=false;if(texts.Count>512)ClearText();
        if(showing){previousPaint=0;paintIntervals.Clear();Show();}Invalidate();Update();
    }
    void OptionsChanged(object sender,PropertyChangedEventArgs e){dirty=true;if(e.PropertyName=="Stroke"){foreach(var sizes in pens.Values)foreach(var pen in sizes.Values)pen.Dispose();pens.Clear();}if(e.PropertyName=="FontSize"){ClearText();foreach(var font in fonts.Values)font.Dispose();fonts.Clear();}}
    protected override void OnPaintBackground(Forms.PaintEventArgs e){}
    protected override void OnPaint(Forms.PaintEventArgs e) {
        long now=Stopwatch.GetTimestamp();
        if(previousPaint!=0){paintIntervals.Enqueue((now-previousPaint)/(double)Stopwatch.Frequency);while(paintIntervals.Count>120)paintIntervals.Dequeue();double total=0;foreach(double interval in paintIntervals)total+=interval;if(total>0)paintRate=paintIntervals.Count/total;}
        previousPaint=now;if(frame!=null&&frame.SampleTimestamp>0)SampleAgeMilliseconds=Math.Max(0,(now-frame.SampleTimestamp)*1000.0/Stopwatch.Frequency);
        var timer=Stopwatch.StartNew();Draw(e.Graphics);PaintMilliseconds=timer.Elapsed.TotalMilliseconds;
    }
    static Color AccentColor(string name){return ColorTranslator.FromHtml(name=="Blue"?"#38bdf8":name=="Rose"?"#fb7185":name=="Amber"?"#fbbf24":"#34d399");}
    internal static Media.Brush Accent(string name){Media.Brush brush;if(!accents.TryGetValue(name,out brush)){var c=AccentColor(name);var b=new Media.SolidColorBrush(Media.Color.FromRgb(c.R,c.G,c.B));b.Freeze();accents[name]=b;brush=b;}return brush;}
    SolidBrush Brush(Color color){SolidBrush brush;if(!brushes.TryGetValue(color,out brush)){brush=new SolidBrush(color);brushes[color]=brush;}return brush;}
    Pen Pen(Color color,float width){Dictionary<float,Pen> sizes;if(!pens.TryGetValue(color,out sizes)){sizes=new Dictionary<float,Pen>();pens[color]=sizes;}Pen pen;if(!sizes.TryGetValue(width,out pen)){pen=new Pen(color,width);sizes[width]=pen;}return pen;}
    Font FontFor(float size){Font font;if(!fonts.TryGetValue(size,out font)){font=new Font("Segoe UI",size,FontStyle.Regular,GraphicsUnit.Pixel);fonts[size]=font;}return font;}
    void DrawLabel(Graphics graphics,string value,float x,float y,Color color,float size,long id,bool avoidOverlap=false) {
        double now=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;TextRun run;
        if(!texts.TryGetValue(id,out run)||run.Size!=size||run.Color!=color||(run.Text!=value&&now>=run.RefreshAfter)) {
            Font font=FontFor(size);SizeF dimensions=graphics.MeasureString(value,font,PointF.Empty,StringFormat.GenericTypographic);
            var image=new Bitmap(Math.Max(1,(int)Math.Ceiling(dimensions.Width)+5),Math.Max(1,(int)Math.Ceiling(dimensions.Height)+5),PixelFormat.Format32bppArgb);
            using(var text=Graphics.FromImage(image)){text.Clear(KeyColor);text.TextRenderingHint=TextRenderingHint.SingleBitPerPixelGridFit;text.DrawString(value,font,Brush(Color.Black),new PointF(1,1),StringFormat.GenericTypographic);text.DrawString(value,font,Brush(color),PointF.Empty,StringFormat.GenericTypographic);}
            image.MakeTransparent(KeyColor);if(run!=null)run.Image.Dispose();run=new TextRun{Text=value,Size=size,Color=color,Image=image,RefreshAfter=now+.1+((id>>4)&7)*.005};texts[id]=run;
        }
        if(avoidOverlap){
            int left=Math.Max(2,Math.Min(frame.Width-run.Image.Width-2,(int)Math.Round(x-run.Image.Width/2.0)));
            int top=Math.Max(2,Math.Min(frame.Height-run.Image.Height-2,(int)Math.Round(y)));
            var area=new Rectangle(left,top,run.Image.Width+3,run.Image.Height+3);
            if(!LabelFits(area,labelAreas))return;labelAreas.Add(area);x=left;y=top;
        }
        graphics.DrawImageUnscaled(run.Image,(int)Math.Round(x),(int)Math.Round(y));
    }
    void ClearText(){foreach(var run in texts.Values)run.Image.Dispose();texts.Clear();}
    void Line(Graphics g,Color color,float width,double x,double y,double x2,double y2){g.DrawLine(Pen(color,width),(float)x,(float)y,(float)x2,(float)y2);}
    void Corner(Graphics g,Color color,float width,double x,double y,double dx,double dy,double length){float outline=width+(width<=1?1:2);Line(g,Color.Black,outline,x,y,x+dx*length,y);Line(g,Color.Black,outline,x,y,x,y+dy*length);Line(g,color,width,x,y,x+dx*length,y);Line(g,color,width,x,y,x,y+dy*length);}
    internal static int DetailLevel(double height,bool adaptive){return !adaptive||height>=72?2:height>=28?1:0;}
    internal static Color HealthColor(int health){return health<=25?Color.Tomato:health<=60?Color.Gold:Color.MediumSpringGreen;}
    internal static Vec3 HeadPosition(Player p,double fallbackHeight){Vec3 head;if(p.Bones!=null&&p.Bones.TryGetValue("head",out head))return head;double height=p.BoundsValid?p.Maximum.Z:fallbackHeight;return new Vec3(p.Position.X,p.Position.Y,p.Position.Z+p.Minimum.Z+(height-p.Minimum.Z)*.92);}
    internal static bool LabelFits(Rectangle area,List<Rectangle> occupied){foreach(var other in occupied)if(area.IntersectsWith(other))return false;return true;}
    internal static string ShortName(string name,int length){name=name??"Player";return name.Length<=length?name:name.Substring(0,length-1)+"…";}
    int ComparePlayers(Player a,Player b){if(a.Address==target)return b.Address==target?0:-1;if(b.Address==target)return 1;int distance=a.Distance.CompareTo(b.Distance);return distance!=0?distance:a.Address.CompareTo(b.Address);}
    internal static System.Windows.Point? AimPoint(Player p,Frame f,Options o){Vec3 position;string joint=o.AimPoint.ToLowerInvariant();if(p.Bones==null||!p.Bones.TryGetValue(joint,out position)){double height=p.BoundsValid?p.Maximum.Z:o.Height;position=new Vec3(p.Position.X,p.Position.Y,p.Position.Z+p.Minimum.Z+(height-p.Minimum.Z)*(joint=="head"?.92:.70));}return MathTools.Project(position,f.Matrix,f.Width,f.Height,true);}
    void Draw(Graphics graphics) {
        graphics.Clear(KeyColor);if(frame==null||frame.Width<=0||frame.Height<=0)return;
        graphics.SmoothingMode=SmoothingMode.None;graphics.PixelOffsetMode=PixelOffsetMode.None;
        var f=frame;var o=options;double w=f.Width,h=f.Height,cx=w/2,cy=h/2;
        labelAreas.Clear();labelAreas.Add(new Rectangle(12,10,430,25));drawOrder.Clear();drawOrder.AddRange(f.Players);drawOrder.Sort(ComparePlayers);
        if(o.Crosshair){Line(graphics,Color.White,1,cx-7,cy,cx+7,cy);Line(graphics,Color.White,1,cx,cy-7,cx,cy+7);}
        if(o.Aim&&o.ShowRadius)graphics.DrawEllipse(Pen(Color.SlateGray,1),(float)(cx-o.Radius),(float)(cy-o.Radius),(float)o.Radius*2,(float)o.Radius*2);
        foreach(Player p in drawOrder) {
            if(!o.Esp||(p.Team==f.Team&&!o.Teammates)||(o.MaxDistance>0&&p.Distance>o.MaxDistance))continue;
            Color color=p.Address==target?Color.Gold:p.Team==f.Team?Color.LightSkyBlue:AccentColor(o.Palette);
            if(o.SpottedColors&&p.Address!=target&&p.Team!=f.Team)color=p.SpottedByLocal==true?Color.MediumSpringGreen:p.SpottedByLocal==false?Color.SlateGray:Color.LightSlateGray;
            Vec3 min=p.Minimum,max=p.BoundsValid?p.Maximum:new Vec3(16,16,o.Height);bool invalid=false;double left=double.MaxValue,right=double.MinValue,top=double.MaxValue,bottom=double.MinValue;
            for(int corner=0;corner<8;corner++){var point=MathTools.Project(new Vec3(p.Position.X+((corner&1)==0?min.X:max.X),p.Position.Y+((corner&2)==0?min.Y:max.Y),p.Position.Z+((corner&4)==0?min.Z:max.Z)),f.Matrix,w,h,false);if(!point.HasValue){invalid=true;break;}left=Math.Min(left,point.Value.X);right=Math.Max(right,point.Value.X);top=Math.Min(top,point.Value.Y);bottom=Math.Max(bottom,point.Value.Y);}
            if(invalid)continue;left=Math.Max(0,left);right=Math.Min(w,right);top=Math.Max(0,top);bottom=Math.Min(h,bottom);if(right-left<2||bottom-top<3)continue;double height=bottom-top;
            int detail=DetailLevel(height,o.AdaptiveEsp);float stroke=detail<2?1:(float)o.Stroke;
            left=Math.Round(left);right=Math.Round(right);top=Math.Round(top);bottom=Math.Round(bottom);height=bottom-top;
            if(detail==2&&o.Skeleton&&p.Bones!=null&&f.SkeletonEdges!=null)foreach(string[] edge in f.SkeletonEdges){Vec3 a,b;if(p.Bones.TryGetValue(edge[0],out a)&&p.Bones.TryGetValue(edge[1],out b)&&a.Distance(b)>.1&&a.Distance(b)<128){var first=MathTools.Project(a,f.Matrix,w,h,true);var second=MathTools.Project(b,f.Matrix,w,h,true);if(first.HasValue&&second.HasValue){Line(graphics,Color.Black,stroke+2,first.Value.X,first.Value.Y,second.Value.X,second.Value.Y);Line(graphics,color,stroke,first.Value.X,first.Value.Y,second.Value.X,second.Value.Y);}}}
            if(o.Corners&&detail>0){double length=Math.Min((right-left)*.25,height*.15);Corner(graphics,color,stroke,left,top,1,1,length);Corner(graphics,color,stroke,right,top,-1,1,length);Corner(graphics,color,stroke,left,bottom,1,-1,length);Corner(graphics,color,stroke,right,bottom,-1,-1,length);}
            else{graphics.DrawRectangle(Pen(Color.Black,stroke+(detail<2?1:2)),(float)left,(float)top,(float)(right-left),(float)height);graphics.DrawRectangle(Pen(color,stroke),(float)left,(float)top,(float)(right-left),(float)height);}
            if(detail>0){Line(graphics,Color.Black,detail==2?4:2,left-4,bottom,left-4,top);Line(graphics,o.HealthColors?HealthColor(p.Health):color,detail==2?2:1,left-4,bottom,left-4,bottom-height*Math.Min(100,p.Health)/100.0);}
            if(detail==2&&o.HeadMarker){var head=MathTools.Project(HeadPosition(p,o.Height),f.Matrix,w,h,true);if(head.HasValue){float radius=(float)Options.Clamp(height*.045,3,9);graphics.DrawEllipse(Pen(Color.Black,3),(float)head.Value.X-radius,(float)head.Value.Y-radius,radius*2,radius*2);graphics.DrawEllipse(Pen(color,1),(float)head.Value.X-radius,(float)head.Value.Y-radius,radius*2,radius*2);}}
            if(detail==2&&o.Armor&&p.Armor.HasValue){Line(graphics,Color.Black,4,left,bottom+5,right,bottom+5);Line(graphics,Color.LightSkyBlue,2,left,bottom+5,left+(right-left)*Math.Min(100,p.Armor.Value)/100.0,bottom+5);}
            if(o.Snaplines&&detail>0)Line(graphics,color,1,cx,h,(left+right)/2,bottom);
            if(detail>0||p.Address==target){
                string label=(o.Names?ShortName(p.Name,detail==2?18:10)+" ":"")+(detail==2?p.Health+" HP":"")+(o.Distances?" "+p.Distance.ToString("F0")+"u":"")+(o.ReviewIndicators&&p.Team!=f.Team&&p.RapidTurns>=3?" REVIEW":"");
                float font=detail<2?(float)Math.Min(10,o.FontSize):(float)o.FontSize;
                if(label.Trim().Length>0)DrawLabel(graphics,label.Trim(),(float)((left+right)/2),(float)Math.Max(2,top-font-6),Color.White,font,p.Address*2,o.AdaptiveEsp);
                if(detail==2){string lower=(o.Weapons?p.Weapon+(p.Ammo.HasValue&&p.Ammo.Value>=0?" ["+p.Ammo+"]":""):"")+(o.ShowStatus&&p.StatusFlags.Trim().Length>0?"  "+p.StatusFlags.Trim():"");if(lower.Trim().Length>0)DrawLabel(graphics,lower.Trim(),(float)((left+right)/2),(float)bottom+8,Color.LightGray,font,p.Address*2+1,o.AdaptiveEsp);}
            }
        }
        if(aim.HasValue)graphics.DrawEllipse(Pen(Color.Gold,2),(float)aim.Value.X-4,(float)aim.Value.Y-4,8,8);
        DrawLabel(graphics,"pika op / native overlay / "+f.Players.Count+" players / tracking "+(o.Aim?"ON":"OFF"),18,16,Color.White,12,-1);
    }
    internal double[] MeasureDraw(Frame sample,Options settings,int count,string preview=null){ClearText();frame=sample;options=settings;var times=new double[count];using(var image=new Bitmap(sample.Width,sample.Height,PixelFormat.Format32bppPArgb))using(var g=Graphics.FromImage(image)){for(int i=0;i<count+10;i++){var timer=Stopwatch.StartNew();Draw(g);if(i>=10)times[i-10]=timer.Elapsed.TotalMilliseconds;}if(preview!=null)image.Save(preview,ImageFormat.Png);}return times;}
    protected override void Dispose(bool disposing){if(disposing){if(options!=null)options.PropertyChanged-=OptionsChanged;ClearText();foreach(var brush in brushes.Values)brush.Dispose();foreach(var widths in pens.Values)foreach(var pen in widths.Values)pen.Dispose();foreach(var font in fonts.Values)font.Dispose();}base.Dispose(disposing);}
}
}
