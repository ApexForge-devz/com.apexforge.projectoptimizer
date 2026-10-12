using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace ApexForge.ProjectOptimizer.Tests
{
    public sealed class RuleIdMigrationTests
    {
        [SetUp]
        public void SetUp() => RuleBootstrap.EnsureBuiltInRules();

        [Test]
        public void CanonicalRuleNumberingIsContiguousWithinEachPrefixAndUsesThreeDigits()
        {
            var ids = RuleCatalog.All.Select(rule => rule.Id).ToList();
            Assert.AreEqual(322, ids.Count, "The canonical catalog should contain every active rule once.");
            Assert.AreEqual(ids.Count, ids.Distinct(StringComparer.Ordinal).Count(), "Active IDs must be unique.");

            var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (string id in ids)
            {
                Match match = Regex.Match(id ?? string.Empty, "^([A-Z]+)([0-9]{3})$");
                Assert.IsTrue(match.Success, "Canonical IDs must have a prefix and exactly three numeric digits: " + id);
                string prefix = match.Groups[1].Value;
                int number = int.Parse(match.Groups[2].Value);
                if (!groups.TryGetValue(prefix, out var values)) groups[prefix] = values = new List<int>();
                values.Add(number);
            }

            foreach (var pair in groups)
            {
                pair.Value.Sort();
                for (int i = 0; i < pair.Value.Count; i++)
                    Assert.AreEqual(i + 1, pair.Value[i], pair.Key + " contains a numbering gap or collision.");
            }
            CollectionAssert.AreEqual(Enumerable.Range(1, 28), groups["PAS"]);
            CollectionAssert.AreEqual(Enumerable.Range(1, 5), groups["PAA"]);
            CollectionAssert.AreEqual(Enumerable.Range(1, 2), groups["PAP"]);
        }

        [TestCase("PRJ010", "PRJ009")]
        [TestCase("PRJ009", null)]
        [TestCase("PRJ014", "PRJ013")]
        [TestCase("PRJ015", "PRJ014")]
        [TestCase("PAS1011", "PAS027")]
        [TestCase("PAA3001", "PAA004")]
        [TestCase("PAP0001", "PAP001")]
        [TestCase("PAS0003", "PAS001")]
        [TestCase("VID008", "VID007")]
        [TestCase("PAS1007", "PRJ008")]
        [TestCase("VID007", "VID001")]
        [TestCase("PRJ077", "PRJ013")]
        [TestCase("PAS0031", "PRJ030")]
        public void PreviousSchemaIdsMapToTheIntendedCurrentId(string oldId, string currentId)
        {
            Assert.AreEqual(currentId, RuleIdMigration.MapFromPreviousSchema(oldId));
        }

        [TestCase("PAA0001", "PAA001")]
        [TestCase("PAA0002", "PAA002")]
        [TestCase("PAA0003", "PAA003")]
        [TestCase("PAA3001", "PAA004")]
        [TestCase("PAA3002", "PAA005")]
        [TestCase("PAP0001", "PAP001")]
        [TestCase("PAP0002", "PAP002")]
        [TestCase("PAS0001", "PAS001")]
        [TestCase("PAS0002", "PAS002")]
        [TestCase("PAS0003", "PAS003")]
        [TestCase("PAS0004", "PAS004")]
        [TestCase("PAS0005", "PAS005")]
        [TestCase("PAS0006", "PAS006")]
        [TestCase("PAS0007", "PAS007")]
        [TestCase("PAS0008", "PAS008")]
        [TestCase("PAS0009", "PAS009")]
        [TestCase("PAS0010", "PAS010")]
        [TestCase("PAS0011", "PAS011")]
        [TestCase("PAS0012", "PAS012")]
        [TestCase("PAS0013", "PAS013")]
        [TestCase("PAS0014", "PAS014")]
        [TestCase("PAS0015", "PAS015")]
        [TestCase("PAS0016", "PAS016")]
        [TestCase("PAS0017", "PAS017")]
        [TestCase("PAS0018", "PAS018")]
        [TestCase("PAS0019", "PAS019")]
        [TestCase("PAS1000", "PAS020")]
        [TestCase("PAS1001", "PAS021")]
        [TestCase("PAS1002", "PAS022")]
        [TestCase("PAS1003", "PAS023")]
        [TestCase("PAS1004", "PAS024")]
        [TestCase("PAS1005", "PAS025")]
        [TestCase("PAS1006", "PAS026")]
        [TestCase("PAS1007", "PAS027")]
        [TestCase("PAS1008", "PAS028")]
        [TestCase("PAS0031", "PRJ030")]
        [TestCase("PRJ077", "PRJ013")]
        [TestCase("PRJ078", "PRJ014")]
        [TestCase("PRJ081", "PRJ059")]
        [TestCase("VID007", "VID007")]
        public void SchemaOneIdsMapToTheFlattenedCatalog(string schemaOneId, string currentId)
        {
            Assert.AreEqual(currentId, RuleIdMigration.MapFromSchema(1, schemaOneId));
        }

        [Test]
        public void SchemaOneShortIdsThatWereNotAssignedAreDiscarded()
        {
            foreach (string id in new[]
            {
                "PAA001", "PAA002", "PAA003", "PAA004", "PAA005", "PAP001", "PAP002",
                "PAS001", "PAS002", "PAS003", "PAS004", "PAS005", "PAS006", "PAS007",
                "PAS008", "PAS009", "PAS010", "PAS011", "PAS012", "PAS013", "PAS014",
                "PAS015", "PAS016", "PAS017", "PAS018", "PAS019", "PAS020", "PAS021",
                "PAS022", "PAS023", "PAS024", "PAS025", "PAS026", "PAS027", "PAS028"
            })
                Assert.IsNull(RuleIdMigration.MapFromSchema(1, id), id + " was not assigned in schema one and must not be applied to a new rule.");
        }

        [Test]
        public void PreviouslyUnassignedReclaimedNumbersAreDiscardedFromSchemaZeroData()
        {
            foreach (string id in new[]
            {
                "CTL007", "PAA0001", "PAA0002", "PAA0003", "PAS0001", "PAS0002",
                "PAS0007", "PAS0008", "PAS0009", "PAS0010", "PAS0011", "PAS0012",
                "PAS0013", "PAS0014", "PAS1001", "PAS1002", "PAS1008", "PRJ009",
                "PRJ033", "PRJ053", "PRJ061", "PAA001", "PAA002", "PAA003", "PAA004", "PAA005",
                "PAP001", "PAP002", "PAS001", "PAS002", "PAS003", "PAS004", "PAS005", "PAS006",
                "PAS007", "PAS008", "PAS009", "PAS010", "PAS011", "PAS012", "PAS013", "PAS014",
                "PAS015", "PAS016", "PAS017", "PAS018", "PAS019", "PAS020", "PAS021", "PAS022",
                "PAS023", "PAS024", "PAS025", "PAS026", "PAS027", "PAS028"
            })
                Assert.IsNull(RuleIdMigration.MapFromPreviousSchema(id), id + " was unassigned in schema zero and must not transfer old preferences to a new rule.");
        }

        [Test]
        public void SettingsMigrationIsCollisionSafeAndPrefersCanonicalOverrides()
        {
            var settings = new OptimizerSettings
            {
                ruleIdSchemaVersion = 0,
                disabledRules = new List<string> { "PRJ014", "PRJ077", "PAS1007", "PAS1011", "PRJ009" },
                severityOverrideIds = new List<string> { "PRJ077", "PRJ014" },
                severityOverrideValues = new List<int> { (int)IssueSeverity.Info, (int)IssueSeverity.Critical },
                suppressions = new List<RuleSuppression>
                {
                    new RuleSuppression { RuleId = "PRJ077", AssetPath = "Assets/Target.asset", Reason = "alias reason" },
                    new RuleSuppression { RuleId = "PRJ014", AssetPath = "Assets/Target.asset", Reason = "canonical reason" }
                }
            };

            Assert.IsTrue(RuleIdMigration.MigrateSettings(settings));
            Assert.AreEqual(RuleIdMigration.CurrentSchemaVersion, settings.ruleIdSchemaVersion);
            CollectionAssert.AreEquivalent(new[] { "PRJ013", "PRJ008", "PAS027" }, settings.disabledRules);
            Assert.AreEqual(1, settings.severityOverrideIds.Count);
            Assert.AreEqual("PRJ013", settings.severityOverrideIds[0]);
            Assert.AreEqual((int)IssueSeverity.Critical, settings.severityOverrideValues[0]);
            Assert.AreEqual(1, settings.suppressions.Count);
            Assert.AreEqual("PRJ013", settings.suppressions[0].RuleId);
            Assert.AreEqual("canonical reason", settings.suppressions[0].Reason);
            Assert.IsFalse(RuleIdMigration.MigrateSettings(settings), "A current-schema settings file must not be migrated twice.");
        }

        [Test]
        public void SchemaOneSettingsMigrateOnlyFromSchemaOneAndDoNotMisreadReusedIds()
        {
            var settings = new OptimizerSettings
            {
                ruleIdSchemaVersion = 1,
                disabledRules = new List<string> { "PAS1007", "PAA3001", "PAS0031", "PRJ077", "VID007" }
            };

            Assert.IsTrue(RuleIdMigration.MigrateSettings(settings));
            Assert.AreEqual(2, settings.ruleIdSchemaVersion);
            CollectionAssert.AreEquivalent(new[] { "PAS027", "PAA004", "PRJ030", "PRJ013", "VID007" }, settings.disabledRules);
        }

        [Test]
        public void OlderBaselineFormatMigratesAndPreservesSchemaOneIdentifiers()
        {
            var baseline = new OptimizationBaseline
            {
                Version = 2,
                RuleIdSchemaVersion = 1,
                Complete = true,
                Rules = new List<OptimizationBaselineRule>
                {
                    new OptimizationBaselineRule { RuleId = "PRJ010", Count = 3 }
                }
            };

            Assert.IsTrue(RuleIdMigration.MigrateBaseline(baseline));
            Assert.AreEqual(3, baseline.Version);
            Assert.AreEqual("PRJ010", baseline.Rules[0].RuleId);
        }

        [Test]
        public void BaselineMigrationRekeysIdsAndAvoidsCountingAliasDuplicatesTwice()
        {
            var baseline = new OptimizationBaseline
            {
                Version = 2,
                RuleIdSchemaVersion = 0,
                ToolSchema = "11",
                Complete = true,
                Rules = new List<OptimizationBaselineRule>
                {
                    new OptimizationBaselineRule { RuleId = "PRJ008", Count = 10 },
                    new OptimizationBaselineRule { RuleId = "PRJ009", Count = 999 },
                    new OptimizationBaselineRule { RuleId = "PRJ010", Count = 5 },
                    new OptimizationBaselineRule { RuleId = "PAS1007", Count = 3 },
                    new OptimizationBaselineRule { RuleId = "PAS1011", Count = 5 },
                    new OptimizationBaselineRule { RuleId = "VID001", Count = 7 },
                    new OptimizationBaselineRule { RuleId = "VID007", Count = 4 },
                    new OptimizationBaselineRule { RuleId = "VID008", Count = 2 }
                }
            };

            Assert.IsTrue(RuleIdMigration.MigrateBaseline(baseline));
            Assert.AreEqual(3, baseline.Version);
            Assert.AreEqual(RuleIdMigration.CurrentSchemaVersion, baseline.RuleIdSchemaVersion);
            var counts = baseline.Rules.ToDictionary(rule => rule.RuleId, rule => rule.Count, StringComparer.Ordinal);
            Assert.AreEqual(10, counts["PRJ008"], "The old alias must not inflate the canonical rule count.");
            Assert.AreEqual(5, counts["PRJ009"], "The unassigned old PRJ009 value is discarded while old PRJ010 maps to current PRJ009.");
            Assert.AreEqual(5, counts["PAS027"], "The old PAS1011 rule is assigned the current PAS027 ID.");
            Assert.AreEqual(7, counts["VID001"], "The old VID007 alias must not inflate VID001.");
            Assert.AreEqual(2, counts["VID007"], "The old VID008 rule is assigned the current VID007 ID.");
            Assert.AreEqual("11", baseline.ToolSchema, "Keep the old tool schema so comparisons remain directional only.");
        }
    }
}
