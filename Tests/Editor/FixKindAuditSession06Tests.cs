using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer.Tests
{
    /// <summary>
    /// Session 06 regression coverage for TextureAnalyzer's conservative auto-fix gates.
    /// These tests deliberately call analyzer-owned helpers through reflection so no production
    /// helper needs to become public solely for a test assembly.
    /// </summary>
    public sealed class FixKindAuditSession06Tests
    {
        const BindingFlags StaticNonPublic = BindingFlags.Static | BindingFlags.NonPublic;
        string m_TestFolder;

        [SetUp]
        public void SetUp()
        {
            m_TestFolder = "Assets/__ProjectOptimizer_S06Tests_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(Application.dataPath, Path.GetFileName(m_TestFolder)));
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                string absolute = Path.Combine(Application.dataPath, Path.GetFileName(m_TestFolder));
                if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
                AssetDatabase.Refresh();
            }
            catch { /* Test cleanup must not mask the assertion that originally failed. */ }
        }

        [TestCase(-10, 0)]
        [TestCase(0, 0)]
        [TestCase(8, 8)]
        [TestCase(16, 16)]
        [TestCase(24, 16)]
        public void TEX010_AnisotropyTargetIsClampedToUnityRange(int requested, int expected)
        {
            Assert.AreEqual(expected, InvokeStatic<int>("ClampAnisotropyTarget", requested));
        }

        [Test]
        public void TEX010_OnlyLowersAnisotropyWhenItIsAboveTheConfiguredLimit()
        {
            Assert.IsTrue(InvokeStatic<bool>("ShouldClampAnisotropy", true, true, 1024, 16, 8));
            Assert.IsFalse(InvokeStatic<bool>("ShouldClampAnisotropy", false, true, 1024, 16, 8));
            Assert.IsFalse(InvokeStatic<bool>("ShouldClampAnisotropy", true, false, 1024, 16, 8));
            Assert.IsFalse(InvokeStatic<bool>("ShouldClampAnisotropy", true, true, 255, 16, 8));
            Assert.IsFalse(InvokeStatic<bool>("ShouldClampAnisotropy", true, true, 1024, 8, 8));
            Assert.IsTrue(InvokeStatic<bool>("ShouldClampAnisotropy", true, true, 1024, 8, 0));
        }

        [Test]
        public void TEX015_OnlyTrilinearWithoutMipmapsAndLowAnisotropyIsAutomatic()
        {
            Assert.IsTrue(InvokeStatic<bool>("ShouldAutoFixTrilinearWithoutMipmaps", false, FilterMode.Trilinear, 1));
            Assert.IsTrue(InvokeStatic<bool>("ShouldAutoFixTrilinearWithoutMipmaps", false, FilterMode.Trilinear, 0));
            Assert.IsFalse(InvokeStatic<bool>("ShouldAutoFixTrilinearWithoutMipmaps", true, FilterMode.Trilinear, 1));
            Assert.IsFalse(InvokeStatic<bool>("ShouldAutoFixTrilinearWithoutMipmaps", false, FilterMode.Bilinear, 1));
            Assert.IsFalse(InvokeStatic<bool>("ShouldAutoFixTrilinearWithoutMipmaps", false, FilterMode.Point, 1));
            Assert.IsFalse(InvokeStatic<bool>("ShouldAutoFixTrilinearWithoutMipmaps", false, FilterMode.Trilinear, 2));
        }

        [Test]
        public void TEX002AndTEX018_CompressionGateExcludesHDRNormalAndDataCases()
        {
            var facts = new TextureAnalyzer.TextureFacts
            {
                textureShape = TextureImporterShape.Texture2D,
                textureType = TextureImporterType.Default,
                format = "RGBA32"
            };
            Assert.IsTrue(InvokeStatic<bool>("IsCompressionCommonCase", "Assets/Textures/albedo.png", facts));
            Assert.IsFalse(InvokeStatic<bool>("IsCompressionCommonCase", "Assets/Textures/light.exr", facts));
            Assert.IsFalse(InvokeStatic<bool>("IsCompressionCommonCase", "Assets/Textures/metal_mask.png", facts));
            facts.textureType = TextureImporterType.NormalMap;
            Assert.IsFalse(InvokeStatic<bool>("IsCompressionCommonCase", "Assets/Textures/stone_normal.png", facts));
        }

        [Test]
        public void TEX018_StandardCompressionIsDefaultForEligibleLargeHighQualityColorTextures()
        {
            var facts = new TextureAnalyzer.TextureFacts
            {
                textureShape = TextureImporterShape.Texture2D,
                textureType = TextureImporterType.Default,
                format = "RGBA32"
            };
            Assert.IsTrue(InvokeStatic<bool>("ShouldAutoDowngradeHighQualityCompression", "Assets/Textures/albedo.png", facts,
                TextureImporterCompression.CompressedHQ, 2048, false, false, false, true));
            Assert.IsFalse(InvokeStatic<bool>("ShouldAutoDowngradeHighQualityCompression", "Assets/Textures/albedo.png", facts,
                TextureImporterCompression.CompressedHQ, 2048, false, false, true, false));
            // The project preference applies across supported target profiles, not only platforms where
            // the old target-format heuristic happened to prove a memory concern.
            Assert.IsTrue(InvokeStatic<bool>("ShouldAutoDowngradeHighQualityCompression", "Assets/Textures/albedo.png", facts,
                TextureImporterCompression.CompressedHQ, 2048, false, false, false, true));
            Assert.IsFalse(InvokeStatic<bool>("ShouldAutoDowngradeHighQualityCompression", "Assets/Textures/albedo.png", facts,
                TextureImporterCompression.CompressedHQ, 512, false, false, false, true));
            Assert.IsFalse(InvokeStatic<bool>("ShouldAutoDowngradeHighQualityCompression", "Assets/Textures/albedo.png", facts,
                TextureImporterCompression.Compressed, 2048, false, false, false, true));
            Assert.IsFalse(InvokeStatic<bool>("ShouldAutoDowngradeHighQualityCompression", "Assets/Textures/metal_mask.png", facts,
                TextureImporterCompression.CompressedHQ, 2048, false, false, false, true));
            Assert.IsFalse(InvokeStatic<bool>("ShouldAutoDowngradeHighQualityCompression", "Assets/Textures/albedo.png", facts,
                TextureImporterCompression.CompressedHQ, 2048, true, false, false, true));
            Assert.IsFalse(InvokeStatic<bool>("ShouldAutoDowngradeHighQualityCompression", "Assets/Textures/albedo.png", facts,
                TextureImporterCompression.CompressedHQ, 2048, false, true, false, true));
        }

        [Test]
        public void PAA001_StrictPngDecoderAcceptsPlainPngAndRejectsMetadataChunks()
        {
            byte[] png = MakePng(2, 2, new[]
            {
                new Color32(32, 80, 128, 255), new Color32(32, 80, 128, 255),
                new Color32(32, 80, 128, 255), new Color32(32, 80, 128, 255)
            });
            Assert.IsTrue(TryDecodeStrictPng(png, out Texture2D decoded, out bool hasAlpha));
            Assert.IsNotNull(decoded);
            Assert.IsFalse(hasAlpha);
            UnityEngine.Object.DestroyImmediate(decoded);

            byte[] withMetadata = InsertChunkBeforeIdat(png, "tEXt", new byte[] { 75, 101, 121 });
            Assert.IsFalse(TryDecodeStrictPng(withMetadata, out decoded, out hasAlpha));
            Assert.IsNull(decoded);
            Assert.IsFalse(TryDecodeStrictPng(new byte[] { 1, 2, 3 }, out decoded, out hasAlpha));
        }

        [Test]
        public void PAA001_FullResolutionProofAcceptsUniformPixelsAndRejectsSinglePixelDifference()
        {
            string assetPath = m_TestFolder + "/constant.png";
            string absolute = Path.Combine(Application.dataPath, Path.GetFileName(m_TestFolder), "constant.png");
            File.WriteAllBytes(absolute, MakePng(4, 2, new[]
            {
                new Color32(20, 40, 60, 255), new Color32(20, 40, 60, 255),
                new Color32(20, 40, 60, 255), new Color32(20, 40, 60, 255),
                new Color32(20, 40, 60, 255), new Color32(20, 40, 60, 255),
                new Color32(20, 40, 60, 255), new Color32(20, 40, 60, 255)
            }));

            Assert.IsTrue(TryGetConstantPngSource(assetPath, out _, out Color32 color, out bool hasAlpha, out int width, out int height));
            Assert.AreEqual(4, width);
            Assert.AreEqual(2, height);
            Assert.AreEqual(new Color32(20, 40, 60, 255), color);
            Assert.IsFalse(hasAlpha);

            File.WriteAllBytes(absolute, MakePng(4, 2, new[]
            {
                new Color32(20, 40, 60, 255), new Color32(20, 40, 60, 255),
                new Color32(20, 40, 60, 255), new Color32(20, 40, 60, 255),
                new Color32(20, 40, 60, 255), new Color32(20, 40, 60, 255),
                new Color32(20, 40, 61, 255), new Color32(20, 40, 60, 255)
            }));
            Assert.IsFalse(TryGetConstantPngSource(assetPath, out _, out _, out _, out _, out _));

            string alphaPath = m_TestFolder + "/constant-alpha.png";
            string alphaAbsolute = Path.Combine(Application.dataPath, Path.GetFileName(m_TestFolder), "constant-alpha.png");
            File.WriteAllBytes(alphaAbsolute, MakePng(2, 2, new[]
            {
                new Color32(20, 40, 60, 128), new Color32(20, 40, 60, 128),
                new Color32(20, 40, 60, 128), new Color32(20, 40, 60, 128)
            }));
            Assert.IsTrue(TryGetConstantPngSource(alphaPath, out _, out Color32 alphaColor, out bool alpha, out _, out _));
            Assert.IsTrue(alpha);
            Assert.AreEqual((byte)128, alphaColor.a);
        }

        [Test]
        public void PAA001_MainThreadCaptureCachesStrictSourceEvidenceForBackgroundAnalysis()
        {
            string assetPath = m_TestFolder + "/captured.png";
            string absolute = Path.Combine(Application.dataPath, Path.GetFileName(m_TestFolder), "captured.png");
            File.WriteAllBytes(absolute, MakePng(3, 2, new[]
            {
                new Color32(10, 20, 30, 255), new Color32(10, 20, 30, 255), new Color32(10, 20, 30, 255),
                new Color32(10, 20, 30, 255), new Color32(10, 20, 30, 255), new Color32(10, 20, 30, 255)
            }));

            Type settingsType = typeof(TextureAnalyzer).Assembly.GetType("ApexForge.ProjectOptimizer.OptimizerSettings");
            Assert.IsNotNull(settingsType);
            PropertyInfo instanceProperty = settingsType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(instanceProperty);
            object settings = instanceProperty.GetValue(null);
            Assert.IsNotNull(settings);
            FieldInfo deepScan = settingsType.GetField("deepTextureScan", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(deepScan);
            bool previous = (bool)deepScan.GetValue(settings);
            var facts = new TextureAnalyzer.TextureFacts
            {
                textureType = TextureImporterType.Default,
                textureShape = TextureImporterShape.Texture2D,
                solidColor = true,
                width = 3,
                height = 2
            };

            try
            {
                deepScan.SetValue(settings, true);
                InvokeStatic<object>("CaptureConstantPngEvidence", assetPath, facts, settings);
                Assert.IsTrue(facts.constantPngEvidenceEvaluated);
                Assert.IsTrue(facts.constantPngSourceSupported);
                Assert.IsNotEmpty(facts.constantPngHash);
                Assert.AreEqual(3, facts.constantPngSourceWidth);
                Assert.AreEqual(2, facts.constantPngSourceHeight);
                Assert.AreEqual(new Color32(10, 20, 30, 255), facts.constantPngColor);
            }
            finally { deepScan.SetValue(settings, previous); }
        }

        [Test]
        public void TEX008AndTEX013_StaleMaterialEvidenceFailsClosed()
        {
            var field = typeof(TextureAnalyzer).GetField("s_TextureUsageDirty", StaticNonPublic);
            Assert.IsNotNull(field);
            bool previous = (bool)field.GetValue(null);
            try
            {
                field.SetValue(null, true);
                Assert.IsFalse(InvokeStatic<bool>("HasExclusiveNormalMaterialUsage", "Assets/Textures/stone_normal.png"));
                Assert.IsFalse(InvokeStatic<bool>("HasExclusiveLinearDataMaterialUsage", "Assets/Textures/metal_mask.png"));
                Assert.IsFalse(InvokeStatic<bool>("HasVerifiedNonUiMaterialUsage", "Assets/Textures/world.png"));
            }
            finally { field.SetValue(null, previous); }
        }

        [Test]
        public void VerifiedRulesAndHighRiskReviewRequireBackups()
        {
            string[] verifiedRules = { "PAA001", "TEX001", "TEX002", "TEX003", "TEX005", "TEX006", "TEX007", "TEX008", "TEX009", "TEX010", "TEX011", "TEX013", "TEX015", "TEX018" };
            foreach (string ruleId in verifiedRules)
            {
                object definition = GetRuleDefinition(ruleId);
                Assert.AreEqual("Verified", GetMember(definition, "FixKind").ToString(), ruleId + " FixKind");
                Assert.IsTrue((bool)GetMember(definition, "DefaultBackupRequired"), ruleId + " must have a backup for verified rollback");
            }
            object atlasDefinition = GetRuleDefinition("TEX016");
            Assert.AreEqual("Review", GetMember(atlasDefinition, "FixKind").ToString());
            Assert.IsTrue((bool)GetMember(atlasDefinition, "DefaultBackupRequired"));
            object channelFormatDefinition = GetRuleDefinition("TEX014");
            Assert.AreEqual("Review", GetMember(channelFormatDefinition, "FixKind").ToString());
            Assert.IsTrue((bool)GetMember(channelFormatDefinition, "DefaultBackupRequired"));
        }

        [Test]
        public void TEX001_RejectsAPlatformTargetThatNoLongerMatchesTheProfile()
        {
            Type settingsType = typeof(TextureAnalyzer).Assembly.GetType("ApexForge.ProjectOptimizer.OptimizerSettings");
            Assert.IsNotNull(settingsType);
            PropertyInfo instanceProperty = settingsType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(instanceProperty);
            object settings = instanceProperty.GetValue(null);
            Assert.IsNotNull(settings);
            string configuredPlatform = Convert.ToString(GetMember(settings, "PlatformString"));
            string wrongPlatform = configuredPlatform + "_stale_session06";
            MethodInfo apply = typeof(TextureAnalyzer).GetMethod("ApplyTargetMaxSize", StaticNonPublic);
            Assert.IsNotNull(apply);
            object[] args = { null, wrongPlatform, settings, false, 512, 256, false, 1024, 1024 };
            TargetInvocationException error = Assert.Throws<TargetInvocationException>(() => apply.Invoke(null, args));
            Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);
        }

        static object GetRuleDefinition(string ruleId)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TextureAnalyzer).TypeHandle);
            Type catalog = typeof(TextureAnalyzer).Assembly.GetType("ApexForge.ProjectOptimizer.RuleCatalog");
            Assert.IsNotNull(catalog);
            MethodInfo get = catalog.GetMethod("Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(string) }, null);
            Assert.IsNotNull(get);
            object definition = get.Invoke(null, new object[] { ruleId });
            Assert.IsNotNull(definition, ruleId);
            return definition;
        }

        static object GetMember(object instance, string name)
        {
            Type type = instance.GetType();
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null) return property.GetValue(instance);
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, type.FullName + "." + name);
            return field.GetValue(instance);
        }

        static T InvokeStatic<T>(string name, params object[] args)
        {
            MethodInfo method = typeof(TextureAnalyzer).GetMethod(name, StaticNonPublic | BindingFlags.Public);
            Assert.IsNotNull(method, name);
            var parameters = method.GetParameters();
            if (args.Length < parameters.Length)
            {
                var expanded = new object[parameters.Length];
                Array.Copy(args, expanded, args.Length);
                for (int i = args.Length; i < parameters.Length; i++)
                {
                    Assert.IsTrue(parameters[i].IsOptional, name + " has an unexpected required parameter at index " + i);
                    expanded[i] = parameters[i].DefaultValue;
                }
                args = expanded;
            }
            Assert.AreEqual(parameters.Length, args.Length, name + " argument count");
            return (T)method.Invoke(null, args);
        }

        static bool TryDecodeStrictPng(byte[] bytes, out Texture2D decoded, out bool hasAlpha)
        {
            object[] args = { bytes, null, false };
            bool result = InvokeStatic<bool>("TryDecodeStrictPng", args);
            decoded = args[1] as Texture2D;
            hasAlpha = (bool)args[2];
            return result;
        }

        static bool TryGetConstantPngSource(string assetPath, out string md5, out Color32 color, out bool hasAlpha, out int width, out int height)
        {
            object[] args = { assetPath, null, default(Color32), false, 0, 0 };
            bool result = InvokeStatic<bool>("TryGetConstantPngSource", args);
            md5 = args[1] as string;
            color = (Color32)args[2];
            hasAlpha = (bool)args[3];
            width = (int)args[4];
            height = (int)args[5];
            return result;
        }

        static byte[] MakePng(int width, int height, Color32[] pixels)
        {
            bool hasNonOpaquePixel = false;
            if (pixels != null)
                for (int i = 0; i < pixels.Length; i++)
                    if (pixels[i].a != 255) { hasNonOpaquePixel = true; break; }
            var texture = new Texture2D(width, height, hasNonOpaquePixel ? TextureFormat.RGBA32 : TextureFormat.RGB24, false, true);
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                return KeepOnlyStandardPngChunks(ImageConversion.EncodeToPNG(texture));
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        // Positive fixtures obey PAA001's canonical IHDR/IDAT/IEND contract independently of
        // ancillary color/profile chunks emitted by different Editor versions.
        static byte[] KeepOnlyStandardPngChunks(byte[] png)
        {
            if (png == null || png.Length < 8) throw new InvalidDataException("PNG fixture is too short.");
            using (var output = new MemoryStream())
            {
                output.Write(png, 0, 8);
                int offset = 8;
                bool sawHeader = false, sawData = false, sawEnd = false;
                while (offset + 12 <= png.Length)
                {
                    int chunkStart = offset;
                    uint chunkLength = ((uint)png[offset] << 24) | ((uint)png[offset + 1] << 16)
                        | ((uint)png[offset + 2] << 8) | png[offset + 3];
                    if (chunkLength > int.MaxValue || (long)offset + 12 + chunkLength > png.Length)
                        throw new InvalidDataException("PNG fixture has an invalid chunk length.");
                    string type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
                    int chunkBytes = checked((int)chunkLength + 12);
                    if (type == "IHDR" || type == "IDAT" || type == "IEND")
                    {
                        output.Write(png, chunkStart, chunkBytes);
                        sawHeader |= type == "IHDR";
                        sawData |= type == "IDAT";
                        if (type == "IEND")
                        {
                            sawEnd = true;
                            offset += chunkBytes;
                            break;
                        }
                    }
                    offset += chunkBytes;
                }
                if (!sawHeader || !sawData || !sawEnd || offset != png.Length)
                    throw new InvalidDataException("PNG fixture is incomplete or has trailing bytes.");
                return output.ToArray();
            }
        }

        static byte[] InsertChunkBeforeIdat(byte[] png, string type, byte[] data)
        {
            int offset = 8;
            while (offset + 8 <= png.Length)
            {
                uint length = ((uint)png[offset] << 24) | ((uint)png[offset + 1] << 16)
                              | ((uint)png[offset + 2] << 8) | png[offset + 3];
                string chunkType = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
                if (chunkType == "IDAT")
                {
                    byte[] chunk = new byte[12 + data.Length];
                    chunk[0] = (byte)(data.Length >> 24);
                    chunk[1] = (byte)(data.Length >> 16);
                    chunk[2] = (byte)(data.Length >> 8);
                    chunk[3] = (byte)data.Length;
                    byte[] chunkTypeBytes = System.Text.Encoding.ASCII.GetBytes(type);
                    Buffer.BlockCopy(chunkTypeBytes, 0, chunk, 4, 4);
                    Buffer.BlockCopy(data, 0, chunk, 8, data.Length);
                    uint crc = 0xffffffffu;
                    for (int i = 4; i < 8 + data.Length; i++)
                    {
                        crc ^= chunk[i];
                        for (int bit = 0; bit < 8; bit++)
                            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
                    }
                    crc ^= 0xffffffffu;
                    int crcOffset = 8 + data.Length;
                    chunk[crcOffset] = (byte)(crc >> 24);
                    chunk[crcOffset + 1] = (byte)(crc >> 16);
                    chunk[crcOffset + 2] = (byte)(crc >> 8);
                    chunk[crcOffset + 3] = (byte)crc;
                    int insertAt = offset;
                    byte[] result = new byte[png.Length + chunk.Length];
                    Buffer.BlockCopy(png, 0, result, 0, insertAt);
                    Buffer.BlockCopy(chunk, 0, result, insertAt, chunk.Length);
                    Buffer.BlockCopy(png, insertAt, result, insertAt + chunk.Length, png.Length - insertAt);
                    return result;
                }
                offset += checked((int)length + 12);
            }
            Assert.Fail("PNG had no IDAT chunk.");
            return null;
        }
    }
}
