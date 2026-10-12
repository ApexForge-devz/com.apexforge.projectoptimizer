using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using ApexForge.ProjectOptimizer;

namespace ApexForge.ProjectOptimizer.Tests
{
    /// <summary>Session 01 policy-boundary regression tests. Unity Editor test execution is required.</summary>
    public sealed class FixKindAuditSession01Tests
    {
        static object InvokePolicy(string methodName, params object[] arguments)
        {
            var method = typeof(ProjectSettingsAnalyzer).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing analyzer policy helper: " + methodName);
            return method.Invoke(null, arguments);
        }

        static ManagedStrippingLevel Stripping(string name) => (ManagedStrippingLevel)Enum.Parse(typeof(ManagedStrippingLevel), name, true);

        [TestCase("Disabled", "Low")]
        [TestCase("Minimal", "Low")]
        [TestCase("Low", "Low")]
        [TestCase("Medium", "Medium")]
        [TestCase("High", "High")]
        public void ManagedStrippingPolicyPromotesOnlyLevelsBelowLow(string input, string expected)
        {
            var actual = (ManagedStrippingLevel)InvokePolicy("PreferredManagedStrippingLevel", Stripping(input));
            Assert.That(actual.ToString(), Is.EqualTo(expected));
        }

        [TestCase(0, 0)]
        [TestCase(1, 1)]
        [TestCase(2, 2)]
        [TestCase(4, 4)]
        [TestCase(8, 4)]
        public void UrpMobileMsaaIsMaximumFourWithoutRaisingLowerValues(int input, int expected)
        {
            Assert.That((int)InvokePolicy("ClampUrpMobileMsaa", input), Is.EqualTo(expected));
        }

        [TestCase(0, 0)]
        [TestCase(1, 1)]
        [TestCase(2, 2)]
        [TestCase(4, 2)]
        [TestCase(8, 2)]
        public void BuiltInMobileMsaaIsMaximumTwoWithoutRaisingLowerValues(int input, int expected)
        {
            Assert.That((int)InvokePolicy("ClampBuiltinMobileMsaa", input), Is.EqualTo(expected));
        }


        [TestCase(false, false, true)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        public void BuiltInQualitySettingsRequireNoQualityOverrideAndNoGlobalPipeline(bool hasQualityOverride, bool hasProjectDefaultPipeline, bool expected)
        {
            Assert.That((bool)InvokePolicy("IsBuiltInPipeline", hasQualityOverride, hasProjectDefaultPipeline), Is.EqualTo(expected));
        }

        [TestCase(true, false, true, 4, true)]
        [TestCase(true, false, true, 2, false)]
        [TestCase(true, true, true, 8, false)]
        [TestCase(false, false, true, 8, false)]
        [TestCase(true, false, false, 8, false)]
        public void BuiltInMobileMsaaCapDoesNotOverrideVrFloor(bool isMobile, bool isVr, bool isBuiltIn, int current, bool expected)
        {
            Assert.That((bool)InvokePolicy("ShouldClampBuiltinMobileMsaa", isMobile, isVr, isBuiltIn, current), Is.EqualTo(expected));
        }

        [TestCase(0, 4)]
        [TestCase(1, 4)]
        [TestCase(2, 4)]
        [TestCase(4, 4)]
        [TestCase(8, 8)]
        public void VrMsaaIsMinimumFourWithoutReducingHigherValues(int input, int expected)
        {
            Assert.That((int)InvokePolicy("RaiseVrMsaaToFour", input), Is.EqualTo(expected));
        }
    }
}
