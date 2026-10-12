using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using ApexForge.ProjectOptimizer;

namespace ApexForge.ProjectOptimizer.Tests
{
    public sealed class FixKindAuditSession04ScriptAnalyzerTests
    {
        const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

        static string Rewrite(string methodName, string source, out bool changed)
        {
            MethodInfo method = typeof(ScriptAnalyzer).GetMethod(methodName, PrivateStatic);
            Assert.NotNull(method, "Expected analyzer-local rewrite helper: " + methodName);
            object[] args = { source, null };
            changed = (bool)method.Invoke(null, args);
            return (string)args[1];
        }

        [Test]
        public void Scr001_RemovesOnlyProvablyEmptyPrivateUnityCallback()
        {
            const string source = "using UnityEngine;\npublic class Sample : MonoBehaviour\n{\n    private void Update() { }\n    private void LateUpdate() { Work(); }\n    private void FixedUpdate() { }\n    private void Work() { }\n}\n";
            string result = Rewrite("TryRewriteEmptyPrivateCallbacks", source, out bool changed);

            Assert.IsTrue(changed);
            StringAssert.DoesNotContain("private void Update()", result);
            StringAssert.DoesNotContain("private void FixedUpdate()", result);
            StringAssert.Contains("private void LateUpdate() { Work(); }", result);
        }

        [Test]
        public void Scr001_LeavesAttributedAndNonPrivateCallbacksAlone()
        {
            const string source = "using UnityEngine;\npublic class Sample : MonoBehaviour\n{\n    [ContextMenu(\"Run\")]\n    private void Update() { }\n    protected void LateUpdate() { }\n}\n";
            string result = Rewrite("TryRewriteEmptyPrivateCallbacks", source, out bool changed);

            Assert.IsFalse(changed);
            Assert.AreEqual(source, result);
        }

        [Test]
        public void Scr001_PreservesCallbacksWithNearbyComments()
        {
            const string source = "using UnityEngine;\npublic class Sample : MonoBehaviour\n{\n    // Kept intentionally as a named Unity callback.\n    private void Update() { }\n}\n";
            string result = Rewrite("TryRewriteEmptyPrivateCallbacks", source, out bool changed);

            Assert.IsFalse(changed);
            Assert.AreEqual(source, result);
        }

        [Test]
        public void Scr003_RewritesDirectSelfGetComponentAndLeavesOtherReceiversAlone()
        {
            const string source = "using UnityEngine;\npublic class Sample : MonoBehaviour\n{\n    public GameObject other;\n    private void Update()\n    {\n        var selfBody = GetComponent<Rigidbody>();\n        var explicitSelf = this.GetComponent<Collider>();\n        var otherBody = other.GetComponent<Rigidbody>();\n    }\n}\n";
            string result = Rewrite("TryRewriteGetComponentCache", source, out bool changed);

            Assert.IsTrue(changed);
            StringAssert.Contains("var selfBody = __poGetComponent_Rigidbody();", result);
            StringAssert.Contains("var explicitSelf = __poGetComponent_Collider();", result);
            StringAssert.Contains("var otherBody = other.GetComponent<Rigidbody>();", result);
            StringAssert.Contains("private Rigidbody __poCachedComponent_Rigidbody;", result);
            StringAssert.Contains("private Collider __poCachedComponent_Collider;", result);
        }

        [Test]
        public void Scr003_RejectsLookupWhenReceiverWouldBeAmbiguous()
        {
            const string source = "using UnityEngine;\npublic class Sample : MonoBehaviour\n{\n    public GameObject other;\n    private void Update() { other.GetComponent<Rigidbody>(); }\n}\n";
            string result = Rewrite("TryRewriteGetComponentCache", source, out bool changed);

            Assert.IsFalse(changed);
            Assert.AreEqual(source, result);
        }

        [Test]
        public void Scr020_PrecomputesOnlyLiteralHashAndPropertyIds()
        {
            const string source = "using UnityEngine;\npublic class Sample : MonoBehaviour\n{\n    public string parameter;\n    private void Update()\n    {\n        var speed = Animator.StringToHash(\"Speed\");\n        var dynamicHash = Animator.StringToHash(parameter);\n        var tint = Shader.PropertyToID(\"_Tint\");\n    }\n}\n";
            string result = Rewrite("TryRewriteLiteralHashCalls", source, out bool changed);

            Assert.IsTrue(changed);
            StringAssert.Contains("private static readonly int __poAnimatorHash_Speed_", result);
            StringAssert.Contains("private static readonly int __poShaderId_Tint_", result);
            StringAssert.Contains("var dynamicHash = Animator.StringToHash(parameter);", result);
            StringAssert.DoesNotContain("var speed = Animator.StringToHash(\"Speed\")", result);
            StringAssert.DoesNotContain("var tint = Shader.PropertyToID(\"_Tint\")", result);
        }

        [Test]
        public void Scr024_RewritesSupportedBracedInputTouchesForeach()
        {
            const string source = "using UnityEngine;\npublic class Sample : MonoBehaviour\n{\n    private void Update()\n    {\n        foreach (Touch touch in Input.touches)\n        {\n            Debug.Log(touch.fingerId);\n        }\n    }\n}\n";
            string result = Rewrite("TryRewriteInputTouchesLoops", source, out bool changed);

            Assert.IsTrue(changed);
            StringAssert.Contains("for (int __projectOptimizerTouchIndex = 0; __projectOptimizerTouchIndex < Input.touchCount; __projectOptimizerTouchIndex++)", result);
            StringAssert.Contains("Touch touch = Input.GetTouch(__projectOptimizerTouchIndex);", result);
            StringAssert.DoesNotContain("foreach (Touch touch in Input.touches)", result);
        }


        [Test]
        public void Scr002_ReviewSceneSearchCacheGeneratesValidMemberNames()
        {
            const string source = "using UnityEngine;\npublic class Sample : MonoBehaviour\n{\n    private void Update() { var found = FindObjectOfType<Rigidbody>(); }\n}\n";
            string result = Rewrite("TryRewriteSimpleSceneSearchCache", source, out bool changed);

            Assert.IsTrue(changed);
            StringAssert.Contains("var found = __poSceneSearch_Rigidbody_", result);
            StringAssert.Contains("_Get();", result);
            StringAssert.Contains("private Rigidbody __poSceneSearch_Rigidbody_", result);
            StringAssert.Contains("= FindObjectOfType<Rigidbody>();", result);
            StringAssert.DoesNotContain("Rigidbody|", result);
        }

        [Test]
        public void Scr005_ReviewLogRemovalOnlyRemovesStandaloneStatements()
        {
            const string source = "using UnityEngine;\npublic class Sample : MonoBehaviour\n{\n    private void Update()\n    {\n        Debug.Log(\"diagnostic\");\n        Debug.Log(\"keep\"); DoWork();\n    }\n    private void DoWork() { }\n}\n";
            string result = Rewrite("TryRewriteStandaloneDebugLogLines", source, out bool changed);

            Assert.IsFalse(changed, "A source file containing a log call sharing a line with executable code must not be rewritten partially.");
            StringAssert.Contains("Debug.Log(\"diagnostic\")", result);
            StringAssert.Contains("Debug.Log(\"keep\"); DoWork();", result);
        }

        [Test]
        public void Scr011_ReviewCacheInsertsMembersOutsideTheMethodBody()
        {
            const string source = "using UnityEngine;\npublic class Sample : MonoBehaviour\n{\n    private void Update() { var target = transform.Find(\"Hand/Target\"); }\n}\n";
            string result = Rewrite("TryRewriteTransformFindCache", source, out bool changed);

            Assert.IsTrue(changed);
            StringAssert.Contains("var target = __poTransformFind_", result);
            StringAssert.Contains("private Transform __poTransformFind_", result);
            StringAssert.Contains("private Transform __poTransformFind_", result);
            StringAssert.Contains("= transform.Find(\"Hand/Target\");", result);
        }

        [Test]
        public void Scr021_ReviewRemovalTouchesOnlyStandaloneCallsInsidePerFrameCallbacks()
        {
            const string source = "using UnityEngine;\npublic class Sample : MonoBehaviour\n{\n    private void Awake() { Physics.SyncTransforms(); }\n    private void Update()\n    {\n        Physics.SyncTransforms();\n        DoWork();\n    }\n    private void DoWork() { }\n}\n";
            MethodInfo method = typeof(ScriptAnalyzer).GetMethod("TryRewriteReviewCallLines", PrivateStatic);
            Assert.NotNull(method);
            object[] args = { source, "SCR021", null };
            bool changed = (bool)method.Invoke(null, args);
            string result = (string)args[2];

            Assert.IsTrue(changed);
            StringAssert.Contains("private void Awake() { Physics.SyncTransforms(); }", result);
            StringAssert.DoesNotContain("        Physics.SyncTransforms();\n        DoWork();", result);
        }

        [Test]
        public void Scr011_LeavesDynamicTransformFindPathsAlone()
        {
            const string source = "using UnityEngine;\npublic class Sample : MonoBehaviour\n{\n    public string path;\n    private void Update() { var target = transform.Find(path); }\n}\n";
            string result = Rewrite("TryRewriteTransformFindCache", source, out bool changed);

            Assert.IsFalse(changed);
            Assert.AreEqual(source, result);
        }

        [Test]
        public void Scr024_LeavesUnbracedInputTouchesForeachAlone()
        {
            const string source = "using UnityEngine;\npublic class Sample : MonoBehaviour\n{\n    private void Update() { foreach (Touch touch in Input.touches) Use(touch); }\n    private void Use(Touch touch) { }\n}\n";
            string result = Rewrite("TryRewriteInputTouchesLoops", source, out bool changed);

            Assert.IsFalse(changed);
            Assert.AreEqual(source, result);
        }

        [Test]
        public void Scr022_ReviewMaterialFixDoesNotDoubleRewriteSharedAccessors()
        {
            const string source = "using UnityEngine;\npublic class Sample : MonoBehaviour\n{\n    public Renderer renderer;\n    private void Update() { renderer.material.color = Color.red; var all = renderer.materials; var alreadyShared = renderer.sharedMaterials; }\n}\n";
            string result = Rewrite("TryRewriteToSharedMaterialAccess", source, out bool changed);

            Assert.IsTrue(changed);
            StringAssert.Contains("renderer.sharedMaterial.color", result);
            StringAssert.Contains("renderer.sharedMaterials", result);
            StringAssert.DoesNotContain("sharedSharedMaterials", result);
        }
    }
}
