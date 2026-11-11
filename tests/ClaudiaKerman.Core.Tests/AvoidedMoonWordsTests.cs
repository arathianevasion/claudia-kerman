using System.Linq;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#894 fix round: which moons a leg's words plan, with the avoid word governing articles, verbs, lists and the SOI forms.</summary>
    public sealed class AvoidedMoonWordsTests
    {
        private static string Planned(string words)
        {
            var snapshot = new GameSnapshot();
            foreach (var n in new[] { "Kerbin", "Mun", "Minmus", "Duna", "Ike", "Dres" }) snapshot.Bodies.Add(new SnapshotBody { Name = n });
            return string.Join(",", SequenceWords.Destinations(snapshot, words).Select(b => b.Name).OrderBy(x => x));
        }

        [Theory]
        [InlineData("Raise Ap clear of the Mun", "")]
        [InlineData("Raise Ap clear of the Mun's SOI", "")]
        [InlineData("Avoid an Ike encounter", "")]
        [InlineData("Avoid a Mun encounter", "")]
        [InlineData("Stay out of Ike's SOI", "")]
        [InlineData("Coast without entering Ike's SOI", "")]
        [InlineData("Coast, don't enter Ike's SOI", "")]
        [InlineData("Coast, do not enter Ike's SOI", "")]
        [InlineData("Avoid encountering Ike", "")]
        [InlineData("Avoid hitting the Mun", "")]
        [InlineData("Coast, having missed Ike", "")]
        [InlineData("Avoid Ike and the Mun", "")]
        [InlineData("Clear of Ike or Minmus", "")]
        [InlineData("Avoid Ike, Mun, and Minmus", "")]
        [InlineData("Avoid: Ike", "")]
        [InlineData("Avoid (Ike)", "")]
        [InlineData("Stay below the SOI of Ike", "")]
        [InlineData("Stay under the Mun's orbit", "")]
        [InlineData("Stay outside Ike", "")]
        [InlineData("Transfer to Ike, avoiding Dres", "Ike")]
        [InlineData("Avoid Ike and go to the Mun", "Mun")]
        [InlineData("Return from Ike to Duna", "Duna")]
        [InlineData("Periapsis below the Mun's surface", "Mun")]
        [InlineData("Capture into orbit below Ike", "Ike")]
        [InlineData("Circularize under Minmus", "Minmus")]
        [InlineData("Lower Pe under Minmus' SOI then capture", "")]
        [InlineData("Lower orbit at Mun", "Mun")]
        [InlineData("Land on Ike", "Ike")]
        [InlineData("Orbit under 30 km at Ike", "Ike")]
        [InlineData("Ike orbit below 50 km", "Ike")]
        [InlineData("Raise Ap clear of the SOI of Ike", "")]
        [InlineData("Avoid the SOI of Ike", "")]
        [InlineData("Avoid an encounter with Ike", "")]
        [InlineData("Avoid any Ike encounter", "")]
        [InlineData("Coast with no Ike encounter", "")]
        [InlineData("Avoid Ike's and the Mun's SOI", "")]
        [InlineData("Stay below the Mun SOI", "")]
        [InlineData("Transfer to Ike, no Mun landing", "Ike,Mun")]
        public void WhichMoonsAreNamedAsPlaces(string words, string expected) => Assert.Equal(expected, Planned(words));
    }
}
