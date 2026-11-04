using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Roster;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#583: Claudia is never "other crew", whichever spelling of her trait the snapshot recorded.</summary>
    public class HasOtherCrewTests
    {
        private static GameSnapshot Aboard(params SnapshotCrewMember[] crew)
        {
            var snapshot = new GameSnapshot();
            snapshot.Crew.AddRange(crew);
            return snapshot;
        }

        private static SnapshotCrewMember Claudia(string trait)
        {
            return new SnapshotCrewMember { Name = ClaudiaIdentity.Name, Trait = trait };
        }

        [Theory]
        [InlineData("Super Intelligence")]
        [InlineData("SuperIntelligence")]
        public void ClaudiaAloneIsNotOtherCrewWhicheverSpellingOfHerTraitWasRecorded(string trait)
        {
            Assert.False(Aboard(Claudia(trait)).HasOtherCrew);
        }

        [Theory]
        [InlineData("Pilot", true)]
        [InlineData("Engineer", true)]
        [InlineData("Tourist", false)]
        public void ClaudiaPlusAnotherKerbalIsCrewedUnlessTheyAreATourist(string trait, bool expected)
        {
            var snapshot = Aboard(Claudia("Super Intelligence"), new SnapshotCrewMember { Name = "Jebediah Kerman", Trait = trait });

            Assert.Equal(expected, snapshot.HasOtherCrew);
        }
    }
}
