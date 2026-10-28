namespace ClaudiaKermin.Kerboscript.Tests
{
    public class TmpProbe
    {
        [Xunit.Fact]
        public void Probe()
        {
            KosHarness.Run("print 1.");
            var t = typeof(kOS.Safe.Function.FunctionManager);
            var text = "";
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
            {
                var v = f.GetValue(null) as System.Collections.IDictionary;
                text += f.Name + ":";
                if (v != null) foreach (System.Collections.DictionaryEntry e in v) text += ((kOS.Safe.Function.FunctionAttribute)e.Key).Names[0] + "=" + e.Value + ",";
                text += "\n";
            }
            foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                text += "\nMETHOD " + m;
            foreach (var m in typeof(kOS.Safe.Utilities.AssemblyWalkAttribute).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                text += "\nWALK " + m;
            System.IO.File.WriteAllText("/tmp/claudia-444-probe.txt", text);
        }
    }
}
