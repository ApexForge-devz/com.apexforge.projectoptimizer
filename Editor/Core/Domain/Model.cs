using System;
using System.Collections.Generic;
using System.IO;

namespace ApexForge.ProjectOptimizer
{
    /// <summary>How badly a rule violation hurts. Ordered so numeric comparison == priority.</summary>
    public enum IssueSeverity
    {
        Info = 0,
        Warning = 1,
        Critical = 2
    }

    /// <summary>
    /// How well the analyzer can stand behind a finding, which is a different question from how much
    /// the finding hurts.
    /// <para>
    /// <see cref="IssueSeverity"/> answers "if this is true, how bad is it"; this answers "how sure
    /// are we that it is true, and that acting on it is safe". A missing script is Critical and
    /// <see cref="Proven"/>; a Debug.Log inside Update is Info and only <see cref="Diagnostic"/>;
    /// "GetComponent in Update" is a real cost and still only a <see cref="Heuristic"/>, because a
    /// text scan cannot tell a hot path from one that returns on the first line.
    /// </para>
    /// <para>
    /// The two were previously conflated, which is how a naming heuristic ended up carrying the same
    /// weight as a measured fact. Automatic fixes are gated on this and never on severity - see
    /// <see cref="MinimumForAutomaticFix"/>.
    /// </para>
    /// </summary>
    public enum OptimizationConfidence
    {
        /// <summary>Reported so a human can look. The tool is not claiming anything is wrong.</summary>
        Diagnostic = 0,
        /// <summary>A pattern that is usually a problem, inferred from names, text or shape.</summary>
        Heuristic = 1,
        /// <summary>Read off the asset or importer. True unless Unity's own API lied.</summary>
        HighConfidence = 2,
        /// <summary>Established by direct comparison - identical bytes, a resolved null, a mapped id.</summary>
        Proven = 3
    }

    /// <summary>
    /// How the product lets a fix be applied, which is a different question from how sure the
    /// analyzer is or how much the finding costs.
    /// <para>
    /// <see cref="OptimizationConfidence"/> answers "how sure are we this is true and that acting on
    /// it is safe"; this answers "what is the user allowed to do about it". A rule can be
    /// <see cref="OptimizationConfidence.HighConfidence"/> on the fact - "Read/Write is enabled" is
    /// simply read off the importer - while the fix for it stays <see cref="Review"/>, because
    /// disabling Read/Write can break a runtime CPU-mesh read the analyzer has no way to see. The two
    /// must never be collapsed into one number.
    /// </para>
    /// </summary>
    public enum FixKind
    {
        /// <summary>No action the tool can construct with enough confidence to offer at all.</summary>
        None = 0,
        /// <summary>
        /// A finding that requires human review. It may expose an executable action/options, or it may
        /// intentionally provide manual guidance only. Never included in Fix All.
        /// </summary>
        Review = 1,
        /// <summary>Safe enough to apply without a human looking first. Eligible for Fix All.</summary>
        Verified = 2
    }

    /// <summary>Risk of the concrete action, independent from finding severity and confidence.</summary>
    public enum FixRisk
    {
        Unspecified = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Destructive = 4
    }

    /// <summary>
    /// How a batch of fixes came to be requested, so <see cref="FixEngine"/> never has to infer intent
    /// from a bare boolean.
    /// <para>
    /// The distinction that matters is not "how many assets" but "who chose them and why": Fix All /
    /// Fix Selected pick every eligible Verified issue in view with no per-issue approval, while a
    /// Review batch is the user picking the assets, picking the one action, and approving the batch -
    /// see <see cref="ReviewActionDescriptor"/>. Both still run through the identical transaction; this
    /// only changes which <see cref="OptimizationIssue.FixKind"/> is allowed through the gate and how
    /// the result is reported.
    /// </para>
    /// </summary>
    public enum FixExecutionMode
    {
        /// <summary>Fix All / Fix all shown: every Verified issue currently in view, tool-selected.</summary>
        VerifiedBulk = 0,
        /// <summary>One Verified issue's own "Verified Fix" button.</summary>
        VerifiedSingle = 1,
        /// <summary>One issue's "Review Fix" button, for one asset.</summary>
        ReviewSingle = 2,
        /// <summary>An explicitly chosen Review action applied across an explicit multi-asset selection.</summary>
        ReviewBatch = 3
    }

    /// <summary>
    /// One candidate state a Review Fix can move an asset to, when there is more than one reasonable
    /// choice - a mesh compression level, a texture max size, an audio load type. Each option is a
    /// complete, deterministic action in its own right; picking one never touches what the others
    /// would have changed.
    /// </summary>
    public sealed class FixOption
    {
        /// <summary>Stable machine-readable action identity. When omitted, a deterministic legacy id is derived from the label.</summary>
        public string ActionId;

        /// <summary>Short label for the picker, e.g. "Cull Completely" or "Max Size 1024".</summary>
        public string Label;

        /// <summary>One line describing the state this option leaves the asset in.</summary>
        public string ResultDescription;

        /// <summary>Why this option might not be right for every asset that reaches it.</summary>
        public string Warning;

        /// <summary>True for the option the analyzer thinks most projects want, for the picker's default.</summary>
        public bool Recommended;

        /// <summary>Optional explicit backup requirement. Null means destructive => required, otherwise false.</summary>
        public bool? BackupRequiredOverride;

        /// <summary>Risk of this exact option, separate from finding confidence and severity.</summary>
        public FixRisk Risk = FixRisk.Unspecified;

        /// <summary>Best-effort savings if this option, specifically, is applied. 0 when unknown.</summary>
        public long EstimatedSavings;

        public SavingsBasis Basis = SavingsBasis.Unknown;

        /// <summary>Mutates the asset to this option's state. Required.</summary>
        public Action<FixContext> Apply;

        /// <summary>Confirms the option actually took, the same way <see cref="OptimizationIssue.Verify"/> does.</summary>
        public Func<FixContext, bool> Verify;

        public string StableActionId(string ruleId)
        {
            if (!string.IsNullOrEmpty(ActionId)) return ActionId;
            return NormalizeActionId(ruleId, Label);
        }

        public FixRisk EffectiveRisk(FixRisk fallback) =>
            Risk != FixRisk.Unspecified ? Risk : (fallback != FixRisk.Unspecified ? fallback : FixRisk.Medium);
        [Obsolete("Pass the already-resolved effective risk to EffectiveBackupRequired instead of inferring it from the option alone.")]
        public bool EffectiveBackupRequired(bool inheritedBackupRequired, bool destructive) => (BackupRequiredOverride ?? inheritedBackupRequired) || destructive || Risk >= FixRisk.High;
        public bool EffectiveBackupRequired(bool inheritedBackupRequired, FixRisk effectiveRisk) => (BackupRequiredOverride ?? inheritedBackupRequired) || effectiveRisk >= FixRisk.High;
        public long EffectiveSavings => Basis == SavingsBasis.Unknown ? 0L : Math.Max(0L, EstimatedSavings);

        static string NormalizeActionId(string ruleId, string label)
        {
            string seed = (ruleId ?? "rule") + "_" + (label ?? "action");
            var chars = new char[seed.Length];
            int count = 0;
            bool previousSeparator = false;
            for (int i = 0; i < seed.Length; i++)
            {
                char c = char.ToLowerInvariant(seed[i]);
                bool valid = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (valid)
                {
                    chars[count++] = c;
                    previousSeparator = false;
                }
                else if (!previousSeparator)
                {
                    chars[count++] = '_';
                    previousSeparator = true;
                }
            }
            while (count > 0 && chars[count - 1] == '_') count--;
            return new string(chars, 0, count);
        }
    }

    /// <summary>Logical bucket an asset belongs to. Tabs map to one or more of these.</summary>
    public enum AssetCategory
    {
        Texture,
        Audio,
        Model,
        Material,
        Shader,
        Animation,
        Prefab,
        Scene,
        Script,
        Font,
        SpriteAtlas,
        Video,
        Controller,
        Duplicate,
        Unused,
        ProjectSetting,
        Other,
        Build,
        Package,
        BuildStep,
        BuildFile,
        Addressables,
        UIToolkit,
        Rendering
    }

    /// <summary>Shared execution-contract helpers used by the catalog, review UI, and FixEngine.</summary>
    public static class FixContract
    {
        public static bool MeetsReviewConfidence(OptimizationConfidence confidence) => confidence >= RuleCatalog.MinimumForReviewFix;
        public static bool MeetsAutomaticConfidence(OptimizationConfidence confidence) => confidence >= RuleCatalog.MinimumForAutomaticFix;
        public static bool RequiresPostconditionVerifier(FixRisk risk) => risk >= FixRisk.High;

        public static long EffectiveSavings(OptimizationIssue issue, FixOption option)
        {
            if (option != null)
                return option.Basis == SavingsBasis.Unknown ? 0L : Math.Max(0L, option.EstimatedSavings);
            if (issue == null || issue.Basis == SavingsBasis.Unknown) return 0L;
            return Math.Max(0L, issue.EstimatedSavings);
        }

        public static SavingsBasis EffectiveSavingsBasis(OptimizationIssue issue, FixOption option)
            => option != null ? option.Basis : issue?.Basis ?? SavingsBasis.Unknown;

        public static bool EffectiveBackupRequired(OptimizationIssue issue, FixOption option)
        {
            FixRisk inheritedRisk = issue?.Risk ?? FixRisk.Unspecified;
            if (inheritedRisk == FixRisk.Unspecified) inheritedRisk = FixRisk.Medium;
            if (option == null) return issue != null && (issue.BackupRequired || inheritedRisk >= FixRisk.High);
            return option.EffectiveBackupRequired(issue?.BackupRequired ?? false, option.EffectiveRisk(inheritedRisk));
        }

        public static Func<FixContext, bool> EffectiveVerify(OptimizationIssue issue, FixOption option)
        {
            if (option == null) return issue?.Verify;
            FixRisk inheritedRisk = issue?.Risk ?? FixRisk.Unspecified;
            if (inheritedRisk == FixRisk.Unspecified) inheritedRisk = FixRisk.Medium;
            FixRisk risk = option.EffectiveRisk(inheritedRisk);
            return RequiresPostconditionVerifier(risk) ? option.Verify : option.Verify ?? issue?.Verify;
        }

        /// <summary>Caps estimated savings against the actual per-asset resource pool, preventing impossible totals without inventing an overlap curve.</summary>
        public static long AddCappedSavings(ref long memorySaved, ref long diskSaved, long memoryBase, long diskBase, OptimizationIssue issue, FixOption option)
        {
            long saving = EffectiveSavings(issue, option);
            var basisKind = EffectiveSavingsBasis(issue, option);
            if (saving <= 0 || basisKind == SavingsBasis.Unknown) return 0L;
            long basis = basisKind == SavingsBasis.Disk ? diskBase : memoryBase;
            if (basis <= 0) return 0L;
            long alreadySaved = basisKind == SavingsBasis.Disk ? diskSaved : memorySaved;
            long remaining = Math.Max(0L, basis - Math.Min(basis, alreadySaved));
            long applied = Math.Min(saving, remaining);
            if (basisKind == SavingsBasis.Disk) diskSaved += applied;
            else memorySaved += applied;
            return applied;
        }

        public static bool EvidenceAllowsAutomatic(OptimizationIssue issue)
        {
            if (issue == null || issue.EvidenceState != EvidenceState.Fresh) return false;

            switch (issue.RequiredEvidence)
            {
                case EvidenceRequirement.CrossAssetFresh:
                    return EvidenceContract.IsCrossAssetEvidenceCurrent(issue.EvidenceSignature);
                case EvidenceRequirement.ProjectProofComplete:
                    return EvidenceContract.IsProofEvidenceCurrent(issue.EvidenceSignature);
                case EvidenceRequirement.RuntimeBuildIdentity:
                    {
                        var runtime = RuntimeEvidenceStore.LoadLatest();
                        return runtime != null && runtime.EvidenceState == EvidenceState.Fresh
                            && string.Equals(runtime.ContentIdentity, issue.EvidenceSignature, StringComparison.Ordinal);
                    }
                case EvidenceRequirement.AddressablesAnalyzeFresh:
                    {
                        var addressables = AddressablesAnalyzeEvidenceStore.LoadLatest();
                        return addressables != null && addressables.EvidenceState == EvidenceState.Fresh
                            && string.Equals(addressables.ContentIdentity, issue.EvidenceSignature, StringComparison.Ordinal);
                    }
                case EvidenceRequirement.BuildLayoutMetrics:
                    // Build Layout evidence is currently produced by an ephemeral report object,
                    // not a persisted/live evidence store that can be revalidated here. Fail closed
                    // rather than treating the presence of an old signature as proof.
                    return false;
                case EvidenceRequirement.None:
                default:
                    // Every automatic asset mutation carries the identity of the asset it analyzed.
                    // That identity is checked again at execution time so a file edited after scan
                    // cannot retain a Verified button merely because no scanner callback ran yet.
                    return EvidenceContract.IsAssetEvidenceCurrent(issue.AssetEvidencePath, issue.AssetEvidenceSignature);
            }
        }

        public static string EvidenceFailureReason(OptimizationIssue issue)
        {
            if (issue == null) return "The finding is missing.";
            if (issue.EvidenceState != EvidenceState.Fresh) return "Required evidence is " + issue.EvidenceState + " rather than Fresh.";
            return "Required evidence is no longer current for the project state.";
        }

        static bool SemanticContractMatchesCurrent(OptimizationIssue issue)
        {
            if (issue == null) return false;
            if (!string.Equals(issue.RuleCatalogSignature, RuleCatalog.SemanticSignature, StringComparison.Ordinal)) return false;
            if (!string.Equals(issue.AnalyzerSignature, EvidenceContract.AnalyzerSignature, StringComparison.Ordinal)) return false;
            return string.Equals(issue.FixContractSignature, EvidenceContract.FixSignature, StringComparison.Ordinal);
        }

        public static bool CanExecute(FixExecutionMode mode, OptimizationIssue issue, FixOption option, out string reason)
        {
            reason = null;
            if (issue == null) { reason = "The finding is missing."; return false; }
            if (issue.Stale) { reason = "The finding is stale and must be rescanned."; return false; }

            bool reviewMode = mode == FixExecutionMode.ReviewSingle || mode == FixExecutionMode.ReviewBatch;
            bool automaticMode = mode == FixExecutionMode.VerifiedSingle || mode == FixExecutionMode.VerifiedBulk;
            if (!reviewMode && !automaticMode) { reason = "The fix execution mode is invalid."; return false; }

            if (automaticMode)
            {
                if (issue.FixKind != FixKind.Verified) { reason = "Automatic execution accepts Verified findings only."; return false; }
                if (!MeetsAutomaticConfidence(issue.Confidence) || issue.RecommendationConfidence < RuleCatalog.MinimumForAutomaticFix) { reason = "The finding or recommendation is below the automatic-fix confidence floor."; return false; }
                if (!EvidenceAllowsAutomatic(issue)) { reason = EvidenceFailureReason(issue); return false; }
                if (!SemanticContractMatchesCurrent(issue)) { reason = "The rule/analyzer/fix semantics changed since this finding was produced."; return false; }
                if (option != null) { reason = "Automatic execution cannot choose Review options."; return false; }
                if (issue.Fix == null) { reason = "The Verified finding has no executable fix."; return false; }
                if (issue.Verify == null) { reason = "A Verified fix requires a postcondition verifier."; return false; }
                if (issue.Risk >= FixRisk.High && !issue.BackupRequired) { reason = "High-risk Verified fixes require a backup."; return false; }
                return true;
            }

            if (issue.FixKind != FixKind.Review) { reason = "The finding is not a Review Fix."; return false; }
            if (!MeetsReviewConfidence(issue.Confidence)) { reason = "The finding is below the Review confidence floor."; return false; }
            if (option != null)
            {
                if (issue.Options == null || !issue.Options.Contains(option)) { reason = "The selected Review option does not belong to the finding."; return false; }
            }

            Action<FixContext> apply = option?.Apply ?? issue.Fix;
            Func<FixContext, bool> verify = EffectiveVerify(issue, option);
            FixRisk risk = option?.EffectiveRisk(issue.Risk) ?? issue.Risk;
            if (risk == FixRisk.Unspecified) risk = FixRisk.Medium;
            if (apply == null) { reason = "The Review action has no executable delegate."; return false; }
            if (RequiresPostconditionVerifier(risk) && verify == null)
            {
                reason = "High-risk Review actions require a postcondition verifier.";
                return false;
            }
            return true;
        }
    }

    public sealed class RuleDef
    {
        public string Id;
        public string Title;
        public string Why;
        public string Fix;
        public AssetCategory Category;
        public IssueSeverity DefaultSeverity;

        /// <summary>
        /// The fix kind declared for every finding produced by this rule. None means no executable fix;
        /// Review means the fix requires explicit human approval; Verified means the fix is safe for
        /// automatic execution.
        /// </summary>
        public FixKind FixKind;

        /// <summary>
        /// How far the analyzer can stand behind this rule. Automatic fixes are refused below
        /// <see cref="OptimizationConfidence.HighConfidence"/>; see <see cref="RuleCatalog.MinimumForAutomaticFix"/>.
        /// Review fixes are refused below <see cref="OptimizationConfidence.Heuristic"/>; see
        /// <see cref="RuleCatalog.MinimumForReviewFix"/>.
        /// </summary>
        public OptimizationConfidence Confidence = OptimizationConfidence.Heuristic;

        /// <summary>Default action risk for a one-action rule. Concrete Review options may override it.</summary>
        public FixRisk DefaultFixRisk = FixRisk.Unspecified;

        /// <summary>How strongly the recommended mutation itself is supported, independent of finding detection confidence.</summary>
        public OptimizationConfidence RecommendationConfidence = OptimizationConfidence.Heuristic;

        /// <summary>Evidence gate required before the concrete action can execute automatically.</summary>
        public EvidenceRequirement RequiredEvidence = EvidenceRequirement.None;

        /// <summary>Semantic contract version for this rule family.</summary>
        public string SemanticVersion = "1";

        /// <summary>Default backup policy for this rule's concrete action.</summary>
        public bool DefaultBackupRequired;

        string m_Summary;

        /// <summary>
        /// One crisp line for list views: the opening sentence of <see cref="Why"/>, which is
        /// always written to carry the point on its own.
        /// </summary>
        public string Summary
        {
            get
            {
                if (m_Summary != null) return m_Summary;
                m_Summary = FirstSentence(Why);
                return m_Summary;
            }
        }

        static string FirstSentence(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            // Split on ". " so decimals such as "0.5" and versions do not end the sentence.
            int end = text.IndexOf(". ", StringComparison.Ordinal);
            var sentence = end > 0 ? text.Substring(0, end + 1) : text;

            const int limit = 190;
            if (sentence.Length > limit)
            {
                int cut = sentence.LastIndexOf(' ', limit - 1);
                sentence = sentence.Substring(0, cut > 40 ? cut : limit).TrimEnd(',', ';', ' ') + "...";
            }
            return sentence;
        }
    }

    /// <summary>Every rule the tool knows about, keyed by id.</summary>
    public static class RuleCatalog
    {
        static readonly Dictionary<string, RuleDef> s_Rules = new Dictionary<string, RuleDef>();
        static readonly List<RuleDef> s_Ordered = new List<RuleDef>();
        // Legacy IDs remain resolvable for persisted scan results and settings, but do not appear as
        // independent entries in the active catalogue/UI.
        static readonly Dictionary<string, string> s_Aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly Dictionary<string, IReadOnlyList<string>> s_AliasesByCanonical = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        static string s_SemanticSignature;

        public static IReadOnlyList<RuleDef> All { get { RuleBootstrap.EnsureBuiltInRules(); return s_Ordered; } }

        public static string SemanticSignature
        {
            get
            {
                RuleBootstrap.EnsureBuiltInRules();
                if (s_SemanticSignature == null) s_SemanticSignature = EvidenceContract.ComputeRuleCatalogSignature(s_Ordered, s_Aliases);
                return s_SemanticSignature;
            }
        }

        /// <summary>
        /// The floor an automatic fix has to clear. A fix acts on the analyzer's word without anyone
        /// checking it first, so a rule that can only guess must not carry one - a heuristic that is
        /// right nine times in ten is a useful warning and an unacceptable edit.
        /// </summary>
        public const OptimizationConfidence MinimumForAutomaticFix = OptimizationConfidence.HighConfidence;

        /// <summary>
        /// The floor a Review finding has to clear. Lower than the Verified floor on purpose - a human
        /// may inspect a heuristic/manual-guidance finding before deciding what to do. Diagnostic-only
        /// findings still do not carry executable actions.
        /// </summary>
        public const OptimizationConfidence MinimumForReviewFix = OptimizationConfidence.Heuristic;

        public static void Register(RuleDef def)
        {
            if (def == null || string.IsNullOrEmpty(def.Id)) return;
            if (s_Aliases.ContainsKey(def.Id))
            {
                UnityEngine.Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Rule id '" + def.Id + "' is already a legacy alias and cannot be registered as an active rule.");
                return;
            }
            if (s_Rules.ContainsKey(def.Id))
            {
                // Silently keeping the first definition is how two rules end up sharing an id and one
                // of them quietly stops being configurable.
                UnityEngine.Debug.LogWarning(
                    ProjectOptimizerConstants.LogPrefix + " Duplicate rule id '" + def.Id + "'; the later registration was ignored.");
                return;
            }

            if (def.RecommendationConfidence == OptimizationConfidence.Heuristic) def.RecommendationConfidence = def.Confidence;

            if (def.FixKind == FixKind.Review && def.Confidence < MinimumForReviewFix)
            {
                UnityEngine.Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Rule '" + def.Id + "' declares Review below the " + MinimumForReviewFix + " confidence floor; the executable fix kind has been downgraded to None.");
                def.FixKind = FixKind.None;
                def.DefaultFixRisk = FixRisk.Unspecified;
                def.DefaultBackupRequired = false;
            }
            else if (def.FixKind == FixKind.Verified && def.Confidence < MinimumForAutomaticFix)
            {
                UnityEngine.Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Rule '" + def.Id + "' declares Verified below the " + MinimumForAutomaticFix + " confidence floor; the executable fix kind has been downgraded to None.");
                def.FixKind = FixKind.None;
                def.DefaultFixRisk = FixRisk.Unspecified;
                def.DefaultBackupRequired = false;
            }

            if (def.Category == AssetCategory.ProjectSetting && def.FixKind != FixKind.None)
                def.DefaultBackupRequired = true;
            if (def.FixKind != FixKind.None && def.DefaultFixRisk >= FixRisk.High)
                def.DefaultBackupRequired = true;

            s_Rules[def.Id] = def;
            s_Ordered.Add(def);
            s_SemanticSignature = null;
        }

        /// <summary>
        /// Registers a legacy rule ID as an alias of an active rule. Persisted findings and settings
        /// can still resolve the old ID, but it is excluded from active rule enumeration.
        /// </summary>
        public static bool RegisterAlias(string aliasId, string canonicalId)
        {
            if (string.IsNullOrWhiteSpace(aliasId) || string.IsNullOrWhiteSpace(canonicalId) ||
                string.Equals(aliasId, canonicalId, StringComparison.Ordinal))
                return false;

            if (!s_Rules.ContainsKey(canonicalId))
            {
                UnityEngine.Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Cannot register rule alias '" + aliasId + "': canonical rule '" + canonicalId + "' is not registered.");
                return false;
            }

            if (s_Rules.ContainsKey(aliasId))
            {
                UnityEngine.Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Cannot register rule alias '" + aliasId + "': that ID is already an active rule.");
                return false;
            }

            if (s_Aliases.TryGetValue(aliasId, out var existing))
                return string.Equals(existing, canonicalId, StringComparison.Ordinal);

            // Avoid alias chains: an alias must resolve directly to a canonical active rule.
            if (s_Aliases.ContainsKey(canonicalId)) return false;

            s_Aliases.Add(aliasId, canonicalId);
            var aliases = new List<string>();
            if (s_AliasesByCanonical.TryGetValue(canonicalId, out var existingAliases))
                aliases.AddRange(existingAliases);
            aliases.Add(aliasId);
            aliases.Sort(StringComparer.Ordinal);
            s_AliasesByCanonical[canonicalId] = aliases.AsReadOnly();
            s_SemanticSignature = null;
            return true;
        }

        /// <summary>Returns the canonical ID for a known legacy alias, or the input ID unchanged.</summary>
        public static string CanonicalizeId(string id)
        {
            RuleBootstrap.EnsureBuiltInRules();
            if (string.IsNullOrEmpty(id)) return id;
            return s_Aliases.TryGetValue(id, out var canonicalId) ? canonicalId : id;
        }

        public static bool IsAlias(string id)
        {
            RuleBootstrap.EnsureBuiltInRules();
            return !string.IsNullOrEmpty(id) && s_Aliases.ContainsKey(id);
        }

        public static bool IsAliasFor(string aliasId, string canonicalId)
        {
            RuleBootstrap.EnsureBuiltInRules();
            return !string.IsNullOrEmpty(aliasId) && !string.IsNullOrEmpty(canonicalId)
                   && s_Aliases.TryGetValue(aliasId, out var target)
                   && string.Equals(target, canonicalId, StringComparison.Ordinal);
        }

        /// <summary>A copy of the alias map for deterministic auditing; callers cannot mutate catalogue state.</summary>
        public static IDictionary<string, string> GetAliasMappings()
        {
            RuleBootstrap.EnsureBuiltInRules();
            return new Dictionary<string, string>(s_Aliases, StringComparer.Ordinal);
        }

        public static IReadOnlyList<string> GetAliasesFor(string canonicalId)
        {
            RuleBootstrap.EnsureBuiltInRules();
            return !string.IsNullOrEmpty(canonicalId) && s_AliasesByCanonical.TryGetValue(canonicalId, out var aliases)
                ? aliases
                : (IReadOnlyList<string>)Array.Empty<string>();
        }

        public static RuleDef Get(string id)
        {
            RuleBootstrap.EnsureBuiltInRules();
            if (id != null && s_Aliases.TryGetValue(id, out var canonicalId)) id = canonicalId;
            return id != null && s_Rules.TryGetValue(id, out var def) ? def : null;
        }

        public static bool Exists(string id)
        {
            RuleBootstrap.EnsureBuiltInRules();
            return id != null && (s_Rules.ContainsKey(id) || s_Aliases.ContainsKey(id));
        }

        /// <summary>Structured hover text: what the rule is, why it matters, and what fixing it does.</summary>
        public static string Describe(string ruleId, IssueSeverity? severity = null)
        {
            var def = Get(ruleId);
            if (def == null) return ruleId;

            var level = severity ?? def.DefaultSeverity;
            var header = def.Id + "   \u00b7   " + DisplayUtility.SeverityLabel(level) + "   \u00b7   "
                         + DisplayUtility.ConfidenceLabel(def.Confidence) + "   \u00b7   "
                         + (def.FixKind == FixKind.Verified ? "verified fix" : def.FixKind == FixKind.Review ? "review fix" : "no fix");

            return header + "\n" + def.Title
                   + "\n\nWHY IT MATTERS\n" + def.Why
                   + "\n\nWHAT THE FIX DOES\n" + def.Fix;
        }
    }

    /// <summary>A single problem found on a single asset.</summary>
    /// <summary>
    /// Which pool a fix takes its saving out of. Two fixes on the same asset overlap when they draw
    /// on the same pool - dropping a texture to a quarter of its resolution and then compressing it
    /// does not save the sum of the two - so the pool has to be known before the savings can be
    /// added up honestly.
    /// </summary>
    public enum SavingsBasis
    {
        /// <summary>Bytes resident at runtime.</summary>
        Memory = 0,
        /// <summary>Bytes in the source file, and so in the build and in version control.</summary>
        Disk = 1,
        /// <summary>
        /// The fix helps, but by an amount this analyzer cannot compute. Savings are forced to zero
        /// rather than guessed: an invented figure is worse than an honest blank, because it gets
        /// added into a total someone then plans around.
        /// </summary>
        Unknown = 2
    }

    /// <summary>What one line of an issue's evidence means, which is what decides how it is coloured.</summary>
    public enum DetailKind
    {
        /// <summary>Plain supporting text.</summary>
        Note = 0,
        /// <summary>The copy being kept, or the state the fix is aiming at.</summary>
        Good = 1,
        /// <summary>The thing at fault: the duplicate to delete, the broken reference, the slow call.</summary>
        Bad = 2
    }

    /// <summary>
    /// One labelled line of evidence under an issue.
    /// <para>
    /// Kept as data rather than as a paragraph of prepared text so the window can lay it out: an
    /// asset path reads far better on its own line, coloured by what it is and clickable, than
    /// buried mid-sentence in a wrapped block where it also breaks in the wrong places.
    /// </para>
    /// </summary>
    public sealed class IssueDetail
    {
        public string Label;
        public string Text;
        public DetailKind Kind;

        /// <summary>Set when <see cref="Text"/> names an asset, so the row can select it.</summary>
        public string AssetPath;
    }

    public sealed class OptimizationIssue
    {
        /// <summary>Stable identity for this concrete finding within its asset/scan ordering.</summary>
        public string IssueInstanceId;

        public string RuleId;
        public string Title;
        public string Why;
        public string FixHint;
        public IssueSeverity Severity;
        public AssetCategory Category;

        /// <summary>Human readable "what it is now".</summary>
        public string Current;
        /// <summary>Human readable "what it should be".</summary>
        public string Recommended;
        /// <summary>
        /// Best-effort byte estimate of what fixing this saves <em>on its own</em>, with no other fix
        /// applied. 0 when unknown. These do not add up across an asset - see
        /// <see cref="AssetEntry.Finalise"/> for how they are combined.
        /// </summary>
        public long EstimatedSavings;

        /// <summary>Which pool <see cref="EstimatedSavings"/> comes out of.</summary>
        public SavingsBasis Basis;

        /// <summary>How far the analyzer can stand behind this particular finding.</summary>
        public OptimizationConfidence Confidence = OptimizationConfidence.Heuristic;

        /// <summary>How strongly the recommendation is supported for this concrete finding.</summary>
        public OptimizationConfidence RecommendationConfidence = OptimizationConfidence.Heuristic;

        /// <summary>Evidence contract required to execute this finding automatically.</summary>
        public EvidenceRequirement RequiredEvidence = EvidenceRequirement.None;

        /// <summary>Evidence state captured when this finding was produced.</summary>
        public EvidenceState EvidenceState = EvidenceState.Unconfirmed;

        /// <summary>Identity of the evidence that justified the finding.</summary>
        public string EvidenceSignature;

        /// <summary>Per-asset identity used to invalidate restored Verified findings when the underlying asset changes.</summary>
        public string AssetEvidenceSignature;

        /// <summary>Asset path associated with <see cref="AssetEvidenceSignature"/> for execution-time validation.</summary>
        public string AssetEvidencePath;

        /// <summary>Semantic rule/analyzer/fix signatures captured with the finding.</summary>
        public string RuleCatalogSignature;
        public string AnalyzerSignature;
        public string FixContractSignature;

        /// <summary>True when the concrete action risk is explicitly destructive.</summary>
        public bool Destructive => Risk == FixRisk.Destructive;

        /// <summary>The specifics behind this finding: which paths, which objects, which lines.</summary>
        public readonly List<IssueDetail> Details = new List<IssueDetail>();

        /// <summary>
        /// How this finding may be acted on. It is inherited directly from the registered rule.
        /// </summary>
        public FixKind FixKind;

        /// <summary>Action-level risk for a single-action issue. Option-specific risk may override this.</summary>
        public FixRisk Risk = FixRisk.Unspecified;

        /// <summary>Whether the issue's default action requires a backup even when it is not destructive.</summary>
        public bool BackupRequired;

        /// <summary>Mutates the asset / importer. Null when the fix needs a human.</summary>
        public Action<FixContext> Fix;

        /// <summary>
        /// When this finding has more than one reasonable target state - a compression level, a max
        /// size, a load type - the choices are here instead of forcing one guess into <see cref="Fix"/>.
        /// Empty for anything with a single action. Never applied by Fix All, whatever <see cref="FixKind"/>
        /// says: an option list exists precisely because the tool cannot pick one on its own.
        /// </summary>
        public readonly List<FixOption> Options = new List<FixOption>();

        /// <summary>
        /// Reads the post-fix state back and returns whether the change actually took.
        /// <para>
        /// Run after the save/reimport, so it observes what Unity ended up with rather than what the
        /// fix asked for. The two differ more often than one would like: an importer clamps a value,
        /// a platform override wins, a serialized property is renamed between versions. Without this
        /// the tool reports "Max Size = 1024" on the strength of having assigned it, which is a claim
        /// about the code rather than about the project.
        /// </para>
        /// <para>Null when the rule has nothing meaningful to check.</para>
        /// </summary>
        public Func<FixContext, bool> Verify;

        /// <summary>True when Fix All / Fix Selected may run this without asking anyone first.</summary>
        public bool AutoFixable => FixKind == FixKind.Verified
                                      && Confidence >= RuleCatalog.MinimumForAutomaticFix
                                      && RecommendationConfidence >= RuleCatalog.MinimumForAutomaticFix
                                      && Fix != null
                                      && FixContract.EvidenceAllowsAutomatic(this)
                                      && Verify != null
                                      && (Options == null || Options.Count == 0);

        /// <summary>
        /// True when there is something a person could explicitly run - either the Verified action
        /// or a Review one. Used to decide whether the issue card shows an action at all, as opposed
        /// to "no automated action".
        /// </summary>
        public bool ReviewFixable
        {
            get
            {
                if (FixKind == FixKind.Verified) return AutoFixable;
                if (FixKind != FixKind.Review || Confidence < RuleCatalog.MinimumForReviewFix) return false;
                if (Options != null && Options.Count > 0)
                {
                    for (int i = 0; i < Options.Count; i++)
                    {
                        var option = Options[i];
                        if (option?.Apply == null) continue;
                        var risk = option.EffectiveRisk(Risk == FixRisk.Unspecified ? FixRisk.Medium : Risk);
                        if (FixContract.RequiresPostconditionVerifier(risk) && option.Verify == null) continue;
                        return true;
                    }
                    return false;
                }
                if (Fix == null) return false;
                var defaultRisk = Risk == FixRisk.Unspecified ? FixRisk.Medium : Risk;
                return !FixContract.RequiresPostconditionVerifier(defaultRisk) || Verify != null;
            }
        }

        /// <summary>
        /// Line in the source file this finding is about, for script rules that can point at one.
        /// -1 when the rule is not script-based or could not localise the finding to a single line.
        /// </summary>
        public int SourceLine = -1;

        /// <summary>
        /// Where this finding was measured, when the answer depends on a platform and quality level
        /// rather than on the project as a whole. -1 when it does not.
        /// <para>
        /// A quality-level finding without this is not reproducible: "shadow cascades: 4" says
        /// nothing about which of six levels, on which platform, was carrying that value - and a fix
        /// derived from it would land on whichever level the editor happened to be sitting on.
        /// </para>
        /// </summary>
        public int QualityLevelIndex = -1;

        public string QualityLevelName;

        /// <summary>The platform this finding was evaluated for, when it is platform-specific.</summary>
        public string TargetPlatform;

        /// <summary>Exact setting/importer scope touched by this finding, e.g. AndroidOverride or Default.</summary>
        public string ImporterScope;

        /// <summary>
        /// True when the analysis behind this finding has been outdated by a change made since the
        /// scan, so it describes a project state that no longer exists.
        /// <para>
        /// This matters most for the cross-asset findings. A duplicate group, an "unused" verdict and
        /// a material family are all conclusions about the whole project, and editing one prefab can
        /// invalidate any of them - while the row describing them sits on screen with a Fix button
        /// still under it. Marking the finding stale removes that button; see
        /// <see cref="ScanResult.InvalidateCrossAssetFindings"/>.
        /// </para>
        /// </summary>
        public bool Stale;

        /// <summary>Why this finding went stale, in a sentence the UI can show.</summary>
        public string StaleReason;

        /// <summary>
        /// Retires this finding's automatic fix. The finding stays visible - it may well still be
        /// true - but nothing can act on it until a fresh pass confirms it.
        /// </summary>
        public void MarkStale(string reason)
        {
            if (Stale) return;

            Stale = true;
            StaleReason = reason;
            Fix = null;
            Verify = null;
            FixKind = FixKind.None;
            Options.Clear();
        }
    }

    /// <summary>Shared state handed to every fix that runs against one asset, so we reimport once.</summary>
    public sealed class FixContext
    {
        public string AssetPath;
        public UnityEditor.AssetImporter Importer;
        public UnityEngine.Object Asset;

        /// <summary>Set by a fix that changed importer settings.</summary>
        public bool NeedsReimport;
        /// <summary>Set by a fix that changed a serialized asset in place.</summary>
        public bool NeedsSave;
        /// <summary>Set by a fix that changed something outside the asset database (project settings).</summary>
        public bool ChangedProjectSettings;

        /// <summary>
        /// Ask for another file to be snapshotted before it is modified or deleted, and say whether
        /// the snapshot succeeded. A fix that touches files beyond its own asset must call this for
        /// each one, and must abort when it returns false, or Revert cannot undo what follows.
        /// <para>
        /// This returns a result rather than being a plain callback because the previous shape could
        /// not express failure: the snapshot was attempted, the exception was logged, and the fix
        /// went on to delete the file anyway.
        /// </para>
        /// </summary>
        public Func<string, bool> TryBackupExtra;
        public Func<string, bool> RegisterCreatedAssetCallback;
		/// <summary>Ephemeral data retained for the immediate post-fix verifier. It is never serialized.</summary>
		public object VerificationData;


        /// <summary>
        /// True when this fix batch is taking backups at all. A destructive fix must refuse to run
        /// when this is false rather than proceeding unprotected.
        /// </summary>
        public bool BackupEnabled;

        /// <summary>True only for verifier eligibility probes; mutations through such a context are prohibited.</summary>
        public bool IsVerifierProbe;

        /// <summary>Throws if a verifier probe attempts to mutate editor/project state.</summary>
        public void EnsureWritable()
        {
            if (IsVerifierProbe)
                throw new InvalidOperationException("Verifier probes are read-only and cannot mutate editor or project state.");
        }

        /// <summary>Snapshots a file, or throws so the surrounding fix is recorded as failed.</summary>
        /// <summary>Records an asset created by a fix so rollback can remove it again.</summary>
        public void RegisterCreatedAsset(string assetPath)
        {
            EnsureWritable();
            if (string.IsNullOrEmpty(assetPath))
                throw new ArgumentException("Created asset path is empty.", nameof(assetPath));
            if (RegisterCreatedAssetCallback == null || !RegisterCreatedAssetCallback(assetPath))
                throw new IOException("Could not record created asset '" + assetPath + "' for rollback.");
        }

        public void RequireBackup(string assetPath)
        {
            if (IsVerifierProbe)
                throw new InvalidOperationException("Verifier probes are read-only and cannot request backups.");
            if (!BackupEnabled)
                throw new InvalidOperationException(
                    "This fix modifies or deletes files and cannot run with backups switched off.");

            if (TryBackupExtra == null || !TryBackupExtra(assetPath))
                throw new IOException("Could not back up '" + assetPath + "', so nothing was changed.");
        }

        /// <summary>Set by a fix that deleted its asset, so no reimport is attempted afterwards.</summary>
        public bool DeletedAsset;
    }
}

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Terse front door for analyzers registering their rules.</summary>
	public static class Rules
	{
		/// <summary>
		/// Registers a rule.
		/// <para>
		/// <paramref name="confidence"/> is separate from <paramref name="severity"/> on purpose: one
		/// says how much the problem costs, the other how sure we are it exists. Left unset it is
		/// inferred - a rule that ships an automatic fix is asserting at least
		/// <see cref="OptimizationConfidence.HighConfidence"/> by doing so, and one that does not is
		/// assumed to be a <see cref="OptimizationConfidence.Heuristic"/> until it says otherwise.
		/// </para>
		/// </summary>
		public static void Register(string id, string title, AssetCategory category, string why, string fix,
			IssueSeverity severity, FixKind fixKind = FixKind.None,
			OptimizationConfidence? confidence = null, FixRisk risk = FixRisk.Unspecified,
			bool backupRequired = false, OptimizationConfidence? recommendationConfidence = null,
			EvidenceRequirement requiredEvidence = EvidenceRequirement.None, string semanticVersion = "1")
		{
			RuleCatalog.Register(new RuleDef
			{
				Id = id,
				Title = title,
				Category = category,
				Why = why,
				Fix = fix,
				DefaultSeverity = severity,
				FixKind = fixKind,
				Confidence = confidence ?? (fixKind == FixKind.Verified
					? OptimizationConfidence.HighConfidence
					: OptimizationConfidence.Heuristic),
				DefaultFixRisk = risk,
				RecommendationConfidence = recommendationConfidence ?? (fixKind == FixKind.Verified ? OptimizationConfidence.HighConfidence : OptimizationConfidence.Heuristic),
				RequiredEvidence = requiredEvidence,
				SemanticVersion = string.IsNullOrEmpty(semanticVersion) ? "1" : semanticVersion,
				DefaultBackupRequired = backupRequired || risk == FixRisk.Destructive
			});
		}
	}
}
