using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using UnityEditor.Build;

namespace ApexForge.ProjectOptimizer
{
	// Unity 6 / 6000.x settings analyzer. Findings are deliberately conservative: settings that
	// change rendering, runtime behavior, compatibility or shipping policy stay advisory unless the
	// transition is mechanically safe and can be verified by the shared FixEngine. Rule IDs/order are
	// preserved so existing UI, reports and saved rule configuration remain compatible.
	/// <summary>Project-wide settings that affect build size, memory and frame cost.</summary>
	public sealed class ProjectSettingsAnalyzer : IProjectAnalyzer
	{
		Dictionary<string, string> m_ProjectTextIndex;
		bool m_ProjectTextIndexBuilt;
		bool m_ProjectTextIndexComplete;

		public string DisplayName => "Project settings";

		static ProjectSettingsAnalyzer()
		{
			// --- project-wide / common -------------------------------------------------
			Rules.Register(id: "PRJ001", title: "Optimize Mesh Data disabled", category: AssetCategory.ProjectSetting,
							why: "Strips vertex attributes that the build can prove are unused, reducing mesh build size, loading cost and runtime memory. Do not use it blindly when materials or shaders are changed at runtime.",
							fix: "Enable Optimize Mesh Data in Player Settings > Other Settings, after verifying runtime material/shader changes do not depend on stripped vertex channels.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ002", title: "Managed code stripping is below Low", category: AssetCategory.ProjectSetting,
							why: "Low is the default managed stripping preference for general targets. If a build exposes a compatibility issue, the user can adjust the level.",
							fix: "Set Managed Stripping Level to Low when the level is Disabled or Minimal. Android, iOS and WebGL are owned by PAS015, PAS016 and PRJ037 respectively.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ003", title: "Mono scripting backend on a mobile-capable target", category: AssetCategory.ProjectSetting,
							why: "IL2CPP compiles to native code: faster managed execution, smaller stripped binaries, and it is required for 64-bit store submissions.",
							fix: "Set Scripting Backend to IL2CPP for mobile targets.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ004", title: "Incremental garbage collection disabled", category: AssetCategory.ProjectSetting,
							why: "Incremental GC spreads collection over several frames instead of stopping the game for one long pause, which is what players perceive as a hitch.",
							fix: "Enable Use Incremental GC in Player Settings.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ005", title: "Prebake Collision Meshes disabled", category: AssetCategory.ProjectSetting,
							why: "Prebaking moves supported mesh-collider cooking work out of player startup/load time and into the build pipeline, at the cost of build data and build time.",
							fix: "Enable Prebake Collision Meshes when faster player loading is more important than the additional build cost.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ006", title: "Static batching disabled", category: AssetCategory.ProjectSetting,
							why: "Static batching can reduce draw-call overhead for eligible non-moving renderers, while increasing build data and memory. It is most useful when the scene contains many static objects that can actually batch.",
							fix: "Enable Static Batching when profiling shows draw-call or render-state overhead is significant and the memory/build-size tradeoff is acceptable.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ007", title: "Dynamic batching enabled alongside a Scriptable Render Pipeline", category: AssetCategory.ProjectSetting,
							why: "Dynamic batching can move additional vertex work onto the CPU each frame. In SRP projects the SRP Batcher and GPU instancing may be better fits, but the best choice remains workload-dependent.",
							fix: "Profile with the active SRP and disable Dynamic Batching when it increases CPU render time without providing a measurable benefit.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ008", title: "Texture streaming disabled", category: AssetCategory.ProjectSetting,
							why: "Texture streaming keeps only the mip levels the cameras need resident, capping texture memory at a budget you choose instead of the sum of everything loaded.",
							fix: "Enable Texture Streaming in Quality Settings, and enable Streaming Mipmaps on large textures.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ009", title: "Anisotropic filtering forced on for every texture", category: AssetCategory.ProjectSetting,
							why: "Force Enable overrides each texture's anisotropic-filtering importer setting. Per Texture restores that asset-level policy instead of forcing anisotropy onto every texture.",
							fix: "Set Anisotropic Textures to Per Texture for the affected Quality Level.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ010", title: "SRP Batcher disabled", category: AssetCategory.ProjectSetting,
							why: "The SRP Batcher reduces CPU render-state setup for compatible shaders and is a major CPU optimization in URP/HDRP. It is only useful when the active pipeline and shaders support it.",
							fix: "Enable SRP Batcher on the render pipeline asset.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ011", title: "Shadow distance is very large", category: AssetCategory.ProjectSetting,
							why: "Shadow distance decides how much of the world is re-rendered into shadow maps every frame. It is usually the cheapest quality dial to turn down.",
							fix: "Reduce the shadow distance on the render pipeline asset, or in Quality Settings.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ012", title: "Layer collision matrix is fully enabled", category: AssetCategory.ProjectSetting,
							why: "Every enabled cell in the collision matrix is a pair of layers the broadphase must test against each other. Projects that never prune the matrix pay for collisions that can never matter, such as UI or effects layers colliding with terrain.",
							fix: "Uncheck the layer pairs that should never interact in Project Settings > Physics.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ013", title: "Collision callbacks are not reused", category: AssetCategory.ProjectSetting,
							why: "Without reuse, every OnCollision callback allocates a fresh Collision object, producing constant garbage in physics-heavy scenes.",
							fix: "Enable Reuse Collision Callbacks when collision callback data does not escape the callback lifetime.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ014", title: "Accelerometer polled on a mobile target", category: AssetCategory.ProjectSetting,
							why: "The accelerometer is sampled continuously whether or not any script reads it, costing CPU and battery.",
							fix: "Set Accelerometer Frequency to Disabled only when project code does not read acceleration/gyro input.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ015", title: "Real audio voice budget is high", category: AssetCategory.ProjectSetting,
							why: "Every real voice is decoded and mixed each audio frame. Most games are indistinguishable at 24-32 real voices and pay noticeably less CPU.",
							fix: "Lower Max Real Voices in Audio settings.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ016", title: "Android texture compression does not prefer ASTC", category: AssetCategory.ProjectSetting,
							why: "ASTC is a strong default for modern Android hardware, but texture-compression targeting can intentionally ship multiple formats through an Android App Bundle.",
							fix: "Prefer ASTC for modern Android targets, or configure ASTC alongside a compatibility format when device coverage requires it.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ017", title: "Physics solver iteration count above the default", category: AssetCategory.ProjectSetting,
							why: "Solver iterations are run for every contact every fixed step. Raising the global count to fix one unstable joint makes the whole simulation more expensive.",
							fix: "Return the project default to 6 and raise iterations per-Rigidbody on the few bodies that need it.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ018", title: "MSAA below 4x on a VR target", category: AssetCategory.ProjectSetting,
							why: "VR makes edge and geometric aliasing more noticeable. The project preference is a 4x minimum; values already at or above 4x are preserved.",
							fix: "Raise VR MSAA to 4x on the applicable effective render pipeline setting only when current values are below 4x; preserve values at or above 4x.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ019", title: "Engine code stripping disabled", category: AssetCategory.ProjectSetting,
							why: "Engine code stripping removes whole engine modules the game never touches - physics, video, terrain - from the built player. On IL2CPP builds it is one of the larger reductions in binary size available, on top of managed stripping.",
							fix: "Enable Strip Engine Code in Player Settings. Add a link.xml entry for anything reached only by reflection.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PAS015", title: "Android managed stripping is below Low", category: AssetCategory.ProjectSetting,
							why: "Low is the default Android managed-stripping preference. If a player build reveals reflection/native-integration issues, the user can raise it.",
							fix: "Use Low when the current Android managed stripping level is Disabled or Minimal. If the resulting player build exposes a compatibility issue, the user can raise the level.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PAS016", title: "iOS managed stripping is below Low", category: AssetCategory.ProjectSetting,
							why: "Low is the default iOS managed-stripping preference. If a player build reveals reflection/native-integration issues, the user can raise it.",
							fix: "Use Low when the current iOS managed stripping level is Disabled or Minimal. If the resulting player build exposes a compatibility issue, the user can raise the level.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PAS023", title: "IL2CPP compiler configuration is Debug", category: AssetCategory.ProjectSetting,
							why: "Final IL2CPP builds should use Release configuration; development/debug build options can be enabled explicitly when needed.",
							fix: "Use Release for final builds when the active target is currently Debug. Development/debugging options can be enabled separately on the Player Build page.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PAS024", title: "Lightmap Streaming is disabled", category: AssetCategory.ProjectSetting,
							why: "Without Lightmap Streaming, all lightmap detail levels remain resident when mipmap streaming is otherwise available. Large baked-lighting datasets can therefore consume avoidable GPU memory.",
							fix: "Enable Lightmap Streaming for the target platform when the project already uses mipmap streaming and its lightmap memory budget benefits from it.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PAS025", title: "URP Asset is not specified", category: AssetCategory.ProjectSetting,
							why: "This ProjectOptimizer configuration targets URP. Without a Universal Render Pipeline Asset, the project falls back to the built-in renderer and the intended URP rendering configuration is not actually active.",
							fix: "Assign a Universal Render Pipeline Asset in Graphics Settings and verify the quality-level pipeline overrides.",
							severity: IssueSeverity.Critical,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PAS027", title: "URP mobile MSAA exceeds 4x", category: AssetCategory.ProjectSetting,
							why: "URP MSAA above 4x can materially increase bandwidth and framebuffer cost on mobile GPUs. The project preference caps mobile URP at 4x without raising lower values.",
							fix: "Treat 4x as the maximum for mobile URP: reduce values above 4x to 4x and preserve values below 4x.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);
			// --- cross-platform player ------------------------------------------------
			Rules.Register(id: "PRJ020", title: "CPU mesh deformation is enabled", category: AssetCategory.ProjectSetting,
							why: "Unity 6 can process skinning mesh deformation on the CPU, GPU, or GPU in batches. CPU deformation can become a significant main-thread cost when many skinned meshes are active.",
							fix: "Use GPUBatched mesh deformation when the target graphics API and runtime use case support it; validate any code that reads deformed mesh data back on the CPU.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ021", title: "Mip Stripping disabled", category: AssetCategory.ProjectSetting,
							why: "Mip Stripping removes the mip levels no quality level can ever select from the build. On a project whose lowest quality level caps texture size, those top mips are shipped and downloaded for nothing.",
							fix: "Enable Texture Mipmap Stripping in Player Settings > Other Settings. It only helps when your quality levels actually set different texture limits.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ022", title: "IL2CPP compiler configuration is Debug", category: AssetCategory.ProjectSetting,
							why: "Release is the approved default IL2CPP compiler configuration for final builds. Development/debugging behavior can be enabled explicitly through the Player Build page when required.",
							fix: "Use Release as the default compiler configuration for final builds. Enable Development Build/debugging options separately on the Player Build page when needed.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ023", title: "IL2CPP code generation is set to OptimizeSize", category: AssetCategory.ProjectSetting,
							why: "OptimizeSize favors smaller native output, while OptimizeSpeed favors runtime performance. This is a build-size versus runtime-speed tradeoff, not a debug/release distinction.",
							fix: "Use OptimizeSpeed rather than OptimizeSize; runtime speed/performance is preferred over binary size.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ024", title: "Graphics Jobs disabled on a supported target", category: AssetCategory.ProjectSetting,
							why: "Graphics Jobs can move graphics work away from the main thread, but the benefit is workload- and platform-dependent and Unity documents compatibility considerations.",
							fix: "Profile Graphics Jobs on the target device and enable them only when they reduce render-thread time without introducing instability.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ025", title: "Colour space is Gamma", category: AssetCategory.ProjectSetting,
							why: "Gamma space does lighting maths on non-linear values, so every blend, fade and light falloff is subtly wrong. Linear is the correct default on any GPU from the last decade, and is required for physically based shading to look right.",
							fix: "Switch to Linear in Player Settings > Other Settings. Expect to re-tune lighting and any hand-authored colour after the change, so do not do it late in a project.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			// --- Android ----------------------------------------------------------------
			Rules.Register(id: "PRJ026", title: "ARM64 missing from the Android target architectures", category: AssetCategory.ProjectSetting,
							why: "Google Play has required a 64-bit binary since 2019, so an ARMv7-only build cannot be published. Beyond that, a 64-bit device forced to run the 32-bit binary loses a substantial amount of performance for nothing.",
							fix: "Tick ARM64 in Player Settings > Other Settings > Target Architectures.",
							severity: IssueSeverity.Critical,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ027", title: "ARMv7 is shipped alongside ARM64 without architecture targeting", category: AssetCategory.ProjectSetting,
							why: "Shipping multiple CPU architectures increases native code size, but Android App Bundles can deliver architecture-specific content to devices. This should be based on your supported-device policy, not assumed to be wasted size.",
							fix: "Prefer ARM64-only when your supported-device data allows it; otherwise use an App Bundle/architecture targeting so devices receive only what they need.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ028", title: "Optimized Frame Pacing disabled", category: AssetCategory.ProjectSetting,
							why: "Without frame pacing Unity presents frames as soon as they are ready, so a run of 12 ms and 20 ms frames is displayed as unevenly as it was produced. The Android Frame Pacing library spaces presentation to the display's refresh rate, which reads as much smoother at the same average frame rate, and stops the display being driven faster than the game needs.",
							fix: "Enable Optimized Frame Pacing in Player Settings > Resolution and Presentation.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ029", title: "Android application entry point is Activity", category: AssetCategory.ProjectSetting,
							why: "Unity 6 provides Activity and GameActivity application entry points. GameActivity is the default entry point for new Unity projects and provides a different Android integration path, so the choice is compatibility- and integration-dependent rather than an unconditional performance switch.",
							fix: "Review GameActivity for new Android projects or when its integration features fit the project. Test custom manifests, native plugins, input and lifecycle behavior before changing the release entry point.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ030", title: "Vulkan is not the preferred Android graphics API", category: AssetCategory.ProjectSetting,
							why: "Vulkan is the preferred first API while OpenGLES3 remains an explicit fallback. Graphics APIs are tried in the configured order; device testing is still required for final release validation.",
							fix: "Set Vulkan first and OpenGLES3 second, preserving any other configured APIs afterward.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ031", title: "R8 minification disabled for release builds", category: AssetCategory.ProjectSetting,
							why: "R8 shrinks and obfuscates the Java side of the build. It does nothing for your C# code, but on a project carrying several Android plugins it removes a meaningful amount of dead Java and resources.",
							fix: "Enable Minify > Release in Player Settings > Publishing Settings. Keep a ProGuard keep-rule file for anything reached by reflection or JNI.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ032", title: "Blit Type forces a full-screen copy every frame", category: AssetCategory.ProjectSetting,
							why: "Always forces offscreen rendering followed by a blit. Auto lets Unity choose direct framebuffer presentation when supported and retain the blit path when needed. Unity ignores this setting when Vulkan is used.",
							fix: "Set Android Blit Type to Auto.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);
			// --- iOS --------------------------------------------------------------------
			Rules.Register(id: "PRJ033", title: "iOS script call optimization keeps exception checks", category: AssetCategory.ProjectSetting,
							why: "Slow and Safe emits a null check and an exception path around managed calls. Fast but no Exceptions removes them, which measurably reduces call overhead in hot managed code - at the cost of a hard crash instead of a managed exception when something is null.",
							fix: "Set Script Call Optimization to Fast but no Exceptions once the game is stable and you are no longer relying on caught exceptions in shipped builds.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			// --- WebGL ------------------------------------------------------------------
			Rules.Register(id: "PRJ034", title: "WebGL compression is not Brotli", category: AssetCategory.ProjectSetting,
							why: "Brotli produces noticeably smaller files than gzip, and on WebGL the download is the loading screen. It costs longer build times and needs the server to send Content-Encoding: br.",
							fix: "Set Compression Format to Brotli in Player Settings > Publishing Settings, and configure the host to serve the .br files with the right Content-Encoding header.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ035", title: "WebGL exception support is above the minimum", category: AssetCategory.ProjectSetting,
							why: "Full exception support adds checks around every managed operation and inflates the WebAssembly module substantially. Explicitly Thrown Exceptions Only keeps your own throws working and drops the rest.",
							fix: "Set Enable Exceptions to Explicitly Thrown Exceptions Only for release builds. None is smaller still, but a null reference then becomes an unrecoverable crash with no stack trace.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ036", title: "WebGL data caching disabled", category: AssetCategory.ProjectSetting,
							why: "Without data caching the browser re-downloads the entire data file on every visit. With it, a returning player loads from IndexedDB instead.",
							fix: "Enable Data Caching in Player Settings > Publishing Settings.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ037", title: "WebGL managed stripping is below High", category: AssetCategory.ProjectSetting,
							why: "Every byte of managed code becomes WebAssembly that has to be downloaded and compiled before the game starts. WebGL is the one platform where High stripping is worth the risk it carries.",
							fix: "Set Managed Stripping Level to High for WebGL, and add a link.xml for anything you reach by reflection.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			// --- quality / target rendering --------------------------------------------
			Rules.Register(id: "PRJ038", title: "Shadow cascades on a mobile quality level", category: AssetCategory.ProjectSetting,
							why: "Additional shadow cascades add shadow-map rendering and sampling work. The useful number depends on scene scale, shadow distance, GPU bandwidth and the visual target, so mobile projects should treat extra cascades as a measured quality cost.",
							fix: "Use one cascade or no cascades on constrained mobile tiers when profiling shows the extra cascade cost is not justified by the visual benefit.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ039", title: "LOD Bias keeps high-detail meshes in view too long", category: AssetCategory.ProjectSetting,
							why: "LOD Bias changes when LOD transitions occur. Values above 1 generally keep higher-detail LODs active for longer, increasing vertex, raster and memory work compared with the authored thresholds.",
							fix: "Lower LOD Bias on mobile tiers when profiling shows higher-detail LODs remain active longer than the visual budget requires.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ040", title: "Skin Weights above two bones per vertex", category: AssetCategory.ProjectSetting,
							why: "Every extra bone influence is another matrix multiply for every vertex of every skinned mesh, every frame. Four influences are visible on shoulders and hips on a close-up hero character, and almost never on anything else.",
							fix: "Set Skin Weights to Two Bones on mobile quality levels.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ041", title: "Realtime reflection probes enabled on a mobile quality level", category: AssetCategory.ProjectSetting,
							why: "A realtime probe re-renders the scene into a cubemap - six more views of everything. Very few mobile games can afford one, and baked probes look nearly identical for static environments.",
							fix: "Turn off Realtime Reflection Probes on mobile quality levels and bake instead.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ042", title: "Soft particles enabled on a mobile quality level", category: AssetCategory.ProjectSetting,
							why: "Soft particles need the depth texture and a per-pixel depth comparison on every particle. Combined with the overdraw particles already cause, this is expensive on a tile-based GPU for an effect that is subtle at phone screen size.",
							fix: "Turn off Soft Particles on mobile quality levels.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ043", title: "GPU Resident Drawer is disabled on a compatible URP target", category: AssetCategory.ProjectSetting,
							why: "Unity 6's GPU Resident Drawer can substantially reduce CPU rendering overhead in suitable scenes, but it requires SRP Batcher, Forward+ and retained BatchRendererGroup shader variants.",
							fix: "Enable GPU Resident Drawer only after verifying SRP Batcher, Forward+, BRG Keep All and shader/content compatibility on the target hardware.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ044", title: "GPU occlusion culling is off while the GPU Resident Drawer is on", category: AssetCategory.ProjectSetting,
							why: "With the GPU Resident Drawer already keeping instance data on the GPU, occlusion culling can be done there too - so objects hidden behind other geometry are never rasterised. Most of the setup cost is already paid.",
							fix: "Enable GPU Occlusion Culling on the URP asset after reviewing the scene's occlusion characteristics and validating the target hardware.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ045", title: "BatchRendererGroup variants are stripped", category: AssetCategory.ProjectSetting,
							why: "GPU Resident Drawer can require BatchRendererGroup shader variants to remain in the build, but the correct stripping policy depends on the renderer paths and shader usage actually shipped by the project.",
							fix: "Review the Build Profile / renderer configuration and set BatchRendererGroup Variants to Keep All only when the target build requires those variants.",
							severity: IssueSeverity.Critical,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.High,
							backupRequired: true
							);

			Rules.Register(id: "PRJ046", title: "Static batching fights the GPU Resident Drawer", category: AssetCategory.ProjectSetting,
							why: "Static batching merges meshes at build time into combined buffers, which takes those objects out of the GPU Resident Drawer's hands and inflates the build with duplicated geometry. The two systems solve the same problem, and the newer one solves it better.",
							fix: "Turn off Static Batching in Player Settings > Other Settings when the GPU Resident Drawer is enabled.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ047", title: "Opaque Texture enabled", category: AssetCategory.ProjectSetting,
							why: "Opaque Texture copies the whole colour buffer mid-frame so shaders can read what is behind them. It exists for refraction and distortion effects; if nothing samples it, it is a full-screen copy every frame for nothing.",
							fix: "Turn off Opaque Texture on the URP asset unless a shader actually samples _CameraOpaqueTexture.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ048", title: "Depth Texture enabled", category: AssetCategory.ProjectSetting,
							why: "A depth prepass or depth copy is not free, particularly on tile-based mobile GPUs. Soft particles, SSAO and many post effects need it - nothing else does.",
							fix: "Turn off Depth Texture on the URP asset if no effect in the project needs _CameraDepthTexture.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ049", title: "Built-in mobile MSAA exceeds 2x", category: AssetCategory.ProjectSetting,
							why: "The approved Built-in mobile policy caps MSAA at 2x. Values above 2x increase bandwidth and framebuffer cost; values at or below 2x are preserved. VR is excluded because PRJ018 applies its separate 4x minimum.",
							fix: "For a non-VR mobile target using the effective Built-in pipeline, reduce MSAA values above 2x to 2x and preserve values at or below 2x.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ050", title: "Fast sRGB/Linear conversion disabled on a mobile target", category: AssetCategory.ProjectSetting,
							why: "URP's Fast sRGB/Linear Conversions setting uses faster, less accurate approximation functions between the sRGB and Linear color spaces. It trades numerical accuracy for less shader work.",
							fix: "Enable Fast sRGB/Linear Conversions on the affected URP asset.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PAS026", title: "URP HDR is enabled on a mobile target", category: AssetCategory.ProjectSetting,
							why: "An HDR colour buffer increases render-target bandwidth and memory cost on mobile. It is useful for effects that require values above 1, but is unnecessary when the project does not use an HDR rendering path.",
							fix: "Turn off HDR on the URP asset unless the project's post-processing and material pipeline requires it.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ051", title: "Additional lights are per-pixel with a large shadow map on mobile", category: AssetCategory.ProjectSetting,
							why: "Per-pixel additional lights are shaded for every fragment they touch, and a 2048 shadow map for them costs both memory and a render pass. Per-vertex additional lights are a fraction of the cost and are usually indistinguishable on a phone screen.",
							fix: "On the URP asset, use Per Vertex or lower shadow resolution when profiling shows additional-light cost is significant.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ052", title: "Android target API is below the current Google Play submission requirement", category: AssetCategory.ProjectSetting,
							why: "For standard Android apps, Google Play requires new apps and app updates submitted from August 31, 2026 to target Android 16 (API 36) or higher. This is a publishing requirement, not a minimum-API compatibility setting.",
							fix: "Set Target API Level to API 36 for the standard Android target only after confirming third-party SDK and device compatibility. This is a publishing/compliance change, not a safe universal optimization.",
							severity: IssueSeverity.Critical,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ053", title: "LOD Cross Fade enabled on a mobile quality level", category: AssetCategory.ProjectSetting,
							why: "LOD cross-fade adds extra shading work while two LOD representations are blended. Unity recommends reducing this cost on performance-constrained mobile targets.",
							fix: "Disable LOD Cross Fade on mobile tiers when the transition quality is not worth the additional pixel cost.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ054", title: "Soft real-time shadows enabled on a mobile quality level", category: AssetCategory.ProjectSetting,
							why: "Soft shadows increase shadow filtering cost. Unity's current URP performance guidance recommends disabling them or using a low setting on performance-constrained targets.",
							fix: "Use hard shadows or a low-cost shadow setting on mobile tiers when profiling shows shadow filtering is a bottleneck.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ055", title: "URP render scale is above 1 on a mobile target", category: AssetCategory.ProjectSetting,
							why: "Rendering above native resolution increases pixel shading, bandwidth and post-processing cost. Lowering Render Scale is a standard URP performance control, but it changes image quality.",
							fix: "Keep Render Scale at or below 1 on mobile unless your visual target and performance budget explicitly justify supersampling.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ056", title: "URP main-light shadows enabled on a mobile target", category: AssetCategory.ProjectSetting,
							why: "The main light shadow map and shadow sampling add both rendering and bandwidth cost. Unity's performance guidance explicitly recommends disabling main-light shadows when the target does not need them.",
							fix: "Disable or lower main-light shadows on mobile quality profiles when they are not required by the visual target.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ057", title: "URP additional-light count is high on a mobile target", category: AssetCategory.ProjectSetting,
							why: "Additional lights increase per-object lighting work, and their shadows add further passes and memory traffic. A high global limit can therefore reserve budget the game never uses.",
							fix: "Lower the Additional Lights per Object limit when scene profiling shows that fewer lights are sufficient.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ058", title: "Quality setting keeps too many frames queued", category: AssetCategory.ProjectSetting,
							why: "Queued frames can increase input-to-photon latency. The right value is platform- and workload-dependent, especially for VR and latency-sensitive games.",
							fix: "Review Max Queued Frames on latency-sensitive profiles and validate the tradeoff on the target hardware.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			Rules.Register(id: "PRJ059", title: "Physics autoSyncTransforms is enabled", category: AssetCategory.ProjectSetting,
							why: "Automatic transform synchronization can add synchronization work when transforms are changed outside the physics step. It is safe to disable only when the project can prove it synchronizes before physics-dependent reads/queries.",
							fix: "Disable Physics.autoSyncTransforms when the project-wide evidence scan proves no dependency requires the automatic synchronization behavior.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);
			Rules.Register(id: "PRJ060", title: "Editor async shader compilation is disabled", category: AssetCategory.ProjectSetting,
							why: "Asynchronous shader compilation keeps new shader variants from blocking Game and Scene view rendering during editor iteration.",
							fix: "Enable EditorSettings.asyncShaderCompilation for asynchronous Game/Scene view shader compilation.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);
			Rules.Register(id: "PRJ061", title: "Editor refresh import mode is not OutOfProcessPerQueue", category: AssetCategory.ProjectSetting,
						why: "Unity can import independent asset queues in parallel worker processes during refresh. The effective mode can also differ from the persisted preference because of active transactions or launch overrides, so this project-wide setting should be changed deliberately.",
						fix: "Set the persisted and active refresh import mode to OutOfProcessPerQueue after confirming that the project's editor and import workflow is compatible with it.",
						severity: IssueSeverity.Info,
						fixKind: FixKind.Verified,
						confidence: OptimizationConfidence.HighConfidence,
						risk: FixRisk.Medium,
						backupRequired: false
						);
			Rules.Register(id: "PRJ062", title: "Import worker configuration may be materially below the host baseline", category: AssetCategory.ProjectSetting,
							why: "Import worker policy is host-dependent; Unity initializes new projects from a logical-core-based baseline rather than a universal worker count.",
							fix: "Review import worker counts and shutdown delay against the host CPU and the project's import workload.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);
			Rules.Register(id: "PRJ063", title: "Parallel AssetBundle building is disabled", category: AssetCategory.ProjectSetting,
							why: "Unity can use multiple processes when the project directly invokes BuildPipeline.BuildAssetBundles(). This does not imply Addressables or SBP coverage.",
							fix: "Review enabling EditorBuildSettings.UseParallelAssetBundleBuilding for direct BuildPipeline.BuildAssetBundles workflows.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);
			Rules.Register(id: "PRJ064", title: "Dedicated Server optimizations are disabled", category: AssetCategory.ProjectSetting,
							why: "When the effective build context is a Dedicated Server, this option lets Unity strip player content that is unnecessary for a server build.",
							fix: "Enable Dedicated Server optimizations on the effective Dedicated Server Build Profile or project setting.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);
			Rules.Register(id: "PRJ065", title: "Shader variant loading memory exceeds the configured budget", category: AssetCategory.ProjectSetting,
							why: "Large shader chunk sizes or counts can reserve substantial shader-variant loading memory. The useful target is project/budget-specific, not universal.",
							fix: "Review shader chunk size/count against the project's target-platform shader memory budget.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);
			Rules.Register(id: "PRJ066", title: "Lightmap resolution is high for the project's baked-lighting usage", category: AssetCategory.ProjectSetting,
							why: "Lightmap texel density increases baked quality and bake cost. A useful threshold depends on scene scale and content policy.",
							fix: "Review unusually high LightingSettings.lightmapResolution against project/scene policy.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);
			Rules.Register(id: "PRJ067", title: "Baked GI sample or bounce budget is high", category: AssetCategory.ProjectSetting,
							why: "Higher direct/indirect/environment samples and bounce counts improve bake convergence or fidelity at increased bake cost.",
							fix: "Review baked GI sample counts and max bounces against project/quality policy.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);
			Rules.Register(id: "PRJ068", title: "Physics2D solver iterations exceed the project policy", category: AssetCategory.ProjectSetting,
							why: "2D solver iterations increase simulation cost. Any reduction must account for gameplay stability and fidelity.",
							fix: "Review Physics2D position and velocity iterations against the project's 2D simulation policy.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);
			Rules.Register(id: "PRJ069", title: "3D solver velocity iterations exceed the project policy", category: AssetCategory.ProjectSetting,
							why: "Physics.defaultSolverVelocityIterations is global and is overridden by Rigidbody-specific values. Reducing it globally can affect bounce and joint behavior.",
							fix: "Review the global velocity-iteration count against the project's approved fidelity/performance threshold.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);
			Rules.Register(id: "PRJ070", title: "Force Asset Unload and GC on scene load is enabled", category: AssetCategory.ProjectSetting,
							why: "Forcing finalization and asset unloading on every relevant scene load can change timing and increase pauses. Disable it only after confirming no workflow intentionally depends on immediate finalization.",
							fix: "Review disabling EditorSettings.forceAssetUnloadAndGCOnSceneLoad when immediate finalizer timing is not a project requirement.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);
			Rules.Register(id: "PRJ072", title: "SRP Batcher setting belongs to an unknown custom pipeline", category: AssetCategory.ProjectSetting,
							why: "The active render pipeline exposes the SRP Batcher property, but this analyzer cannot prove what a custom pipeline does with that setting.",
							fix: "Inspect the custom render pipeline implementation and enable its SRP Batcher setting only when the pipeline documents compatible behavior.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ073", title: "IL2CPP Debug configuration is intentional for development", category: AssetCategory.ProjectSetting,
							why: "Legacy rule retained for canonical identity compatibility. The approved Release-default policy is implemented by PRJ022/PAS023; Development Build/debugging options are configured separately on the Player Build page. No independent PRJ073 finding is emitted to avoid contradicting that policy.",
							fix: "No independent fix is emitted by PRJ073; PRJ022/PAS023 own the Release-default behavior.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ074", title: "ARM64 cannot be enabled until native plugins support it", category: AssetCategory.ProjectSetting,
							why: "Google Play requires 64-bit Android support, but the project contains native plugins without ARM64 libraries that would make an ARM64 build fail.",
							fix: "Update or replace the incompatible native plugins before enabling ARM64.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ075", title: "URP Render Graph compatibility mode is enabled", category: AssetCategory.ProjectSetting,
							why: "Unity 6 URP uses Render Graph as the modern rendering path; Compatibility Mode disables that path and keeps legacy ScriptableRenderPass behaviour. The choice can be intentional, so this remains a review finding.",
							fix: "Review custom renderer features and legacy ScriptableRenderPass code, then disable URP Compatibility Mode only after confirming the renderer works correctly with Render Graph.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ076", title: "URP Render Graph compilation caching is disabled", category: AssetCategory.ProjectSetting,
							why: "Render Graph compilation caching can reduce repeated graph compilation work. The property is version-dependent, so this rule only exists when Unity exposes the setting on the selected URP asset.",
							fix: "Review enabling Render Graph compilation caching when the setting is present and the project's renderer-feature workflow is compatible with cached compilation.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Low,
							backupRequired: false
							);
		}

		/// <summary>Short enough that a progress figure would only ever read as 0 or 1.</summary>
		public float Progress => -1f;

		public bool Enabled(OptimizerSettings settings) => true;

		public IEnumerable<AssetEntry> Analyze(ScanContext ctx)
		{
			m_ProjectTextIndex = null;
			m_ProjectTextIndexBuilt = false;
			m_ProjectTextIndexComplete = false;
			if (ctx == null) yield break;
			if (!PlatformUtility.HasConcreteTarget(ctx.Settings.targetPlatform))
			{
				var unavailable = NewEntry("ProjectSettings/TargetProfile", "Target Profile", "Project/Target");
				unavailable.Category = AssetCategory.ProjectSetting;
				unavailable.Set("target", ctx.Settings.Profile.DisplayName);
				unavailable.Set("status", "Concrete Unity target unavailable");
				unavailable.Set("reason", "The Console profile is abstract. Select an active PlayStation/Xbox/Switch console target before evaluating platform-specific settings.");
				unavailable.Finalise();
				yield return unavailable;
				yield break;
			}

			yield return AnalyzePlayerSettings(ctx);
			yield return AnalyzeQualitySettings(ctx);

			// One entry per distinct render pipeline asset the target's quality levels actually use,
			// rather than one for whichever pipeline the editor happens to be showing.
			foreach (var entry in AnalyzeRenderPipelines(ctx)) yield return entry;

			yield return AnalyzePhysicsSettings(ctx);
			yield return AnalyzeAudioSettings(ctx);
		}

		// --- player ---------------------------------------------------------------

		AssetEntry AnalyzePlayerSettings(ScanContext ctx)
		{
			var entry = NewEntry("ProjectSettings/ProjectSettings.asset", "Player Settings", "Project/Player");
			var settings = ctx.Settings;
			var target = PlatformUtility.ToNamedBuildTarget(settings.targetPlatform);

			EvaluateCommonPlayer(entry, ctx, target);
			if (IsAndroidLike(settings)) TryEvaluate(() => EvaluateAndroid(entry, ctx));
			else if (IsIOS(settings)) TryEvaluate(() => EvaluateIOS(entry, ctx));
			else if (IsWebGL(settings)) TryEvaluate(() => EvaluateWebGL(entry, ctx));

			// Reported only. Unity strips vertex channels it can prove the build does not use, and
			// "the build" does not include a material or shader swapped in at runtime. Whether this
			// project does that is a question about its code, which this analyzer cannot read.
			using (RuleTiming.Begin("PRJ001", entry.Path))
				if (!PlayerSettings.stripUnusedMeshComponents)
				{
					string[] unsafeMeshTokens = { "shader.find(", "new material(", "material.shader =", "sharedmaterial.shader =", "resources.load<shader>", "addressables.loadassetasync<shader>", "loadassetatpath<shader>" };
					bool proofComplete = TrySearchProjectText(ctx, unsafeMeshTokens, out bool shaderSwapOrDynamicShaderUse);
					var issue = entry.AddIssue("PRJ001", "Off", "On after runtime shader/material use is ruled out", 0,
						c => ImporterFixUtility.Project(c, () => PlayerSettings.stripUnusedMeshComponents = true),
						"Enables mesh vertex-channel stripping. Automatic action requires complete project text proof with no known runtime shader/material swapping pattern.",
						verify: _ => PlayerSettings.stripUnusedMeshComponents);
					if (!proofComplete || shaderSwapOrDynamicShaderUse)
						RouteToReview(issue, !proofComplete ? "Complete project proof is unavailable; review runtime shader/material changes before applying." : "Runtime/dynamic shader loading patterns were found; review vertex-channel dependencies before applying.", "Enable Optimize Mesh Data after review");
				}

			using (RuleTiming.Begin("PRJ002", entry.Path))
				try
				{
					var level = PlayerSettings.GetManagedStrippingLevel(target);
					entry.Set("stripping", level.ToString());
					bool platformOwnedElsewhere = settings.targetPlatform == OptimizerPlatform.Android
						|| settings.targetPlatform == OptimizerPlatform.iOS || IsWebGL(settings);
					if (!platformOwnedElsewhere && PreferredManagedStrippingLevel(level) != level)
						entry.AddIssue("PRJ002", level.ToString(), "Low", 0,
							c => ImporterFixUtility.Project(c, () => PlayerSettings.SetManagedStrippingLevel(target, ManagedStrippingLevel.Low)),
							"PRJ002 owns general targets only. Set Disabled/Minimal to Low; preserve Low/Medium/High. Android, iOS and WebGL have dedicated rules. The user may raise the level if a player build reveals a compatibility issue.",
							verify: _ => PlayerSettings.GetManagedStrippingLevel(target) == ManagedStrippingLevel.Low);
				}
				catch { /* the target may not be installed */ }

			// Platform-specific stripping policy overrides the general PRJ002 rule.
			try
			{
				if (settings.targetPlatform == OptimizerPlatform.Android)
				{
					using (RuleTiming.Begin("PAS015", entry.Path))
					{
						var androidStrip = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.Android);
						if (PreferredManagedStrippingLevel(androidStrip) != androidStrip)
							entry.AddIssue("PAS015", androidStrip.ToString(), "Low", 0,
								c => ImporterFixUtility.Project(c, () => PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Android, ManagedStrippingLevel.Low)),
								"Set Android managed stripping to Low only when currently Disabled or Minimal. Preserve Low and stronger levels; the user can raise it manually if a player build demonstrates a compatibility issue.",
								verify: _ => PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.Android) == ManagedStrippingLevel.Low);
					}
				}
				else if (settings.targetPlatform == OptimizerPlatform.iOS)
				{
					using (RuleTiming.Begin("PAS016", entry.Path))
					{
						var iosStrip = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.iOS);
						if (PreferredManagedStrippingLevel(iosStrip) != iosStrip)
							entry.AddIssue("PAS016", iosStrip.ToString(), "Low", 0,
								c => ImporterFixUtility.Project(c, () => PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.iOS, ManagedStrippingLevel.Low)),
								"Set iOS managed stripping to Low only when currently Disabled or Minimal. Preserve Low and stronger levels; the user can raise it manually if a player build demonstrates a compatibility issue.",
								verify: _ => PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.iOS) == ManagedStrippingLevel.Low);
					}
				}
			}
			catch { /* target-specific stripping APIs can be unavailable when the module is not installed */ }

			using (RuleTiming.Begin("PRJ003", entry.Path))
				try
				{
					var backend = PlayerSettings.GetScriptingBackend(target);
					entry.Set("backend", backend.ToString());
					if (settings.IsMobile && backend == ScriptingImplementation.Mono2x)
					{
						var buildTarget = PlatformUtility.ToBuildTarget(settings.targetPlatform);
						var issue = entry.AddIssue("PRJ003", "Mono", "IL2CPP", 0,
							c => ImporterFixUtility.Project(c, () => PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.IL2CPP)),
							"Selects IL2CPP for the selected mobile target. Native plugins are checked first; if a compatible plugin set cannot be established, use the review path.",
							verify: _ => PlayerSettings.GetScriptingBackend(target) == ScriptingImplementation.IL2CPP);
						if (HasNativePluginForTarget(buildTarget))
							RouteToReview(issue, "At least one native plugin is present for this target; source scanning cannot prove complete IL2CPP compatibility.", "Switch to IL2CPP after native-plugin compatibility review");
					}
				}
				catch { /* the target may not be installed */ }

			using (RuleTiming.Begin("PRJ004", entry.Path))
				if (!PlayerSettings.gcIncremental)
				{
					var issue = entry.AddIssue("PRJ004", "Off", "On", 0,
						c => ImporterFixUtility.Project(c, () => PlayerSettings.gcIncremental = true),
						"Enables incremental garbage collection for the Editor's active player-settings target. The setting is a PlayerSettings global in this API surface, so mismatched scan/active targets are not mutated automatically.",
						verify: _ => PlayerSettings.gcIncremental);
					if (EditorUserBuildSettings.activeBuildTarget != PlatformUtility.ToBuildTarget(settings.targetPlatform))
						RouteToDiagnosticOnly(issue, "Selected scan target differs from the active build target; this PlayerSettings property is not target-parameterized here.");
				}

			bool isIl2Cpp = false;
			try
			{
				isIl2Cpp = PlayerSettings.GetScriptingBackend(target) == ScriptingImplementation.IL2CPP;
			}
			catch { /* the target may not be installed */ }

			using (RuleTiming.Begin("PAS023", entry.Path))
				try
				{
					var activeBuildTarget = EditorUserBuildSettings.activeBuildTarget;
					if (activeBuildTarget != PlatformUtility.ToBuildTarget(settings.targetPlatform))
					{
						var activeNamedTarget = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(activeBuildTarget));
						if (PlayerSettings.GetScriptingBackend(activeNamedTarget) == ScriptingImplementation.IL2CPP
							&& PlayerSettings.GetIl2CppCompilerConfiguration(activeNamedTarget) == Il2CppCompilerConfiguration.Debug)
							entry.AddIssue("PAS023", activeBuildTarget + ": Debug", "Release", 0,
								c => ImporterFixUtility.Project(c, () => PlayerSettings.SetIl2CppCompilerConfiguration(activeNamedTarget, Il2CppCompilerConfiguration.Release)),
								"Default IL2CPP configuration is Release for final builds. Development/debugging options can be enabled explicitly on the Player Build page when needed.",
								verify: _ => PlayerSettings.GetIl2CppCompilerConfiguration(activeNamedTarget) == Il2CppCompilerConfiguration.Release);
					}
				}
				catch { /* some build modules may not expose a target-specific IL2CPP configuration */ }

			using (RuleTiming.Begin("PRJ019", entry.Path))
				if (isIl2Cpp && !PlayerSettings.stripEngineCode)
				{
					string[] engineReflectionTokens = { "system.reflection", "gettype(", "assembly.load", "type.gettype", "resources.findobjectsoftypeall", "link.xml", "il2cpp_set_option", "preserveattribute" };
					bool proofComplete = TrySearchProjectText(ctx, engineReflectionTokens, out bool possibleDynamicEngineUse);
					var issue = entry.AddIssue("PRJ019", "Off", "On", 0,
						c => ImporterFixUtility.Project(c, () => PlayerSettings.stripEngineCode = true),
						"Enables engine-module stripping to remove unused Unity engine code. Apply automatically only when project text proof is complete and does not detect known reflection/preservation patterns.",
						verify: _ => PlayerSettings.stripEngineCode);
					if (!proofComplete || possibleDynamicEngineUse || HasNativePluginForTarget(PlatformUtility.ToBuildTarget(settings.targetPlatform)))
						RouteToReview(issue, !proofComplete ? "Complete project text proof is unavailable; engine-module dependencies are not established." : possibleDynamicEngineUse ? "Reflection/preservation patterns were found; verify engine-module dependencies before enabling stripping." : "Native plugin dependencies exist; verify required Unity engine modules before enabling stripping.", "Enable Strip Engine Code after dependency review");
				}

			using (RuleTiming.Begin("PRJ005", entry.Path))
				if (!PlayerSettings.bakeCollisionMeshes)
				{
					bool proofComplete = TrySearchProjectText(ctx, new[] { "meshcollider", "sharedmesh" }, out bool hasMeshColliderReferences);
					var issue = entry.AddIssue("PRJ005", "Off", "On", 0,
						c => ImporterFixUtility.Project(c, () => PlayerSettings.bakeCollisionMeshes = true),
						"Prebakes supported mesh-collider cooking to reduce runtime loading work at the cost of build data/build time. Automatic action requires evidence of mesh-collider use.",
						verify: _ => PlayerSettings.bakeCollisionMeshes);
					if (!proofComplete || !hasMeshColliderReferences)
						RouteToDiagnosticOnly(issue, !proofComplete ? "Complete project proof is unavailable; mesh-collider benefit is not established." : "No mesh-collider use was found in complete project text proof; no automatic change is justified.");
				}

			using (RuleTiming.Begin("PAS024", entry.Path))
			{
				var group = BuildPipeline.GetBuildTargetGroup(PlatformUtility.ToBuildTarget(settings.targetPlatform));
				if (TryGetLightmapStreamingForTarget(group, out bool enabled) && !enabled)
				{
					var issue = entry.AddIssue("PAS024", "Off", "On", 0,
						c => ImporterFixUtility.Project(c, () => { if (!TrySetLightmapStreamingForTarget(group, true)) throw new InvalidOperationException("Unity did not expose a writable lightmap streaming setting for this target."); }),
						"Enables Lightmap Streaming for the selected target when Unity exposes the target-scoped setting. Runtime benefit still depends on relevant lightmap mip streaming configuration.",
						verify: _ => TryGetLightmapStreamingForTarget(group, out bool current) && current);
					if (!HasAnyLightmapStreamingSetter()) RouteToDiagnosticOnly(issue, "This Unity version exposes the getter but not a writable target-scoped setter.");
				}
			}

			AnalyzeBatching(entry, ctx);


			EvaluateValidatedSettings(entry, ctx);

			using (RuleTiming.Begin("PRJ014", entry.Path))
				if (ctx.Settings.projectProofPass && settings.IsMobile && PlayerSettings.accelerometerFrequency != 0)
				{
					string[] sensorTokens = { "input.acceleration", "input.gyro", "accelerometer.current", "gyroscope.current", "gravitysensor.current", "attitudesensor.current", "linearaccelerationsensor.current", "<accelerometer>/acceleration", "<gyroscope>/angularvelocity", "<gravitysensor>/gravity", "<attitudesensor>/attitude", "<linearaccelerationsensor>/acceleration", "\"accelerometer\"", "\"gyroscope\"", "\"gravitysensor\"", "\"attitudesensor\"", "\"linearaccelerationsensor\"" };
					bool proofComplete = TrySearchProjectText(ctx, sensorTokens, out bool sensorUsage);
					var issue = entry.AddIssue("PRJ014", PlayerSettings.accelerometerFrequency + " Hz", "0 Hz when no sensor consumer exists", 0,
						c => ImporterFixUtility.Project(c, () => PlayerSettings.accelerometerFrequency = 0),
						"User-preferred AutoFix: disables accelerometer polling only after complete project text proof finds no known legacy or Input System sensor consumer.",
						verify: _ => PlayerSettings.accelerometerFrequency == 0);
					if (EditorUserBuildSettings.activeBuildTarget != PlatformUtility.ToBuildTarget(settings.targetPlatform))
						RouteToDiagnosticOnly(issue, "Selected target differs from the active target, while this sensor setting is not target-parameterized in the current API path.");
					else if (!proofComplete || sensorUsage || HasNativePluginForTarget(PlatformUtility.ToBuildTarget(settings.targetPlatform)))
						RouteToReview(issue, !proofComplete ? "Complete project proof is unavailable; review legacy and Input System sensor use before disabling." : sensorUsage ? "Known accelerometer/gyroscope sensor references were found; review before disabling." : "Native plugin dependencies exist and may read sensors outside managed project text; review before disabling.", "Disable accelerometer only after sensor-use review");
				}


			using (RuleTiming.Begin("PRJ016", entry.Path))
				if (PlatformUtility.ToNamedBuildTarget(settings.targetPlatform) == NamedBuildTarget.Android)
				{
					var format = EditorUserBuildSettings.androidBuildSubtarget;
					entry.Set("androidTexture", format.ToString());
					var targetFormats = PlayerSettings.Android.textureCompressionFormats;
					bool hasConfiguredTargets = targetFormats != null && targetFormats.Length > 0;
					bool astcFirst = hasConfiguredTargets && targetFormats[0] == TextureCompressionFormat.ASTC;
					bool hasAstc = hasConfiguredTargets && Array.IndexOf(targetFormats, TextureCompressionFormat.ASTC) >= 0;

					// With Texture Compression Targeting, Unity treats the first format as the
					// default; additional formats are for AAB/device targeting. Merely including
					// ASTC second does not make ASTC the preferred/default format.
					bool astcPreferred = format == MobileTextureSubtarget.ASTC || astcFirst;
					if (!astcPreferred)
					{
						entry.AddIssue("PRJ016",
							format + (hasAstc ? " (ASTC available but not preferred)" : " (ASTC not configured)"),
							"ASTC as the preferred/default target", 0, null,
							"Modern Android devices generally benefit from ASTC. If compatibility coverage requires "
							+ "ETC2 or another format, keep it as an additional targeted format rather than assuming "
							+ "its presence alone makes ASTC the default.",
							options: new List<FixOption> { new FixOption {
								Label = "Prefer ASTC while preserving other texture targets",
								Recommended = true,
								Risk = FixRisk.Medium,
								Warning = "Review supported Android devices and the intended build subtarget before applying.",
								Apply = c => ImporterFixUtility.Project(c, () => {
									var current = PlayerSettings.Android.textureCompressionFormats ?? Array.Empty<TextureCompressionFormat>();
									var updated = new List<TextureCompressionFormat> { TextureCompressionFormat.ASTC };
									for (int i = 0; i < current.Length; i++) if (current[i] != TextureCompressionFormat.ASTC) updated.Add(current[i]);
									PlayerSettings.Android.textureCompressionFormats = updated.ToArray();
								}),
								Verify = _ => PlayerSettings.Android.textureCompressionFormats != null && PlayerSettings.Android.textureCompressionFormats.Length > 0 && PlayerSettings.Android.textureCompressionFormats[0] == TextureCompressionFormat.ASTC
							} });
					}
				}

			entry.Finalise();
			return entry;
		}

		static string GetActiveIl2CppConfiguration()
		{
			try
			{
				var property = typeof(EditorUserBuildSettings).GetProperty("il2cppConfiguration", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
				if (property != null) return property.GetValue(null, null)?.ToString();
			}
			catch { }
			return null;
		}

		/// <summary>Batching flags are only reachable through internal APIs, so this degrades quietly if they move.</summary>
		void AnalyzeBatching(AssetEntry entry, ScanContext ctx)
		{
			var settings = ctx.Settings;
			var buildTarget = PlatformUtility.ToBuildTarget(settings.targetPlatform);
			try
			{
				bool staticBatching = PlayerSettings.GetStaticBatchingForPlatform(buildTarget);
				bool dynamicBatching = PlayerSettings.GetDynamicBatchingForPlatform(buildTarget);
				entry.Set("batching", "static " + (staticBatching ? "on" : "off") + " / dynamic " + (dynamicBatching ? "on" : "off"));

				using (RuleTiming.Begin("PRJ006", entry.Path))
					if (!staticBatching)
					{
						bool proofComplete = TrySearchProjectText(ctx, new[] { "meshfilter", "meshrenderer" }, out bool rendererUse);
						bool staticCandidateProof = TrySearchProjectText(ctx, new[] { "m_staticeditorflags: 16", "m_staticeditorflags: 17", "m_staticeditorflags: 18", "m_staticeditorflags: 20", "m_staticeditorflags: 24", "staticeditorflags.batchingstatic" }, out bool staticCandidate);
						bool drawerCoverageKnown;
						bool drawerEnabled = TargetUsesGpuResidentDrawer(settings, out drawerCoverageKnown);
						var issue = entry.AddIssue("PRJ006", "Static batching off", "On", 0,
							c => ImporterFixUtility.Project(c, () => PlayerSettings.SetStaticBatchingForPlatform(buildTarget, true)),
							"Enables static batching for the selected build target, which can reduce CPU draw-call overhead for eligible static renderers. GPU Resident Drawer targets are handled by PRJ046.",
							verify: _ => PlayerSettings.GetStaticBatchingForPlatform(buildTarget));
						if (drawerEnabled)
							RouteToDiagnosticOnly(issue, "The target uses GPU Resident Drawer; PRJ046 owns the static-batching conflict and takes precedence over PRJ006.");
						else if (!drawerCoverageKnown || !proofComplete || !staticCandidateProof)
							RouteToDiagnosticOnly(issue, "Complete target-quality and static-renderer proof is unavailable; enabling static batching automatically is withheld.");
						else if (!rendererUse || !staticCandidate)
							RouteToDiagnosticOnly(issue, "Complete project text proof did not identify both renderer use and serialized static-batching candidates.");
					}

				using (RuleTiming.Begin("PRJ007", entry.Path))
					if (dynamicBatching && UsesScriptableRenderPipelineForTarget(settings))
						entry.AddIssue("PRJ007", "Dynamic batching on", "Off", 0,
							c => ImporterFixUtility.Project(c, () => PlayerSettings.SetDynamicBatchingForPlatform(buildTarget, false)),
							"Disables dynamic batching for a Scriptable Render Pipeline target to avoid extra CPU-side vertex batching work; the setting is target-scoped and postcondition-verified.",
							verify: _ => !PlayerSettings.GetDynamicBatchingForPlatform(buildTarget));
			}
			catch (Exception e)
			{
				entry.Set("batching", "Unavailable");
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not read per-platform batching settings: " + e.Message);
			}
		}

		// --- quality --------------------------------------------------------------

		/// <summary>
		/// Walks the quality levels that the selected platform actually uses, and evaluates the
		/// per-level rules inside each one.
		/// <para>
		/// Almost every setting on QualitySettings belongs to a level, not to the project: reading
		/// <c>QualitySettings.shadowCascades</c> returns whichever level the editor happens to be
		/// sitting on. So each level is entered explicitly, and every finding it produces records
		/// the platform and level index it came from - without that a finding cannot be reproduced,
		/// and a fix derived from one would land on the wrong level.
		/// </para>
		/// </summary>
		AssetEntry AnalyzeQualitySettings(ScanContext ctx)
		{
			var entry = NewEntry("ProjectSettings/QualitySettings.asset", "Quality Settings", "Project/Quality");
			var settings = ctx.Settings;

			int currentLevel = QualitySettings.GetQualityLevel();
			if (currentLevel < 0 || currentLevel >= QualitySettings.names.Length) currentLevel = 0;
			entry.Set("level", QualitySettings.names.Length > 0 ? QualitySettings.names[currentLevel] : "-");
			entry.Set("qualityLevels", (long)QualitySettings.count);

			var buildTarget = PlatformUtility.ToBuildTarget(settings.targetPlatform);
			string platformName = PlatformUtility.ToNamedBuildTarget(settings.targetPlatform).TargetName;

			var activeLevels = GetActiveQualityLevelsForPlatform(buildTarget);
			entry.Set("activeLevels", (long)activeLevels.Length);
			entry.Set("platform", platformName);

			// Whether these levels are the platform's own, or just the one the editor happens to be
			// on, changes what every finding below is entitled to claim.
			bool editorFallback = activeLevels.Length == 0;
			if (editorFallback)
			{
				activeLevels = new[] { currentLevel };
				entry.Set("qualityCoverage", "editor-current-level fallback");
				entry.Set("activeLevelsNote",
					"Unity reported no quality levels for " + platformName + ", so only the editor's current "
					+ "level was read. This is a sample, not coverage of that platform.");
			}
			else
			{
				entry.Set("qualityCoverage", "target-active quality levels");
			}

			for (int i = 0; i < activeLevels.Length; i++)
			{
				int levelIndex = activeLevels[i];
				if (levelIndex < 0 || levelIndex >= QualitySettings.count) continue;

				string levelName = QualitySettings.names[levelIndex];
				WithQualityLevel(levelIndex,
					() => AnalyzeQualityLevel(entry, ctx, platformName, levelIndex, levelName, editorFallback));
			}

			AddMipmapStreamingAggregateIssue(entry, ctx, platformName, activeLevels, editorFallback);
			entry.Finalise();
			return entry;
		}

		/// <summary>Emits a single PRJ008 finding for all affected levels in the selected target profile.</summary>
		static void AddMipmapStreamingAggregateIssue(AssetEntry entry, ScanContext ctx, string platformName,
			int[] activeLevels, bool editorFallback)
		{
			if (entry == null || ctx == null || ctx.Settings == null || activeLevels == null) return;
			var disabledIndices = new List<int>();
			var disabledNames = new List<string>();
			int consideredLevels = 0;
			for (int i = 0; i < activeLevels.Length; i++)
			{
				int index = activeLevels[i];
				if (index < 0 || index >= QualitySettings.count || index >= QualitySettings.names.Length) continue;
				consideredLevels++;
				string name = QualitySettings.names[index];
				string state = entry.Field("streamingMipmaps." + index + "." + name) as string;
				if (!string.Equals(state, "Off", StringComparison.Ordinal)) continue;
				disabledIndices.Add(index);
				disabledNames.Add(name + " (index " + index + ")");
			}
			if (disabledIndices.Count == 0) return;

			OptimizerPlatform targetPlatform = ctx.Settings.targetPlatform;
			int[] expectedIndices = disabledIndices.ToArray();
			string[] expectedNames = expectedIndices.Select(index => QualitySettings.names[index]).ToArray();
			var issue = entry.AddIssue("PRJ008", disabledIndices.Count + " of " + consideredLevels + " target Quality Level(s) have Streaming Mipmaps Off",
				"Enable Streaming Mipmaps on the affected levels", 0,
				editorFallback ? null : (Action<FixContext>)(c => ImporterFixUtility.Project(c, () => MipmapStreamingFixUtility.EnableForTargetLevels(targetPlatform, expectedIndices, expectedNames))),
				"This is one target-aware finding aggregated across the selected platform's active Quality Levels. Streaming lowers resident texture memory only when relevant textures are also marked for streaming; it trades this benefit for CPU and IO work.",
				SavingsBasis.Unknown,
				verify: editorFallback ? null : (Func<FixContext, bool>)(_ => MipmapStreamingFixUtility.AreTargetLevelsEnabled(targetPlatform, expectedIndices, expectedNames)));
			if (issue == null) return;
			issue.Note("Target profile: " + ctx.Settings.Profile.DisplayName + "; graphics target: " + platformName + ".", "Target scope");
			issue.Items("Affected Quality Levels", disabledNames);
			if (editorFallback)
			{
				RouteToDiagnosticOnly(issue, "Unity returned no active Quality Levels for this target, so the finding uses the editor's current level only. No automatic target-level action is safe until target coverage is available.");
				issue.Note("Unity returned no active Quality Levels for this target, so the finding uses the editor's current level only.", "Fallback");
			}
		}

		/// <summary>
		/// Evaluates the rules that read per-level QualitySettings state. Called from inside
		/// <see cref="WithQualityLevel"/>, so the statics below refer to <paramref name="levelIndex"/>.
		/// <para>
		/// Quality settings remain advisory by default. A small number of explicitly approved rules use
		/// deterministic, verified mutations; those actions are available only when the target Quality
		/// Level is resolved and validated. Unresolved editor-current fallbacks remain diagnostic-only.
		/// </para>
		/// </summary>
		void AnalyzeQualityLevel(AssetEntry entry, ScanContext ctx, string platform, int levelIndex,
			string levelName, bool editorFallback)
		{
			string at = levelName + ": ";

			// Every finding this method makes goes through here, so the fallback caveat cannot be
			// attached to some of them and forgotten on others. A reading taken from the editor's
			// current level is evidence about that level and a guess about the platform, and the
			// confidence is dropped to match rather than the finding being labelled target-specific.
			System.Action<OptimizationIssue> mark = issue =>
			{
				if (issue == null) return;
				issue.AtQualityLevel(platform, levelIndex, levelName);

				if (!editorFallback) return;
				issue.Confidence = OptimizationConfidence.Heuristic;
				if (issue.FixKind == FixKind.Verified)
					RouteToDiagnosticOnly(issue, "The selected target has no resolved active Quality Levels; the editor's current level is only a sample, not a verified target setting.");
				issue.Note("Unity listed no quality levels for " + platform + ", so this was read from the "
						   + "editor's current level (" + levelName + ") instead. It may not be a level "
						   + platform + " actually ships.", "Fallback");
			};

			// Texture streaming and anisotropic filtering are per-level settings, not project-wide
			// ones. Reading them outside a level walk answers for whichever level the editor was
			// showing, and writing them there would have changed that level instead of the one that
			// produced the finding.
			entry.Set("streamingMipmaps." + levelName, QualitySettings.streamingMipmapsActive ? "On" : "Off");
			entry.Set("streamingMipmaps." + levelIndex + "." + levelName, QualitySettings.streamingMipmapsActive ? "On" : "Off");

			entry.Set("aniso." + levelName, QualitySettings.anisotropicFiltering.ToString());
			using (RuleTiming.Begin("PRJ009", entry.Path))
				if (QualitySettings.anisotropicFiltering == AnisotropicFiltering.ForceEnable)
				{
					mark(entry.AddIssue("PRJ009", at + "Forced On", "Per Texture", 0,
						c => SetAnisotropicFilteringPerTexture(c, levelIndex, levelName),
						"Changes only this validated Quality Level from Force Enable to Enable (Per Texture), restoring texture importer anisotropic settings instead of forcing filtering globally.",
						verify: _ => ReadQualityLevelStrict(levelIndex, levelName, () => QualitySettings.anisotropicFiltering) == AnisotropicFiltering.Enable));
				}

			entry.Set("vSync." + levelName, (long)QualitySettings.vSyncCount);

			// Built-in Render Pipeline keeps these controls in QualitySettings.
			// URP moves the relevant controls onto the URP asset and is handled there.
			if (IsCurrentQualityLevelBuiltInPipeline())
			{
				float shadowDistanceBudget = ctx.Settings.Profile.ShadowDistanceWarn;
				entry.Set("shadowDistance." + levelName, (double)QualitySettings.shadowDistance);
				if (shadowDistanceBudget > 0f && QualitySettings.shadowDistance > shadowDistanceBudget)
					mark(entry.AddIssue("PRJ011",
						at + "Shadow Distance " + QualitySettings.shadowDistance.ToString("0") + " m",
						"under " + shadowDistanceBudget.ToString("0") + " m", 0,
						c => SetQualitySetting(c, levelIndex, levelName, () => QualitySettings.shadowDistance = shadowDistanceBudget),
						"Applies the configured shadow-distance budget to the exact target Quality Level.",
						verify: _ => ReadQualityLevelStrict(levelIndex, levelName, () => QualitySettings.shadowDistance) <= shadowDistanceBudget + 0.01f));

				if (ctx.Settings.IsVR && QualitySettings.antiAliasing < 4)
					mark(entry.AddIssue("PRJ018",
						at + "MSAA " + (QualitySettings.antiAliasing <= 1 ? "Disabled" : QualitySettings.antiAliasing + "x"),
						"4x minimum for VR profiles", 0,
						c => SetQualitySetting(c, levelIndex, levelName, () => QualitySettings.antiAliasing = RaiseVrMsaaToFour(QualitySettings.antiAliasing)),
						"Raises Built-in VR MSAA to 4x only when lower than 4x. Existing settings above 4x are preserved.",
						verify: _ => ReadQualityLevelStrict(levelIndex, levelName, () => QualitySettings.antiAliasing) >= 4));
			}

			// maxQueuedFrames is only implemented by D3D11, D3D12 and Vulkan in Unity 6.
			using (RuleTiming.Begin("PRJ058", entry.Path))
				if (QualitySettings.maxQueuedFrames > 2 && TargetSupportsMaxQueuedFrames(ctx.Settings))
					mark(entry.AddIssue("PRJ058", at + QualitySettings.maxQueuedFrames + " queued frames",
						"Review for latency", 0, null,
						"Unity only uses this setting on D3D11, D3D12 and Vulkan. On those APIs, larger queues can increase input latency.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ058 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => SetQualitySetting(c, levelIndex, levelName, () => QualitySettings.maxQueuedFrames = Mathf.Min(QualitySettings.maxQueuedFrames, 2)),
							Verify = _ => ReadQualityLevelStrict(levelIndex, levelName, () => QualitySettings.maxQueuedFrames) <= 2
						} }
					));

			if (!ctx.Settings.IsMobile) return;

			using (RuleTiming.Begin("PRJ049", entry.Path))
				if (ShouldClampBuiltinMobileMsaa(ctx.Settings.IsMobile, ctx.Settings.IsVR, IsCurrentQualityLevelBuiltInPipeline(), QualitySettings.antiAliasing))
					mark(entry.AddIssue("PRJ049", at + QualitySettings.antiAliasing + "x", "2x maximum", 0,
						c => SetQualitySetting(c, levelIndex, levelName, () => QualitySettings.antiAliasing = ClampBuiltinMobileMsaa(QualitySettings.antiAliasing)),
						"Clamps Built-in mobile MSAA to 2x only when it is above 2x. Existing 2x, 1x and disabled values are preserved.",
						verify: _ => ReadQualityLevelStrict(levelIndex, levelName, () => QualitySettings.antiAliasing) <= 2));

			using (RuleTiming.Begin("PRJ038", entry.Path))
				if (IsCurrentQualityLevelBuiltInPipeline() && QualitySettings.shadowCascades > 1)
					mark(entry.AddIssue("PRJ038", at + QualitySettings.shadowCascades + " cascades", "1 cascade or none", 0, null,
						"Choose one cascade only when it meets the target's shadow quality requirement.",
						options: new List<FixOption> { new FixOption {
							Label = "Set this quality level to 1 shadow cascade",
							Recommended = false,
							Risk = FixRisk.Medium,
							Warning = "Review shadow quality on the target device.",
							Apply = c => SetQualitySetting(c, levelIndex, levelName, () => QualitySettings.shadowCascades = 1),
							Verify = _ => ReadQualityLevelStrict(levelIndex, levelName, () => QualitySettings.shadowCascades) == 1
						} }
						));

			entry.Set("lodBias." + levelName, (double)QualitySettings.lodBias);
			using (RuleTiming.Begin("PRJ039", entry.Path))
				if (QualitySettings.lodBias > 1.01f)
					mark(entry.AddIssue("PRJ039", at + "LOD Bias " + QualitySettings.lodBias.ToString("0.##"), "Profile-appropriate value", 0, null,
						"LOD Bias is content-dependent; the review option sets this quality level to 1.0, which should be visually checked.",
						options: new List<FixOption> { new FixOption {
							Label = "Set this quality level LOD Bias to 1.0",
							Recommended = false,
							Risk = FixRisk.Medium,
							Warning = "Check LOD transition distance and visible quality after applying.",
							Apply = c => SetQualitySetting(c, levelIndex, levelName, () => QualitySettings.lodBias = 1f),
							Verify = _ => ReadQualityLevelStrict(levelIndex, levelName, () => QualitySettings.lodBias) <= 1.001f
						} }
						));

			entry.Set("skinWeights." + levelName, QualitySettings.skinWeights.ToString());
			using (RuleTiming.Begin("PRJ040", entry.Path))
				if (QualitySettings.skinWeights == SkinWeights.FourBones || QualitySettings.skinWeights == SkinWeights.Unlimited)
					mark(entry.AddIssue("PRJ040", at + QualitySettings.skinWeights, "Two or four bones as appropriate", 0, null,
						"Use the lowest influence count that preserves the required deformation quality.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ040 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => SetQualitySetting(c, levelIndex, levelName, () => QualitySettings.skinWeights = SkinWeights.TwoBones),
							Verify = _ => ReadQualityLevelStrict(levelIndex, levelName, () => QualitySettings.skinWeights) == SkinWeights.TwoBones
						} }
					)
						);

			using (RuleTiming.Begin("PRJ041", entry.Path))
				if (QualitySettings.realtimeReflectionProbes)
				{
					string[] tokens = { "reflectionprobemode.realtime", "refreshmode = reflectionprobemodetype.realtime", "reflectionprobe.refreshmode", "setprobeandrenderingdata", "rendertexturecubemap(" };
					bool complete = TrySearchProjectText(ctx, tokens, out bool usageFound);
					var issue = entry.AddIssue("PRJ041", at + "On", "Off", 0,
						c => SetQualitySetting(c, levelIndex, levelName, () => QualitySettings.realtimeReflectionProbes = false),
						"Disables realtime reflection-probe updates only when complete project text proof finds no explicit realtime reflection-probe workflow.",
						verify: _ => !ReadQualityLevelStrict(levelIndex, levelName, () => QualitySettings.realtimeReflectionProbes));
					if (!complete || usageFound) RouteToReview(issue, !complete ? "Complete project proof is unavailable; review reflection-probe dependencies before disabling." : "Realtime reflection-probe usage patterns were found; review the affected reflection workflows before disabling.", "Disable realtime reflection probes after review");
				}

			using (RuleTiming.Begin("PRJ042", entry.Path))
				if (QualitySettings.softParticles)
				{
					string[] tokens = { "softparticles_on", "softparticles", "soft particles", "softparticlesfeature" };
					bool complete = TrySearchProjectText(ctx, tokens, out bool usageFound);
					var issue = entry.AddIssue("PRJ042", at + "On", "Off", 0,
						c => SetQualitySetting(c, levelIndex, levelName, () => QualitySettings.softParticles = false),
						"Disables Built-in soft particles only when complete project text proof finds no known soft-particle consumers.",
						verify: _ => !ReadQualityLevelStrict(levelIndex, levelName, () => QualitySettings.softParticles));
					if (!complete || usageFound) RouteToReview(issue, !complete ? "Complete project proof is unavailable; review soft-particle material/shader use before disabling." : "Soft-particle shader/material patterns were found; review dependencies before disabling.", "Disable soft particles after review");
				}

			using (RuleTiming.Begin("PRJ053", entry.Path))
				if (IsCurrentQualityLevelBuiltInPipeline() && QualitySettings.enableLODCrossFade)
				{
					string[] tokens = { "animatecrossfading", "crossfadeanimationduration", "enablelodcrossfade", "m_AnimateCrossFading" };
					bool complete = TrySearchProjectText(ctx, tokens, out bool usageFound);
					var issue = entry.AddIssue("PRJ053", at + "On", "Off", 0,
						c => SetQualitySetting(c, levelIndex, levelName, () => ReflectionUtility.SetStaticBool(typeof(QualitySettings), "lodCrossFade", false)),
						"Disables Built-in LOD crossfade on mobile only when project text proof finds no explicit dependency on crossfade timing.",
						verify: _ => !ReadQualityLevelStrict(levelIndex, levelName, () => QualitySettings.enableLODCrossFade));
					if (!complete || usageFound) RouteToReview(issue, !complete ? "Complete project proof is unavailable; review LOD crossfade dependencies before disabling." : "LOD crossfade configuration patterns were found; review transition expectations before disabling.", "Disable LOD Cross Fade after review");
				}

			using (RuleTiming.Begin("PRJ054", entry.Path))
				if (IsCurrentQualityLevelBuiltInPipeline() && QualitySettings.shadows == ShadowQuality.All)
					mark(entry.AddIssue("PRJ054", at + "Soft shadows", "Hard shadows or lower-cost setting", 0, null,
						options: new List<FixOption> { new FixOption {
							Label = "PRJ054 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => SetQualitySetting(c, levelIndex, levelName, () => QualitySettings.shadows = ShadowQuality.HardOnly),
							Verify = _ => ReadQualityLevelStrict(levelIndex, levelName, () => QualitySettings.shadows) == ShadowQuality.HardOnly
						} }
					)
						);

			entry.Set("maxQueuedFrames." + levelName, (long)QualitySettings.maxQueuedFrames);
			if (QualitySettings.maxQueuedFrames > 2)
				mark(entry.AddIssue("PRJ058", at + QualitySettings.maxQueuedFrames + " queued frames", "Review for latency", 0, null,
						options: new List<FixOption> { new FixOption {
							Label = "PRJ058 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => SetQualitySetting(c, levelIndex, levelName, () => QualitySettings.maxQueuedFrames = Mathf.Min(QualitySettings.maxQueuedFrames, 2)),
							Verify = _ => ReadQualityLevelStrict(levelIndex, levelName, () => QualitySettings.maxQueuedFrames) <= 2
						} }
					)
					);
		}

		// --- render pipeline ----------------------------------------------------------

		/// <summary>
		/// One render pipeline asset, and which quality levels select it.
		/// </summary>
		sealed class PipelineUse
		{
			public UnityEngine.Object Asset;
			public string AssetPath;
			public readonly List<int> LevelIndices = new List<int>();
			public readonly List<string> LevelNames = new List<string>();

			/// <summary>True when this is the project-wide default rather than a per-level override.</summary>
			public bool IsProjectDefault;
		}

		/// <summary>
		/// Finds every render pipeline asset the selected target's quality levels actually use.
		/// <para>
		/// <c>GraphicsSettings.currentRenderPipeline</c> resolves against whichever quality level the
		/// editor is sitting on, so using it as the project's pipeline analyzes one level and
		/// attributes the result to the whole target. A project with a cut-down URP asset on its
		/// mobile tier and a full one on desktop would have had whichever the editor was showing
		/// reported for both - and PRJ010's fix would have written to that one asset.
		/// </para>
		/// </summary>
		List<PipelineUse> ResolvePipelines(ScanContext ctx, out bool usedEditorFallback)
		{
			usedEditorFallback = false;

			var buildTarget = PlatformUtility.ToBuildTarget(ctx.Settings.targetPlatform);
			var levels = GetActiveQualityLevelsForPlatform(buildTarget);

			if (levels.Length == 0)
			{
				usedEditorFallback = true;
				levels = new[] { QualitySettings.GetQualityLevel() };
			}

			var byPath = new Dictionary<string, PipelineUse>(StringComparer.Ordinal);
			var projectDefault = GraphicsSettings.defaultRenderPipeline;

			for (int i = 0; i < levels.Length; i++)
			{
				int levelIndex = levels[i];
				if (levelIndex < 0 || levelIndex >= QualitySettings.count) continue;

				UnityEngine.Object resolved = null;
				bool isDefault = false;

				WithQualityLevel(levelIndex, () =>
				{
					// The per-level override wins; where a level sets none it inherits the project
					// default, which is the distinction this whole method exists to preserve.
					var levelPipeline = QualitySettings.renderPipeline;
					isDefault = levelPipeline == null;
					resolved = levelPipeline != null ? levelPipeline : projectDefault;
				});

				var key = resolved != null ? AssetDatabase.GetAssetPath(resolved) : string.Empty;
				if (string.IsNullOrEmpty(key)) key = "<built-in>";

				if (!byPath.TryGetValue(key, out var use))
				{
					use = new PipelineUse
					{
						Asset = resolved,
						AssetPath = resolved != null ? AssetDatabase.GetAssetPath(resolved) : null,
						IsProjectDefault = isDefault
					};
					byPath[key] = use;
				}

				use.LevelIndices.Add(levelIndex);
				use.LevelNames.Add(QualitySettings.names[levelIndex]);
			}

			return new List<PipelineUse>(byPath.Values);
		}

		IEnumerable<AssetEntry> AnalyzeRenderPipelines(ScanContext ctx)
		{
			var uses = ResolvePipelines(ctx, out bool usedEditorFallback);
			string platformName = PlatformUtility.ToNamedBuildTarget(ctx.Settings.targetPlatform).TargetName;

			foreach (var use in uses)
				yield return AnalyzeOnePipeline(ctx, use, platformName, usedEditorFallback);
		}

		AssetEntry AnalyzeOnePipeline(ScanContext ctx, PipelineUse use, string platformName, bool usedEditorFallback)
		{
			var pipeline = use.Asset;
			var path = pipeline != null ? use.AssetPath : "ProjectSettings/GraphicsSettings.asset";
			var entry = NewEntry(path, pipeline != null ? pipeline.name : "Graphics Settings", "Project/Graphics");

			entry.Set("pipeline", pipeline != null ? pipeline.GetType().Name : "Built-in");
			entry.Set("platform", platformName);
			entry.Set("qualityLevels", string.Join(", ", use.LevelNames.ToArray()));
			entry.Set("pipelineCoverage", use.LevelNames.Count > 1 ? "Shared by multiple target quality levels" : "Single target quality level");
			entry.Set("isProjectDefaultPipeline", use.IsProjectDefault ? "Yes" : "No");

			using (RuleTiming.Begin("PAS025", entry.Path))
				if (pipeline == null)
				{
					// ProjectOptimizer's configured product intent is URP. Assign the sole URP asset when
					// unambiguous; multiple candidate assets preserve the user's choice as ReviewFix options.
					List<string> candidates = FindUrpAssetPaths();
					var issue = entry.AddIssue("PAS025", "Built-in Render Pipeline / no URP Asset", "Assign a URP Asset");
					if (candidates.Count == 1)
					{
						string candidatePath = candidates[0];
						issue.Fix = c =>
						{
							c.EnsureWritable();
						if (GraphicsSettings.defaultRenderPipeline != null)
							throw new InvalidOperationException("Graphics Settings changed after analysis. Rescan before assigning a URP asset.");
						var candidate = AssetDatabase.LoadMainAssetAtPath(candidatePath) as RenderPipelineAsset;
						if (candidate == null || !IsUrpPipeline(candidate)) throw new InvalidOperationException("The selected URP asset is unavailable or no longer a URP asset.");
						c.RequireBackup(GraphicsSettingsAssetPath());
						GraphicsSettings.defaultRenderPipeline = candidate;
						EditorUtility.SetDirty(GraphicsSettings.GetGraphicsSettings());
						c.NeedsSave = true;
					};
					issue.Verify = _ => GraphicsSettings.defaultRenderPipeline != null && string.Equals(AssetDatabase.GetAssetPath(GraphicsSettings.defaultRenderPipeline), candidatePath, StringComparison.Ordinal);
					issue.Note("Only one URP asset was found; assignment targets Graphics Settings and is backup-backed.", "Automatic assignment");
					}
					else if (candidates.Count > 1)
					{
						issue.FixKind = FixKind.Review;
						issue.Fix = null;
						issue.Verify = null;
						issue.Confidence = OptimizationConfidence.Heuristic;
						issue.Options.Clear();
						foreach (string candidatePath in candidates)
						{
							string selectedPath = candidatePath;
						issue.Options.Add(new FixOption
							{
								Label = "Assign URP asset: " + selectedPath,
								Recommended = false,
								Risk = FixRisk.Medium,
								Warning = "Choose the URP asset that matches this project's intended rendering configuration.",
								Apply = c =>
								{
									c.EnsureWritable();
									if (GraphicsSettings.defaultRenderPipeline != null) throw new InvalidOperationException("Graphics Settings changed after analysis. Rescan first.");
									var candidate = AssetDatabase.LoadMainAssetAtPath(selectedPath) as RenderPipelineAsset;
									if (candidate == null || !IsUrpPipeline(candidate)) throw new InvalidOperationException("The selected URP asset is unavailable.");
									c.RequireBackup(GraphicsSettingsAssetPath());
									GraphicsSettings.defaultRenderPipeline = candidate;
									EditorUtility.SetDirty(GraphicsSettings.GetGraphicsSettings());
									c.NeedsSave = true;
								},
								Verify = _ => GraphicsSettings.defaultRenderPipeline != null && string.Equals(AssetDatabase.GetAssetPath(GraphicsSettings.defaultRenderPipeline), selectedPath, StringComparison.Ordinal)
							});
						}
						issue.Note("Multiple URP assets exist; the user must choose which one should become the default.", "Review required");
					}
					else RouteToDiagnosticOnly(issue, "No URP asset was found in Assets. A safe fix cannot assign an asset that does not exist; create/import a URP asset first.");
					entry.Finalise();
					return entry;
				}

			// Every finding below is attributed to the first level that selects this asset, and its
			// note names them all. A fix reaches the asset by path, so it cannot land on whichever
			// pipeline the editor is showing when the button is pressed.
			int levelIndex = use.LevelIndices.Count > 0 ? use.LevelIndices[0] : -1;
			string levelName = use.LevelNames.Count > 0 ? string.Join(", ", use.LevelNames.ToArray()) : null;
			string pipelinePath = use.AssetPath;

			System.Action<OptimizationIssue> attribute = issue =>
			{
				if (issue == null) return;
				issue.AtQualityLevel(platformName, levelIndex, levelName);

				if (usedEditorFallback)
				{
					if (issue.FixKind == FixKind.Verified)
						RouteToDiagnosticOnly(issue, "The selected target has no resolved active Quality Levels; the Editor's current pipeline is only a sample, not verified target coverage.");
					issue.Note("Unity reported no per-platform quality level list, so this was read from the editor's current quality level rather than from the target's own tiers. Treat it as a sample, not as coverage of " + platformName + ".", "Fallback");
					issue.Confidence = OptimizationConfidence.Heuristic;
				}
				else if (use.LevelNames.Count > 0)
					issue.Note("Used by quality level(s): " + string.Join(", ", use.LevelNames.ToArray()) + " on " + platformName + ".", "Applies to");
			};

			try
			{
				var so = new SerializedObject(pipeline);
				EvaluateUnity6Rendering(entry, ctx, so, pipelinePath, attribute);

				using (RuleTiming.Begin("PRJ010", entry.Path))
				{
					var srpBatcher = so.FindProperty("m_UseSRPBatcher");
					if (srpBatcher != null)
					{
						entry.Set("srpBatcher", srpBatcher.boolValue ? "On" : "Off");
						if (!srpBatcher.boolValue)
						{
							// Automatic only for URP and HDRP, whose SRP Batcher behaviour is documented:
							// it takes over for shaders that declare themselves compatible and leaves
							// everything else on its existing path, so nothing renders differently. A
							// custom pipeline can read the same property and mean something else by it,
							// and there is no way to find out from here.
							bool knownPipeline = IsUrpPipeline(pipeline) || IsHdrpPipeline(pipeline);

							if (!knownPipeline)
							{
								attribute(entry.AddIssue("PRJ072", "Off", "On, if your pipeline supports it", 0, null,
									"This is a custom render pipeline asset. It exposes an SRP Batcher setting, but "
									+ "what the setting does in it is up to its author, so the change is left to you."));
							}
							else
							{
								attribute(entry.AddIssue("PRJ010", "Off", "On", 0,
									c => RenderPipelineUtility.SetBool(c, pipelinePath, "m_UseSRPBatcher", true),
									verify: _ => RenderPipelineUtility.ReadBool(pipelinePath, "m_UseSRPBatcher") == true));
							}
						}
					}
				}

				using (RuleTiming.Begin("PRJ011", entry.Path))
				{
					var shadowDistance = so.FindProperty("m_ShadowDistance");
					if (shadowDistance != null)
					{
						float budget = ctx.Settings.Profile.ShadowDistanceWarn;
						entry.Set("shadowDistance", (double)shadowDistance.floatValue);
						if (budget > 0f && shadowDistance.floatValue > budget)
							attribute(entry.AddIssue("PRJ011", shadowDistance.floatValue.ToString("0") + " m",
								"under " + budget.ToString("0") + " m", 0,
								c => RenderPipelineUtility.SetFloat(c, pipelinePath, "m_ShadowDistance", budget),
								"Applies the configured shadow-distance budget to this target's resolved render-pipeline asset.",
								verify: _ => ReadPipelineFloat(pipelinePath, "m_ShadowDistance") <= budget + 0.01f));
					}
				}

				// VR renders the scene once per eye, so edge quality and fill cost both matter more.
				using (RuleTiming.Begin("PRJ018", entry.Path))
				{
					if (ctx.Settings.IsVR)
					{
						var msaa = so.FindProperty("m_MSAA");
						if (msaa != null)
						{
							entry.Set("msaa", (long)msaa.intValue);
							if (msaa.intValue < 4)
								attribute(entry.AddIssue("PRJ018", msaa.intValue <= 1 ? "Disabled" : msaa.intValue + "x",
									"4x minimum for VR profiles", 0,
									c => RenderPipelineUtility.SetInt(c, pipelinePath, "m_MSAA", 4),
									"Raises URP MSAA to 4x only when below 4x. Existing values above 4x are preserved.",
									verify: _ => RenderPipelineUtility.ReadInt(pipelinePath, "m_MSAA") >= 4));
						}
					}
				}
			}
			catch (Exception e)
			{
				// Unknown pipeline asset layout. Recorded rather than swallowed: an entry with no
				// findings and no explanation reads as a clean pipeline.
				entry.Set("analysisError", e.Message);
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not fully analyze the render pipeline asset '"
								 + path + "': " + e.Message);
			}

			entry.Finalise();
			return entry;
		}

		void EvaluateValidatedSettings(AssetEntry entry, ScanContext ctx)
		{
			using (RuleTiming.Begin("PRJ060", entry.Path))
				if (!EditorSettings.asyncShaderCompilation)
					entry.AddIssue("PRJ060", "Off", "On", 0, c => ImporterFixUtility.Project(c, () => EditorSettings.asyncShaderCompilation = true),
						verify: _ => EditorSettings.asyncShaderCompilation);

			using (RuleTiming.Begin("PRJ061", entry.Path))
			{
				var persisted = EditorSettings.refreshImportMode;
				var effective = AssetDatabase.ActiveRefreshImportMode;
				bool overrideActive = HasRefreshModeCommandLineOverride();
				entry.Set("refreshImportPersisted", persisted.ToString());
				entry.Set("refreshImportEffective", effective.ToString());
				if (persisted != AssetDatabase.RefreshImportMode.OutOfProcessPerQueue || effective != AssetDatabase.RefreshImportMode.OutOfProcessPerQueue)
				{
					var issue = entry.AddIssue("PRJ061", persisted + " / " + effective,
						"OutOfProcessPerQueue / OutOfProcessPerQueue", 0,
						c => ImporterFixUtility.Project(c, () =>
						{
							EditorSettings.refreshImportMode = AssetDatabase.RefreshImportMode.OutOfProcessPerQueue;
							AssetDatabase.ActiveRefreshImportMode = AssetDatabase.RefreshImportMode.OutOfProcessPerQueue;
						}),
						"Sets persisted and active import mode. A detected command-line override takes precedence and blocks the automatic mutation.",
						verify: _ => EditorSettings.refreshImportMode == AssetDatabase.RefreshImportMode.OutOfProcessPerQueue && AssetDatabase.ActiveRefreshImportMode == AssetDatabase.RefreshImportMode.OutOfProcessPerQueue);
					if (overrideActive) RouteToDiagnosticOnly(issue, "A refresh-mode command-line override is active; do not overwrite command-line policy with a persisted setting.");
				}
			}


			using (RuleTiming.Begin("PRJ062", entry.Path))
			{
				int logical = Math.Max(1, SystemInfo.processorCount);
				int memoryMb = Math.Max(0, SystemInfo.systemMemorySize);
				int baseline = Math.Max(1, Mathf.CeilToInt(logical * 0.25f));
				int safeMemoryWorkers = memoryMb >= 32768 ? baseline : memoryMb >= 16384 ? Math.Max(1, Math.Min(baseline, logical / 4)) : 0;
				int desired = EditorUserSettings.desiredImportWorkerCount;
				int standby = EditorUserSettings.standbyImportWorkerCount;
				int delay = EditorUserSettings.idleImportWorkerShutdownDelayMilliseconds;
				entry.Set("logicalProcessors", (long)logical);
				entry.Set("systemMemoryMb", (long)memoryMb);
				entry.Set("desiredImportWorkers", (long)desired);
				entry.Set("standbyImportWorkers", (long)standby);
				entry.Set("idleImportWorkerShutdownDelayMs", (long)delay);
				bool ci = Environment.GetEnvironmentVariable("CI") != null || Environment.GetEnvironmentVariable("BUILD_NUMBER") != null;
				if (safeMemoryWorkers > 0 && desired > 0 && desired < safeMemoryWorkers && !ci)
				{
					var issue = entry.AddIssue("PRJ062", desired + " workers", safeMemoryWorkers + " workers for this host", 0,
						c => { c.EnsureWritable(); EditorUserSettings.desiredImportWorkerCount = safeMemoryWorkers; c.ChangedProjectSettings = true; },
						"Raises an unusually low import-worker count only on hosts with at least 16 GiB RAM, bounded by the conservative CPU-derived baseline. CI environments and low-memory hosts are left unchanged.",
						verify: _ => EditorUserSettings.desiredImportWorkerCount >= safeMemoryWorkers);
					if (EditorUserSettings.desiredImportWorkerCount != desired) RouteToDiagnosticOnly(issue, "Import worker count changed during analysis; rescan before applying.");
				}
			}


			// The legacy direct AssetBundle toggle remains inspectable in Unity 6.
#pragma warning disable CS0618
			using (RuleTiming.Begin("PRJ063", entry.Path))
			{
				bool direct = ctx.Settings.projectProofPass && HasDirectAssetBundleBuildUsage(ctx);
				if (direct && !EditorBuildSettings.UseParallelAssetBundleBuilding)
					entry.AddIssue("PRJ063", "Off", "On", 0,
						c => ImporterFixUtility.Project(c, () => EditorBuildSettings.UseParallelAssetBundleBuilding = true),
						"The complete project proof contains direct BuildPipeline.BuildAssetBundles() usage. Addressables/SBP-only workflows are not claimed by this detection.",
						verify: _ => EditorBuildSettings.UseParallelAssetBundleBuilding);
			}

			#pragma warning restore CS0618
			using (RuleTiming.Begin("PRJ064", entry.Path))
			{
				var profile = ctx.BuildProfiles != null ? ctx.BuildProfiles.Active : null;
				bool server = profile != null ? profile.IsDedicatedServer : IsEffectiveDedicatedServerBuild();
				if (server)
				{
					var owner = profile?.PlayerSettingsOwner;
					bool? enabled = owner != null ? ReadOwnedBool(owner, "dedicatedServerOptimizations") : PlayerSettings.dedicatedServerOptimizations;
					if (enabled == false)
					{
						string ownerWriteBlockReason = owner != null ? OwnedBoolWriteBlockReason(owner) : null;
						Action<FixContext> enable = owner != null
							? c => SetOwnedBool(owner, "dedicatedServerOptimizations", true, c)
							: c => ImporterFixUtility.Project(c, () => PlayerSettings.dedicatedServerOptimizations = true);
						var issue = entry.AddIssue("PRJ064", "Off", "On", 0, enable,
							owner != null ? "The effective Dedicated Server Build Profile has a profile-local PlayerSettings owner; the fix targets that owner." : "The effective build context is a Standalone Dedicated Server / Server subtarget with global PlayerSettings ownership.",
							verify: _ => { bool? value = owner != null ? ReadOwnedBool(owner, "dedicatedServerOptimizations") : PlayerSettings.dedicatedServerOptimizations; return value == true; });
						if (!string.IsNullOrEmpty(ownerWriteBlockReason))
							RouteToDiagnosticOnly(issue, ownerWriteBlockReason);
					}
				}
			}

			using (RuleTiming.Begin("PRJ065", entry.Path))
				TryEvaluate(() => EvaluateShaderVariantMemory(entry, ctx));

			using (RuleTiming.Begin("PRJ066", entry.Path))
				TryEvaluate(() => EvaluateLightmapPolicy(entry, ctx));

			using (RuleTiming.Begin("PRJ067", entry.Path))
				TryEvaluate(() => EvaluateGiPolicy(entry, ctx));

			using (RuleTiming.Begin("PRJ068", entry.Path))
			{
				int positionLimit = Math.Max(0, ctx.Settings.physics2DPositionIterationsReview);
				int velocityLimit = Math.Max(0, ctx.Settings.physics2DVelocityIterationsReview);
				if (Has2DPhysicsUsage(ctx) && ((positionLimit > 0 && Physics2D.positionIterations > positionLimit) || (velocityLimit > 0 && Physics2D.velocityIterations > velocityLimit)))
					entry.AddIssue("PRJ068", Physics2D.positionIterations + "/" + Physics2D.velocityIterations, "Position <= " + positionLimit + ", velocity <= " + velocityLimit, 0,
						c => ImporterFixUtility.Project(c, () => { if (positionLimit > 0) Physics2D.positionIterations = Math.Min(Physics2D.positionIterations, positionLimit); if (velocityLimit > 0) Physics2D.velocityIterations = Math.Min(Physics2D.velocityIterations, velocityLimit); }),
						"Applies configured 2D solver iteration caps. These caps represent the project's policy; test Rigidbody/joint stability after applying.",
						verify: _ => (positionLimit <= 0 || Physics2D.positionIterations <= positionLimit) && (velocityLimit <= 0 || Physics2D.velocityIterations <= velocityLimit));
			}

			using (RuleTiming.Begin("PRJ069", entry.Path))
				if (Has3DPhysicsUsage(ctx) && ctx.Settings.physicsVelocityIterationsReview > 0 && Physics.defaultSolverVelocityIterations > ctx.Settings.physicsVelocityIterationsReview)
				{
					int velocityLimit = ctx.Settings.physicsVelocityIterationsReview;
					entry.AddIssue("PRJ069", Physics.defaultSolverVelocityIterations.ToString(), "Global default velocity iterations <= " + velocityLimit, 0,
						c => ImporterFixUtility.Project(c, () => Physics.defaultSolverVelocityIterations = Math.Min(Physics.defaultSolverVelocityIterations, velocityLimit)),
						"Applies the configured global velocity-iteration cap. Per-Rigidbody overrides are preserved; verify simulation and joint stability.",
						verify: _ => Physics.defaultSolverVelocityIterations <= velocityLimit);
				}

			using (RuleTiming.Begin("PRJ070", entry.Path))
			{
				if (TryGetForceAssetUnloadAndGCOnSceneLoad(out bool forceUnloadAndGc) && forceUnloadAndGc)
					entry.AddIssue("PRJ070", "On", "Off when immediate finalizer timing is not required", 0, null,
						"Disabling this setting can reduce forced unload/GC work on scene load, but it changes when finalizers and asset unloading occur. Confirm no tooling/runtime workflow depends on immediate finalization.", options: new List<FixOption> { new FixOption {
							Label = "Disable Force Asset Unload + GC", Recommended = true, Risk = FixRisk.Medium,
							Warning = "Changing this alters scene-load memory/finalizer timing; review lifecycle-sensitive code first.",
							Apply = c => ImporterFixUtility.Project(c, () => SetForceAssetUnloadAndGCOnSceneLoad(false)),
							Verify = _ => TryGetForceAssetUnloadAndGCOnSceneLoad(out bool value) && !value
						} });
			}
		}

		static bool TryGetForceAssetUnloadAndGCOnSceneLoad(out bool value)
		{
			value = false;
			try
			{
				PropertyInfo property = typeof(EditorSettings).GetProperty("forceAssetUnloadAndGCOnSceneLoad", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (property == null || property.PropertyType != typeof(bool) || !property.CanRead) return false;
				object raw = property.GetValue(null, null);
				if (!(raw is bool)) return false;
				value = (bool)raw;
				return true;
			}
			catch { return false; }
		}

		static void SetForceAssetUnloadAndGCOnSceneLoad(bool value)
		{
			try
			{
				PropertyInfo property = typeof(EditorSettings).GetProperty("forceAssetUnloadAndGCOnSceneLoad", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				MethodInfo setter = property?.GetSetMethod(true);
				if (property == null || property.PropertyType != typeof(bool) || setter == null)
					throw new MissingMemberException("EditorSettings.forceAssetUnloadAndGCOnSceneLoad is unavailable in this Unity version.");
				setter.Invoke(null, new object[] { value });
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not set EditorSettings.forceAssetUnloadAndGCOnSceneLoad: " + e.Message);
			}
		}

		static bool HasRefreshModeCommandLineOverride()
		{
			var args = Environment.GetCommandLineArgs();
			for (int i = 0; i < args.Length; i++)
				if (args[i].IndexOf("refreshImportMode", StringComparison.OrdinalIgnoreCase) >= 0) return true;
			return false;
		}

		static bool HasDirectAssetBundleBuildUsage(ScanContext ctx)
		{
			if (ctx == null || !ctx.Settings.projectProofPass) return false;
			var proof = ctx.GetProofIndex();
			if (proof == null || !proof.IsBuilt || proof.BuildFailed || !proof.CoverageComplete) return false;
			return proof.ContainsSourcePattern("BuildPipeline.BuildAssetBundles") || proof.ContainsSourcePattern("BuildAssetBundles(");
		}

		static bool IsEffectiveDedicatedServerBuild()
		{
			var target = EditorUserBuildSettings.activeBuildTarget;
			if (target != BuildTarget.StandaloneWindows64 && target != BuildTarget.StandaloneWindows && target != BuildTarget.StandaloneLinux64 && target != BuildTarget.StandaloneOSX) return false;
			try
			{
				var property = typeof(EditorUserBuildSettings).GetProperty("standaloneBuildSubtarget", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				return property != null && Convert.ToInt32(property.GetValue(null, null)) == (int)StandaloneBuildSubtarget.Server;
			}
			catch { return false; }
		}

		static bool Has2DPhysicsUsage(ScanContext ctx)
		{
			if (ctx == null || !ctx.Settings.projectProofPass) return false;
			var proof = ctx.GetProofIndex();
			if (proof == null || !proof.IsBuilt || proof.BuildFailed || !proof.CoverageComplete) return false;
			return proof.ContainsSourcePattern("Rigidbody2D.") || proof.ContainsSourcePattern("Collider2D.") || proof.ContainsSourcePattern("GetComponent<Rigidbody2D>") || proof.ContainsSourcePattern("GetComponent<Collider2D>") || proof.ContainsSourcePattern("Physics2D.");
		}

		static bool Has3DPhysicsUsage(ScanContext ctx)
		{
			if (ctx == null || !ctx.Settings.projectProofPass) return false;
			var proof = ctx.GetProofIndex();
			if (proof == null || !proof.IsBuilt || proof.BuildFailed || !proof.CoverageComplete) return false;
			return proof.ContainsSourcePattern("Rigidbody.") || proof.ContainsSourcePattern("Collider.") || proof.ContainsSourcePattern("GetComponent<Rigidbody>") || proof.ContainsSourcePattern("GetComponent<Collider>") || proof.ContainsSourcePattern("Physics.");
		}

		static void EvaluateShaderVariantMemory(AssetEntry entry, ScanContext ctx)
		{
			var target = PlatformUtility.ToBuildTarget(ctx.Settings.targetPlatform);
			bool platformOverride = false;
			try { platformOverride = PlayerSettings.GetOverrideShaderChunkSettingsForPlatform(target); } catch { }
			int sizeMb = platformOverride ? PlayerSettings.GetShaderChunkSizeInMBForPlatform(target) : PlayerSettings.GetDefaultShaderChunkSizeInMB();
			int count = platformOverride ? PlayerSettings.GetShaderChunkCountForPlatform(target) : PlayerSettings.GetDefaultShaderChunkCount();
			entry.Set("shaderChunkScope", platformOverride ? target + " override" : "default");
			entry.Set("shaderChunkSizeMB", (long)sizeMb);
			entry.Set("shaderChunkCount", (long)count);
			int budget = Math.Max(0, ctx.Settings.shaderVariantMemoryBudgetMb);
			entry.Set("shaderVariantMemoryPolicyMB", (long)budget);
			// Chunk size and chunk count are separate Unity controls. Their product is not a documented
			// resident-memory measurement, so do not expose a fabricated total-memory estimate. The
			// existing ProjectOptimizer policy field is used here only as an explicit per-chunk policy.
			if (budget > 0 && sizeMb > budget)
			{
				bool writable = CanSetShaderChunkSize(target, platformOverride);
				var issue = entry.AddIssue("PRJ065", sizeMb + " MB chunk / " + count + " chunks", "chunk size <= configured " + budget + " MB policy", sizeMb - budget,
					c => ImporterFixUtility.Project(c, () => { if (!TrySetShaderChunkSize(target, platformOverride, budget)) throw new InvalidOperationException("Unity did not expose a writable shader chunk-size setting for this scope."); }),
					"Applies the configured per-chunk policy, not a claim about total resident memory. Preserves target/default scope and verifies the new chunk size.",
					verify: _ => TryReadShaderChunkSize(target, platformOverride, out int current) && current <= budget);
				if (!writable) RouteToDiagnosticOnly(issue, "This Unity version does not expose a matching setter for the selected shader chunk-size scope.");
			}
		}

		bool HasWebGlBrotliHostingConfiguration()
		{
			if (!EnsureProjectTextIndex() || !m_ProjectTextIndexComplete) return false;
			foreach (var item in m_ProjectTextIndex)
			{
				string file = (item.Key ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
				if (!(file.EndsWith(".htaccess") || file.EndsWith("web.config") || file.EndsWith("netlify.toml") || file.EndsWith("vercel.json") || file.EndsWith("nginx.conf") || file.EndsWith(".conf"))) continue;
				string content = item.Value ?? string.Empty;
				if ((content.Contains("content-encoding: br") || content.Contains("content-encoding br"))
					&& (content.Contains("application/wasm") || content.Contains(".wasm") || content.Contains("brotli"))) return true;
			}
			return false;
		}

		static string SafeLightingSettingsAssetPath(LightingSettings lighting)
		{
			try
			{
				if (lighting == null) return null;
				string path = AssetDatabase.GetAssetPath(lighting);
				return !string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal) ? path : null;
			}
			catch { return null; }
		}

		static void EvaluateLightmapPolicy(AssetEntry entry, ScanContext ctx)
		{
			ForEachRelevantLightingScene((scene, lighting) =>
			{
				if (lighting == null) return;
				bool baked = lighting.bakedGI || (LightmapSettings.lightmaps != null && LightmapSettings.lightmaps.Length > 0);
				if (!baked) return;
				float policy = Math.Max(0f, ctx.Settings.lightmapResolutionReview);
				if (policy <= 0f || lighting.lightmapResolution <= policy) return;
				string lightingPath = SafeLightingSettingsAssetPath(lighting);
				var issue = entry.AddIssue("PRJ066", scene.path + ": " + lighting.lightmapResolution.ToString("0.##"),
					"lightmap resolution <= configured " + policy.ToString("0.##"), 0,
					c =>
					{
						if (string.IsNullOrEmpty(lightingPath)) throw new InvalidOperationException("The LightingSettings object is not a writable project asset.");
						c.EnsureWritable(); c.RequireBackup(lightingPath);
						var targetLighting = AssetDatabase.LoadAssetAtPath<LightingSettings>(lightingPath);
						if (targetLighting == null) throw new InvalidOperationException("Could not reload the target LightingSettings asset.");
						targetLighting.lightmapResolution = Math.Min(targetLighting.lightmapResolution, policy);
						EditorUtility.SetDirty(targetLighting); c.NeedsSave = true;
					},
					"Clamps the persistent LightingSettings asset to the configured resolution. It is backed up first and may be shared by multiple scenes.",
					verify: _ => { var current = AssetDatabase.LoadAssetAtPath<LightingSettings>(lightingPath); return current != null && current.lightmapResolution <= policy; });
				if (string.IsNullOrEmpty(lightingPath)) RouteToDiagnosticOnly(issue, "The scene's LightingSettings object has no writable persistent asset path; a backup-backed action cannot be made.");
			});
		}

		static void EvaluateGiPolicy(AssetEntry entry, ScanContext ctx)
		{
			ForEachRelevantLightingScene((scene, lighting) =>
			{
				if (lighting == null || !lighting.bakedGI) return;
				bool high = (ctx.Settings.giDirectSampleReview > 0 && lighting.directSampleCount > ctx.Settings.giDirectSampleReview)
					|| (ctx.Settings.giIndirectSampleReview > 0 && lighting.indirectSampleCount > ctx.Settings.giIndirectSampleReview)
					|| (ctx.Settings.giEnvironmentSampleReview > 0 && lighting.environmentSampleCount > ctx.Settings.giEnvironmentSampleReview)
					|| (ctx.Settings.giMaxBouncesReview > 0 && lighting.maxBounces > ctx.Settings.giMaxBouncesReview);
				if (!high) return;
				string lightingPath = SafeLightingSettingsAssetPath(lighting);
				int directLimit = Math.Max(0, ctx.Settings.giDirectSampleReview);
				int indirectLimit = Math.Max(0, ctx.Settings.giIndirectSampleReview);
				int environmentLimit = Math.Max(0, ctx.Settings.giEnvironmentSampleReview);
				int bounceLimit = Math.Max(0, ctx.Settings.giMaxBouncesReview);
				var issue = entry.AddIssue("PRJ067", scene.path + ": direct=" + lighting.directSampleCount + ", indirect=" + lighting.indirectSampleCount + ", environment=" + lighting.environmentSampleCount + ", bounces=" + lighting.maxBounces,
					"Configured direct/indirect/environment sample and bounce limits", 0,
					c =>
					{
						if (string.IsNullOrEmpty(lightingPath)) throw new InvalidOperationException("The LightingSettings object is not a writable project asset.");
						c.EnsureWritable(); c.RequireBackup(lightingPath);
						var targetLighting = AssetDatabase.LoadAssetAtPath<LightingSettings>(lightingPath);
						if (targetLighting == null) throw new InvalidOperationException("Could not reload the target LightingSettings asset.");
						if (directLimit > 0) targetLighting.directSampleCount = Math.Min(targetLighting.directSampleCount, directLimit);
						if (indirectLimit > 0) targetLighting.indirectSampleCount = Math.Min(targetLighting.indirectSampleCount, indirectLimit);
						if (environmentLimit > 0) targetLighting.environmentSampleCount = Math.Min(targetLighting.environmentSampleCount, environmentLimit);
						if (bounceLimit > 0) targetLighting.maxBounces = Math.Min(targetLighting.maxBounces, bounceLimit);
						EditorUtility.SetDirty(targetLighting); c.NeedsSave = true;
					},
					"Clamps only baked-GI fields exceeding their individual configured limits. The LightingSettings asset is backed up first and may be shared by multiple scenes.",
					verify: _ => { var current = AssetDatabase.LoadAssetAtPath<LightingSettings>(lightingPath); return current != null && (directLimit <= 0 || current.directSampleCount <= directLimit) && (indirectLimit <= 0 || current.indirectSampleCount <= indirectLimit) && (environmentLimit <= 0 || current.environmentSampleCount <= environmentLimit) && (bounceLimit <= 0 || current.maxBounces <= bounceLimit); });
				if (string.IsNullOrEmpty(lightingPath)) RouteToDiagnosticOnly(issue, "The scene's LightingSettings object has no writable persistent asset path; a backup-backed action cannot be made.");
			});
		}

		static void ForEachRelevantLightingScene(Action<UnityEngine.SceneManagement.Scene, LightingSettings> action)
		{
			if (action == null) return;
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var buildScenes = EditorBuildSettings.scenes;
			for (int i = 0; i < buildScenes.Length; i++)
			{
				if (!buildScenes[i].enabled || string.IsNullOrEmpty(buildScenes[i].path) || !seen.Add(buildScenes[i].path)) continue;
				EvaluateLightingScene(buildScenes[i].path, action);
			}
			var active = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
			if (active.IsValid() && !string.IsNullOrEmpty(active.path) && seen.Add(active.path))
				action(active, Lightmapping.GetLightingSettingsForScene(active));
		}

		static void EvaluateLightingScene(string path, Action<UnityEngine.SceneManagement.Scene, LightingSettings> action)
		{
			if (!System.IO.File.Exists(AssetPathUtility.ToAbsolutePath(path))) return;
			var loaded = UnityEditor.SceneManagement.EditorSceneManager.GetSceneByPath(path);
			if (loaded.IsValid() && loaded.isLoaded)
			{
				action(loaded, Lightmapping.GetLightingSettingsForScene(loaded));
				return;
			}
			UnityEngine.SceneManagement.Scene opened = default;
			try
			{
				opened = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(path, UnityEditor.SceneManagement.OpenSceneMode.Additive);
				if (opened.IsValid()) action(opened, Lightmapping.GetLightingSettingsForScene(opened));
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not inspect lighting settings for '" + path + "': " + e.Message);
			}
			finally
			{
				if (opened.IsValid() && opened.isLoaded)
					try { UnityEditor.SceneManagement.EditorSceneManager.CloseScene(opened, true); } catch { }
			}
		}

		static bool? ReadOwnedBool(UnityEngine.Object owner, string fieldName)
		{
			try
			{
				var prop = SerializedPropertyUtility.Find(new SerializedObject(owner), fieldName);
				return prop != null && prop.propertyType == SerializedPropertyType.Boolean ? prop.boolValue : (bool?)null;
			}
			catch { return null; }
		}

		static string OwnedBoolWriteBlockReason(UnityEngine.Object owner)
		{
			if (owner == null) return "The profile-owned settings object is missing.";
			string ownerPath = AssetDatabase.GetAssetPath(owner);
			if (string.IsNullOrEmpty(ownerPath))
				return "The profile-owned settings object is not backed by a persistent asset.";
			if (!UnityEditorUtility.CanEditPath(ownerPath, out string editReason))
				return "The profile-owned settings asset cannot be edited: " + editReason;
			if (EditorUtility.IsDirty(owner))
				return "The profile-owned settings asset has unsaved changes. Save it and rescan before applying this fix.";
			return null;
		}

		static void SetOwnedBool(UnityEngine.Object owner, string fieldName, bool value, FixContext context)
		{
			if (context == null) throw new InvalidOperationException("The fix context is missing.");
			context.EnsureWritable();
			string ownerWriteBlockReason = OwnedBoolWriteBlockReason(owner);
			if (!string.IsNullOrEmpty(ownerWriteBlockReason)) throw new InvalidOperationException(ownerWriteBlockReason);
			string ownerPath = AssetDatabase.GetAssetPath(owner);

			// ProjectSettings fixes can also mutate a Build Profile asset. Snapshot that asset
			// separately so a failed verifier can roll back both targets atomically.
			context.RequireBackup(ownerPath);
			var so = new SerializedObject(owner);
			var prop = SerializedPropertyUtility.Find(so, fieldName);
			if (prop == null || prop.propertyType != SerializedPropertyType.Boolean)
				throw new InvalidOperationException("The profile-owned setting '" + fieldName + "' could not be resolved.");
			if (prop.boolValue == value)
				throw new InvalidOperationException("The profile-owned setting changed after analysis. Rescan before applying this fix.");
			prop.boolValue = value;
			so.ApplyModifiedPropertiesWithoutUndo();
			EditorUtility.SetDirty(owner);
			AssetDatabase.SaveAssetIfDirty(owner);
			context.ChangedProjectSettings = true;
		}

		// --- physics ------------------------------------------------------------------

		AssetEntry AnalyzePhysicsSettings(ScanContext ctx)
		{
			var entry = NewEntry("ProjectSettings/DynamicsManager.asset", "Physics Settings", "Project/Physics");
			entry.Set("solverIterations", (long)Physics.defaultSolverIterations);

			int namedLayers = 0;
			int ignoredPairs = 0;
			for (int a = 0; a < 32; a++)
			{
				if (string.IsNullOrEmpty(LayerMask.LayerToName(a))) continue;
				namedLayers++;
				for (int b = a; b < 32; b++)
				{
					if (string.IsNullOrEmpty(LayerMask.LayerToName(b))) continue;
					if (Physics.GetIgnoreLayerCollision(a, b)) ignoredPairs++;
				}
			}
			entry.Set("layers", (long)namedLayers);

			using (RuleTiming.Begin("PRJ012", entry.Path))
				if (namedLayers >= 4 && ignoredPairs == 0)
				{
					int pairs = namedLayers * (namedLayers + 1) / 2;
					var options = new List<FixOption>();
					for (int a = 0; a < 32; a++)
					{
						string layerA = LayerMask.LayerToName(a);
						if (string.IsNullOrEmpty(layerA)) continue;
						for (int b = a; b < 32; b++)
						{
							string layerB = LayerMask.LayerToName(b);
							if (string.IsNullOrEmpty(layerB) || Physics.GetIgnoreLayerCollision(a, b)) continue;
							int layerAIndex = a;
							int layerBIndex = b;
							options.Add(new FixOption {
								Label = "Ignore collision: " + layerA + " × " + layerB,
								Recommended = false,
								Risk = FixRisk.Medium,
								Warning = "Only select this pair if gameplay and physics queries never require these layers to collide.",
								Apply = c => ImporterFixUtility.Project(c, () => Physics.IgnoreLayerCollision(layerAIndex, layerBIndex, true)),
								Verify = _ => Physics.GetIgnoreLayerCollision(layerAIndex, layerBIndex)
							});
						}
					}
					if (ignoredPairs == 0 && pairs > 0)
						entry.AddIssue("PRJ012", (pairs - ignoredPairs) + " named-layer pairs still allow collision", "Choose only pairs proven unnecessary", 0, null,
							"The detector cannot infer gameplay collision semantics. Each Review Fix disables exactly one selected pair. After applying one, use Project Settings > Physics to review any additional pairs rather than treating all pairs as unnecessary.", options: options);
				}

			using (RuleTiming.Begin("PRJ017", entry.Path))
				if (Physics.defaultSolverIterations > 6)
				{
					entry.AddIssue("PRJ017", Physics.defaultSolverIterations + " iterations", "6 or profile-specific value", 0, null,
						"Increasing the global solver count has a project-wide cost; prefer Rigidbody-specific overrides when possible.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ017 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => Physics.defaultSolverIterations = 6),
							Verify = _ => Physics.defaultSolverIterations == 6
						} }
					);
				}

			// Preserve analysis of this legacy physics setting while Unity 6 still exposes it.
#pragma warning disable CS0618
			using (RuleTiming.Begin("PRJ013", entry.Path))
				if (!Physics.reuseCollisionCallbacks)
				{
					bool proofComplete = TrySearchProjectText(ctx, new[] { "oncollisionenter(", "oncollisionstay(", "oncollisionexit(", "collision collision" }, out bool callbackUse);
					var issue = entry.AddIssue("PRJ013", "Off", "On when Collision objects do not escape their callback", 0,
						c => ImporterFixUtility.Project(c, () => Physics.reuseCollisionCallbacks = true),
						"User-preferred AutoFix: enables collision callback reuse in the common case. Complete source proof gates known callback use; callback-object escape analysis cannot be proven by simple text matching.",
						verify: _ => Physics.reuseCollisionCallbacks);
					if (!proofComplete || callbackUse || HasNativePluginForTarget(EditorUserBuildSettings.activeBuildTarget))
						RouteToReview(issue, !proofComplete ? "Complete project proof is unavailable; Collision object lifetime is not established." : callbackUse ? "Collision callbacks were found; confirm Collision instances are not retained beyond callback return." : "Native plugins may retain collision objects outside scanned project text.", "Enable collision callback reuse after lifetime review");
				}
			using (RuleTiming.Begin("PRJ059", entry.Path))
				if (Physics.autoSyncTransforms)
				{
					var disable = new Action<FixContext>(c => ImporterFixUtility.Project(c, () => Physics.autoSyncTransforms = false));
					entry.AddIssue("PRJ059", "On", "Off after transform/physics lifetime review", 0, null,
						"Automatic absence proof is not deterministic for transform-write / physics-read ordering, aliases and indirect calls. Review the runtime synchronization contract before changing this setting.",
						options: new List<FixOption> { new FixOption { Label = "Disable Auto Sync Transforms", Recommended = false, Risk = FixRisk.Medium, Warning = "Confirm all physics-dependent reads occur after explicit synchronization where required.", Apply = disable, Verify = _ => !Physics.autoSyncTransforms } });
				}


			entry.Finalise();
			return entry;
		}

		// --- audio --------------------------------------------------------------------

		AssetEntry AnalyzeAudioSettings(ScanContext ctx)
		{
			var entry = NewEntry("ProjectSettings/AudioManager.asset", "Audio Settings", "Project/Audio");
			using (RuleTiming.Begin("PRJ015", entry.Path))
				try
				{
					var config = AudioSettings.GetConfiguration();
					entry.Set("realVoices", (long)config.numRealVoices);
					entry.Set("virtualVoices", (long)config.numVirtualVoices);
					entry.Set("dspBuffer", (long)config.dspBufferSize);

					if (config.numRealVoices > 32)
						entry.AddIssue("PRJ015", config.numRealVoices + " real voices", "24-32 real voices",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ015 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => { var cfg = AudioSettings.GetConfiguration(); cfg.numRealVoices = Mathf.Min(cfg.numRealVoices, 32); AudioSettings.Reset(cfg); }),
							Verify = _ => AudioSettings.GetConfiguration().numRealVoices <= 32
						} }
					);
				}
				catch
				{
					// Audio subsystem unavailable in this editor session.
				}

			entry.Finalise();
			return entry;
		}

		// --- helpers -------------------------------------------------------------------

		// --- platform predicates ---------------------------------------------------
		//
		// Every rule below is gated on the target profile chosen in the toolbar. A Vulkan
		// recommendation is right for Android and meaningless for WebGL, and a tool that reports
		// both at once teaches people to ignore the whole section.

		static bool IsAndroidLike(OptimizerSettings s) =>
			s.targetPlatform == OptimizerPlatform.Android || s.targetPlatform == OptimizerPlatform.StandaloneVR;

		static bool IsIOS(OptimizerSettings s) => s.targetPlatform == OptimizerPlatform.iOS;

		static bool IsWebGL(OptimizerSettings s) => s.targetPlatform == OptimizerPlatform.WebGL;

		/// <summary>True when the URP asset has the Unity 6 GPU Resident Drawer switched on.</summary>
		static bool GpuResidentDrawerEnabled(UnityEngine.Object pipelineAsset = null)
		{
			var pipeline = pipelineAsset != null ? pipelineAsset : GraphicsSettings.currentRenderPipeline;
			if (pipeline == null) return false;
			try
			{
				var mode = new SerializedObject(pipeline).FindProperty("m_GPUResidentDrawerMode");
				if (mode == null) return false;
				if (mode.enumDisplayNames != null && mode.enumDisplayNames.Length > mode.enumValueIndex)
					return !string.Equals(mode.enumDisplayNames[mode.enumValueIndex], "Disabled", StringComparison.OrdinalIgnoreCase);
				return mode.enumValueIndex > 0;
			}
			catch { return false; }
		}

		// --- cross-platform player -------------------------------------------------

		void EvaluateCommonPlayer(AssetEntry entry, ScanContext ctx, NamedBuildTarget target)
		{
			var settings = ctx.Settings;

			using (RuleTiming.Begin("PRJ020", entry.Path))
				if (PlayerSettings.meshDeformation == MeshDeformation.CPU)
				{
					bool proofComplete = TrySearchProjectText(ctx, new[] { "bakeMesh(", ".vertices", "getvertices(", "getvertexbuffer(", "asyncgpureadback" }, out bool cpuReadbackUse);
					bool targetCompute = TargetSupportsComputeShaders(settings);
					var issue = entry.AddIssue("PRJ020", "CPU", "GPUBatched", 0,
						c => ImporterFixUtility.Project(c, () => PlayerSettings.meshDeformation = MeshDeformation.GPUBatched),
						"Selects GPUBatched only when the target graphics API supports compute shaders and complete source proof finds no known CPU mesh readback dependency.",
						verify: _ => PlayerSettings.meshDeformation == MeshDeformation.GPUBatched);
					if (!proofComplete || cpuReadbackUse || !targetCompute || EditorUserBuildSettings.activeBuildTarget != PlatformUtility.ToBuildTarget(settings.targetPlatform))
						RouteToReview(issue, !proofComplete ? "Complete project proof is unavailable; CPU mesh readback dependencies are not established." : cpuReadbackUse ? "CPU mesh readback patterns were found; GPUBatched may change these results." : !targetCompute ? "The target graphics APIs do not establish GPUBatched compute support." : "Selected target differs from the active Editor build target; this setting is not target-parameterized.", "Switch to GPUBatched after target/runtime review");
				}

			EvaluateMipStripping(entry, ctx);

			bool isIl2Cpp = false;
			try
			{
				isIl2Cpp = PlayerSettings.GetScriptingBackend(target) == ScriptingImplementation.IL2CPP;
			}
			catch { /* target backend may be unavailable */ }

			if (isIl2Cpp)
				TryEvaluate(() =>
				{
					using (RuleTiming.Begin("PRJ022", entry.Path))
						if (PlayerSettings.GetIl2CppCompilerConfiguration(target) == Il2CppCompilerConfiguration.Debug)
							entry.AddIssue("PRJ022", "Debug", "Release", 0,
								c => ImporterFixUtility.Project(c, () => PlayerSettings.SetIl2CppCompilerConfiguration(target, Il2CppCompilerConfiguration.Release)),
								"Release is the default IL2CPP configuration for final builds. Development/debugging options can be explicitly enabled from the Player Build page when needed.",
								verify: _ => PlayerSettings.GetIl2CppCompilerConfiguration(target) == Il2CppCompilerConfiguration.Release);
				});

			if (isIl2Cpp)
				TryEvaluate(() =>
				{
					using (RuleTiming.Begin("PRJ023", entry.Path))
					{
						var codegen = PlayerSettings.GetIl2CppCodeGeneration(target);
						if (codegen == UnityEditor.Build.Il2CppCodeGeneration.OptimizeSize)
							entry.AddIssue("PRJ023", "OptimizeSize", "OptimizeSpeed", 0,
								c => ImporterFixUtility.Project(c, () => PlayerSettings.SetIl2CppCodeGeneration(target, UnityEditor.Build.Il2CppCodeGeneration.OptimizeSpeed)),
								"This project policy prioritizes runtime performance over binary size on all IL2CPP target platforms, including WebGL.",
								verify: _ => PlayerSettings.GetIl2CppCodeGeneration(target) == UnityEditor.Build.Il2CppCodeGeneration.OptimizeSpeed);
					}
				});

			// WebGL is single-threaded, so graphics jobs have nowhere to go.
			using (RuleTiming.Begin("PRJ024", entry.Path))
				if (!PlayerSettings.graphicsJobs && !IsWebGL(settings) && PlayerSettings.graphicsJobMode != GraphicsJobMode.Legacy)
					entry.AddIssue("PRJ024", "Off (" + PlayerSettings.graphicsJobMode + ")", "Profile- and device-tested", 0, null,
						"Graphics Jobs is workload- and driver-dependent; this explicit option enables it, but the user must profile the target device.",
						options: new List<FixOption> { new FixOption {
							Label = "Enable Graphics Jobs for target profiling",
							Recommended = false,
							Risk = FixRisk.Medium,
							Warning = "Test rendering and stability on the actual target device before shipping.",
							Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.graphicsJobs = true),
							Verify = _ => PlayerSettings.graphicsJobs
						} });

			entry.Set("colorSpace", PlayerSettings.colorSpace.ToString());
			using (RuleTiming.Begin("PRJ025", entry.Path))
				if (PlayerSettings.colorSpace == ColorSpace.Gamma)
				{
					entry.AddIssue("PRJ025", "Gamma", "Linear", 0, null,
						"Not auto-fixed: switching colour space changes how every existing light and colour reads, "
						+ "so it is a decision with re-authoring attached rather than a setting to flip.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ025 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.colorSpace = ColorSpace.Linear),
							Verify = _ => PlayerSettings.colorSpace == ColorSpace.Linear
						} }
					);
				}
		}

		// --- Android ----------------------------------------------------------------

		void EvaluateAndroid(AssetEntry entry, ScanContext ctx)
		{
			var settings = ctx.Settings;
			var architectures = PlayerSettings.Android.targetArchitectures;
			entry.Set("architectures", architectures.ToString());

			using (RuleTiming.Begin("PRJ026", entry.Path))
				if ((architectures & AndroidArchitecture.ARM64) == 0)
				{
					// Adding an architecture takes nothing away from any device, and Google Play will
					// not accept a build without it. What it can do is break the build itself: a native
					// plugin shipped only as armeabi-v7a has no ARM64 library to link, and the failure
					// arrives at build time with a linker error rather than here.
					//
					// So the plugin folder is checked first. Where every native plugin has an ARM64
					// variant the fix is offered; where any does not, the finding names them and stops.
					var missingArm64 = NativePluginsWithoutArm64();

					if (missingArm64.Count > 0)
					{
						entry.AddIssue("PRJ074", architectures.ToString(), "ARM64, once the plugins ship it",
							0, null,
							"Google Play requires a 64-bit binary, but these native plugins have no ARM64 library, "
							+ "so enabling ARM64 would fail the build until they are updated.")
							?.Items("Native plugins with no ARM64 variant", missingArm64, DetailKind.Bad, arePaths: true);
					}
					else
					{
						entry.AddIssue("PRJ026", architectures.ToString(), "ARM64", 0,
							c =>
							{
								PlayerSettings.Android.targetArchitectures = architectures | AndroidArchitecture.ARM64;
								c.ChangedProjectSettings = true;
							},
							verify: _ => (PlayerSettings.Android.targetArchitectures & AndroidArchitecture.ARM64) != 0);
					}
				}
				else using (RuleTiming.Begin("PRJ027", entry.Path))
					if ((architectures & AndroidArchitecture.ARMv7) != 0
						&& !EditorUserBuildSettings.buildAppBundle
						&& !PlayerSettings.Android.buildApkPerCpuArchitecture)
					{
						// APK-only case. Android App Bundles already support architecture-targeted delivery;
						// buildApkPerCpuArchitecture only matters to split APK output.
						entry.AddIssue("PRJ027", "ARM64 + ARMv7 APK", "ARM64 only, or split APKs", 0, null,
							"An unsplit APK containing both architectures carries both native binaries. This is a package-size "
							+ "recommendation only; Android App Bundles can deliver architecture-specific variants automatically.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ027 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64),
							Verify = _ => PlayerSettings.Android.targetArchitectures == AndroidArchitecture.ARM64
						} }
					);
					}

			using (RuleTiming.Begin("PRJ028", entry.Path))
				if (!PlayerSettings.Android.optimizedFramePacing)
					entry.AddIssue("PRJ028", "Off", "On", 0,
						c => ImporterFixUtility.Project(c, () => PlayerSettings.Android.optimizedFramePacing = true),
						"Enables optimized Android frame pacing for the common mobile-release case. It can be turned off manually if runtime cadence or target-device testing requires it.",
						verify: _ => PlayerSettings.Android.optimizedFramePacing);

			TryEvaluate(() =>
			{
				using (RuleTiming.Begin("PRJ029", entry.Path))
					if (PlayerSettings.Android.applicationEntry == AndroidApplicationEntry.Activity)
					{
						var issue = entry.AddIssue("PRJ029", "Activity", "GameActivity", 0,
							c => ImporterFixUtility.Project(c, () => PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.GameActivity),
							"Selects GameActivity for the standard Android player. Known Android plugin/library/manifest assets route only those cases to Review.",
							verify: _ => PlayerSettings.Android.applicationEntry == AndroidApplicationEntry.GameActivity);
						if (HasAndroidEntryPointSensitiveAssets())
							RouteToReview(issue, "Android plugin binaries, manifests or entry-point-related assets were found; confirm GameActivity compatibility before changing the entry point.", "Use GameActivity after Android plugin compatibility review");
					}
			});

			var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android) ?? Array.Empty<GraphicsDeviceType>();
			bool autoApis = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android);
			entry.Set("graphicsApis", autoApis ? "Auto" : Join(apis));

			GraphicsDeviceType[] preferredApis = PlatformUtility.BuildAndroidVulkanPreferredOrder(apis);
			using (RuleTiming.Begin("PRJ030", entry.Path))
				if (autoApis || !PlatformUtility.GraphicsApiListsEqual(apis, preferredApis))
				{
					// Product policy prefers Vulkan and guarantees OpenGLES3 fallback. The action compares
					// the current API settings with this scanned snapshot before changing them.
					string apiReason = autoApis
						? "Automatic graphics API selection is enabled, so Vulkan is not explicitly guaranteed as the first API."
						: apis.Length == 0
							? "The explicit Android graphics API list is empty or unavailable."
							: !apis.Contains(GraphicsDeviceType.Vulkan)
								? "Vulkan is absent from the explicit Android graphics API list."
								: apis[0] != GraphicsDeviceType.Vulkan
									? "Vulkan is enabled but is not the first preferred API."
									: apis.Length < 2 || apis[1] != GraphicsDeviceType.OpenGLES3
										? "OpenGLES3 is not the second fallback API."
										: "The explicit graphics API list contains duplicate entries or differs from the canonical preferred order.";
					entry.AddIssue("PRJ030", autoApis ? "Auto" : Join(apis), "Vulkan, then OpenGLES3", 0,
						c => ImporterFixUtility.Project(c, () => PlatformUtility.PreferAndroidVulkan(autoApis, apis, preferredApis)),
						apiReason + " Sets an explicit Android API order with Vulkan first and OpenGLES3 second as fallback, preserving other configured APIs afterward. Validate the final order against the project's supported-device matrix.",
						verify: _ => HasAndroidVulkanPreferred(preferredApis));
				}

			using (RuleTiming.Begin("PRJ031", entry.Path))
				if (!PlayerSettings.Android.minifyRelease)
				{
					bool proofComplete = TrySearchProjectText(ctx, new[] { "class.forname(", "java.lang.reflect", "getdeclaredmethod(", "getdeclaredfield(", "getmethod(", "androidjavaobject", "androidjavaclass", "jni" }, out bool reflectionOrJniUse);
					bool opaqueAndroidPlugin = HasAndroidEntryPointSensitiveAssets();
					var issue = entry.AddIssue("PRJ031", "Off", "On for release", 0,
						c => ImporterFixUtility.Project(c, () => PlayerSettings.Android.minifyRelease = true),
						"Enables R8 for the common release-build case. Complete project text proof and Android plugin checks route JNI/reflection and opaque plugin cases to Review.",
						verify: _ => PlayerSettings.Android.minifyRelease);
					if (!proofComplete || reflectionOrJniUse || opaqueAndroidPlugin)
						RouteToReview(issue, !proofComplete ? "Complete project text proof is unavailable; keep-rule requirements are not established." : reflectionOrJniUse ? "JNI/reflection patterns were found; confirm matching keep rules before enabling R8." : "Android plugin binaries/manifests are present; inspect their keep-rule requirements before enabling R8.", "Enable R8 after keep-rule review");
				}

			entry.Set("targetSdk", PlayerSettings.Android.targetSdkVersion.ToString());
			// Google Play's standard Android requirement is API 36 for new apps and updates
			// submitted from 31 August 2026. Android XR/TV have separate exceptions; the
			// dedicated Android target here therefore does not claim those platform exceptions.
			using (RuleTiming.Begin("PRJ052", entry.Path))
				if (settings.targetPlatform == OptimizerPlatform.Android && (int)PlayerSettings.Android.targetSdkVersion < 36)
				{
					entry.AddIssue(
						"PRJ052",
						"API " + (int)PlayerSettings.Android.targetSdkVersion,
						"API 36",
						0,
						c => ImporterFixUtility.Project(c, () => PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel36),
						verify: c => (int)PlayerSettings.Android.targetSdkVersion >= 36);
				}

			entry.Set("minSdk", PlayerSettings.Android.minSdkVersion.ToString());

			TryEvaluate(() =>
			{
				using (RuleTiming.Begin("PRJ032", entry.Path))
					if (ShouldRecommendAndroidBlitAuto(PlayerSettings.Android.blitType == AndroidBlitType.Always, autoApis, apis))
					{
						entry.AddIssue("PRJ032", "Always", "Auto", 0,
							c => ImporterFixUtility.Project(c, () => SetAndroidBlitTypeAuto(autoApis, apis)),
							"Auto selects direct framebuffer presentation where supported and falls back to blitting when needed. Unity ignores this setting when Vulkan is used; this finding is emitted only when Android can use a non-Vulkan API.",
							verify: _ => PlayerSettings.Android.blitType == AndroidBlitType.Auto);
					}
			});
		}

		// --- iOS ---------------------------------------------------------------------

		void EvaluateIOS(AssetEntry entry, ScanContext ctx)
		{
			TryEvaluate(() =>
			{
				using (RuleTiming.Begin("PRJ033", entry.Path))
					if (PlayerSettings.iOS.scriptCallOptimization == ScriptCallOptimizationLevel.SlowAndSafe)
					{
						// Reported only. This turns a caught NullReferenceException into a hard crash.
						// Any project relying on a caught exception in shipped code would start crashing
						// in the field, and nothing here can tell whether this one does.
						entry.AddIssue("PRJ033", "Slow and Safe", "Fast but no Exceptions, once stable", 0, null,
							"This removes the null checks around managed calls: what was a caught exception becomes "
							+ "a crash. Only worth it on code you no longer rely on exceptions in.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ033 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => ReflectionUtility.SetStaticEnum(typeof(PlayerSettings.iOS), "scriptCallOptimization", "FastButNoExceptions")),
							Verify = _ => PlayerSettings.iOS.scriptCallOptimization == ScriptCallOptimizationLevel.FastButNoExceptions
						} }
					);
					}
			});
		}

		// --- WebGL -------------------------------------------------------------------

		void EvaluateWebGL(AssetEntry entry, ScanContext ctx)
		{
			entry.Set("compression", PlayerSettings.WebGL.compressionFormat.ToString());
			using (RuleTiming.Begin("PRJ034", entry.Path))
				if (PlayerSettings.WebGL.compressionFormat != WebGLCompressionFormat.Brotli)
				{
					var issue = entry.AddIssue("PRJ034", PlayerSettings.WebGL.compressionFormat.ToString(), "Brotli when a matching deployment configuration is present", 0,
						c => ImporterFixUtility.Project(c, () => ReflectionUtility.SetStaticEnum(typeof(PlayerSettings.WebGL), "compressionFormat", "Brotli")),
						"Selects Brotli only when a tracked deployment configuration explicitly proves the br Content-Encoding. Unverifiable external hosting is a Review exception.",
						verify: _ => PlayerSettings.WebGL.compressionFormat == WebGLCompressionFormat.Brotli);
					if (!HasWebGlBrotliHostingConfiguration())
						RouteToReview(issue, "No recognized project deployment configuration proves that Brotli files are served with Content-Encoding: br. Confirm the actual host before changing this setting.", "Use Brotli after hosting header verification");
				}

			using (RuleTiming.Begin("PRJ035", entry.Path))
				if (PlayerSettings.WebGL.exceptionSupport != WebGLExceptionSupport.None
					&& PlayerSettings.WebGL.exceptionSupport != WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly)
				{
					// Reported only, for the same reason as PRJ033: it changes what happens when
					// something is null, from an exception to an unrecoverable crash.
					entry.AddIssue("PRJ035", PlayerSettings.WebGL.exceptionSupport.ToString(),
						"ExplicitlyThrownExceptionsOnly", 0, null,
						"Your own throws keep working; everything else stops being catchable. Check for code that "
						+ "depends on catching engine-raised exceptions first.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ035 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => ReflectionUtility.SetStaticEnum(typeof(PlayerSettings.WebGL), "exceptionSupport", "ExplicitlyThrownExceptionsOnly")),
							Verify = _ => PlayerSettings.WebGL.exceptionSupport.ToString() == "ExplicitlyThrownExceptionsOnly"
						} }
					);
				}

			using (RuleTiming.Begin("PRJ036", entry.Path))
				if (!PlayerSettings.WebGL.dataCaching)
				{
					// Kept automatic: this caches the data file in IndexedDB on the player's own machine.
					// Nothing about the build changes, and nothing in the game behaves differently.
					entry.AddIssue("PRJ036", "Off", "On", 0,
						c => { PlayerSettings.WebGL.dataCaching = true; c.ChangedProjectSettings = true; },
						verify: _ => PlayerSettings.WebGL.dataCaching);
				}

			TryEvaluate(() =>
			{
				var level = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.WebGL);
				using (RuleTiming.Begin("PRJ037", entry.Path))
					if (level != ManagedStrippingLevel.High)
					{
						// Reported only. High stripping on WebGL is worth reaching for and is also the
						// setting most likely to remove something reflection needed.
						entry.AddIssue("PRJ037", level.ToString(), "High, with a link.xml", 0, null,
							"Every byte of managed code becomes WebAssembly to download and compile. High is worth "
							+ "the risk here, but it needs a link.xml for anything reached by reflection.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ037 Review Fix",
							Recommended = true,
							Risk = FixRisk.High,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.High)),
							Verify = _ => PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.WebGL) == ManagedStrippingLevel.High
						} }
					);
					}
			});
		}

		// --- mip stripping ---------------------------------------------------------------

		/// <summary>
		/// Mip Stripping only removes something when the quality levels disagree about how far
		/// textures may be reduced. Unity strips the mip levels that <em>no</em> active level for the
		/// platform can select, so on a project whose levels all share one mipmap limit there is
		/// nothing above the limit to strip and the setting saves nothing.
		/// <para>
		/// Recommending it unconditionally - which is what the rule used to do - is a fabricated
		/// saving: the setting is real, the benefit for that project is zero. So the levels are read
		/// first, and the finding says which limits it found.
		/// </para>
		/// </summary>
		void EvaluateMipStripping(AssetEntry entry, ScanContext ctx)
		{
			entry.Set("mipStripping", PlayerSettings.mipStripping ? "On" : "Off");
			if (PlayerSettings.mipStripping) return;

			var levels = GetActiveQualityLevelsForPlatform(PlatformUtility.ToBuildTarget(ctx.Settings.targetPlatform));
			if (levels.Length == 0) return;

			var limits = new List<string>();
			var distinct = new HashSet<int>();
			for (int i = 0; i < levels.Length; i++)
			{
				int levelIndex = levels[i];
				if (levelIndex < 0 || levelIndex >= QualitySettings.count) continue;

				int limit = 0;
				WithQualityLevel(levelIndex, () => limit = QualitySettings.globalTextureMipmapLimit);
				distinct.Add(limit);
				limits.Add(QualitySettings.names[levelIndex] + ": mipmap limit " + limit);
			}

			entry.Set("mipmapLimits", string.Join(", ", limits.ToArray()));

			// One shared limit across every level means the top mips are selectable everywhere, so
			// there is nothing for stripping to remove. Saying nothing is the honest result.
			using (RuleTiming.Begin("PRJ021", entry.Path))
				if (distinct.Count < 2) return;

			entry.AddIssue("PRJ021", "Off", "On", 0,
					c => ImporterFixUtility.Project(c, () => PlayerSettings.mipStripping = true),
					"Enables mip stripping because target quality levels use different global mip limits. Textures marked Ignore Mipmap Limit remain explicit exceptions.",
					verify: _ => PlayerSettings.mipStripping)
				?.Items("Mipmap limits by quality level", limits, DetailKind.Note)
				.Note("Textures with Ignore Mipmap Limit set opt out of this, and keep their top mips. "
					  + "TEX017 lists them.");
		}

		// --- Unity 6 rendering ------------------------------------------------------------

		void EvaluateUnity6Rendering(AssetEntry entry, ScanContext ctx, SerializedObject pipeline,
			string pipelinePath, System.Action<OptimizationIssue> attribute)
		{
			var settings = ctx.Settings;
			bool drawerOn = false;
			bool isUrp = IsUrpPipeline(pipeline.targetObject);
			bool forwardPlus = isUrp && HasForwardPlusRenderer(pipeline.targetObject);

			if (isUrp)
			{
				var renderGraph = FindSerializedBoolean(pipeline,
						"m_CompatibilityMode", "m_UseRenderGraph", "m_EnableRenderGraph",
						"Compatibility Mode", "Render Graph");
				if (renderGraph != null)
				{
					bool compatibility = IsCompatibilityModeProperty(renderGraph) ? renderGraph.boolValue : !renderGraph.boolValue;
					entry.Set("renderGraph", compatibility ? "Compatibility Mode" : "Render Graph");
					using (RuleTiming.Begin("PRJ075", entry.Path))
						if (compatibility)
						{
							string renderGraphProperty = renderGraph.propertyPath;
							bool desired = IsCompatibilityModeProperty(renderGraph) ? false : true;
							var issue = entry.AddIssue("PRJ075", "Compatibility Mode", "Render Graph", 0,
								c => RenderPipelineUtility.SetBool(c, pipelinePath, renderGraphProperty, desired),
								"Switches URP to Render Graph. Automatic execution requires complete project text proof with no known legacy renderer-pass patterns.",
								verify: _ => RenderPipelineUtility.ReadBool(pipelinePath, renderGraphProperty) == desired);
							bool proofComplete = TrySearchProjectText(ctx, new[] { "scriptablerenderpass", "scriptablerendererfeature", "addrenderpasses(", "oncamerasetup(", "oncameracleanup(", "execute(cameradata" }, out bool legacyPassFound);
							if (!proofComplete || legacyPassFound) RouteToReview(issue, !proofComplete ? "Complete project proof is unavailable; review custom renderer features and legacy passes before enabling Render Graph." : "Legacy ScriptableRenderPass/RendererFeature patterns were found; verify Render Graph compatibility before switching.", "Enable Render Graph after compatibility review");
						}
				}

				var compilationCaching = FindSerializedBoolean(pipeline,
						"m_EnableCompilationCaching", "m_RenderGraphCompilationCaching", "Enable Compilation Caching", "Compilation Caching");
				if (compilationCaching != null)
				{
					entry.Set("renderGraphCompilationCaching", compilationCaching.boolValue ? "On" : "Off");
					using (RuleTiming.Begin("PRJ076", entry.Path))
						if (!compilationCaching.boolValue)
							attribute(entry.AddIssue("PRJ076", "Off", "On", 0,
								c => RenderPipelineUtility.SetBool(c, pipelinePath, compilationCaching.propertyPath, true),
								"Enables Render Graph compilation caching on the exact resolved URP asset.",
								verify: _ => RenderPipelineUtility.ReadBool(pipelinePath, compilationCaching.propertyPath) == true));
				}
			}

			var drawerMode = isUrp ? pipeline.FindProperty("m_GPUResidentDrawerMode") : null;
			if (drawerMode != null)
			{
				drawerOn = !IsSerializedEnumNamed(drawerMode, "Disabled");
				entry.Set("gpuDrawer", drawerOn ? "Instanced Drawing" : "Disabled");

				bool srpBatcherOn = false;
				var srpBatcherForDrawer = pipeline.FindProperty("m_UseSRPBatcher");
				if (srpBatcherForDrawer != null) srpBatcherOn = srpBatcherForDrawer.boolValue;
				bool computeCapable = TargetSupportsComputeShaders(settings);

				using (RuleTiming.Begin("PRJ043", entry.Path))
					if (!drawerOn && srpBatcherOn && forwardPlus && computeCapable)
					{
						attribute(entry.AddIssue("PRJ043", "Disabled", "Instanced Drawing", 0,
							c => { c.EnsureWritable(); var pipeline = AssetDatabase.LoadMainAssetAtPath(pipelinePath); if (pipeline == null) throw new InvalidOperationException("The render pipeline asset could not be loaded."); var so = new SerializedObject(pipeline); var p = so.FindProperty("m_GPUResidentDrawerMode"); if (p == null) throw new InvalidOperationException("GPU Resident Drawer is not available on the selected pipeline."); int targetIndex = FindSerializedEnumIndex(p, "Instanced Drawing", -1); if (targetIndex < 0) throw new InvalidOperationException("The pipeline does not expose the Instanced Drawing GPU Resident Drawer mode."); p.enumValueIndex = targetIndex; so.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(pipeline); c.NeedsSave = true; },
							verify: _ => RenderPipelineUtility.ReadEnumName(pipelinePath, "m_GPUResidentDrawerMode") == "Instanced Drawing"));
					}
			}

			if (drawerOn)
			{
				var occlusion = pipeline.FindProperty("m_GPUResidentDrawerEnableOcclusionCullingInCameras");
				using (RuleTiming.Begin("PRJ044", entry.Path))
					if (occlusion != null && !occlusion.boolValue && forwardPlus && TargetSupportsComputeShaders(settings))
					{
						attribute(entry.AddIssue("PRJ044", "Off", "On", 0,
							c =>
							{
								c.EnsureWritable();
								var targetAsset = AssetDatabase.LoadMainAssetAtPath(pipelinePath);
								if (targetAsset == null) throw new InvalidOperationException("The analyzed render pipeline asset no longer exists; rescan before fixing.");
								var target = new SerializedObject(targetAsset);
								var prop = target.FindProperty("m_GPUResidentDrawerEnableOcclusionCullingInCameras");
								if (prop == null) throw new InvalidOperationException("GPU Resident Drawer occlusion-culling property is unavailable.");
								prop.boolValue = true;
								target.ApplyModifiedPropertiesWithoutUndo();
								EditorUtility.SetDirty(targetAsset);
								c.NeedsSave = true;
							},
						verify: _ => RenderPipelineUtility.ReadBool(pipelinePath, "m_GPUResidentDrawerEnableOcclusionCullingInCameras") == true));
					}
				// BRG variants must survive shader stripping or the drawer silently fails in a player.
				TryEvaluate(() =>
				{
					var graphics = GraphicsSettingsObject(ctx);
					var brg = graphics?.FindProperty("m_BrgStripping");

					// The enum reads [Strip if Entities Graphics is not installed | Strip All |
					// Keep All], so Keep All is the last entry rather than the first. Read off the
					// live property rather than assumed: the GPU Resident Drawer ships in URP, not in
					// Entities Graphics, so the first option strips exactly the variants it needs.
					int keepAll = FindSerializedEnumIndex(brg, "Keep All", -1);
					using (RuleTiming.Begin("PRJ045", entry.Path))
						if (brg != null && keepAll >= 0 && brg.enumValueIndex != keepAll)
						{
							var current = brg.enumValueIndex >= 0 && brg.enumValueIndex < brg.enumDisplayNames.Length
								? brg.enumDisplayNames[brg.enumValueIndex]
								: brg.enumValueIndex.ToString();

							// Verified because this branch is reached only when the GPU Resident Drawer is already
							// enabled and Keep All is a documented prerequisite for that renderer path. The
							// GPU Resident Drawer is switched on, which is the case where the stripped
							// variants are ones the project actively needs. Keep All does grow the build,
							// so recommending it unconditionally would be trading size for nothing; here
							// it buys back a renderer that would otherwise silently fall back in a player.
							attribute(entry.AddIssue("PRJ045", current, "Keep All", 0,
								c =>
								{
										string graphicsPath = GraphicsSettingsAssetPath();
										if (string.IsNullOrEmpty(graphicsPath)) throw new InvalidOperationException("Could not resolve GraphicsSettings.asset for a safe backup.");
										c.RequireBackup(graphicsPath);
									var target = GraphicsSettingsObject();
									var prop = target?.FindProperty("m_BrgStripping");
									if (prop == null)
										throw new InvalidOperationException(
											"Graphics Settings has no m_BrgStripping property to change.");

									prop.enumValueIndex = keepAll;
									target.ApplyModifiedPropertiesWithoutUndo();
									c.NeedsSave = true;
								},
								verify: _ =>
								{
									var prop = GraphicsSettingsObject(ctx)?.FindProperty("m_BrgStripping");
									return prop != null && prop.enumValueIndex == keepAll;
								}));
						}
				});

				// The batching flags are internal, so they are read and written the same way the
				// static-batching rule above does it.
				using (RuleTiming.Begin("PRJ046", entry.Path))
				{
					int staticBatching = ReadBatching(PlatformUtility.ToBuildTarget(settings.targetPlatform), out _);
					if (staticBatching == 1)
					{
						var batchTarget = PlatformUtility.ToBuildTarget(settings.targetPlatform);
						attribute(entry.AddIssue("PRJ046", "Static batching on", "Off", 0,
							c => ImporterFixUtility.Project(c, () => PlayerSettings.SetStaticBatchingForPlatform(batchTarget, false)),
							"The GPU Resident Drawer is enabled for this URP asset. Disable target static batching so the drawer can manage eligible renderers; PRJ006 is suppressed for this configuration.",
							verify: _ => !PlayerSettings.GetStaticBatchingForPlatform(batchTarget)));
					}
				}
			}

			var opaque = isUrp ? pipeline.FindProperty("m_RequireOpaqueTexture") : null;
			using (RuleTiming.Begin("PRJ047", entry.Path))
				if (opaque != null && opaque.boolValue)
				{
					string[] tokens = { "_cameraopaquetexture", "sampletexture2d_x(_cameraopaquetexture", "samplescenecolor(", "opaque texture", "grabpass" };
					bool complete = TrySearchProjectText(ctx, tokens, out bool consumerFound);
					var issue = entry.AddIssue("PRJ047", "On", "Off", 0,
						c => RenderPipelineUtility.SetBool(c, pipelinePath, "m_RequireOpaqueTexture", false),
						"Disables URP Opaque Texture only when complete project text proof finds no known camera-color/opaque-texture consumers.",
						verify: _ => RenderPipelineUtility.ReadBool(pipelinePath, "m_RequireOpaqueTexture") == false);
					if (!complete || consumerFound) RouteToReview(issue, !complete ? "Complete project proof is unavailable; verify refraction, distortion, glass and shader-graph dependencies before disabling." : "Opaque-texture consumer patterns were found; inspect shaders/materials before disabling.", "Disable Opaque Texture after dependency review");
				}

			var depth = isUrp ? pipeline.FindProperty("m_RequireDepthTexture") : null;
			using (RuleTiming.Begin("PRJ048", entry.Path))
				if (depth != null && depth.boolValue)
				{
					string[] tokens = { "_cameradepthtexture", "samplescenedepth(", "declaredepthtexture", "depthnormals", "screen_space_ambient_occlusion", "screen space ambient occlusion", "softparticles_on", "_cameraopaqueorcustomdepthtexture" };
					bool complete = TrySearchProjectText(ctx, tokens, out bool consumerFound);
					var issue = entry.AddIssue("PRJ048", "On", "Off", 0,
						c => RenderPipelineUtility.SetBool(c, pipelinePath, "m_RequireDepthTexture", false),
						"Disables URP Depth Texture only when complete project text proof finds no known depth-dependent shader or rendering feature.",
						verify: _ => RenderPipelineUtility.ReadBool(pipelinePath, "m_RequireDepthTexture") == false);
					if (!complete || consumerFound) RouteToReview(issue, !complete ? "Complete project proof is unavailable; verify SSAO, soft-particle and depth-effect dependencies before disabling." : "Depth-dependent shader/render-feature patterns were found; inspect dependencies before disabling.", "Disable Depth Texture after dependency review");
				}

			if (settings.IsMobile && isUrp)
			{
				var cascadeCount = pipeline.FindProperty("m_ShadowCascadeCount");
				if (cascadeCount != null && cascadeCount.intValue > 1)
					attribute(entry.AddIssue("PRJ038", cascadeCount.intValue + " cascades", "1 cascade or none", 0, null,
						"URP stores cascade count on the URP asset rather than QualitySettings. Select one cascade only if the shadow-quality loss is acceptable.",
						options: new List<FixOption> { new FixOption {
							Label = "Set URP shadow cascades to 1",
							Recommended = false,
							Risk = FixRisk.Medium,
							Warning = "Review shadow quality and distance on the target device.",
							Apply = c => RenderPipelineUtility.SetInt(c, pipelinePath, "m_ShadowCascadeCount", 1),
							Verify = _ => RenderPipelineUtility.ReadInt(pipelinePath, "m_ShadowCascadeCount") == 1
						} }));

				var urpCrossFade = pipeline.FindProperty("m_EnableLODCrossFade");
				if (urpCrossFade != null && urpCrossFade.boolValue)
				{
					bool complete = TrySearchProjectText(ctx, new[] { "animatecrossfading", "crossfadeanimationduration", "enablelodcrossfade", "m_enablelodcrossfade" }, out bool usageFound);
					var issue = entry.AddIssue("PRJ053", "On", "Off", 0,
						c => RenderPipelineUtility.SetBool(c, pipelinePath, "m_EnableLODCrossFade", false),
						"Disables URP LOD crossfade on mobile only when complete project text proof finds no known crossfade-specific dependencies.",
						verify: _ => RenderPipelineUtility.ReadBool(pipelinePath, "m_EnableLODCrossFade") == false);
					if (!complete || usageFound) RouteToReview(issue, !complete ? "Complete project proof is unavailable; review LOD crossfade dependencies before disabling." : "LOD crossfade configuration patterns were found; review transition expectations before disabling.", "Disable LOD Cross Fade after review");
				}

				var softShadows = pipeline.FindProperty("m_SoftShadowsSupported");
				if (softShadows != null && softShadows.boolValue)
					attribute(entry.AddIssue("PRJ054", "Soft shadows", "Hard shadows or lower-cost setting", 0, null,
						"URP stores soft-shadow support on the URP asset. Soft filtering adds GPU work.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ054 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => RenderPipelineUtility.SetBool(c, pipelinePath, "m_SoftShadowsSupported", false),
							Verify = _ => RenderPipelineUtility.ReadBool(pipelinePath, "m_SoftShadowsSupported") == false
						} }
					));

				// Stored as the sample count, not the enum position: Disabled is 1, not 0.
				var msaa = pipeline.FindProperty("m_MSAA");
				using (RuleTiming.Begin("PAS027", entry.Path))
					if (msaa != null && settings.IsMobile && msaa.intValue > 4)
						attribute(entry.AddIssue("PAS027", msaa.intValue + "x", "4x maximum", 0,
							c => RenderPipelineUtility.SetInt(c, pipelinePath, "m_MSAA", ClampUrpMobileMsaa(msaa.intValue)),
							"Clamps URP mobile MSAA to 4x only when it exceeds 4x. Values below 4x are left unchanged.",
							verify: _ => RenderPipelineUtility.ReadInt(pipelinePath, "m_MSAA") <= 4));

				var fastSrgb = pipeline.FindProperty("m_UseFastSRGBLinearConversion");
				using (RuleTiming.Begin("PRJ050", entry.Path))
					if (fastSrgb != null && !fastSrgb.boolValue)
					{
						attribute(entry.AddIssue("PRJ050", "Off", "On", 0,
							c => SetFastSrgbLinearConversion(c, pipelinePath, true),
							"Enables URP's documented fast approximation for sRGB/Linear conversion on this mobile-used pipeline asset. The asset may also be shared by other targets; the trade-off is reduced numerical accuracy for faster shader conversion.",
							verify: _ => RenderPipelineUtility.ReadBool(pipelinePath, "m_UseFastSRGBLinearConversion") == true));
					}

				var hdr = pipeline.FindProperty("m_SupportsHDR");
				using (RuleTiming.Begin("PAS026", entry.Path))
					if (hdr != null && hdr.boolValue)
						attribute(entry.AddIssue("PAS026", "On", "Off unless post-processing needs it",
						options: new List<FixOption> { new FixOption {
							Label = "Disable URP HDR",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => RenderPipelineUtility.SetBool(c, pipelinePath, "m_SupportsHDR", false),
							Verify = _ => RenderPipelineUtility.ReadBool(pipelinePath, "m_SupportsHDR") == false
						} }
					));

				var lightsMode = pipeline.FindProperty("m_AdditionalLightsRenderingMode");
				var lightShadows = pipeline.FindProperty("m_AdditionalLightsShadowmapResolution");
				using (RuleTiming.Begin("PRJ051", entry.Path))
					if (!forwardPlus
						&& lightsMode != null && IsSerializedEnumNamed(lightsMode, "Per Pixel")
						&& lightShadows != null && lightShadows.intValue >= 2048)
					{
						attribute(entry.AddIssue("PRJ051", "Per Pixel, " + lightShadows.intValue + " shadow map",
							"Per Vertex, or a lower shadow map", 0, null,
							"Choose whether to reduce per-light shading cost or shadow-map resolution; the visual trade-offs differ.",
							options: new List<FixOption> {
								new FixOption {
									Label = "Use Per Vertex additional lights",
									Recommended = false,
									Risk = FixRisk.Medium,
									Warning = "Additional light appearance may change; inspect the target scene.",
									Apply = c => SetPipelineEnumByDisplayName(c, pipelinePath, "m_AdditionalLightsRenderingMode", "Per Vertex"),
									Verify = _ => RenderPipelineUtility.ReadEnumName(pipelinePath, "m_AdditionalLightsRenderingMode") == "Per Vertex"
								},
								new FixOption {
									Label = "Reduce additional-light shadow map to 1024",
									Recommended = false,
									Risk = FixRisk.Medium,
									Warning = "Shadow detail may decrease; inspect important close-up shadows.",
									Apply = c => RenderPipelineUtility.SetInt(c, pipelinePath, "m_AdditionalLightsShadowmapResolution", 1024),
									Verify = _ => RenderPipelineUtility.ReadInt(pipelinePath, "m_AdditionalLightsShadowmapResolution") <= 1024
								}
							}));
					}

				var renderScale = pipeline.FindProperty("m_RenderScale");
				using (RuleTiming.Begin("PRJ055", entry.Path))
					if (renderScale != null && renderScale.floatValue > 1.001f)
					{
						attribute(entry.AddIssue("PRJ055", renderScale.floatValue.ToString("0.###"), "1 or below", 0, null,
						options: new List<FixOption> { new FixOption {
							Label = "PRJ055 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => RenderPipelineUtility.SetFloat(c, pipelinePath, "m_RenderScale", 1f),
							Verify = _ => ReadPipelineFloat(pipelinePath, "m_RenderScale") <= 1.001f
						} }
					));
					}

				var mainLightShadows = pipeline.FindProperty("m_MainLightShadowsSupported");
				using (RuleTiming.Begin("PRJ056", entry.Path))
					if (mainLightShadows != null && mainLightShadows.boolValue)
					{
						attribute(entry.AddIssue("PRJ056", "On", "Disable or lower for mobile when not visually required", 0, null,
						options: new List<FixOption> { new FixOption {
							Label = "PRJ056 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => RenderPipelineUtility.SetBool(c, pipelinePath, "m_MainLightShadowsSupported", false),
							Verify = _ => RenderPipelineUtility.ReadBool(pipelinePath, "m_MainLightShadowsSupported") == false
						} }
					));
					}

				var maxAdditionalLights = pipeline.FindProperty("m_MaxAdditionalLightsCount");
				using (RuleTiming.Begin("PRJ057", entry.Path))
					if (!forwardPlus && maxAdditionalLights != null && maxAdditionalLights.intValue > 4)
					{
						attribute(entry.AddIssue("PRJ057", maxAdditionalLights.intValue.ToString(), "4 or fewer where the scene permits", 0, null,
						options: new List<FixOption> { new FixOption {
							Label = "PRJ057 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => SetPipelineIntFirstAvailable(c, pipelinePath, 4, "m_AdditionalLightsCount", "m_MaxAdditionalLightsCount"),
							Verify = _ => (RenderPipelineUtility.ReadInt(pipelinePath, "m_AdditionalLightsCount") ?? RenderPipelineUtility.ReadInt(pipelinePath, "m_MaxAdditionalLightsCount") ?? -1) == 4
						} }
					));
					}
			}
		}

		/// <summary>
		/// Native plugins configured for Android that have no ARM64 library, listed by path.
		/// <para>
		/// Read off the plugin importers rather than guessed from folder names: a plugin declares
		/// which CPU it is for, and that declaration is what the build uses. Only plugins actually
		/// enabled for Android matter - one disabled, or set for another platform, cannot fail an
		/// Android link.
		/// </para>
		/// </summary>
		static List<string> NativePluginsWithoutArm64()
		{
			var missing = new List<string>();

			try
			{
				foreach (var importer in PluginImporter.GetAllImporters())
				{
					if (importer == null || !importer.isNativePlugin) continue;
					if (!importer.GetCompatibleWithPlatform(BuildTarget.Android)) continue;

					var cpu = importer.GetPlatformData(BuildTarget.Android, "CPU");

					// "AnyCPU" and an unset value both mean the plugin does not restrict itself, so
					// there is nothing to be missing.
					if (string.IsNullOrEmpty(cpu)
						|| cpu.Equals("AnyCPU", StringComparison.OrdinalIgnoreCase)
						|| cpu.IndexOf("ARM64", StringComparison.OrdinalIgnoreCase) >= 0)
						continue;

					missing.Add(importer.assetPath + " (" + cpu + ")");
				}
			}
			catch (Exception e)
			{
				// Unable to tell means unable to promise. The caller treats a non-empty list as a
				// reason to stand down, so a failed inspection has to do the same - hence the marker
				// entry rather than an empty list.
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not inspect native plugin architectures: " + e.Message);
				missing.Add("native plugin architectures could not be read");
			}

			return missing;
		}

		static bool TargetUsesGpuResidentDrawer(OptimizerSettings settings, out bool coverageKnown)
		{
			coverageKnown = false;
			try
			{
				BuildTarget target = PlatformUtility.ToBuildTarget(settings.targetPlatform);
				int[] levels = GetActiveQualityLevelsForPlatform(target);
				if (levels == null || levels.Length == 0) return false;
				coverageKnown = true;
				bool anyEnabled = false;
				UnityEngine.Object projectDefault = GraphicsSettings.defaultRenderPipeline;
				foreach (int level in levels)
				{
					if (level < 0 || level >= QualitySettings.count) { coverageKnown = false; continue; }
					UnityEngine.Object selectedPipeline = null;
					WithQualityLevel(level, () => selectedPipeline = QualitySettings.renderPipeline != null ? QualitySettings.renderPipeline : projectDefault);
					if (selectedPipeline == null || !IsUrpPipeline(selectedPipeline)) continue;
					var mode = new SerializedObject(selectedPipeline).FindProperty("m_GPUResidentDrawerMode");
					if (mode == null) { coverageKnown = false; continue; }
					if (mode.enumDisplayNames != null && mode.enumValueIndex >= 0 && mode.enumValueIndex < mode.enumDisplayNames.Length)
						anyEnabled |= !string.Equals(mode.enumDisplayNames[mode.enumValueIndex], "Disabled", StringComparison.OrdinalIgnoreCase);
					else anyEnabled |= mode.enumValueIndex > 0;
				}
				return anyEnabled;
			}
			catch { coverageKnown = false; return false; }
		}

		static bool UsesScriptableRenderPipelineForTarget(OptimizerSettings settings)
		{
			try
			{
				var levels = GetActiveQualityLevelsForPlatform(PlatformUtility.ToBuildTarget(settings.targetPlatform));
				if (levels.Length == 0) return GraphicsSettings.defaultRenderPipeline != null;
				for (int i = 0; i < levels.Length; i++)
				{
					bool hasPipeline = false;
					WithQualityLevel(levels[i], () => hasPipeline = QualitySettings.renderPipeline != null || GraphicsSettings.defaultRenderPipeline != null);
					if (hasPipeline) return true;
				}
			}
			catch { }
			return false;
		}

		static bool TargetSupportsComputeShaders(OptimizerSettings settings)
		{
			if (IsWebGL(settings)) return false;

			try
			{
				var apis = PlayerSettings.GetGraphicsAPIs(PlatformUtility.ToBuildTarget(settings.targetPlatform));
				if (apis == null || apis.Length == 0) return false;

				for (int i = 0; i < apis.Length; i++)
				{
					switch (apis[i])
					{
						// Legacy graphics API values can still appear in saved project settings.
#pragma warning disable CS0618
						case GraphicsDeviceType.OpenGLES2:
#pragma warning restore CS0618
						case GraphicsDeviceType.OpenGLES3:
							continue;
						default:
							return true;
					}
				}
			}
			catch { }
			return false;
		}

		static bool TargetSupportsMaxQueuedFrames(OptimizerSettings settings)
		{
			try
			{
				var apis = PlayerSettings.GetGraphicsAPIs(PlatformUtility.ToBuildTarget(settings.targetPlatform));
				if (apis == null) return false;
				for (int i = 0; i < apis.Length; i++)
				{
					if (apis[i] == GraphicsDeviceType.Direct3D11
						|| apis[i] == GraphicsDeviceType.Direct3D12
						|| apis[i] == GraphicsDeviceType.Vulkan)
						return true;
				}
			}
			catch { }
			return false;
		}

		static SerializedProperty FindSerializedBoolean(SerializedObject so, params string[] candidates)
		{
			if (so == null || candidates == null || candidates.Length == 0) return null;
			for (int i = 0; i < candidates.Length; i++)
			{
				var direct = so.FindProperty(candidates[i]);
				if (direct != null && direct.propertyType == SerializedPropertyType.Boolean) return direct;
			}
			try
			{
				var iterator = so.GetIterator();
				bool enter = true;
				while (iterator.NextVisible(enter))
				{
					enter = false;
					if (iterator.propertyType != SerializedPropertyType.Boolean) continue;
					string name = iterator.name ?? string.Empty;
					string display = iterator.displayName ?? string.Empty;
					for (int i = 0; i < candidates.Length; i++)
					{
						string candidate = candidates[i];
						if (name.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0 ||
							display.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0)
							return so.FindProperty(iterator.propertyPath);
					}
				}
			}
			catch { }
			return null;
		}

		static bool IsCompatibilityModeProperty(SerializedProperty property)
		{
			if (property == null) return false;
			string name = property.name ?? string.Empty;
			string display = property.displayName ?? string.Empty;
			return name.IndexOf("compatibility", StringComparison.OrdinalIgnoreCase) >= 0 ||
				display.IndexOf("compatibility", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		static bool HasForwardPlusRenderer(UnityEngine.Object pipelineAsset)
		{
			if (pipelineAsset == null || !IsUrpPipeline(pipelineAsset)) return false;

			try
			{
				var so = new SerializedObject(pipelineAsset);
				var renderers = so.FindProperty("m_RendererDataList");
				if (renderers == null || !renderers.isArray || renderers.arraySize == 0) return false;

				bool sawRenderer = false;
				for (int i = 0; i < renderers.arraySize; i++)
				{
					var element = renderers.GetArrayElementAtIndex(i);
					var rendererAsset = element != null ? element.objectReferenceValue : null;
					if (rendererAsset == null) continue;

					sawRenderer = true;
					var renderer = new SerializedObject(rendererAsset);
					var mode = renderer.FindProperty("m_RenderingMode");
					if (mode == null || !IsSerializedEnumNamed(mode, "Forward+"))
						return false;
				}

				return sawRenderer;
			}
			catch
			{
				return false;
			}
		}

		static bool IsHdrpPipeline(UnityEngine.Object pipelineAsset)
		{
			if (pipelineAsset == null) return false;
			return pipelineAsset.GetType().Name
				.IndexOf("HDRenderPipelineAsset", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		static bool IsUrpPipeline(UnityEngine.Object pipelineAsset)
		{
			if (pipelineAsset == null) return false;
			var name = pipelineAsset.GetType().Name;
			return name.IndexOf("UniversalRenderPipelineAsset", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		static bool IsSerializedEnumNamed(SerializedProperty property, string expectedName)
		{
			if (property == null || property.enumDisplayNames == null) return false;
			int index = property.enumValueIndex;
			if (index < 0 || index >= property.enumDisplayNames.Length) return false;
			return string.Equals(property.enumDisplayNames[index], expectedName, StringComparison.OrdinalIgnoreCase);
		}

		static int FindSerializedEnumIndex(SerializedProperty property, string expectedName, int fallbackIndex)
		{
			if (property != null && property.enumDisplayNames != null)
			{
				for (int i = 0; i < property.enumDisplayNames.Length; i++)
					if (string.Equals(property.enumDisplayNames[i], expectedName, StringComparison.OrdinalIgnoreCase)) return i;
			}
			return fallbackIndex >= 0 ? fallbackIndex : -1;
		}

		static int[] GetActiveQualityLevelsForPlatform(BuildTarget target)
		{
			try
			{
				var group = BuildPipeline.GetBuildTargetGroup(target);
				var namedTarget = NamedBuildTarget.FromBuildTargetGroup(group);
				var selected = QualitySettings.GetActiveQualityLevelsForPlatform(namedTarget.TargetName);
				if (selected == null || selected.Length == 0) return Array.Empty<int>();
				var unique = new List<int>(selected.Length);
				for (int i = 0; i < selected.Length; i++)
				{
					if (!unique.Contains(selected[i])) unique.Add(selected[i]);
				}
				unique.Sort();
				return unique.ToArray();
			}
			catch
			{
				return Array.Empty<int>();
			}
		}

		/// <summary>
		/// Reads one quality level by switching to it, then switches back.
		/// <para>
		/// Unity exposes most of QualitySettings only for the active level, so there is no read-only
		/// route to another one; switching is the API. <c>applyExpensiveChanges: false</c> keeps the
		/// cost down - it skips reapplying the settings that force a graphics-device-level change -
		/// but the switch is still not free, which is why the level walk reads everything it needs in
		/// one pass rather than entering a level per rule.
		/// </para>
		/// <para>
		/// Restoration happens in a finally and is checked rather than assumed. Leaving the editor on
		/// a different quality level than it started on is a change to the user's project that
		/// nobody asked for, and it would persist silently.
		/// </para>
		/// </summary>
		static void WithQualityLevel(int levelIndex, Action action)
		{
			int previous = QualitySettings.GetQualityLevel();

			// Refuse rather than switch to something that does not exist: SetQualityLevel with an
			// out-of-range index is not a no-op, and the restore below would then be restoring from
			// an unknown state.
			if (levelIndex < 0 || levelIndex >= QualitySettings.count)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Skipped quality level " + levelIndex
								 + ": the project has " + QualitySettings.count + " level(s).");
				return;
			}

			try
			{
				QualitySettings.SetQualityLevel(levelIndex, false);
				action();
			}
			catch (Exception e)
			{
				// Swallowed here so the finally still runs and the next level is still read. One
				// level failing to analyze must not cost the others, and must not strand the editor.
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not analyze quality level "
								 + levelIndex + ": " + e.Message);
			}
			finally
			{
				Restore(previous);
			}
		}

		/// <summary>
		/// Puts the editor back on the level it was on. Verified afterwards, because the whole point
		/// of the finally is that this is the step that must not quietly fail.
		/// </summary>
		static void Restore(int previous)
		{
			if (previous < 0 || previous >= QualitySettings.count) return;

			try
			{
				if (QualitySettings.GetQualityLevel() != previous)
					QualitySettings.SetQualityLevel(previous, false);

				if (QualitySettings.GetQualityLevel() != previous)
				{
					Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Could not restore the editor's quality level to "
								   + previous + " (it is on " + QualitySettings.GetQualityLevel()
								   + "). Set it back by hand in Project Settings > Quality.");
				}
			}
			catch (Exception e)
			{
				Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Could not restore the editor's quality level: " + e.Message
							   + " Set it back by hand in Project Settings > Quality.");
			}
		}

		// --- analyzer-local fix safety helpers -----------------------------------------------

		/// <summary>Records a per-finding exception without weakening the canonical rule's preferred target.</summary>
		static OptimizationIssue RouteToDiagnosticOnly(OptimizationIssue issue, string reason)
		{
			if (issue == null) return null;
			issue.FixKind = FixKind.None;
			issue.Fix = null;
			issue.Verify = null;
			if (issue.Options != null) issue.Options.Clear();
			issue.Confidence = OptimizationConfidence.Diagnostic;
			issue.Note(reason, "Automatic-fix safety gate");
			return issue;
		}

		/// <summary>Routes a known exceptional instance to Review, retaining its concrete action as an option.</summary>
		static OptimizationIssue RouteToReview(OptimizationIssue issue, string reason, string label = "Apply after review")
		{
			if (issue == null) return null;
			if (issue.Options.Count == 0 && issue.Fix != null)
				issue.Options.Add(new FixOption
				{
					Label = label,
					Recommended = false,
					Risk = issue.Risk == FixRisk.Unspecified ? FixRisk.Medium : issue.Risk,
					Warning = reason,
					Apply = issue.Fix,
					Verify = issue.Verify
				});
			issue.Fix = null;
			issue.Verify = null;
			issue.FixKind = FixKind.Review;
			issue.Note(reason, "Review exception");
			return issue;
		}

		/// <summary>Changes one named Quality Settings level and always restores the Editor's selected level.</summary>
		static void ChangeQualityLevelStrict(int index, string expectedName, Action change)
		{
			if (index < 0 || index >= QualitySettings.count || index >= QualitySettings.names.Length)
				throw new InvalidOperationException("The affected Quality Level no longer exists. Rescan before fixing.");
			if (!string.Equals(QualitySettings.names[index], expectedName, StringComparison.Ordinal))
				throw new InvalidOperationException("The Quality Level name/index changed after analysis. Rescan before fixing.");
			if (change == null) throw new ArgumentNullException(nameof(change));
			int previous = QualitySettings.GetQualityLevel();
			try
			{
				QualitySettings.SetQualityLevel(index, false);
				if (QualitySettings.GetQualityLevel() != index)
					throw new InvalidOperationException("Unity could not select the analyzed Quality Level.");
				change();
			}
			finally
			{
				if (previous >= 0 && previous < QualitySettings.count && QualitySettings.GetQualityLevel() != previous)
				{
					QualitySettings.SetQualityLevel(previous, false);
					if (QualitySettings.GetQualityLevel() != previous)
						throw new InvalidOperationException("Could not restore the Editor's previously selected Quality Level.");
				}
			}
		}

		static bool HasAndroidVulkanPreferred(GraphicsDeviceType[] expected)
		{
			try
			{
				return HasAndroidVulkanPreferred(PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android),
					PlayerSettings.GetGraphicsAPIs(BuildTarget.Android), expected);
			}
			catch { return false; }
		}

		internal static bool HasAndroidVulkanPreferred(bool useDefaultApis, GraphicsDeviceType[] current, GraphicsDeviceType[] expected)
		{
			return !useDefaultApis && expected != null && expected.Length >= 2
				&& expected[0] == GraphicsDeviceType.Vulkan && expected[1] == GraphicsDeviceType.OpenGLES3
				&& PlatformUtility.GraphicsApiListsEqual(current, expected);
		}

		static bool ShouldRecommendAndroidBlitAuto(bool blitAlways, bool useDefaultApis, GraphicsDeviceType[] apis)
			=> blitAlways && (useDefaultApis || (apis != null && apis.Any(api => api != GraphicsDeviceType.Vulkan)));

		static bool CanApplyAndroidBlitAuto(AndroidBlitType currentBlitType, bool useDefaultApis,
			GraphicsDeviceType[] currentApis, bool expectedUseDefaultApis, GraphicsDeviceType[] expectedApis)
			=> currentBlitType == AndroidBlitType.Always
				&& useDefaultApis == expectedUseDefaultApis
				&& PlatformUtility.GraphicsApiListsEqual(currentApis, expectedApis)
				&& ShouldRecommendAndroidBlitAuto(true, useDefaultApis, currentApis);

		static void SetAndroidBlitTypeAuto(bool expectedUseDefaultApis, GraphicsDeviceType[] expectedApis)
		{
			bool useDefaultApis = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android);
			var currentApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android) ?? Array.Empty<GraphicsDeviceType>();
			if (!CanApplyAndroidBlitAuto(PlayerSettings.Android.blitType, useDefaultApis, currentApis,
				expectedUseDefaultApis, expectedApis))
				throw new InvalidOperationException("Android Blit Type or graphics API settings changed after analysis, or the setting is not applicable. Rescan before applying this fix.");

			PlayerSettings.Android.blitType = AndroidBlitType.Auto;
		}

		static void SetFastSrgbLinearConversion(FixContext context, string pipelinePath, bool enabled)
		{
			if (context == null) throw new InvalidOperationException("The fix context is missing.");
			context.EnsureWritable();
			if (string.IsNullOrWhiteSpace(pipelinePath)) throw new InvalidOperationException("The URP asset path is missing.");
			if (!UnityEditorUtility.CanEditPath(pipelinePath, out string editReason))
				throw new InvalidOperationException("The URP asset cannot be edited: " + editReason);
			var asset = AssetDatabase.LoadMainAssetAtPath(pipelinePath);
			if (asset == null || !string.Equals(AssetDatabase.GetAssetPath(asset), pipelinePath, StringComparison.Ordinal))
				throw new InvalidOperationException("The URP asset could not be resolved at " + pipelinePath + ".");
			if (EditorUtility.IsDirty(asset))
				throw new InvalidOperationException("The URP asset has unsaved changes. Save it and rescan before applying this fix.");

			var serialized = new SerializedObject(asset);
			var property = serialized.FindProperty("m_UseFastSRGBLinearConversion");
			if (property == null || property.propertyType != SerializedPropertyType.Boolean)
				throw new InvalidOperationException("The URP asset does not expose the expected Boolean property m_UseFastSRGBLinearConversion.");
			if (property.boolValue == enabled)
				throw new InvalidOperationException("The URP conversion setting changed after analysis. Rescan before applying this fix.");

			context.RequireBackup(pipelinePath);
			property.boolValue = enabled;
			serialized.ApplyModifiedPropertiesWithoutUndo();
			EditorUtility.SetDirty(asset);
			AssetDatabase.SaveAssetIfDirty(asset);
			context.ChangedProjectSettings = true;
		}

		static void SetPipelineIntFirstAvailable(FixContext context, string assetPath, int value, params string[] propertyPaths)
		{
			context.EnsureWritable();
			context.RequireBackup(assetPath);
			var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
			if (asset == null) throw new InvalidOperationException("Could not load render-pipeline asset: " + assetPath);
			var serialized = new SerializedObject(asset);
			for (int i = 0; propertyPaths != null && i < propertyPaths.Length; i++)
			{
				var property = serialized.FindProperty(propertyPaths[i]);
				if (property == null || property.propertyType != SerializedPropertyType.Integer) continue;
				property.intValue = value;
				serialized.ApplyModifiedPropertiesWithoutUndo();
				EditorUtility.SetDirty(asset);
				AssetDatabase.SaveAssets();
				context.NeedsSave = true;
				return;
			}
			throw new InvalidOperationException("The render-pipeline asset does not expose a supported additional-light count property.");
		}

		static void SetPipelineEnumByDisplayName(FixContext context, string assetPath, string propertyPath, string displayName)
		{
			context.EnsureWritable();
			context.RequireBackup(assetPath);
			var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
			if (asset == null) throw new InvalidOperationException("Could not load render-pipeline asset: " + assetPath);
			var serialized = new SerializedObject(asset);
			var property = serialized.FindProperty(propertyPath);
			if (property == null || property.propertyType != SerializedPropertyType.Enum) throw new InvalidOperationException("Expected enum property " + propertyPath);
			int selected = Array.IndexOf(property.enumDisplayNames, displayName);
			if (selected < 0) throw new InvalidOperationException("Enum option not available: " + displayName);
			property.enumValueIndex = selected;
			serialized.ApplyModifiedPropertiesWithoutUndo();
			EditorUtility.SetDirty(asset);
			AssetDatabase.SaveAssets();
		}

		static T ReadQualityLevelStrict<T>(int index, string expectedName, Func<T> read)
		{
			T result = default(T);
			ChangeQualityLevelStrict(index, expectedName, () => result = read());
			return result;
		}

		static void SetAnisotropicFilteringPerTexture(FixContext context, int index, string expectedName)
		{
			if (ReadQualityLevelStrict(index, expectedName, () => QualitySettings.anisotropicFiltering) != AnisotropicFiltering.ForceEnable)
				throw new InvalidOperationException("Anisotropic filtering is no longer Force Enable on the analyzed Quality Level. Rescan before fixing.");
			SetQualitySetting(context, index, expectedName, () => QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable);
		}

		static void SetQualitySetting(FixContext context, int index, string name, Action change)
		{
			context.EnsureWritable();
			ChangeQualityLevelStrict(index, name, change);
			context.ChangedProjectSettings = true;
		}

		/// <summary>Policy helper: raise only Disabled/Minimal to the user's preferred Low; preserve Low and stronger levels.</summary>
		internal static ManagedStrippingLevel PreferredManagedStrippingLevel(ManagedStrippingLevel current)
		{
			return (int)current < (int)ManagedStrippingLevel.Low ? ManagedStrippingLevel.Low : current;
		}

		internal static int ClampUrpMobileMsaa(int current) => current > 4 ? 4 : current;
		internal static int ClampBuiltinMobileMsaa(int current) => current > 2 ? 2 : current;
		internal static int RaiseVrMsaaToFour(int current) => current < 4 ? 4 : current;
		internal static bool ShouldClampBuiltinMobileMsaa(bool isMobile, bool isVr, bool isBuiltInPipeline, int current) =>
			isMobile && !isVr && isBuiltInPipeline && current > 2;
		internal static bool IsBuiltInPipeline(bool hasQualityLevelOverride, bool hasProjectDefaultPipeline) =>
			!hasQualityLevelOverride && !hasProjectDefaultPipeline;

		static bool IsCurrentQualityLevelBuiltInPipeline() =>
			IsBuiltInPipeline(QualitySettings.renderPipeline != null, GraphicsSettings.defaultRenderPipeline != null);

		/// <summary>Conservative text evidence index used only when a project-proof pass is explicitly enabled.</summary>
		bool TrySearchProjectText(ScanContext ctx, string[] tokens, out bool found)
		{
			found = false;
			if (ctx == null || ctx.Settings == null || !ctx.Settings.projectProofPass) return false;
			var proof = ctx.GetProofIndex();
			if (proof == null || !proof.IsBuilt || proof.BuildFailed || !proof.CoverageComplete) return false;
			if (!EnsureProjectTextIndex()) return false;
			if (tokens == null || tokens.Length == 0) return true;
			for (int i = 0; i < tokens.Length; i++)
			{
				if (string.IsNullOrWhiteSpace(tokens[i])) continue;
				string token = tokens[i].ToLowerInvariant();
				foreach (var value in m_ProjectTextIndex.Values)
					if (value.IndexOf(token, StringComparison.Ordinal) >= 0)
					{
						found = true;
						return true;
					}
			}
			return true;
		}

		bool EnsureProjectTextIndex()
		{
			if (m_ProjectTextIndexBuilt) return m_ProjectTextIndexComplete;
			m_ProjectTextIndexBuilt = true;
			m_ProjectTextIndexComplete = false;
			m_ProjectTextIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			string projectRoot = Directory.GetCurrentDirectory();
			string[] roots = { Path.Combine(projectRoot, "Assets"), Path.Combine(projectRoot, "Packages") };
			var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				".cs", ".shader", ".hlsl", ".cginc", ".compute", ".shadergraph", ".mat", ".asset", ".prefab", ".unity",
				".asmdef", ".json", ".txt", ".inputactions", ".xml", ".gradle", ".kts", ".java", ".kt", ".pro",
				".manifest", ".yml", ".yaml", ".uxml", ".uss", ".rsp", ".plist", ".html", ".js", ".ts", ".glsl",
				".metal", ".mm", ".m", ".c", ".cpp", ".hpp", ".csproj", ".sln", ".sh", ".bat", ".config", ".conf", ".toml"
			};
			try
			{
				for (int r = 0; r < roots.Length; r++)
				{
					if (!Directory.Exists(roots[r])) continue;
					foreach (string file in Directory.EnumerateFiles(roots[r], "*", SearchOption.AllDirectories))
					{
						string ext = Path.GetExtension(file);
						if (!extensions.Contains(ext) && !string.Equals(Path.GetFileName(file), ".htaccess", StringComparison.OrdinalIgnoreCase)) continue;
						string normalizedPath = file.Replace('\\', '/');
						if (normalizedPath.EndsWith("/ProjectSettingsAnalyzer.cs", StringComparison.OrdinalIgnoreCase)
							|| normalizedPath.IndexOf("/ProjectOptimizer/", StringComparison.OrdinalIgnoreCase) >= 0
							|| normalizedPath.IndexOf("/ProjectOptimizer_", StringComparison.OrdinalIgnoreCase) >= 0) continue;
						if (file.IndexOf(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0
							|| file.IndexOf(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0
							|| file.IndexOf(Path.DirectorySeparatorChar + "Temp" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0) continue;
						var info = new FileInfo(file);
						if (!info.Exists || info.Length > 8 * 1024 * 1024) return false;
						string content = File.ReadAllText(file).ToLowerInvariant();
						m_ProjectTextIndex[file] = content;
					}
				}
				m_ProjectTextIndexComplete = true;
				return true;
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Project text proof is incomplete; automatic project-setting fix will be withheld: " + e.Message);
				m_ProjectTextIndex.Clear();
				return false;
			}
		}

		static bool HasNativePluginForTarget(BuildTarget target)
		{
			try
			{
				foreach (var importer in PluginImporter.GetAllImporters())
					if (importer != null && importer.isNativePlugin && importer.GetCompatibleWithPlatform(target)) return true;
				return false;
			}
			catch { return true; }
		}

		static bool HasAndroidEntryPointSensitiveAssets()
		{
			try
			{
				string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { "Assets/Plugins/Android" });
				for (int i = 0; i < guids.Length; i++)
				{
					string path = AssetDatabase.GUIDToAssetPath(guids[i]);
					string lower = (path ?? string.Empty).ToLowerInvariant();
					if (lower.EndsWith(".aar") || lower.EndsWith(".jar") || lower.EndsWith(".so") || lower.EndsWith(".dll")
						|| lower.EndsWith("androidmanifest.xml") || lower.Contains("mainactivity") || lower.Contains("gameactivity")) return true;
				}
				return false;
			}
			catch { return true; }
		}

		static List<string> FindUrpAssetPaths()
		{
			var candidates = new List<string>();
			try
			{
				string[] guids = AssetDatabase.FindAssets("t:RenderPipelineAsset", new[] { "Assets" });
				for (int i = 0; i < guids.Length; i++)
				{
					string path = AssetDatabase.GUIDToAssetPath(guids[i]);
					var asset = AssetDatabase.LoadMainAssetAtPath(path);
					if (IsUrpPipeline(asset) && !candidates.Contains(path)) candidates.Add(path);
				}
				candidates.Sort(StringComparer.Ordinal);
			}
			catch { candidates.Clear(); }
			return candidates;
		}

		static string FindUniqueUrpAssetPath()
		{
			List<string> candidates = FindUrpAssetPaths();
			return candidates.Count == 1 ? candidates[0] : null;
		}

		static bool TryGetLightmapStreamingForTarget(BuildTargetGroup group, out bool enabled)
		{
			enabled = false;
			try
			{
				var method = typeof(PlayerSettings).GetMethod("GetLightmapStreamingEnabledForPlatformGroup", BindingFlags.Static | BindingFlags.NonPublic);
				if (method == null) return false;
				object result = method.Invoke(null, new object[] { group });
				if (!(result is bool)) return false;
				enabled = (bool)result;
				return true;
			}
			catch { return false; }
		}

		static bool HasAnyLightmapStreamingSetter()
		{
			return typeof(PlayerSettings).GetMethod("SetLightmapStreamingEnabledForPlatformGroup", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null;
		}

		static bool TrySetLightmapStreamingForTarget(BuildTargetGroup group, bool enabled)
		{
			try
			{
				var method = typeof(PlayerSettings).GetMethod("SetLightmapStreamingEnabledForPlatformGroup", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (method == null) return false;
				method.Invoke(null, new object[] { group, enabled });
				return true;
			}
			catch { return false; }
		}

		static bool CanSetShaderChunkSize(BuildTarget target, bool platformOverride)
		{
			string methodName = platformOverride ? "SetShaderChunkSizeForPlatform" : "SetDefaultShaderChunkSizeInMB";
			try
			{
				foreach (var method in typeof(PlayerSettings).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
				{
					if (!string.Equals(method.Name, methodName, StringComparison.Ordinal)) continue;
					var args = method.GetParameters();
					if (platformOverride && args.Length == 2 && args[0].ParameterType == typeof(BuildTarget) && args[1].ParameterType == typeof(int)) return true;
					if (!platformOverride && args.Length == 1 && args[0].ParameterType == typeof(int)) return true;
				}
			}
			catch { }
			return false;
		}

		static bool TrySetShaderChunkSize(BuildTarget target, bool platformOverride, int sizeMb)
		{
			try
			{
				string methodName = platformOverride ? "SetShaderChunkSizeForPlatform" : "SetDefaultShaderChunkSizeInMB";
				MethodInfo method = null;
				foreach (var candidate in typeof(PlayerSettings).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
				{
					if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal)) continue;
					var args = candidate.GetParameters();
					if (platformOverride && args.Length == 2 && args[0].ParameterType == typeof(BuildTarget) && args[1].ParameterType == typeof(int)) { method = candidate; break; }
					if (!platformOverride && args.Length == 1 && args[0].ParameterType == typeof(int)) { method = candidate; break; }
				}
				if (method == null) return false;
				method.Invoke(null, platformOverride ? new object[] { target, sizeMb } : new object[] { sizeMb });
				return true;
			}
			catch { return false; }
		}

		static bool TryReadShaderChunkSize(BuildTarget target, bool platformOverride, out int sizeMb)
		{
			try
			{
				sizeMb = platformOverride ? PlayerSettings.GetShaderChunkSizeInMBForPlatform(target) : PlayerSettings.GetDefaultShaderChunkSizeInMB();
				return true;
			}
			catch { sizeMb = 0; return false; }
		}

		static bool TrySetEnumProperty(object owner, string propertyName, string enumName)
		{
			if (owner == null) return false;
			try
			{
				var property = owner.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (property == null || !property.CanWrite || !property.PropertyType.IsEnum) return false;
				object value = Enum.Parse(property.PropertyType, enumName, true);
				property.SetValue(owner, value, null);
				return true;
			}
			catch { return false; }
		}

		// --- small helpers ----------------------------------------------------------------

		/// <summary>
		/// Runs a check that touches an API which may not exist for the selected platform, or whose
		/// module is not installed. One unavailable setting must not cost the rest of the report.
		/// </summary>
		static void TryEvaluate(Action check)
		{
			try { check(); }
			catch (Exception e) { Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Settings check skipped: " + e.Message); }
		}

		static string Join(GraphicsDeviceType[] apis)
		{
			if (apis == null || apis.Length == 0) return "-";
			var names = new string[apis.Length];
			for (int i = 0; i < apis.Length; i++) names[i] = apis[i].ToString();
			return string.Join(", ", names);
		}

		/// <summary>Reads the internal per-platform batching flags. Returns -1 when unavailable.</summary>
		static int ReadBatching(BuildTarget target, out int dynamicBatching)
		{
			dynamicBatching = -1;
			try
			{
				var getter = typeof(PlayerSettings).GetMethod("GetBatchingForPlatform",
					System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
				if (getter == null) return -1;

				var args = new object[] { target, 0, 0 };
				getter.Invoke(null, args);
				dynamicBatching = Convert.ToInt32(args[2]);
				return Convert.ToInt32(args[1]);
			}
			catch { return -1; }
		}

		static bool IsStaticBatchingDisabledForTarget(BuildTarget target)
		{
			int ignoredDynamicBatching;
			return ReadBatching(target, out ignoredDynamicBatching) == 0;
		}

		static SerializedObject GraphicsSettingsObject()
		{
			var graphics = GraphicsSettings.GetGraphicsSettings();
			return graphics != null ? new SerializedObject(graphics) : null;
		}

		static SerializedObject GraphicsSettingsObject(ScanContext ctx)
		{
			var owner = ctx?.BuildProfiles?.Active?.GraphicsSettingsOwner;
			if (owner != null) return new SerializedObject(owner);
			return GraphicsSettingsObject();
		}

		static string GraphicsSettingsAssetPath()
		{
			try
			{
				var graphics = GraphicsSettings.GetGraphicsSettings();
				if (graphics != null)
				{
					string path = AssetDatabase.GetAssetPath(graphics);
					if (!string.IsNullOrEmpty(path)) return path;
				}
			}
			catch { }
			return "ProjectSettings/GraphicsSettings.asset";
		}

		/// <summary>
		/// Reads a boolean off the live render pipeline asset, or null when it is not there.
		/// Used by verifiers: the point of checking is to read the object Unity ended up with, not
		/// the SerializedObject the fix wrote through.
		/// </summary>

		/// <summary>
		/// Reads a int off the live render pipeline asset, or null when it is not there.
		/// Used by verifiers: the point of checking is to read the object Unity ended up with, not
		/// the SerializedObject the fix wrote through.
		/// </summary>

		/// <summary>
		/// Writes a boolean on one named pipeline asset.
		/// <para>
		/// Addressed by path rather than through <c>GraphicsSettings.currentRenderPipeline</c>. The
		/// finding was made against a specific asset, possibly one the editor is not currently using,
		/// and a fix that resolves "current" at click time can land on a different quality tier's
		/// pipeline entirely.
		/// </para>
		/// </summary>

		static float ReadPipelineFloat(string pipelinePath, string propertyName)
		{
			var pipeline = LoadPipelineAt(pipelinePath);
			if (pipeline == null) throw new InvalidOperationException("The render-pipeline asset is unavailable for verification.");
			var property = new SerializedObject(pipeline).FindProperty(propertyName);
			if (property == null || property.propertyType != SerializedPropertyType.Float)
				throw new InvalidOperationException("The render-pipeline float property '" + propertyName + "' is unavailable for verification.");
			return property.floatValue;
		}

		static UnityEngine.Object LoadPipelineAt(string pipelinePath)
		{
			if (string.IsNullOrEmpty(pipelinePath)) return GraphicsSettings.currentRenderPipeline;
			return AssetDatabase.LoadMainAssetAtPath(pipelinePath);
		}

		static AssetEntry NewEntry(string path, string name, string settingsPage)
		{
			var entry = new AssetEntry
			{
				Path = path,
				Name = name,
				Directory = "ProjectSettings",
				Extension = ".asset",
				Category = AssetCategory.ProjectSetting,
				TypeLabel = "Settings",
				FileSize = FileUtility.FileSizeOf(path)
			};
			entry.Set("settingsPage", settingsPage);
			return entry;
		}
	}
}
