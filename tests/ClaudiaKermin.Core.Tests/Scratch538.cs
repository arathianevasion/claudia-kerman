using System;
using System.Text.Json;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKermin.Core.Tests
{
    public sealed class Scratch538
    {
        private readonly ITestOutputHelper _o;
        public Scratch538(ITestOutputHelper o) { _o = o; }
        const double KGm = 3.5316e12, KR = 600000, MunSma = 12000000;

        static GameSnapshot Snap(double mAnom, double pe, double ap)
        {
            var rp = KR + pe; var ra = KR + ap;
            var s = new GameSnapshot { Ut = 0, Situation = "ORBITING", Body = "Kerbin" };
            s.Orbit = new SnapshotOrbit { SemiMajorAxis = (rp + ra) / 2, Eccentricity = (ra - rp) / (ra + rp), Inclination = 0, Lan = 0, ArgumentOfPeriapsis = 0, MeanAnomalyAtEpoch = 0, Epoch = 0, Body = "Kerbin", Periapsis = pe, Apoapsis = ap };
            s.Bodies.Add(new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            s.Bodies.Add(new SnapshotBody { Name = "Kerbin", Parent = "Sun", Gm = KGm, Radius = KR, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48, RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14 });
            s.Bodies.Add(new SnapshotBody { Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = MunSma, MeanAnomalyAtEpoch = mAnom });
            return s;
        }

        static string Call(string tool, string input, GameSnapshot s)
        {
            return PlanningTools.Default().Run(tool, input, s, new NoPlanningBody()).Json;
        }

        [Fact]
        public void Explore()
        {
            for (var m = 0.0; m < 6.28; m += 0.02)
            {
                var s = Snap(m, 76000, 17173000);
                var j = Call("predict_burn", "{\"burns\":[{\"at\":\"in 600\",\"dv\":{\"prograde\":0}}],\"target\":\"Mun\"}", s);
                var doc = JsonDocument.Parse(j);
                JsonElement ps;
                if (!doc.RootElement.TryGetProperty("patches", out ps) || ps.GetArrayLength() < 2 || ps[1].GetProperty("body").GetString() != "Mun") continue;
                var pe = ps[1].GetProperty("pe").GetDouble();
                var line = "m=" + m.ToString("F2") + " munPe=" + pe + " soi_ut=" + ps[0].GetProperty("end_ut").GetDouble();
                foreach (var at in new[] { "in 60", "in 300", "in 3000", "in 10000" })
                {
                    var r = Call("solve_burn", "{\"goal\":\"next_patch_pe\",\"value\":30000,\"direction\":\"retrograde\",\"at\":\"" + at + "\"}", s);
                    var d = JsonDocument.Parse(r).RootElement;
                    JsonElement dv;
                    line += " | " + at + ": " + (d.TryGetProperty("dv", out dv) ? dv.GetProperty("prograde").GetDouble().ToString("F1") : "ERR");
                }
                _o.WriteLine(line);
            }
        }
    }
}
