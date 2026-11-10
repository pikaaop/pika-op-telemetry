using System;
using System.Windows;

namespace PikaOp {
internal static class MathTools {
    internal static int SampleDelay(double elapsedMilliseconds,bool foreground){return foreground?Math.Max(1,(int)Math.Ceiling(1000.0/240-elapsedMilliseconds)):100;}
    internal static bool Fresh(Frame frame,long timestamp){return frame.SampleTimestamp>0&&timestamp>=frame.SampleTimestamp&&(timestamp-frame.SampleTimestamp)/(double)System.Diagnostics.Stopwatch.Frequency<.25;}
    internal static bool Finite(double v){return !double.IsNaN(v)&&!double.IsInfinity(v);}
    internal static Point? Project(Vec3 p,float[] m,double width,double height,bool clip) {
        double x=m[0]*p.X+m[1]*p.Y+m[2]*p.Z+m[3];
        double y=m[4]*p.X+m[5]*p.Y+m[6]*p.Z+m[7];
        double w=m[12]*p.X+m[13]*p.Y+m[14]*p.Z+m[15];
        if(!Finite(x)||!Finite(y)||!Finite(w)||w<=1e-6)return null;
        x/=w;y/=w;if(clip&&(Math.Abs(x)>1||Math.Abs(y)>1))return null;
        return new Point((x+1)*width/2,(1-y)*height/2);
    }
    internal static double Norm(float[] m,int start){return Math.Sqrt(m[start]*m[start]+m[start+1]*m[start+1]+m[start+2]*m[start+2]);}
    internal static double Distance(Point a,Point b){return (a-b).Length;}
    internal static Point Tracking(Point target,Point center,Frame f,Options o,double dt,ref Point residual) {
        if(Distance(target,center)<=o.Deadzone){residual=new Point();return new Point();}
        if(!(f.Sensitivity>=.01&&f.Sensitivity<=100&&f.ScopeScale>=.01&&f.ScopeScale<=4))throw new InvalidOperationException("Invalid mouse calibration");
        double scale=Norm(f.Matrix,12),fx=Norm(f.Matrix,0),fy=Norm(f.Matrix,4);
        if(Math.Min(scale,Math.Min(fx,fy))<1e-6)throw new InvalidOperationException("Invalid projection scale");
        double x=(target.X-center.X)/center.X*scale/fx,y=(target.Y-center.Y)/center.Y*scale/fy;
        double degrees=.022*f.Sensitivity*f.ScopeScale,alpha=1-Math.Exp(-Math.Max(0,Math.Min(dt,.05))/o.Tau);
        double dx=Math.Atan(x)*180/Math.PI/degrees*alpha*o.Gain;
        double dy=Math.Atan2(y,Math.Sqrt(x*x+1))*180/Math.PI/degrees*alpha*o.Gain;
        double limit=o.MaxSpeed*Math.Max(0,Math.Min(dt,.05));
        dx=Options.Clamp(dx,-limit,limit)+residual.X;dy=Options.Clamp(dy,-limit,limit)+residual.Y;
        var result=new Point(Math.Truncate(dx),Math.Truncate(dy));
        residual=new Point(dx-result.X,dy-result.Y);return result;
    }
    internal static int ActivationCode(string key){switch(key){case "Right mouse":return 2;case "Mouse 4":return 5;case "Mouse 5":return 6;case "Alt":return 0x12;default:return 1;}}
    internal static bool CanTrack(Frame f,Options o){return !o.ScopedOnly||(f.Local!=null&&f.Local.Scoped);}
    internal static bool TargetAllowed(Player p,Options o){return !o.SpottedOnly||p.SpottedByLocal==true;}
    internal static bool? Spotted(ulong mask,int controllerIndex){return controllerIndex<1||controllerIndex>64?(bool?)null:(mask&(1UL<<(controllerIndex-1)))!=0;}
    internal static double AngleDelta(double a,double b){double d=(a-b)%360;return d>180?d-360:d< -180?d+360:d;}
    internal static double TargetScore(Player p,double crosshairDistance,Options o){return o.Priority=="Distance"?p.Distance:o.Priority=="Lowest health"?p.Health:crosshairDistance;}
}
}
