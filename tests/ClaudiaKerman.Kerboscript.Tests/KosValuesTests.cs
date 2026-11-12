// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKerman.KOS;
using kOS.Safe.Encapsulation;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>KosValues.FromPlain (#951): every C# numeric type reaches kOS as a scalar, not a string.</summary>
    public class KosValuesTests
    {
        public static IEnumerable<object[]> Numbers()
        {
            yield return new object[] { 2.5d, 2.5 };
            yield return new object[] { 2, 2.0 };
            yield return new object[] { -7L, -7.0 };
            yield return new object[] { 3u, 3.0 };
            yield return new object[] { 4UL, 4.0 };
            yield return new object[] { (short)-5, -5.0 };
            yield return new object[] { (byte)200, 200.0 };
            yield return new object[] { 1.5f, 1.5 };
            yield return new object[] { 2.25m, 2.25 };
        }

        [Theory]
        [MemberData(nameof(Numbers))]
        public void EveryNumericTypeCrossesAsAScalarWithTheSameValue(object plain, double expected)
        {
            var scalar = Assert.IsAssignableFrom<ScalarValue>(KosValues.FromPlain(plain));
            Assert.Equal(expected, scalar.GetDoubleValue());
        }

        [Fact]
        public void NumbersInsideLexiconsAndListsAreScalarsToo()
        {
            var lexicon = (Lexicon)KosValues.FromPlain(new Dictionary<string, object>
            {
                { "step", 2 }, { "all", new List<object> { 1, 2L } },
            });
            Assert.IsAssignableFrom<ScalarValue>(lexicon[new StringValue("step")]);
            var all = (ListValue)lexicon[new StringValue("all")];
            foreach (var item in all) Assert.IsAssignableFrom<ScalarValue>(item);
        }

        [Fact]
        public void StringsBooleansNullListsAndLexiconsConvertAsBefore()
        {
            Assert.Equal("a1", Assert.IsType<StringValue>(KosValues.FromPlain("a1")).ToString());
            Assert.True(Assert.IsType<BooleanValue>(KosValues.FromPlain(true)).Value);
            Assert.Equal("", Assert.IsType<StringValue>(KosValues.FromPlain(null)).ToString());
            var list = Assert.IsType<ListValue>(KosValues.FromPlain(new List<object> { "x", false }));
            Assert.IsType<StringValue>(list[0]);
            Assert.IsType<BooleanValue>(list[1]);
            var lexicon = Assert.IsType<Lexicon>(KosValues.FromPlain(new Dictionary<string, object> { { "k", "v" } }));
            Assert.IsType<StringValue>(lexicon[new StringValue("k")]);
        }
    }
}
