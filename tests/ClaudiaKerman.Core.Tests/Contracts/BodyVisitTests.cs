// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1377: the Contract Route is read as Body Visits (ADR 0022): each body once, in the Route's order, holding every Chosen Requirement
    /// served there, whichever Contract it comes from, in the order the Route prices the visit; home is the launch and the return.
    /// </summary>
    public sealed class BodyVisitTests
    {
        private static ContractChoice Chosen(params SnapshotContract[] contracts)
        {
            var check = ContractCheck.Work(ContractCheckTests.With(contracts), "", false, ContractCheckTests.Margin);
            return check.Choose(contracts.Select(c => c.Guid));
        }

        /// <summary>One survey Contract with 3 waypoints over the Mun and 2 over Minmus.</summary>
        private static SnapshotContract MunAndMinmusScans()
        {
            var scans = ScienceSurveyContractTests.Survey("c-scan", "2", "LOW", "crewReport", 30000, "3,10", "3,100", "3,-170");
            var minmus = ScienceSurveyContractTests.Survey("c-scan", "3", "LOW", "crewReport", 30000, "5,20", "5,-60");
            foreach (var requirement in minmus.Requirements)
            {
                requirement.Key = ContractRequirement.KeyOf("c-scan", new[] { scans.Requirements.Count });
                scans.Requirements.Add(requirement);
            }
            scans.Title = "Scan the Mun and Minmus";
            return scans;
        }

        [Fact]
        public void TwoContractsAtTheMunAndMinmusShareOneVisitToEachOrbitFirstThenTheScansThenHome()
        {
            var orbits = ContractCheckTests.Exploration("c-orbit", 10000, "Mun:ORBIT", "Minmus:ORBIT");
            orbits.Title = "Orbit the Mun and Minmus";

            var choice = Chosen(orbits, MunAndMinmusScans());

            Assert.Equal(new[] { "Mun", "Minmus", "Kerbin" }, choice.Visits.Select(v => v.Body));
            Assert.Equal(BodyVisitStop.Return, choice.Visits[2].Stop);
            Assert.Empty(choice.Visits[2].Requirements);

            var mun = choice.Visits[0].Requirements;
            Assert.Equal(4, mun.Count);
            Assert.Equal("orbit the Mun", mun[0].Words);
            Assert.Equal("Orbit the Mun and Minmus", mun[0].Title);
            Assert.All(mun.Skip(1), r => Assert.StartsWith("take a crewReport over the waypoint", r.Words));
            Assert.All(mun.Skip(1), r => Assert.Equal("Scan the Mun and Minmus", r.Title));

            var minmus = choice.Visits[1].Requirements;
            Assert.Equal(3, minmus.Count);
            Assert.Equal("orbit Minmus", minmus[0].Words);
            Assert.All(minmus.Skip(1), r => Assert.EndsWith("over Minmus", r.Words));
        }

        [Fact]
        public void ARequirementTheGameShowsCompleteIsMetAndTheRestAreOpen()
        {
            // The Contract check reads out a Requirement already met, so a met one is the Route rebuilt over a later reading.
            var orbits = ContractCheckTests.Exploration("c-orbit", 10000, "Mun:FLYBY", "Mun:ORBIT");
            orbits.Title = "Explore the Mun";
            var read = ContractKinds.Read(orbits, "Kerbin");
            read[0].Requirement.State = "Complete";
            var route = new PricedRoute { Order = { "Mun" } };

            var mun = route.Visits(read.Select(r => new KeyValuePair<string, ReadRequirement>("Explore the Mun", r)), "Kerbin").Single().Requirements;

            Assert.Equal(new[] { "fly by the Mun", "orbit the Mun" }, mun.Select(r => r.Words));
            Assert.Equal(new[] { true, false }, mun.Select(r => r.Met));
        }

        [Fact]
        public void HomeIsTheLaunchForWorkAtHomeAndTheReturnForWhatIsMetOnceHomeAgain()
        {
            var tour = ContractCheckTests.Exploration("c-home", 10000, "Kerbin:ORBIT", "Mun:FLYBY");
            var read = ContractKinds.Read(tour, "Kerbin");
            read.Add(new ReadRequirement { Requirement = new ContractRequirement { Key = "c-home/9", State = "Incomplete" }, Body = "Mun", Reach = ContractReach.Flyby, Return = true, Words = "bring tourist Lodfrid Kerman home" });
            var route = new PricedRoute { Order = { "Mun" }, HomeUt = 1 };

            var visits = route.Visits(read.Select(r => new KeyValuePair<string, ReadRequirement>("Tour", r)), "Kerbin");

            Assert.Equal(new[] { BodyVisitStop.Launch, BodyVisitStop.Visit, BodyVisitStop.Return }, visits.Select(v => v.Stop));
            Assert.Equal(new[] { "Kerbin", "Mun", "Kerbin" }, visits.Select(v => v.Body));
            Assert.Equal("orbit Kerbin", visits[0].Requirements.Single().Words);
            Assert.Equal("fly by the Mun", visits[1].Requirements.Single().Words);
            Assert.Equal("bring tourist Lodfrid Kerman home", visits[2].Requirements.Single().Words);
        }
    }
}
