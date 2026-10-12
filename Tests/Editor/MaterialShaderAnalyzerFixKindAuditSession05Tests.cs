using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using ApexForge.ProjectOptimizer;

namespace ApexForge.ProjectOptimizer.Tests
{
    /// <summary>
    /// Focused regression coverage for the source transformations added by FixKind audit Session 05.
    /// These tests exercise deterministic shader-source transformations and keyword-control preflight
    /// gates without requiring fixture assets or changing the user's project settings.
    /// </summary>
    public sealed class MaterialShaderAnalyzerFixKindAuditSession05Tests
    {
        static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        static readonly Type AnalyzerType = typeof(MaterialShaderAnalyzer);

        static object Invoke(string methodName, params object[] args)
        {
            var method = AnalyzerType.GetMethod(methodName, PrivateStatic);
            Assert.IsNotNull(method, "Missing analyzer helper: " + methodName);
            return method.Invoke(null, args);
        }


        static object InvokeInstance(object target, string methodName, params object[] args)
        {
            var method = AnalyzerType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, "Missing analyzer instance helper: " + methodName);
            return method.Invoke(target, args);
        }

        static void SetPrivateField(object target, string fieldName, object value)
        {
            var field = AnalyzerType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "Missing analyzer state field: " + fieldName);
            field.SetValue(target, value);
        }

        static T GetPrivateField<T>(object target, string fieldName)
        {
            var field = AnalyzerType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "Missing analyzer state field: " + fieldName);
            return (T)field.GetValue(target);
        }

        static MaterialShaderAnalyzer CreateKeywordConversionAnalyzer()
        {
            var analyzer = new MaterialShaderAnalyzer();
            SetPrivateField(analyzer, "m_RuntimeSourceIndexBuilt", true);
            SetPrivateField(analyzer, "m_RuntimeSourceIndexComplete", true);
            SetPrivateField(analyzer, "m_RuntimeHasDynamicKeywordControl", false);
            GetPrivateField<HashSet<string>>(analyzer, "m_RuntimeControlledKeywords").Clear();
            var users = GetPrivateField<Dictionary<string, HashSet<string>>>(analyzer, "m_ShadersUsingMaterialKeyword");
            users.Clear();
            users["SESSION05_AUDIT_KEYWORD_9FE9"] = new HashSet<string>(StringComparer.Ordinal) { "Test/Shader" };
            return analyzer;
        }

        static IList BuildLocalKeywordConversionEdits(MaterialShaderAnalyzer analyzer, string directive = "multi_compile")
        {
            const string shaderName = "Test/Shader";
            string source = "Shader \"Test/Shader\" { SubShader { Pass {\nHLSLPROGRAM\n#pragma "
                + directive + " _ SESSION05_AUDIT_KEYWORD_9FE9\nENDHLSL\n} } }";
            return (IList)InvokeInstance(analyzer, "BuildShaderFeatureConversionEdits", source, shaderName,
                new HashSet<string>(StringComparer.Ordinal) { "SESSION05_AUDIT_KEYWORD_9FE9" }, true);
        }

        [Test]
        public void ShaderKeywordConversion_AutomatesExactLocalCandidate()
        {
            var analyzer = CreateKeywordConversionAnalyzer();
            var edits = BuildLocalKeywordConversionEdits(analyzer);
            Assert.AreEqual(1, edits.Count, "A fresh, material-used keyword confined to this shader should be convertible.");
            string replacement = (string)edits[0].GetType().GetField("ReplacementLine").GetValue(edits[0]);
            StringAssert.Contains("#pragma shader_feature_local _ SESSION05_AUDIT_KEYWORD_9FE9", replacement);
        }

        [Test]
        public void ShaderKeywordConversion_RejectsKeywordControlledByProjectCode()
        {
            var analyzer = CreateKeywordConversionAnalyzer();
            GetPrivateField<HashSet<string>>(analyzer, "m_RuntimeControlledKeywords").Add("SESSION05_AUDIT_KEYWORD_9FE9");
            Assert.AreEqual(0, BuildLocalKeywordConversionEdits(analyzer).Count);
        }

        [Test]
        public void ShaderKeywordConversion_RejectsIncompleteRuntimeSourceEvidence()
        {
            var analyzer = CreateKeywordConversionAnalyzer();
            SetPrivateField(analyzer, "m_RuntimeSourceIndexComplete", false);
            Assert.AreEqual(0, BuildLocalKeywordConversionEdits(analyzer).Count);
        }

        [Test]
        public void ShaderKeywordConversion_RejectsStageSpecificDirective()
        {
            var analyzer = CreateKeywordConversionAnalyzer();
            Assert.AreEqual(0, BuildLocalKeywordConversionEdits(analyzer, "multi_compile_fragment").Count);
        }

        [Test]
        public void ShaderKeywordConversion_RejectsDynamicKeywordControl()
        {
            var analyzer = CreateKeywordConversionAnalyzer();
            SetPrivateField(analyzer, "m_RuntimeHasDynamicKeywordControl", true);
            Assert.AreEqual(0, BuildLocalKeywordConversionEdits(analyzer).Count);
        }

        [Test]
        public void ReplaceShaderPragmaDirective_PreservesWhitespaceAndComment()
        {
            const string input = "    #pragma multi_compile SESSION05_AUDIT_KEYWORD_9FE9 BAR // preserve this";
            var actual = (string)Invoke("ReplaceShaderPragmaDirective", input, "shader_feature");
            Assert.AreEqual("    #pragma shader_feature SESSION05_AUDIT_KEYWORD_9FE9 BAR // preserve this", actual);
        }

        [Test]
        public void RewriteShaderKeywordPragmaRemovingTokens_PreservesFallbackAndComment()
        {
            const string source = "HLSLPROGRAM\n    #pragma multi_compile _ SESSION05_AUDIT_KEYWORD_9FE9 BAR // keep comment\nENDHLSL";
            var parsed = (IList)Invoke("ParseShaderKeywordPragmas", source);
            Assert.AreEqual(1, parsed.Count);
            var pragma = parsed[0];
            var remove = new HashSet<string>(StringComparer.Ordinal) { "SESSION05_AUDIT_KEYWORD_9FE9", "BAR" };
            var actual = (string)Invoke("RewriteShaderKeywordPragmaRemovingTokens",
                "    #pragma multi_compile _ SESSION05_AUDIT_KEYWORD_9FE9 BAR // keep comment", pragma, remove);
            Assert.AreEqual("    #pragma multi_compile _ // keep comment", actual);
        }

        [Test]
        public void RewriteShaderKeywordPragmaRemovingTokens_RetainsUnselectedKeywords()
        {
            const string source = "HLSLPROGRAM\n#pragma shader_feature _ RED GREEN BLUE\nENDHLSL";
            var parsed = (IList)Invoke("ParseShaderKeywordPragmas", source);
            var actual = (string)Invoke("RewriteShaderKeywordPragmaRemovingTokens",
                "#pragma shader_feature _ RED GREEN BLUE", parsed[0],
                new HashSet<string>(StringComparer.Ordinal) { "GREEN" });
            Assert.AreEqual("#pragma shader_feature _ RED BLUE", actual);
        }

        [Test]
        public void RemoveAlwaysIncludedGuidLine_RemovesOnlySelectedEntryAndPreservesFollowingSection()
        {
            const string selectedGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string retainedGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            string input = "m_AlwaysIncludedShaders:\n"
                + "- {fileID: 4800000, guid: " + selectedGuid + ", type: 3}\n"
                + "- {fileID: 4800000, guid: " + retainedGuid + ", type: 3}\n"
                + "m_PreloadedShaders: []\n";
            string output = (string)Invoke("RemoveAlwaysIncludedGuidLine", input, selectedGuid);
            Assert.IsFalse(output.Contains(selectedGuid));
            Assert.IsTrue(output.Contains(retainedGuid));
            Assert.IsTrue(output.Contains("m_PreloadedShaders: []"));
            Assert.IsFalse((bool)Invoke("IsShaderInAlwaysIncluded", output, selectedGuid));
            Assert.IsTrue((bool)Invoke("IsShaderInAlwaysIncluded", output, retainedGuid));
        }


        [Test]
        public void OpaqueMaterialGuard_RecognizesTransparencyPropertyControls()
        {
            Assert.IsTrue((bool)Invoke("IsTransparencyRelatedMaterialProperty", "_Surface"));
            Assert.IsTrue((bool)Invoke("IsTransparencyRelatedMaterialProperty", "_BaseColor"));
            Assert.IsTrue((bool)Invoke("IsTransparencyRelatedMaterialProperty", "_ZWrite"));
            Assert.IsFalse((bool)Invoke("IsTransparencyRelatedMaterialProperty", "_Metallic"));
        }

        [Test]
        public void OpaqueMaterialGuard_RecognizesMaterialPropertyAnimationBindings()
        {
            Assert.IsTrue((bool)Invoke("IsTransparencyRelatedAnimationBinding", "material._Surface"));
            Assert.IsTrue((bool)Invoke("IsTransparencyRelatedAnimationBinding", "material._BaseColor.a"));
            Assert.IsTrue((bool)Invoke("IsTransparencyRelatedAnimationBinding", "material._BaseMap"));
            Assert.IsTrue((bool)Invoke("IsTransparencyRelatedAnimationBinding", "m_Materials.Array.data[0]"));
            Assert.IsTrue((bool)Invoke("IsTransparencyRelatedAnimationBinding", "m_Material"));
            Assert.IsFalse((bool)Invoke("IsTransparencyRelatedAnimationBinding", "m_LocalPosition.x"));
        }

        [Test]
        public void RemoveAlwaysIncludedGuidLine_DoesNotTouchSameGuidOutsideSection()
        {
            const string selectedGuid = "cccccccccccccccccccccccccccccccc";
            string input = "m_AlwaysIncludedShaders: []\n"
                + "m_CustomSection:\n  retainedGuid: " + selectedGuid + "\n";
            string output = (string)Invoke("RemoveAlwaysIncludedGuidLine", input, selectedGuid);
            Assert.AreEqual(input, output);
        }
    }
}
