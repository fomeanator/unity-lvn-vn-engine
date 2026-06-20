using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class LvnExpressionTests
    {
        private static Dictionary<string, JToken> Vars(params (string key, JToken value)[] kv)
        {
            var d = new Dictionary<string, JToken>();
            foreach (var (key, value) in kv) d[key] = value;
            return d;
        }

        // The regression that broke once-only choices: an unset variable must
        // compare equal to 0 (ink defaulting) so `__once == 0` is true on the
        // first visit.
        [Test]
        public void UnsetVariableEqualsZero()
        {
            Assert.IsTrue(LvnExpression.EvaluateBool("__once == 0", Vars()));
        }

        [Test]
        public void SetVariableClosesOnceGate()
        {
            Assert.IsFalse(LvnExpression.EvaluateBool("__once == 0", Vars(("__once", 1))));
        }

        [Test]
        public void UnsetVariableEqualsEmptyStringAndFalse()
        {
            Assert.IsTrue(LvnExpression.EvaluateBool("name == \"\"", Vars()));
            Assert.IsTrue(LvnExpression.EvaluateBool("flag == false", Vars()));
        }

        [Test]
        public void BooleanAndComparisonOperators()
        {
            Assert.IsTrue(LvnExpression.EvaluateBool("courage >= 2 && !lied", Vars(("courage", 2))));
            Assert.IsFalse(LvnExpression.EvaluateBool("courage >= 2 && !lied", Vars(("courage", 2), ("lied", true))));
        }

        [Test]
        public void Arithmetic()
        {
            Assert.AreEqual(6L, (long)LvnExpression.Evaluate("(1 + 2) * 2", Vars()));
        }

        [Test]
        public void StringEquality()
        {
            Assert.IsTrue(LvnExpression.EvaluateBool("name == \"Mara\"", Vars(("name", "Mara"))));
            Assert.IsFalse(LvnExpression.EvaluateBool("name == \"Mara\"", Vars(("name", "Kel"))));
        }

        [Test]
        public void VisitCountComparisonOnUnset()
        {
            // gte/lt go through AsNum, where null is already 0 — pin it.
            Assert.IsFalse(LvnExpression.EvaluateBool("__seen >= 1", Vars()));
            Assert.IsTrue(LvnExpression.EvaluateBool("__seen >= 1", Vars(("__seen", 1))));
        }
    }
}
