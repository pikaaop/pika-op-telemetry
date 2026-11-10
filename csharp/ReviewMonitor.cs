using System;
using System.Collections.Generic;

namespace PikaOp {
// These are descriptive telemetry observations, not a validated cheating classifier.
internal sealed class ReviewMonitor {
    sealed class State {internal uint Handle;internal double Time,Changed;internal Vec3 Angles,Position;internal readonly Queue<double> Events=new Queue<double>();}
    readonly Dictionary<long,State> states=new Dictionary<long,State>();
    internal void Observe(Player p,double now){
        p.RapidTurns=0;p.Review="No conclusion";if(!p.EyeAngles.HasValue){states.Remove(p.Address);p.Review="Angle data unavailable";return;}
        Vec3 a=p.EyeAngles.Value;State s;
        if(!MathTools.Finite(a.X)||!MathTools.Finite(a.Y)||Math.Abs(a.X)>90.1||Math.Abs(a.Y)>360){states.Remove(p.Address);p.Review="Angle data out of range";return;}
        if(states.Count>128)states.Clear();
        if(!states.TryGetValue(p.Address,out s)||s.Handle!=p.EntityHandle||now<=s.Time||now-s.Time>.25||p.Position.Distance(s.Position)>128){s=new State{Handle=p.EntityHandle,Angles=a,Position=p.Position,Time=now,Changed=now};states[p.Address]=s;return;}
        while(s.Events.Count>0&&now-s.Events.Peek()>5)s.Events.Dequeue();
        double delta=Math.Sqrt(Math.Pow(MathTools.AngleDelta(a.Y,s.Angles.Y),2)+Math.Pow(a.X-s.Angles.X,2));
        if(delta>.01){double dt=now-s.Changed;
            // shortcut: receive-time intervals include network batching; use demos with server ticks for stronger analysis.
            if(dt>=.005&&dt<=.05&&delta>=45&&delta/dt>=1800)s.Events.Enqueue(now);
            s.Changed=now;s.Angles=a;
        }
        s.Time=now;s.Position=p.Position;p.RapidTurns=s.Events.Count;
        p.Review=p.RapidTurns>=3?"Review: repeated rapid turns":"No conclusion";
    }
}
}
