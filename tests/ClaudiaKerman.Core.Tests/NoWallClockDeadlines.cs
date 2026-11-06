// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Reflection;
using ClaudiaKerman.Core.Tools;
using Xunit;
using Xunit.Sdk;

[assembly: ClaudiaKerman.Core.Tests.NoWallClockDeadlines]

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// Every Core test runs with the searches' wall-clock deadlines off (#636): a search that gives up when the machine is
    /// busy answers differently under load (find_landing_site's 3.5 s, the plane-change and brake-path searches' 200 ms).
    /// A search timed by a body interface's own clock (a fake one) still runs out; <see cref="Timing.Within"/> puts the real
    /// budgets back around its work. Audit: grep SearchBudget.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    internal sealed class NoWallClockDeadlinesAttribute : BeforeAfterTestAttribute
    {
        public override void Before(MethodInfo methodUnderTest)
        {
            SearchBudget.Unlimited = true;
        }

        public override void After(MethodInfo methodUnderTest)
        {
            SearchBudget.Unlimited = false;
        }
    }
}

namespace ClaudiaKerman.Core.Tests
{
    public sealed class NoWallClockDeadlinesTests
    {
        [Fact]
        [Trait("Category", "Timing")]
        public void A_test_runs_with_the_wall_clock_deadlines_off_and_Timing_Within_puts_them_back()
        {
            Assert.True(SearchBudget.Unlimited);
            Assert.Same(SearchDeadline.None, SearchDeadline.After(1, null));
            Timing.Within(1e9, () => Assert.False(SearchBudget.Unlimited));
            Assert.True(SearchBudget.Unlimited);
        }
    }
}
