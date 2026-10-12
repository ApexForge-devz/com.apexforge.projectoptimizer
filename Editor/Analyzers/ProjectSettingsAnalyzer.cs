using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
		public string DisplayName => "Project settings";

		static ProjectSettingsAnalyzer()
		{
			// --- project-wide / common -------------------------------------------------
			Rules.Register(id: "PRJ001", title: "Optimize Mesh Data disabled", category: AssetCategory.ProjectSetting,
							why: "Strips vertex attributes that the build can prove are unused, reducing mesh build size, loading cost and runtime memory. Do not use it blindly when materials or shaders are changed at runtime.",
							fix: "Enable Optimize Mesh Data in Player Settings > Other Settings, after verifying runtime material/shader changes do not depend on stripped vertex channels.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ002", title: "Managed code stripping is low or disabled", category: AssetCategory.ProjectSetting,
							why: "Stripping removes IL that nothing calls. On IL2CPP builds it is usually the single largest reduction in binary size available.",
							fix: "Raise Managed Stripping Level to Medium or High, and add a link.xml for types resolved only by reflection.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ003", title: "Mono scripting backend on a mobile-capable target", category: AssetCategory.ProjectSetting,
							why: "IL2CPP compiles to native code: faster managed execution, smaller stripped binaries, and it is required for 64-bit store submissions.",
							fix: "Set Scripting Backend to IL2CPP for mobile targets.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ004", title: "Incremental garbage collection disabled", category: AssetCategory.ProjectSetting,
							why: "Incremental GC spreads collection over several frames instead of stopping the game for one long pause, which is what players perceive as a hitch.",
							fix: "Enable Use Incremental GC in Player Settings.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ005", title: "Prebake Collision Meshes disabled", category: AssetCategory.ProjectSetting,
							why: "Prebaking moves supported mesh-collider cooking work out of player startup/load time and into the build pipeline, at the cost of build data and build time.",
							fix: "Enable Prebake Collision Meshes when faster player loading is more important than the additional build cost.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ006", title: "Static batching disabled", category: AssetCategory.ProjectSetting,
							why: "Static batching can reduce draw-call overhead for eligible non-moving renderers, while increasing build data and memory. It is most useful when the scene contains many static objects that can actually batch.",
							fix: "Enable Static Batching when profiling shows draw-call or render-state overhead is significant and the memory/build-size tradeoff is acceptable.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ007", title: "Dynamic batching enabled alongside a Scriptable Render Pipeline", category: AssetCategory.ProjectSetting,
							why: "Dynamic batching can move additional vertex work onto the CPU each frame. In SRP projects the SRP Batcher and GPU instancing may be better fits, but the best choice remains workload-dependent.",
							fix: "Profile with the active SRP and disable Dynamic Batching when it increases CPU render time without providing a measurable benefit.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ008", title: "Texture streaming disabled", category: AssetCategory.ProjectSetting,
							why: "Texture streaming keeps only the mip levels the cameras need resident, capping texture memory at a budget you choose instead of the sum of everything loaded.",
							fix: "Enable Texture Streaming in Quality Settings, and enable Streaming Mipmaps on large textures.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ010", title: "Anisotropic filtering forced on for every texture", category: AssetCategory.ProjectSetting,
							why: "Forcing anisotropic filtering globally applies it to UI, effects and every other texture that gains nothing from it, at real bandwidth cost.",
							fix: "Set Anisotropic Textures to Per Texture.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ011", title: "SRP Batcher disabled", category: AssetCategory.ProjectSetting,
							why: "The SRP Batcher reduces CPU render-state setup for compatible shaders and is a major CPU optimization in URP/HDRP. It is only useful when the active pipeline and shaders support it.",
							fix: "Enable SRP Batcher on the render pipeline asset.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ012", title: "Shadow distance is very large", category: AssetCategory.ProjectSetting,
							why: "Shadow distance decides how much of the world is re-rendered into shadow maps every frame. It is usually the cheapest quality dial to turn down.",
							fix: "Reduce the shadow distance on the render pipeline asset, or in Quality Settings.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ013", title: "Layer collision matrix is fully enabled", category: AssetCategory.ProjectSetting,
							why: "Every enabled cell in the collision matrix is a pair of layers the broadphase must test against each other. Projects that never prune the matrix pay for collisions that can never matter, such as UI or effects layers colliding with terrain.",
							fix: "Uncheck the layer pairs that should never interact in Project Settings > Physics.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ014", title: "Collision callbacks are not reused", category: AssetCategory.ProjectSetting,
							why: "Without reuse, every OnCollision callback allocates a fresh Collision object, producing constant garbage in physics-heavy scenes.",
							fix: "Enable Reuse Collision Callbacks when collision callback data does not escape the callback lifetime.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ015", title: "Accelerometer polled on a mobile target", category: AssetCategory.ProjectSetting,
							why: "The accelerometer is sampled continuously whether or not any script reads it, costing CPU and battery.",
							fix: "Set Accelerometer Frequency to Disabled only when project code does not read acceleration/gyro input.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ016", title: "Real audio voice budget is high", category: AssetCategory.ProjectSetting,
							why: "Every real voice is decoded and mixed each audio frame. Most games are indistinguishable at 24-32 real voices and pay noticeably less CPU.",
							fix: "Lower Max Real Voices in Audio settings.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ017", title: "Android texture compression does not prefer ASTC", category: AssetCategory.ProjectSetting,
							why: "ASTC is a strong default for modern Android hardware, but texture-compression targeting can intentionally ship multiple formats through an Android App Bundle.",
							fix: "Prefer ASTC for modern Android targets, or configure ASTC alongside a compatibility format when device coverage requires it.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ018", title: "Physics solver iteration count above the default", category: AssetCategory.ProjectSetting,
							why: "Solver iterations are run for every contact every fixed step. Raising the global count to fix one unstable joint makes the whole simulation more expensive.",
							fix: "Return the project default to 6 and raise iterations per-Rigidbody on the few bodies that need it.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ019", title: "MSAA below 4x on a VR target", category: AssetCategory.ProjectSetting,
							why: "VR makes edge and geometric aliasing more noticeable, so MSAA can be valuable. The useful sample count is still a headset-, render-scale- and workload-dependent tradeoff.",
							fix: "Review 4x MSAA as a VR quality target and validate GPU frame time, memory bandwidth and image quality on the actual headset.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ020", title: "Engine code stripping disabled", category: AssetCategory.ProjectSetting,
							why: "Engine code stripping removes whole engine modules the game never touches - physics, video, terrain - from the built player. On IL2CPP builds it is one of the larger reductions in binary size available, on top of managed stripping.",
							fix: "Enable Strip Engine Code in Player Settings. Add a link.xml entry for anything reached only by reflection.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS0025", title: "Android managed stripping is Disabled, Low or Minimal", category: AssetCategory.ProjectSetting,
							why: "Lower managed stripping leaves more managed code in Android players than necessary, increasing binary size. The safe stripping level still depends on reflection and native/managed integration.",
							fix: "Use Medium or High Managed Stripping Level on Android after testing reflection-heavy systems and native integrations.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS0026", title: "iOS managed stripping is Disabled or Low", category: AssetCategory.ProjectSetting,
							why: "Lower managed stripping leaves more managed code in iOS players than necessary, increasing binary size. The safe stripping level still depends on reflection and native/managed integration.",
							fix: "Use Medium or High Managed Stripping Level on iOS after testing reflection-heavy systems and native integrations.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS1005", title: "IL2CPP compiler configuration is Debug", category: AssetCategory.ProjectSetting,
							why: "The Editor's active IL2CPP build-target configuration is Debug while the selected scan target is different. Same-target selected configuration is handled by PRJ023.",
							fix: "Review the active Editor BuildTarget's IL2CPP configuration and use Release when that active target is not being built for debugging.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS1006", title: "Lightmap Streaming is disabled", category: AssetCategory.ProjectSetting,
							why: "Without Lightmap Streaming, all lightmap detail levels remain resident when mipmap streaming is otherwise available. Large baked-lighting datasets can therefore consume avoidable GPU memory.",
							fix: "Enable Lightmap Streaming for the target platform when the project already uses mipmap streaming and its lightmap memory budget benefits from it.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS1009", title: "URP Asset is not specified", category: AssetCategory.ProjectSetting,
							why: "This ProjectOptimizer configuration targets URP. Without a Universal Render Pipeline Asset, the project falls back to the built-in renderer and the intended URP rendering configuration is not actually active.",
							fix: "Assign a Universal Render Pipeline Asset in Graphics Settings and verify the quality-level pipeline overrides.",
							severity: IssueSeverity.Critical,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS1011", title: "URP MSAA is 4x or 8x on a mobile target", category: AssetCategory.ProjectSetting,
							why: "URP MSAA at 4x or 8x can materially increase bandwidth and framebuffer cost on mobile GPUs. The correct level is workload-dependent.",
							fix: "Use the lowest URP MSAA level that meets the visual target after profiling on representative mobile hardware.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			// --- cross-platform player ------------------------------------------------
			Rules.Register(id: "PRJ021", title: "CPU mesh deformation is enabled", category: AssetCategory.ProjectSetting,
							why: "Unity 6 can process skinning mesh deformation on the CPU, GPU, or GPU in batches. CPU deformation can become a significant main-thread cost when many skinned meshes are active.",
							fix: "Use GPUBatched mesh deformation when the target graphics API and runtime use case support it; validate any code that reads deformed mesh data back on the CPU.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ022", title: "Mip Stripping disabled", category: AssetCategory.ProjectSetting,
							why: "Mip Stripping removes the mip levels no quality level can ever select from the build. On a project whose lowest quality level caps texture size, those top mips are shipped and downloaded for nothing.",
							fix: "Enable Texture Mipmap Stripping in Player Settings > Other Settings. It only helps when your quality levels actually set different texture limits.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ023", title: "IL2CPP compiler configuration is Debug", category: AssetCategory.ProjectSetting,
							why: "Debug is intended for development and disables important native-code optimizations. Release is the normal optimized configuration; Master enables additional optimization for final shipping builds at a higher build-time cost.",
							fix: "Use Release for normal optimized builds. Use Master for final shipping builds when the additional build time is acceptable.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ024", title: "IL2CPP code generation favours size on a performance target", category: AssetCategory.ProjectSetting,
							why: "OptimizeSize favors smaller native output, while OptimizeSpeed favors runtime performance. This is a build-size versus runtime-speed tradeoff, not a debug/release distinction.",
							fix: "Use OptimizeSpeed when runtime performance is the priority; use OptimizeSize when package/download size is the higher priority.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ025", title: "Graphics Jobs disabled on a supported target", category: AssetCategory.ProjectSetting,
							why: "Graphics Jobs can move graphics work away from the main thread, but the benefit is workload- and platform-dependent and Unity documents compatibility considerations.",
							fix: "Profile Graphics Jobs on the target device and enable them only when they reduce render-thread time without introducing instability.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ026", title: "Colour space is Gamma", category: AssetCategory.ProjectSetting,
							why: "Gamma space does lighting maths on non-linear values, so every blend, fade and light falloff is subtly wrong. Linear is the correct default on any GPU from the last decade, and is required for physically based shading to look right.",
							fix: "Switch to Linear in Player Settings > Other Settings. Expect to re-tune lighting and any hand-authored colour after the change, so do not do it late in a project.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			// --- Android ----------------------------------------------------------------
			Rules.Register(id: "PRJ027", title: "ARM64 missing from the Android target architectures", category: AssetCategory.ProjectSetting,
							why: "Google Play has required a 64-bit binary since 2019, so an ARMv7-only build cannot be published. Beyond that, a 64-bit device forced to run the 32-bit binary loses a substantial amount of performance for nothing.",
							fix: "Tick ARM64 in Player Settings > Other Settings > Target Architectures.",
							severity: IssueSeverity.Critical,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ028", title: "ARMv7 is shipped alongside ARM64 without architecture targeting", category: AssetCategory.ProjectSetting,
							why: "Shipping multiple CPU architectures increases native code size, but Android App Bundles can deliver architecture-specific content to devices. This should be based on your supported-device policy, not assumed to be wasted size.",
							fix: "Prefer ARM64-only when your supported-device data allows it; otherwise use an App Bundle/architecture targeting so devices receive only what they need.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ029", title: "Optimized Frame Pacing disabled", category: AssetCategory.ProjectSetting,
							why: "Without frame pacing Unity presents frames as soon as they are ready, so a run of 12 ms and 20 ms frames is displayed as unevenly as it was produced. The Android Frame Pacing library spaces presentation to the display's refresh rate, which reads as much smoother at the same average frame rate, and stops the display being driven faster than the game needs.",
							fix: "Enable Optimized Frame Pacing in Player Settings > Resolution and Presentation.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ030", title: "Android application entry point is Activity", category: AssetCategory.ProjectSetting,
							why: "Unity 6 provides Activity and GameActivity application entry points. GameActivity is the default entry point for new Unity projects and provides a different Android integration path, so the choice is compatibility- and integration-dependent rather than an unconditional performance switch.",
							fix: "Review GameActivity for new Android projects or when its integration features fit the project. Test custom manifests, native plugins, input and lifecycle behavior before changing the release entry point.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ031", title: "Vulkan is not the preferred Android graphics API", category: AssetCategory.ProjectSetting,
							why: "Vulkan can reduce driver overhead and expose more explicit GPU features than OpenGL ES, but device drivers, plugins and workload characteristics matter. Graphics API ordering therefore needs real-device validation.",
							fix: "Review Vulkan first with OpenGLES3 as a fallback where your supported-device policy permits it, then profile representative devices before making it the release default.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ032", title: "R8 minification disabled for release builds", category: AssetCategory.ProjectSetting,
							why: "R8 shrinks and obfuscates the Java side of the build. It does nothing for your C# code, but on a project carrying several Android plugins it removes a meaningful amount of dead Java and resources.",
							fix: "Enable Minify > Release in Player Settings > Publishing Settings. Keep a ProGuard keep-rule file for anything reached by reflection or JNI.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ034", title: "Blit Type forces a full-screen copy every frame", category: AssetCategory.ProjectSetting,
							why: "Blit Type Always copies the whole framebuffer once per frame whether or not anything needs it. At 1080p on a mobile GPU that is pure bandwidth, and bandwidth is the scarce resource on a tile-based renderer.",
							fix: "Set Blit Type to Auto in Player Settings > Resolution and Presentation, and check the game still presents correctly on device.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			// --- iOS --------------------------------------------------------------------
			Rules.Register(id: "PRJ035", title: "iOS script call optimization keeps exception checks", category: AssetCategory.ProjectSetting,
							why: "Slow and Safe emits a null check and an exception path around managed calls. Fast but no Exceptions removes them, which measurably reduces call overhead in hot managed code - at the cost of a hard crash instead of a managed exception when something is null.",
							fix: "Set Script Call Optimization to Fast but no Exceptions once the game is stable and you are no longer relying on caught exceptions in shipped builds.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			// --- WebGL ------------------------------------------------------------------
			Rules.Register(id: "PRJ036", title: "WebGL compression is not Brotli", category: AssetCategory.ProjectSetting,
							why: "Brotli produces noticeably smaller files than gzip, and on WebGL the download is the loading screen. It costs longer build times and needs the server to send Content-Encoding: br.",
							fix: "Set Compression Format to Brotli in Player Settings > Publishing Settings, and configure the host to serve the .br files with the right Content-Encoding header.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ037", title: "WebGL exception support is above the minimum", category: AssetCategory.ProjectSetting,
							why: "Full exception support adds checks around every managed operation and inflates the WebAssembly module substantially. Explicitly Thrown Exceptions Only keeps your own throws working and drops the rest.",
							fix: "Set Enable Exceptions to Explicitly Thrown Exceptions Only for release builds. None is smaller still, but a null reference then becomes an unrecoverable crash with no stack trace.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ038", title: "WebGL data caching disabled", category: AssetCategory.ProjectSetting,
							why: "Without data caching the browser re-downloads the entire data file on every visit. With it, a returning player loads from IndexedDB instead.",
							fix: "Enable Data Caching in Player Settings > Publishing Settings.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ039", title: "WebGL managed stripping is below High", category: AssetCategory.ProjectSetting,
							why: "Every byte of managed code becomes WebAssembly that has to be downloaded and compiled before the game starts. WebGL is the one platform where High stripping is worth the risk it carries.",
							fix: "Set Managed Stripping Level to High for WebGL, and add a link.xml for anything you reach by reflection.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			// --- quality / target rendering --------------------------------------------
			Rules.Register(id: "PRJ040", title: "Shadow cascades on a mobile quality level", category: AssetCategory.ProjectSetting,
							why: "Additional shadow cascades add shadow-map rendering and sampling work. The useful number depends on scene scale, shadow distance, GPU bandwidth and the visual target, so mobile projects should treat extra cascades as a measured quality cost.",
							fix: "Use one cascade or no cascades on constrained mobile tiers when profiling shows the extra cascade cost is not justified by the visual benefit.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ041", title: "LOD Bias keeps high-detail meshes in view too long", category: AssetCategory.ProjectSetting,
							why: "LOD Bias changes when LOD transitions occur. Values above 1 generally keep higher-detail LODs active for longer, increasing vertex, raster and memory work compared with the authored thresholds.",
							fix: "Lower LOD Bias on mobile tiers when profiling shows higher-detail LODs remain active longer than the visual budget requires.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ042", title: "Skin Weights above two bones per vertex", category: AssetCategory.ProjectSetting,
							why: "Every extra bone influence is another matrix multiply for every vertex of every skinned mesh, every frame. Four influences are visible on shoulders and hips on a close-up hero character, and almost never on anything else.",
							fix: "Set Skin Weights to Two Bones on mobile quality levels.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ043", title: "Realtime reflection probes enabled on a mobile quality level", category: AssetCategory.ProjectSetting,
							why: "A realtime probe re-renders the scene into a cubemap - six more views of everything. Very few mobile games can afford one, and baked probes look nearly identical for static environments.",
							fix: "Turn off Realtime Reflection Probes on mobile quality levels and bake instead.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ044", title: "Soft particles enabled on a mobile quality level", category: AssetCategory.ProjectSetting,
							why: "Soft particles need the depth texture and a per-pixel depth comparison on every particle. Combined with the overdraw particles already cause, this is expensive on a tile-based GPU for an effect that is subtle at phone screen size.",
							fix: "Turn off Soft Particles on mobile quality levels.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ045", title: "GPU Resident Drawer is disabled on a compatible URP target", category: AssetCategory.ProjectSetting,
							why: "Unity 6's GPU Resident Drawer can substantially reduce CPU rendering overhead in suitable scenes, but it requires SRP Batcher, Forward+ and retained BatchRendererGroup shader variants.",
							fix: "Enable GPU Resident Drawer only after verifying SRP Batcher, Forward+, BRG Keep All and shader/content compatibility on the target hardware.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ046", title: "GPU occlusion culling is off while the GPU Resident Drawer is on", category: AssetCategory.ProjectSetting,
							why: "With the GPU Resident Drawer already keeping instance data on the GPU, occlusion culling can be done there too - so objects hidden behind other geometry are never rasterised. Most of the setup cost is already paid.",
							fix: "Enable GPU Occlusion Culling on the URP asset after reviewing the scene's occlusion characteristics and validating the target hardware.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: true
							);

			Rules.Register(id: "PRJ047", title: "BatchRendererGroup variants are stripped", category: AssetCategory.ProjectSetting,
							why: "GPU Resident Drawer can require BatchRendererGroup shader variants to remain in the build, but the correct stripping policy depends on the renderer paths and shader usage actually shipped by the project.",
							fix: "Review the Build Profile / renderer configuration and set BatchRendererGroup Variants to Keep All only when the target build requires those variants.",
							severity: IssueSeverity.Critical,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.High,
							backupRequired: true
							);

			Rules.Register(id: "PRJ048", title: "Static batching fights the GPU Resident Drawer", category: AssetCategory.ProjectSetting,
							why: "Static batching merges meshes at build time into combined buffers, which takes those objects out of the GPU Resident Drawer's hands and inflates the build with duplicated geometry. The two systems solve the same problem, and the newer one solves it better.",
							fix: "Turn off Static Batching in Player Settings > Other Settings when the GPU Resident Drawer is enabled.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ049", title: "Opaque Texture enabled", category: AssetCategory.ProjectSetting,
							why: "Opaque Texture copies the whole colour buffer mid-frame so shaders can read what is behind them. It exists for refraction and distortion effects; if nothing samples it, it is a full-screen copy every frame for nothing.",
							fix: "Turn off Opaque Texture on the URP asset unless a shader actually samples _CameraOpaqueTexture.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ050", title: "Depth Texture enabled", category: AssetCategory.ProjectSetting,
							why: "A depth prepass or depth copy is not free, particularly on tile-based mobile GPUs. Soft particles, SSAO and many post effects need it - nothing else does.",
							fix: "Turn off Depth Texture on the URP asset if no effect in the project needs _CameraDepthTexture.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ051", title: "Mobile MSAA is 4x or higher", category: AssetCategory.ProjectSetting,
							why: "MSAA can be an excellent quality/performance tradeoff on some mobile GPUs, but 4x is not universally optimal. Higher sample counts increase bandwidth and memory cost, while disabling or reducing MSAA can be a valid performance strategy.",
							fix: "Use the mobile quality profile to select the lowest MSAA level that meets the visual target after profiling.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ052", title: "Fast sRGB/Linear conversion disabled on a mobile target", category: AssetCategory.ProjectSetting,
							why: "This swaps the exact sRGB conversion in shaders for a cheaper approximation. The difference is invisible in normal content and it removes real ALU work from every pixel.",
							fix: "Enable Fast sRGB/Linear Conversions on the URP asset for mobile targets. Check any content that depends on precise colour first.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS1010", title: "URP HDR is enabled on a mobile target", category: AssetCategory.ProjectSetting,
							why: "An HDR colour buffer increases render-target bandwidth and memory cost on mobile. It is useful for effects that require values above 1, but is unnecessary when the project does not use an HDR rendering path.",
							fix: "Turn off HDR on the URP asset unless the project's post-processing and material pipeline requires it.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ054", title: "Additional lights are per-pixel with a large shadow map on mobile", category: AssetCategory.ProjectSetting,
							why: "Per-pixel additional lights are shaded for every fragment they touch, and a 2048 shadow map for them costs both memory and a render pass. Per-vertex additional lights are a fraction of the cost and are usually indistinguishable on a phone screen.",
							fix: "On the URP asset, use Per Vertex or lower shadow resolution when profiling shows additional-light cost is significant.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ055", title: "Android target API is below the current Google Play submission requirement", category: AssetCategory.ProjectSetting,
							why: "For standard Android apps, Google Play requires new apps and app updates submitted from August 31, 2026 to target Android 16 (API 36) or higher. This is a publishing requirement, not a minimum-API compatibility setting.",
							fix: "Set Target API Level to API 36 for the standard Android target only after confirming third-party SDK and device compatibility. This is a publishing/compliance change, not a safe universal optimization.",
							severity: IssueSeverity.Critical,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PRJ056", title: "LOD Cross Fade enabled on a mobile quality level", category: AssetCategory.ProjectSetting,
							why: "LOD cross-fade adds extra shading work while two LOD representations are blended. Unity recommends reducing this cost on performance-constrained mobile targets.",
							fix: "Disable LOD Cross Fade on mobile tiers when the transition quality is not worth the additional pixel cost.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ057", title: "Soft real-time shadows enabled on a mobile quality level", category: AssetCategory.ProjectSetting,
							why: "Soft shadows increase shadow filtering cost. Unity's current URP performance guidance recommends disabling them or using a low setting on performance-constrained targets.",
							fix: "Use hard shadows or a low-cost shadow setting on mobile tiers when profiling shows shadow filtering is a bottleneck.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ058", title: "URP render scale is above 1 on a mobile target", category: AssetCategory.ProjectSetting,
							why: "Rendering above native resolution increases pixel shading, bandwidth and post-processing cost. Lowering Render Scale is a standard URP performance control, but it changes image quality.",
							fix: "Keep Render Scale at or below 1 on mobile unless your visual target and performance budget explicitly justify supersampling.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ059", title: "URP main-light shadows enabled on a mobile target", category: AssetCategory.ProjectSetting,
							why: "The main light shadow map and shadow sampling add both rendering and bandwidth cost. Unity's performance guidance explicitly recommends disabling main-light shadows when the target does not need them.",
							fix: "Disable or lower main-light shadows on mobile quality profiles when they are not required by the visual target.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ060", title: "URP additional-light count is high on a mobile target", category: AssetCategory.ProjectSetting,
							why: "Additional lights increase per-object lighting work, and their shadows add further passes and memory traffic. A high global limit can therefore reserve budget the game never uses.",
							fix: "Lower the Additional Lights per Object limit when scene profiling shows that fewer lights are sufficient.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ062", title: "Quality setting keeps too many frames queued", category: AssetCategory.ProjectSetting,
							why: "Queued frames can increase input-to-photon latency. The right value is platform- and workload-dependent, especially for VR and latency-sensitive games.",
							fix: "Review Max Queued Frames on latency-sensitive profiles and validate the tradeoff on the target hardware.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			Rules.Register(id: "PRJ063", title: "Physics autoSyncTransforms is enabled", category: AssetCategory.ProjectSetting,
							why: "Automatic transform synchronization can add synchronization work when transforms are changed outside the physics step. It is safe to disable only when the project can prove it synchronizes before physics-dependent reads/queries.",
							fix: "Disable Physics.autoSyncTransforms when the project-wide evidence scan proves no dependency requires the automatic synchronization behavior.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);
			Rules.Register(id: "PRJ064", title: "Editor async shader compilation is disabled", category: AssetCategory.ProjectSetting,
							why: "Asynchronous shader compilation keeps new shader variants from blocking Game and Scene view rendering during editor iteration.",
							fix: "Enable EditorSettings.asyncShaderCompilation for asynchronous Game/Scene view shader compilation.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			Rules.Register(id: "PRJ065", title: "Editor refresh import mode is not OutOfProcessPerQueue", category: AssetCategory.ProjectSetting,
						why: "Unity can import independent asset queues in parallel worker processes during refresh. The effective mode can also differ from the persisted preference because of active transactions or launch overrides, so this project-wide setting should be changed deliberately.",
						fix: "Set the persisted and active refresh import mode to OutOfProcessPerQueue after confirming that the project's editor and import workflow is compatible with it.",
						severity: IssueSeverity.Info,
						fixKind: FixKind.Review,
						confidence: OptimizationConfidence.HighConfidence,
						risk: FixRisk.Medium,
						backupRequired: false
						);
			Rules.Register(id: "PRJ066", title: "Import worker configuration may be materially below the host baseline", category: AssetCategory.ProjectSetting,
							why: "Import worker policy is host-dependent; Unity initializes new projects from a logical-core-based baseline rather than a universal worker count.",
							fix: "Review import worker counts and shutdown delay against the host CPU and the project's import workload.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			Rules.Register(id: "PRJ067", title: "Parallel AssetBundle building is disabled", category: AssetCategory.ProjectSetting,
							why: "Unity can use multiple processes when the project directly invokes BuildPipeline.BuildAssetBundles(). This does not imply Addressables or SBP coverage.",
							fix: "Review enabling EditorBuildSettings.UseParallelAssetBundleBuilding for direct BuildPipeline.BuildAssetBundles workflows.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);
			Rules.Register(id: "PRJ068", title: "Dedicated Server optimizations are disabled", category: AssetCategory.ProjectSetting,
							why: "Dedicated Server builds can strip assets that are not needed by the server player.",
							fix: "Review enabling PlayerSettings.dedicatedServerOptimizations for the effective Standalone Dedicated Server build context.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);
			Rules.Register(id: "PRJ069", title: "Shader variant loading memory exceeds the configured budget", category: AssetCategory.ProjectSetting,
							why: "Large shader chunk sizes or counts can reserve substantial shader-variant loading memory. The useful target is project/budget-specific, not universal.",
							fix: "Review shader chunk size/count against the project's target-platform shader memory budget.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			Rules.Register(id: "PRJ070", title: "Lightmap resolution is high for the project's baked-lighting usage", category: AssetCategory.ProjectSetting,
							why: "Lightmap texel density increases baked quality and bake cost. A useful threshold depends on scene scale and content policy.",
							fix: "Review unusually high LightingSettings.lightmapResolution against project/scene policy.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			Rules.Register(id: "PRJ071", title: "Baked GI sample or bounce budget is high", category: AssetCategory.ProjectSetting,
							why: "Higher direct/indirect/environment samples and bounce counts improve bake convergence or fidelity at increased bake cost.",
							fix: "Review baked GI sample counts and max bounces against project/quality policy.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			Rules.Register(id: "PRJ072", title: "Physics2D solver iterations exceed the project policy", category: AssetCategory.ProjectSetting,
							why: "2D solver iterations increase simulation cost. Any reduction must account for gameplay stability and fidelity.",
							fix: "Review Physics2D position and velocity iterations against the project's 2D simulation policy.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			Rules.Register(id: "PRJ073", title: "3D solver velocity iterations exceed the project policy", category: AssetCategory.ProjectSetting,
							why: "Physics.defaultSolverVelocityIterations is global and is overridden by Rigidbody-specific values. Reducing it globally can affect bounce and joint behavior.",
							fix: "Review the global velocity-iteration count against the project's approved fidelity/performance threshold.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			Rules.Register(id: "PRJ074", title: "Force Asset Unload and GC on scene load is enabled", category: AssetCategory.ProjectSetting,
							why: "Forcing finalization and asset unloading on every relevant scene load can change timing and increase pauses. Disable it only after confirming no workflow intentionally depends on immediate finalization.",
							fix: "Review disabling EditorSettings.forceAssetUnloadAndGCOnSceneLoad when immediate finalizer timing is not a project requirement.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);
			Rules.Register(id: "PRJ076", title: "SRP Batcher setting belongs to an unknown custom pipeline", category: AssetCategory.ProjectSetting,
							why: "The active render pipeline exposes the SRP Batcher property, but this analyzer cannot prove what a custom pipeline does with that setting.",
							fix: "Inspect the custom render pipeline implementation and enable its SRP Batcher setting only when the pipeline documents compatible behavior.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ079", title: "IL2CPP Debug configuration is intentional for development", category: AssetCategory.ProjectSetting,
							why: "The project is currently configured for a development/debugging workflow, so switching the IL2CPP compiler configuration to Release automatically would undo an intentional build setup.",
							fix: "Switch the IL2CPP compiler configuration to Release when the project is being prepared for a release build.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ080", title: "ARM64 cannot be enabled until native plugins support it", category: AssetCategory.ProjectSetting,
							why: "Google Play requires 64-bit Android support, but the project contains native plugins without ARM64 libraries that would make an ARM64 build fail.",
							fix: "Update or replace the incompatible native plugins before enabling ARM64.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRJ082", title: "URP Render Graph compatibility mode is enabled", category: AssetCategory.ProjectSetting,
							why: "Unity 6 URP uses Render Graph as the modern rendering path; Compatibility Mode disables that path and keeps legacy ScriptableRenderPass behaviour. The choice can be intentional, so this remains a review finding.",
							fix: "Review custom renderer features and legacy ScriptableRenderPass code, then disable URP Compatibility Mode only after confirming the renderer works correctly with Render Graph.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRJ083", title: "URP Render Graph compilation caching is disabled", category: AssetCategory.ProjectSetting,
							why: "Render Graph compilation caching can reduce repeated graph compilation work. The property is version-dependent, so this rule only exists when Unity exposes the setting on the selected URP asset.",
							fix: "Review enabling Render Graph compilation caching when the setting is present and the project's renderer-feature workflow is compatible with cached compilation.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
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
					entry.AddIssue("PRJ001", "Off", "On, once runtime shader changes are ruled out", 0, null,
						"Enable this after confirming no runtime code assigns a material or shader that needs a "
						+ "vertex channel the build would strip.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ001 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => ReflectionUtility.SetStaticBool(typeof(PlayerSettings), "stripUnusedMeshComponents", true))
						} }
					);
				}

			using (RuleTiming.Begin("PRJ002", entry.Path))
				try
				{
					var level = PlayerSettings.GetManagedStrippingLevel(target);
					entry.Set("stripping", level.ToString());

					// WebGL has a dedicated, stricter rule (PRJ039) because managed code becomes
					// WebAssembly that must be downloaded. Do not report the same state twice.
					if (!IsWebGL(settings)
						&& (level == ManagedStrippingLevel.Disabled || level == ManagedStrippingLevel.Low))
					{
						entry.AddIssue("PRJ002", level.ToString(), "Medium, with a link.xml", 0, null,
							"Raise this by hand together with a link.xml covering anything resolved by reflection, "
							+ "then test a player build. It cannot be verified from the editor.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ002 Review Fix",
							Recommended = true,
							Risk = FixRisk.High,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(PlatformUtility.ToBuildTarget(OptimizerSettings.Instance?.targetPlatform ?? OptimizerPlatform.Desktop))), ManagedStrippingLevel.Medium)),
							Verify = _ => PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(PlatformUtility.ToBuildTarget(OptimizerSettings.Instance?.targetPlatform ?? OptimizerPlatform.Desktop)))) == ManagedStrippingLevel.Medium
						} }
					);
					}
				}
				catch { /* the target may not be installed */ }

			// Project Auditor PAS0025/PAS0026: platform-specific low managed stripping. These are
			// intentionally narrower than PRJ002 so the target-specific build-size guidance is explicit.
			try
			{
				using (RuleTiming.Begin("PAS0025", entry.Path))
					if (settings.targetPlatform == OptimizerPlatform.Android)
					{
						var androidStrip = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.Android);
						if (androidStrip == ManagedStrippingLevel.Disabled || androidStrip == ManagedStrippingLevel.Low || androidStrip.ToString() == "Minimal")
							entry.AddIssue("PAS0025", androidStrip.ToString(), "Medium or High",
						options: new List<FixOption> { new FixOption {
							Label = "PAS0025 Review Fix",
							Recommended = true,
							Risk = FixRisk.High,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Android, ManagedStrippingLevel.Medium)),
							Verify = _ => PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.Android) == ManagedStrippingLevel.Medium
						} }
					);
					}
					else
						using (RuleTiming.Begin("PAS0026", entry.Path))
							if (settings.targetPlatform == OptimizerPlatform.iOS)
							{
								var iosStrip = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.iOS);
								if (iosStrip == ManagedStrippingLevel.Disabled || iosStrip == ManagedStrippingLevel.Low || iosStrip.ToString() == "Minimal")
									entry.AddIssue("PAS0026", iosStrip.ToString(), "Medium or High",
										options: new List<FixOption> {
											new FixOption {
												Label = "Use Medium Managed Stripping",
												Recommended = true,
												Risk = FixRisk.High,
												Warning = "This is a Review Fix. Inspect the target state before applying it.",
												Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.iOS, ManagedStrippingLevel.Medium)),
											Verify = _ => PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.iOS) == ManagedStrippingLevel.Medium
											}
										}
									);
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
						// Reported only. Switching backend changes how every line of managed code is
						// executed, invalidates native plugin assumptions and changes build times
						// substantially. It is a project decision, not a setting to flip on someone's
						// behalf.
						entry.AddIssue("PRJ003", "Mono", "IL2CPP", 0, null,
							"Switching scripting backend changes code generation, plugin compatibility and build "
							+ "times. Make the change deliberately and rebuild.");
					}
				}
				catch { /* the target may not be installed */ }

			using (RuleTiming.Begin("PRJ004", entry.Path))
				if (!PlayerSettings.gcIncremental)
				{
					// Reported only. Incremental GC changes when collection work happens, which can move
					// a stall rather than remove it, and it interacts badly with code that assumes a
					// collection completes within one frame.
					entry.AddIssue("PRJ004", "Off", "On, after profiling", 0, null,
						"Incremental GC spreads collection across frames. Profile the change on the target device: "
						+ "it trades one long pause for more frequent short ones, which is not always the better deal.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ004 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.gcIncremental = true)
						} }
					);
				}

			bool isIl2Cpp = false;
			try
			{
				isIl2Cpp = PlayerSettings.GetScriptingBackend(target) == ScriptingImplementation.IL2CPP;
			}
			catch { /* the target may not be installed */ }

			using (RuleTiming.Begin("PAS1005", entry.Path))
				try
				{
					var activeBuildTarget = EditorUserBuildSettings.activeBuildTarget;
					var activeGroup = BuildPipeline.GetBuildTargetGroup(activeBuildTarget);
					var selectedBuildTarget = PlatformUtility.ToBuildTarget(settings.targetPlatform);
					// PRJ023 owns the selected target. PAS1005 applies only to a different active Editor target.
					if (activeBuildTarget != selectedBuildTarget)
					{
						var activeNamedTarget = NamedBuildTarget.FromBuildTargetGroup(activeGroup);
						bool activeIsIl2Cpp = PlayerSettings.GetScriptingBackend(activeNamedTarget) == ScriptingImplementation.IL2CPP;
						var activeConfig = GetActiveIl2CppConfiguration();
						if (activeIsIl2Cpp && string.Equals(activeConfig, "Debug", StringComparison.OrdinalIgnoreCase))
							entry.AddIssue("PAS1005", activeBuildTarget + ": Debug", "Release when building this active target",
								options: new List<FixOption> { new FixOption {
									Label = "Use Release IL2CPP for Active Editor Target",
									Recommended = true,
									Risk = FixRisk.Medium,
									Warning = "This changes the active Editor build-target configuration, not the separately selected scan target.",
									Apply = c => ImporterFixUtility.Project(c, () => ReflectionUtility.SetStaticEnum(typeof(EditorUserBuildSettings), "il2cppConfiguration", "Release"))
								} }
							);
					}
				}
				catch { }

			using (RuleTiming.Begin("PRJ020", entry.Path))
				if (isIl2Cpp && !PlayerSettings.stripEngineCode)
				{
					// Reported only, for the same reason as PRJ002: engine module stripping is driven by
					// what the build can see being used, and reflection is invisible to it.
					entry.AddIssue("PRJ020", "Off", "On, with a link.xml", 0, null,
						"Removes whole engine modules the build cannot see being used. Verify with a player build "
						+ "rather than from the editor.");
				}

			using (RuleTiming.Begin("PRJ005", entry.Path))
				if (!PlayerSettings.bakeCollisionMeshes)
				{
					// Reported only. Prebaking trades build size and build time for load time, and the
					// right side of that trade depends on the project's shipping constraints.
					entry.AddIssue("PRJ005", "Off", "On when load time matters more than build size", 0, null,
						"This moves mesh collider cooking from load time into the build. It makes the build larger; "
						+ "whether that is worth it is a project decision.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ005 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.bakeCollisionMeshes = true)
						} }
					);
				}

			using (RuleTiming.Begin("PAS1006", entry.Path))
				try
				{
					var method = typeof(PlayerSettings).GetMethod("GetLightmapStreamingEnabledForPlatformGroup", BindingFlags.Static | BindingFlags.NonPublic);
					if (method != null)
					{
						var group = BuildPipeline.GetBuildTargetGroup(PlatformUtility.ToBuildTarget(settings.targetPlatform));
						var result = method.Invoke(null, new object[] { group });
						if (result is bool enabled && !enabled)
							entry.AddIssue("PAS1006", "Off", "On when lightmap mip streaming is useful");
					}
				}
				catch { }

			AnalyzeBatching(entry, settings);

			EvaluateValidatedSettings(entry, ctx);

			using (RuleTiming.Begin("PRJ015", entry.Path))
				if (ctx.Settings.projectProofPass && settings.IsMobile && PlayerSettings.accelerometerFrequency != 0)
				{
					var proof = ctx.GetProofIndex();
					bool coverageComplete = proof != null && proof.CoverageComplete;
					bool sensorUsage = coverageComplete && (
						proof.ContainsSourcePattern("Input.acceleration")
						|| proof.ContainsSourcePattern("Input.gyro")
						|| proof.ContainsSourcePattern("Accelerometer.current")
						|| proof.ContainsSourcePattern("Gyroscope.current")
						|| proof.ContainsSourcePattern("GravitySensor.current")
						|| proof.ContainsSourcePattern("AttitudeSensor.current")
						|| proof.ContainsSourcePattern("LinearAccelerationSensor.current")
						|| proof.ContainsSourcePattern("<Accelerometer>/acceleration")
						|| proof.ContainsSourcePattern("<Gyroscope>/angularVelocity")
						|| proof.ContainsSourcePattern("<GravitySensor>/gravity")
						|| proof.ContainsSourcePattern("<AttitudeSensor>/attitude")
						|| proof.ContainsSourcePattern("<LinearAccelerationSensor>/acceleration")
						|| proof.ContainsSourcePattern("\"Accelerometer\"")
						|| proof.ContainsSourcePattern("\"Gyroscope\"")
						|| proof.ContainsSourcePattern("\"GravitySensor\"")
						|| proof.ContainsSourcePattern("\"AttitudeSensor\"")
						|| proof.ContainsSourcePattern("\"LinearAccelerationSensor\"") );
					entry.AddIssue("PRJ015", PlayerSettings.accelerometerFrequency + " Hz", "Disabled only after sensor-usage review", 0, null,
						sensorUsage ? "Project proof detects legacy or modern Input System sensor usage. Disabling sensor polling would change runtime input behavior."
						: coverageComplete ? "Project proof found no known sensor usage, but deterministic semantic absence of indirect/generated sensor consumers is not established; the action remains Review."
						: "Project proof coverage is incomplete; sensor polling cannot be safely disabled from absence evidence.",
						options: new List<FixOption> { new FixOption { Label = "Disable Accelerometer", Recommended = false, Risk = FixRisk.Medium, Warning = "Confirm legacy and modern Input System sensor usage, including generated input-action code, before disabling.", Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.accelerometerFrequency = 0), Verify = _ => PlayerSettings.accelerometerFrequency == 0 } });
				}

			using (RuleTiming.Begin("PRJ017", entry.Path))
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
						entry.AddIssue("PRJ017",
							format + (hasAstc ? " (ASTC available but not preferred)" : " (ASTC not configured)"),
							"ASTC as the preferred/default target", 0, null,
							"Modern Android devices generally benefit from ASTC. If compatibility coverage requires "
							+ "ETC2 or another format, keep it as an additional targeted format rather than assuming "
							+ "its presence alone makes ASTC the default.");
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
		static void AnalyzeBatching(AssetEntry entry, OptimizerSettings settings)
		{
			try
			{
				var buildTarget = PlatformUtility.ToBuildTarget(settings.targetPlatform);
				bool staticBatching = PlayerSettings.GetStaticBatchingForPlatform(buildTarget);
				bool dynamicBatching = PlayerSettings.GetDynamicBatchingForPlatform(buildTarget);

				entry.Set("batching", "static " + (staticBatching ? "on" : "off")
					+ " / dynamic " + (dynamicBatching ? "on" : "off"));

				using (RuleTiming.Begin("PRJ006", entry.Path))
					if (!staticBatching)
					{
						entry.AddIssue("PRJ006", "Static batching off",
							"On when eligible static geometry benefits", 0, null,
							"This is a baseline candidate only. If a target quality pipeline uses GPU Resident Drawer, "
							+ "review PRJ048 before enabling static batching.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ006 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.SetStaticBatchingForPlatform(PlatformUtility.ToBuildTarget(OptimizerSettings.Instance?.targetPlatform ?? OptimizerPlatform.Desktop), true))
						} }
					);
					}

				using (RuleTiming.Begin("PRJ007", entry.Path))
					if (dynamicBatching && UsesScriptableRenderPipelineForTarget(settings))
					{
						entry.AddIssue("PRJ007", "Dynamic batching on",
							"Profile and disable if it regresses CPU render time", 0, null,
							"Dynamic batching is workload-dependent. Compare CPU render time with it enabled and disabled "
							+ "on representative hardware rather than treating its presence in an SRP project as automatically harmful.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ007 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.SetDynamicBatchingForPlatform(PlatformUtility.ToBuildTarget(OptimizerSettings.Instance?.targetPlatform ?? OptimizerPlatform.Desktop), false))
						} }
					);
					}
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
			List<FixOption> options = null;
			if (!editorFallback)
			{
				options = new List<FixOption> { new FixOption
				{
					ActionId = "PRJ008_ENABLE_MIPMAP_STREAMING_" + targetPlatform,
					Label = "Enable Mipmap Streaming on Affected Levels",
					ResultDescription = "Enables Streaming Mipmaps on every listed target Quality Level.",
					Recommended = true,
					Risk = FixRisk.Medium,
					Warning = "Review the memory, CPU and IO trade-off. A serialized Quality Level may be shared with other target-platform mappings.",
					Apply = c => ImporterFixUtility.Project(c, () => MipmapStreamingFixUtility.EnableForTargetLevels(targetPlatform, expectedIndices, expectedNames)),
					Verify = _ => MipmapStreamingFixUtility.AreTargetLevelsEnabled(targetPlatform, expectedIndices, expectedNames)
				} };
			}

			var issue = entry.AddIssue("PRJ008", disabledIndices.Count + " of " + consideredLevels + " target Quality Level(s) have Streaming Mipmaps Off",
				"Enable Streaming Mipmaps on the affected levels where appropriate", 0, null,
				"This is one target-aware finding aggregated across the selected platform's active Quality Levels. Streaming lowers resident texture memory only when relevant textures are also marked for streaming; it trades this benefit for CPU and IO work.",
				SavingsBasis.Unknown, options: options);
			if (issue == null) return;
			issue.Note("Target profile: " + ctx.Settings.Profile.DisplayName + "; graphics target: " + platformName + ".", "Target scope");
			issue.Items("Affected Quality Levels", disabledNames);
			if (editorFallback)
			{
				issue.Confidence = OptimizationConfidence.Heuristic;
				issue.Note("Unity returned no active Quality Levels for this target, so the finding uses the editor's current level only. No automatic target-level action is offered until target coverage is available.", "Fallback");
			}
		}

		/// <summary>
		/// Evaluates the rules that read per-level QualitySettings state. Called from inside
		/// <see cref="WithQualityLevel"/>, so the statics below refer to <paramref name="levelIndex"/>.
		/// <para>
		/// Every rule here is advisory. Each of these settings is a deliberate quality/performance
		/// choice whose right value depends on the content and the device, and none of them can be
		/// changed without changing what the game looks like or how it behaves - which is exactly the
		/// class of change a tool should describe rather than make.
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
			using (RuleTiming.Begin("PRJ010", entry.Path))
				if (QualitySettings.anisotropicFiltering == AnisotropicFiltering.ForceEnable)
				{
					mark(entry.AddIssue("PRJ010", at + "Forced On", "Per Texture", 0, null,
						"Moving to Per Texture restores each texture's own setting, which lowers filtering quality "
						+ "on any texture left at aniso 0 or 1. That is a visual change, so it is left to you.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ010 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable)
						} }
					)
						);
				}

			entry.Set("vSync." + levelName, (long)QualitySettings.vSyncCount);

			// Built-in Render Pipeline keeps these controls in QualitySettings.
			// URP moves the relevant controls onto the URP asset and is handled there.
			if (QualitySettings.renderPipeline == null)
			{
				float shadowDistanceBudget = ctx.Settings.Profile.ShadowDistanceWarn;
				entry.Set("shadowDistance." + levelName, (double)QualitySettings.shadowDistance);
				if (shadowDistanceBudget > 0f && QualitySettings.shadowDistance > shadowDistanceBudget)
					mark(entry.AddIssue("PRJ012",
						at + "Shadow Distance " + QualitySettings.shadowDistance.ToString("0") + " m",
						"under " + shadowDistanceBudget.ToString("0") + " m", 0, null,
						"Built-in Render Pipeline shadow distance is a per-quality-level setting. "
						+ "Lower it when the target quality tier does not require the current range."));

				if (ctx.Settings.IsVR && QualitySettings.antiAliasing < 4)
					mark(entry.AddIssue("PRJ019",
						at + "MSAA " + (QualitySettings.antiAliasing <= 1 ? "Disabled" : QualitySettings.antiAliasing + "x"),
						"4x for VR profiles that can afford it", 0, null,
						"Built-in VR anti-aliasing is a quality/performance trade-off; validate sample count on the headset.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ019 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => { c.EnsureWritable(); QualitySettings.antiAliasing = 4; c.ChangedProjectSettings = true; },
							Verify = _ => QualitySettings.antiAliasing == 4
						} }
					));
			}

			// maxQueuedFrames is only implemented by D3D11, D3D12 and Vulkan in Unity 6.
			using (RuleTiming.Begin("PRJ062", entry.Path))
				if (QualitySettings.maxQueuedFrames > 2 && TargetSupportsMaxQueuedFrames(ctx.Settings))
					mark(entry.AddIssue("PRJ062", at + QualitySettings.maxQueuedFrames + " queued frames",
						"Review for latency", 0, null,
						"Unity only uses this setting on D3D11, D3D12 and Vulkan. On those APIs, larger queues can increase input latency.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ062 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => QualitySettings.maxQueuedFrames = Mathf.Min(QualitySettings.maxQueuedFrames, 2))
						} }
					));

			if (!ctx.Settings.IsMobile) return;

			using (RuleTiming.Begin("PRJ051", entry.Path))
				if (QualitySettings.renderPipeline == null && QualitySettings.antiAliasing >= 4)
					mark(entry.AddIssue("PRJ051", at + QualitySettings.antiAliasing + "x", "Use the lowest acceptable MSAA level", 0, null,
						"Built-in mobile MSAA above 2x can increase bandwidth and memory cost; validate the lowest level that meets the visual target.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ051 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => QualitySettings.antiAliasing = 2)
						} }
					));

			using (RuleTiming.Begin("PRJ040", entry.Path))
				if (QualitySettings.renderPipeline == null && QualitySettings.shadowCascades > 1)
					mark(entry.AddIssue("PRJ040", at + QualitySettings.shadowCascades + " cascades", "1 cascade or none", 0, null)
						);

			entry.Set("lodBias." + levelName, (double)QualitySettings.lodBias);
			using (RuleTiming.Begin("PRJ041", entry.Path))
				if (QualitySettings.lodBias > 1.01f)
					mark(entry.AddIssue("PRJ041", at + "LOD Bias " + QualitySettings.lodBias.ToString("0.##"), "Profile-appropriate value", 0, null,
						"LOD Bias is content-dependent; lower it only when profiling shows high-detail LODs remain resident too long.")
						);

			entry.Set("skinWeights." + levelName, QualitySettings.skinWeights.ToString());
			using (RuleTiming.Begin("PRJ042", entry.Path))
				if (QualitySettings.skinWeights == SkinWeights.FourBones || QualitySettings.skinWeights == SkinWeights.Unlimited)
					mark(entry.AddIssue("PRJ042", at + QualitySettings.skinWeights, "Two or four bones as appropriate", 0, null,
						"Use the lowest influence count that preserves the required deformation quality.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ042 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => QualitySettings.skinWeights = SkinWeights.TwoBones)
						} }
					)
						);

			using (RuleTiming.Begin("PRJ043", entry.Path))
				if (QualitySettings.realtimeReflectionProbes)
					mark(entry.AddIssue("PRJ043", at + "On", "Off when realtime probes are unnecessary", 0, null,
						options: new List<FixOption> { new FixOption {
							Label = "PRJ043 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => QualitySettings.realtimeReflectionProbes = false)
						} }
					)
						);

			using (RuleTiming.Begin("PRJ044", entry.Path))
				if (QualitySettings.softParticles)
					mark(entry.AddIssue("PRJ044", at + "On", "Off when depth-softening is unnecessary", 0, null,
						options: new List<FixOption> { new FixOption {
							Label = "PRJ044 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => QualitySettings.softParticles = false)
						} }
					)
						);

			using (RuleTiming.Begin("PRJ056", entry.Path))
				if (QualitySettings.renderPipeline == null && QualitySettings.enableLODCrossFade)
					mark(entry.AddIssue("PRJ056", at + "On", "Off", 0, null,
						options: new List<FixOption> { new FixOption {
							Label = "PRJ056 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => ReflectionUtility.SetStaticBool(typeof(QualitySettings), "lodCrossFade", false))
						} }
					)
						);

			using (RuleTiming.Begin("PRJ057", entry.Path))
				if (QualitySettings.renderPipeline == null && QualitySettings.shadows == ShadowQuality.All)
					mark(entry.AddIssue("PRJ057", at + "Soft shadows", "Hard shadows or lower-cost setting", 0, null,
						options: new List<FixOption> { new FixOption {
							Label = "PRJ057 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => QualitySettings.shadows = ShadowQuality.HardOnly)
						} }
					)
						);

			entry.Set("maxQueuedFrames." + levelName, (long)QualitySettings.maxQueuedFrames);
			if (QualitySettings.maxQueuedFrames > 2)
				mark(entry.AddIssue("PRJ062", at + QualitySettings.maxQueuedFrames + " queued frames", "Review for latency", 0, null,
						options: new List<FixOption> { new FixOption {
							Label = "PRJ062 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => QualitySettings.maxQueuedFrames = Mathf.Min(QualitySettings.maxQueuedFrames, 2))
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
		/// reported for both - and PRJ011's fix would have written to that one asset.
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

			using (RuleTiming.Begin("PAS1009", entry.Path))
				if (pipeline == null)
				{
					// This tool is intentionally URP-focused. A null resolved pipeline means the selected
					// quality level is actually running the Built-in Render Pipeline.
					entry.AddIssue("PAS1009", "Built-in Render Pipeline / no URP Asset", "Assign a URP Asset");
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
					issue.Note("Unity reported no per-platform quality level list, so this was read from the "
							   + "editor's current quality level rather than from the target's own tiers. Treat "
							   + "it as a sample, not as coverage of " + platformName + ".", "Fallback");
					issue.Confidence = OptimizationConfidence.Heuristic;
				}
				else if (use.LevelNames.Count > 0)
				{
					issue.Note("Used by quality level(s): " + string.Join(", ", use.LevelNames.ToArray())
							   + " on " + platformName + ".", "Applies to");
				}
			};

			try
			{
				var so = new SerializedObject(pipeline);
				EvaluateUnity6Rendering(entry, ctx, so, pipelinePath, attribute);

				using (RuleTiming.Begin("PRJ011", entry.Path))
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
								attribute(entry.AddIssue("PRJ076", "Off", "On, if your pipeline supports it", 0, null,
									"This is a custom render pipeline asset. It exposes an SRP Batcher setting, but "
									+ "what the setting does in it is up to its author, so the change is left to you."));
							}
							else
							{
								attribute(entry.AddIssue("PRJ011", "Off", "On", 0,
									c => RenderPipelineUtility.SetBool(c, pipelinePath, "m_UseSRPBatcher", true),
									verify: _ => RenderPipelineUtility.ReadBool(pipelinePath, "m_UseSRPBatcher") == true));
							}
						}
					}
				}

				using (RuleTiming.Begin("PRJ012", entry.Path))
				{
					var shadowDistance = so.FindProperty("m_ShadowDistance");
					if (shadowDistance != null)
					{
						float budget = ctx.Settings.Profile.ShadowDistanceWarn;
						entry.Set("shadowDistance", (double)shadowDistance.floatValue);
						if (shadowDistance.floatValue > budget)
						{
							attribute(entry.AddIssue("PRJ012", shadowDistance.floatValue.ToString("0") + " m",
								"under " + budget.ToString("0") + " m", 0, null,
								"Budget for " + ctx.Settings.Profile.DisplayName + "."));
						}
					}
				}

				// VR renders the scene once per eye, so edge quality and fill cost both matter more.
				using (RuleTiming.Begin("PRJ019", entry.Path))
				{
					if (ctx.Settings.IsVR)
					{
						var msaa = so.FindProperty("m_MSAA");
						if (msaa != null)
						{
							entry.Set("msaa", (long)msaa.intValue);
							if (msaa.intValue < 4)
							{
								attribute(entry.AddIssue("PRJ019", msaa.intValue <= 1 ? "Disabled" : msaa.intValue + "x",
									"4x for VR profiles that can afford it", 0, null,
									"VR anti-aliasing is a quality/performance tradeoff; do not force 4x without profiling the headset and render scale.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ019 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => RenderPipelineUtility.SetInt(c, pipelinePath, "m_MSAA", 4),
							Verify = _ => RenderPipelineUtility.ReadInt(pipelinePath, "m_MSAA") == 4
						} }
					));
							}
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
			using (RuleTiming.Begin("PRJ064", entry.Path))
				if (!EditorSettings.asyncShaderCompilation)
					entry.AddIssue("PRJ064", "Off", "On", 0, c => ImporterFixUtility.Project(c, () => EditorSettings.asyncShaderCompilation = true),
						verify: _ => EditorSettings.asyncShaderCompilation);

			using (RuleTiming.Begin("PRJ065", entry.Path))
			{
				var persisted = EditorSettings.refreshImportMode;
				var effective = AssetDatabase.ActiveRefreshImportMode;
				bool overrideActive = HasRefreshModeCommandLineOverride();
				entry.Set("refreshImportPersisted", persisted.ToString());
				entry.Set("refreshImportEffective", effective.ToString());
				if (persisted != AssetDatabase.RefreshImportMode.OutOfProcessPerQueue || effective != AssetDatabase.RefreshImportMode.OutOfProcessPerQueue)
				{
					entry.AddIssue("PRJ065", persisted + " / " + effective,
						"OutOfProcessPerQueue / OutOfProcessPerQueue", 0,
						c => ImporterFixUtility.Project(c, () =>
						{
							EditorSettings.refreshImportMode = AssetDatabase.RefreshImportMode.OutOfProcessPerQueue;
							AssetDatabase.ActiveRefreshImportMode = AssetDatabase.RefreshImportMode.OutOfProcessPerQueue;
						}),
						"Set both the persisted and active refresh import modes to OutOfProcessPerQueue. This is a project-wide editor behavior change and should be applied only after confirming compatibility with the project's import workflow.",
						verify: _ =>
							EditorSettings.refreshImportMode == AssetDatabase.RefreshImportMode.OutOfProcessPerQueue &&
							AssetDatabase.ActiveRefreshImportMode == AssetDatabase.RefreshImportMode.OutOfProcessPerQueue);
				}
			}

			using (RuleTiming.Begin("PRJ066", entry.Path))
			{
				int logical = Math.Max(1, SystemInfo.processorCount);
				int baseline = Math.Max(1, Mathf.CeilToInt(logical * 0.25f));
				int desired = EditorUserSettings.desiredImportWorkerCount;
				int standby = EditorUserSettings.standbyImportWorkerCount;
				int delay = EditorUserSettings.idleImportWorkerShutdownDelayMilliseconds;
				entry.Set("logicalProcessors", (long)logical);
				entry.Set("desiredImportWorkers", (long)desired);
				entry.Set("standbyImportWorkers", (long)standby);
				entry.Set("idleImportWorkerShutdownDelayMs", (long)delay);
				if (desired > 0 && desired < baseline)
					entry.AddIssue("PRJ066", desired + " workers", "Review around " + baseline + " for this host", 0, null,
						"This is a heuristic only; Unity's project-creation baseline is derived from logical CPU count. No user-specific worker policy is auto-mutated.");
			}

			// The legacy direct AssetBundle toggle remains inspectable in Unity 6.
#pragma warning disable CS0618
			using (RuleTiming.Begin("PRJ067", entry.Path))
			{
				bool direct = ctx.Settings.projectProofPass && HasDirectAssetBundleBuildUsage(ctx);
				if (direct && !EditorBuildSettings.UseParallelAssetBundleBuilding)
					entry.AddIssue("PRJ067", "Off", "On", 0, null,
						"The project contains direct BuildPipeline.BuildAssetBundles() usage. Addressables/SBP-only workflows are not claimed by this detection.", options: new List<FixOption> { new FixOption {
							Label = "Enable Parallel AssetBundle Building", Recommended = true, Risk = FixRisk.Medium,
							Warning = "Apply after confirming the direct AssetBundle build workflow is compatible with multi-process operation.",
							Apply = c => ImporterFixUtility.Project(c, () => EditorBuildSettings.UseParallelAssetBundleBuilding = true),
							Verify = _ => EditorBuildSettings.UseParallelAssetBundleBuilding
						} });
				}

			#pragma warning restore CS0618
			using (RuleTiming.Begin("PRJ068", entry.Path))
			{
				var profile = ctx.BuildProfiles != null ? ctx.BuildProfiles.Active : null;
				bool server = profile != null ? profile.IsDedicatedServer : IsEffectiveDedicatedServerBuild();
				if (server)
				{
					var owner = profile?.PlayerSettingsOwner;
					bool? enabled = owner != null ? ReadOwnedBool(owner, "dedicatedServerOptimizations") : PlayerSettings.dedicatedServerOptimizations;
					if (enabled == false)
					{
						Action<FixContext> enable = owner != null
							? c => SetOwnedBool(owner, "dedicatedServerOptimizations", true, c)
							: c => ImporterFixUtility.Project(c, () => PlayerSettings.dedicatedServerOptimizations = true);
						entry.AddIssue("PRJ068", "Off", "On", 0, enable,
							owner != null ? "The effective Dedicated Server Build Profile has a profile-local PlayerSettings owner; the fix targets that owner." : "The effective build context is a Standalone Dedicated Server / Server subtarget with global PlayerSettings ownership.",
							verify: _ => { bool? value = owner != null ? ReadOwnedBool(owner, "dedicatedServerOptimizations") : PlayerSettings.dedicatedServerOptimizations; return value == true; });
					}
				}
			}

			using (RuleTiming.Begin("PRJ069", entry.Path))
				TryEvaluate(() => EvaluateShaderVariantMemory(entry, ctx));

			using (RuleTiming.Begin("PRJ070", entry.Path))
				TryEvaluate(() => EvaluateLightmapPolicy(entry, ctx));

			using (RuleTiming.Begin("PRJ071", entry.Path))
				TryEvaluate(() => EvaluateGiPolicy(entry, ctx));

			using (RuleTiming.Begin("PRJ072", entry.Path))
			{
				if (Has2DPhysicsUsage(ctx) && ((ctx.Settings.physics2DPositionIterationsReview > 0 && Physics2D.positionIterations > ctx.Settings.physics2DPositionIterationsReview) || (ctx.Settings.physics2DVelocityIterationsReview > 0 && Physics2D.velocityIterations > ctx.Settings.physics2DVelocityIterationsReview)))
					entry.AddIssue("PRJ072", Physics2D.positionIterations + "/" + Physics2D.velocityIterations, "Review configured 2D solver policy", 0, null,
						"2D solver iterations above this heuristic threshold may be intentional for stability. No automatic reduction is offered.");
			}

			using (RuleTiming.Begin("PRJ073", entry.Path))
				if (Has3DPhysicsUsage(ctx) && ctx.Settings.physicsVelocityIterationsReview > 0 && Physics.defaultSolverVelocityIterations > ctx.Settings.physicsVelocityIterationsReview)
					entry.AddIssue("PRJ073", Physics.defaultSolverVelocityIterations.ToString(), "Review project-approved velocity-iteration policy", 0, null,
						"The global value is distinct from Physics.defaultSolverIterations and is overridden by Rigidbody-specific values. Reduction can change bounce and joint behavior.");

			using (RuleTiming.Begin("PRJ074", entry.Path))
			{
				if (TryGetForceAssetUnloadAndGCOnSceneLoad(out bool forceUnloadAndGc) && forceUnloadAndGc)
					entry.AddIssue("PRJ074", "On", "Off when immediate finalizer timing is not required", 0, null,
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
			if (!proof.IsBuilt || proof.BuildFailed) return false;
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
			if (!proof.IsBuilt || proof.BuildFailed) return false;
			return proof.ContainsSourcePattern("Rigidbody2D.") || proof.ContainsSourcePattern("Collider2D.") || proof.ContainsSourcePattern("GetComponent<Rigidbody2D>") || proof.ContainsSourcePattern("GetComponent<Collider2D>") || proof.ContainsSourcePattern("Physics2D.");
		}

		static bool Has3DPhysicsUsage(ScanContext ctx)
		{
			if (ctx == null || !ctx.Settings.projectProofPass) return false;
			var proof = ctx.GetProofIndex();
			if (!proof.IsBuilt || proof.BuildFailed) return false;
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
				entry.AddIssue("PRJ069", sizeMb + " MB chunk / " + count + " chunks", "chunk size <= configured " + budget + " MB policy", sizeMb - budget, null,
					"This is a target-aware heuristic. ProjectOptimizer does not infer total shader resident memory from chunk size multiplied by chunk count.");
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
				entry.AddIssue("PRJ070", scene.path + ": " + lighting.lightmapResolution.ToString("0.##"),
					"Review against configured " + policy.ToString("0.##"), 0, null,
					"Baked-lighting policy exceeded for this build scene. The scene is inspected additively and the editor's active scene is restored unchanged.");
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
				entry.AddIssue("PRJ071", scene.path + ": direct=" + lighting.directSampleCount + ", indirect=" + lighting.indirectSampleCount + ", environment=" + lighting.environmentSampleCount + ", bounces=" + lighting.maxBounces,
					"Review against configured GI policy", 0, null,
					"Baked GI policy exceeded for this build scene. The scene is inspected additively and the editor's active scene is restored unchanged.");
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

		static void SetOwnedBool(UnityEngine.Object owner, string fieldName, bool value, FixContext context)
		{
			if (owner == null)
				throw new InvalidOperationException("The profile-owned settings object is missing.");
			var so = new SerializedObject(owner);
			var prop = SerializedPropertyUtility.Find(so, fieldName);
			if (prop == null || prop.propertyType != SerializedPropertyType.Boolean)
				throw new InvalidOperationException("The profile-owned setting '" + fieldName + "' could not be resolved.");
			prop.boolValue = value;
			so.ApplyModifiedPropertiesWithoutUndo();
			EditorUtility.SetDirty(owner);
			context.NeedsSave = true;
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

			using (RuleTiming.Begin("PRJ013", entry.Path))
				if (namedLayers >= 4 && ignoredPairs == 0)
				{
					int pairs = namedLayers * (namedLayers + 1) / 2;
					entry.AddIssue("PRJ013", pairs + " named-layer pairs are not ignored", "Prune pairs that are provably unused", 0, null,
						"A fully enabled matrix is not inherently wrong; only disable layer pairs when the gameplay collision model establishes they can never interact.");
				}

			using (RuleTiming.Begin("PRJ018", entry.Path))
				if (Physics.defaultSolverIterations > 6)
				{
					entry.AddIssue("PRJ018", Physics.defaultSolverIterations + " iterations", "6 or profile-specific value", 0, null,
						"Increasing the global solver count has a project-wide cost; prefer Rigidbody-specific overrides when possible.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ018 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => Physics.defaultSolverIterations = 6)
						} }
					);
				}

			// Preserve analysis of this legacy physics setting while Unity 6 still exposes it.
#pragma warning disable CS0618
			using (RuleTiming.Begin("PRJ014", entry.Path))
				if (!Physics.reuseCollisionCallbacks)
				{
					var enable = new Action<FixContext>(c => ImporterFixUtility.Project(c, () => Physics.reuseCollisionCallbacks = true));
					entry.AddIssue("PRJ014", "Off", "On after Collision lifetime review", 0, null,
						"Collision callback lifetime and escape semantics require dataflow analysis beyond lexical absence. Review whether Collision instances or aliases escape callback scope before enabling reuse.",
						options: new List<FixOption> { new FixOption { Label = "Enable Reuse Collision Callbacks", Recommended = false, Risk = FixRisk.Medium, Warning = "Confirm Collision data is never retained after callbacks return.", Apply = enable, Verify = _ => Physics.reuseCollisionCallbacks } });
				}

			using (RuleTiming.Begin("PRJ063", entry.Path))
				if (Physics.autoSyncTransforms)
				{
					var disable = new Action<FixContext>(c => ImporterFixUtility.Project(c, () => Physics.autoSyncTransforms = false));
					entry.AddIssue("PRJ063", "On", "Off after transform/physics lifetime review", 0, null,
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
			using (RuleTiming.Begin("PRJ016", entry.Path))
				try
				{
					var config = AudioSettings.GetConfiguration();
					entry.Set("realVoices", (long)config.numRealVoices);
					entry.Set("virtualVoices", (long)config.numVirtualVoices);
					entry.Set("dspBuffer", (long)config.dspBufferSize);

					if (config.numRealVoices > 32)
						entry.AddIssue("PRJ016", config.numRealVoices + " real voices", "24-32 real voices",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ016 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => { var cfg = AudioSettings.GetConfiguration(); cfg.numRealVoices = Mathf.Min(cfg.numRealVoices, 32); AudioSettings.Reset(cfg); })
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

			using (RuleTiming.Begin("PRJ021", entry.Path))
				if (PlayerSettings.meshDeformation == MeshDeformation.CPU)
				{
					// Reported only. GPU skinning moves deformation off the main thread, and also changes
					// what is readable back on the CPU. Code sampling a skinned mesh, or a platform whose
					// compute support is partial, both make this the wrong change.
					entry.AddIssue("PRJ021", "CPU", "GPUBatched when the target graphics API supports it", 0, null,
						"Unity 6 exposes CPU, GPU and GPUBatched mesh deformation modes. GPUBatched combines multiple meshes and blendshapes; CPU access patterns and graphics-API support still need validation on the target.");
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
					var config = PlayerSettings.GetIl2CppCompilerConfiguration(target);
					using (RuleTiming.Begin("PRJ023", entry.Path))
						if (config == Il2CppCompilerConfiguration.Debug)
						{
							// Debug is the right setting for a project currently building for debugging, and
							// the editor says so: Development Build, script debugging and the profiler
							// switches are all readable. Where any of those is on, this is reported and not
							// changed - the tool would otherwise silently undo a deliberate setup every time
							// someone ran a fix batch.
							bool developmentIntent = EditorUserBuildSettings.activeBuildTarget == PlatformUtility.ToBuildTarget(settings.targetPlatform)
								&& (EditorUserBuildSettings.development
									|| EditorUserBuildSettings.allowDebugging
									|| EditorUserBuildSettings.connectProfiler);

							if (developmentIntent)
							{
								entry.AddIssue("PRJ079", config + " (Development Build is on)",
									"Release, once you are building for release", 0, null,
									"Left alone: this project is currently set up for a development build, and Debug is "
									+ "the consistent choice for one. Change it when you switch to release builds.");
							}
							else
							{
								entry.AddIssue("PRJ023", config.ToString(), "Release", 0,
									c =>
									{
										PlayerSettings.SetIl2CppCompilerConfiguration(target, Il2CppCompilerConfiguration.Release);
										c.ChangedProjectSettings = true;
									},
									verify: _ => PlayerSettings.GetIl2CppCompilerConfiguration(target)
												 == Il2CppCompilerConfiguration.Release);
							}
						}
				});

			if (isIl2Cpp)
				TryEvaluate(() =>
				{
					using (RuleTiming.Begin("PRJ024", entry.Path))
					{
						var codegen = PlayerSettings.GetIl2CppCodeGeneration(target);
						// Size is the right trade on WebGL, where the module is downloaded before anything runs.
						if (codegen == UnityEditor.Build.Il2CppCodeGeneration.OptimizeSize && !IsWebGL(settings))
						{
							// Reported only. This is a size-against-speed trade, and OptimizeSize is a
							// perfectly reasonable answer on a platform with a download or package-size
							// limit - which is a shipping constraint the analyzer cannot see. Flipping it
							// automatically asserts a priority the project never stated.
							entry.AddIssue("PRJ024", "OptimizeSize", "OptimizeSpeed, if runtime speed matters more",
								0, null,
								"OptimizeSize produces a smaller native binary, OptimizeSpeed a faster one. Which is "
								+ "right depends on whether download size or frame time is the tighter constraint for "
								+ "this title.");
						}
					}
				});

			// WebGL is single-threaded, so graphics jobs have nowhere to go.
			using (RuleTiming.Begin("PRJ025", entry.Path))
				if (!PlayerSettings.graphicsJobs && !IsWebGL(settings) && PlayerSettings.graphicsJobMode != GraphicsJobMode.Legacy)
					entry.AddIssue("PRJ025", "Off (" + PlayerSettings.graphicsJobMode + ")", "Profile- and device-tested", 0, null);

			entry.Set("colorSpace", PlayerSettings.colorSpace.ToString());
			using (RuleTiming.Begin("PRJ026", entry.Path))
				if (PlayerSettings.colorSpace == ColorSpace.Gamma)
				{
					entry.AddIssue("PRJ026", "Gamma", "Linear", 0, null,
						"Not auto-fixed: switching colour space changes how every existing light and colour reads, "
						+ "so it is a decision with re-authoring attached rather than a setting to flip.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ026 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.colorSpace = ColorSpace.Linear)
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

			using (RuleTiming.Begin("PRJ027", entry.Path))
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
						entry.AddIssue("PRJ080", architectures.ToString(), "ARM64, once the plugins ship it",
							0, null,
							"Google Play requires a 64-bit binary, but these native plugins have no ARM64 library, "
							+ "so enabling ARM64 would fail the build until they are updated.")
							?.Items("Native plugins with no ARM64 variant", missingArm64, DetailKind.Bad, arePaths: true);
					}
					else
					{
						entry.AddIssue("PRJ027", architectures.ToString(), "ARM64", 0,
							c =>
							{
								PlayerSettings.Android.targetArchitectures = architectures | AndroidArchitecture.ARM64;
								c.ChangedProjectSettings = true;
							},
							verify: _ => (PlayerSettings.Android.targetArchitectures & AndroidArchitecture.ARM64) != 0);
					}
				}
				else using (RuleTiming.Begin("PRJ028", entry.Path))
					if ((architectures & AndroidArchitecture.ARMv7) != 0
						&& !EditorUserBuildSettings.buildAppBundle
						&& !PlayerSettings.Android.buildApkPerCpuArchitecture)
					{
						// APK-only case. Android App Bundles already support architecture-targeted delivery;
						// buildApkPerCpuArchitecture only matters to split APK output.
						entry.AddIssue("PRJ028", "ARM64 + ARMv7 APK", "ARM64 only, or split APKs", 0, null,
							"An unsplit APK containing both architectures carries both native binaries. This is a package-size "
							+ "recommendation only; Android App Bundles can deliver architecture-specific variants automatically.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ028 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64)
						} }
					);
					}

			using (RuleTiming.Begin("PRJ029", entry.Path))
				if (!PlayerSettings.Android.optimizedFramePacing)
				{
					// Reported only. Frame pacing changes when frames are presented, which is a runtime
					// behaviour change: it can cap an unlocked frame rate at the display's refresh rate.
					entry.AddIssue("PRJ029", "Off", "On, after testing on device", 0, null,
						"Frame pacing spaces presentation to the display's refresh rate. Test it on the target "
						+ "device - it changes presentation timing, and on some content that is felt.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ029 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => { c.EnsureWritable(); PlayerSettings.Android.optimizedFramePacing = true; c.ChangedProjectSettings = true; }
						} }
					);
				}

			TryEvaluate(() =>
			{
				using (RuleTiming.Begin("PRJ030", entry.Path))
					if (PlayerSettings.Android.applicationEntry == AndroidApplicationEntry.Activity)
					{
						// Reported only. Any native plugin, custom manifest or Android library that
						// assumes the legacy Activity breaks on GameActivity, and none of that is
						// visible from here.
						entry.AddIssue("PRJ030", "Activity", "GameActivity, once plugins are checked", 0, null,
							"GameActivity changes the Android entry point. Every native plugin and custom manifest "
							+ "entry that assumes the legacy Activity has to be checked first.");
					}
			});

			var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
			bool autoApis = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android);
			entry.Set("graphicsApis", autoApis ? "Auto" : Join(apis));

			using (RuleTiming.Begin("PRJ031", entry.Path))
				if (autoApis || apis.Length == 0 || apis[0] != GraphicsDeviceType.Vulkan)
				{
					// Reported only. Reordering the graphics APIs decides which driver every player runs
					// on. A minority of chipsets are slower or buggier on Vulkan, and the tool has no way
					// to know which devices this game ships to.
					string apiReason = autoApis
						? "Automatic graphics API selection is enabled, so Vulkan is not explicitly guaranteed as the first API."
						: apis.Length == 0
							? "The explicit Android graphics API list is empty or unavailable."
							: !apis.Contains(GraphicsDeviceType.Vulkan)
								? "Vulkan is absent from the explicit Android graphics API list."
								: "Vulkan is enabled but is not the first preferred API.";
					entry.AddIssue("PRJ031", autoApis ? "Auto" : Join(apis), "Vulkan, then OpenGLES3", 0, null,
						apiReason + " This changes which graphics driver every device uses. Profile on real hardware before "
						+ "reordering, and keep OpenGLES3 below Vulkan as the fallback.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ031 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => PlatformUtility.PreferAndroidVulkan())
						} }
					);
				}

			using (RuleTiming.Begin("PRJ032", entry.Path))
				if (!PlayerSettings.Android.minifyRelease)
				{
					// Reported only. R8 removes Java that nothing appears to call, and JNI and reflection
					// are exactly what it cannot see. Without matching keep rules it produces a build
					// that fails only at runtime.
					entry.AddIssue("PRJ032", "Off", "On for release, with keep rules", 0, null,
						"R8 needs ProGuard keep rules for anything reached by reflection or JNI. Enabling it "
						+ "without them produces a build that only fails on device.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ032 Review Fix",
							Recommended = true,
							Risk = FixRisk.High,
							Warning = "This is a Review Fix. Inspect the target state and confirm JNI/reflection keep rules before applying it.",
							Apply = c => { c.EnsureWritable(); PlayerSettings.Android.minifyRelease = true; c.ChangedProjectSettings = true; },
							Verify = _ => PlayerSettings.Android.minifyRelease
						} }
					);
				}

			entry.Set("targetSdk", PlayerSettings.Android.targetSdkVersion.ToString());
			// Google Play's standard Android requirement is API 36 for new apps and updates
			// submitted from 31 August 2026. Android XR/TV have separate exceptions; the
			// dedicated Android target here therefore does not claim those platform exceptions.
			using (RuleTiming.Begin("PRJ055", entry.Path))
				if (settings.targetPlatform == OptimizerPlatform.Android && (int)PlayerSettings.Android.targetSdkVersion < 36)
				{
					entry.AddIssue(
						"PRJ055",
						"API " + (int)PlayerSettings.Android.targetSdkVersion,
						"API 36",
						0,
						c => ImporterFixUtility.Project(c, () => PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel36),
						verify: c => (int)PlayerSettings.Android.targetSdkVersion >= 36);
				}

			entry.Set("minSdk", PlayerSettings.Android.minSdkVersion.ToString());

			TryEvaluate(() =>
			{
				using (RuleTiming.Begin("PRJ034", entry.Path))
					if (PlayerSettings.Android.blitType == AndroidBlitType.Always)
					{
						// Reported only. Always is sometimes set deliberately to work around a device
						// presenting incorrectly, and that history is not recorded anywhere the tool can
						// read.
						entry.AddIssue("PRJ034", "Always", "Auto, if presentation is still correct", 0, null,
							"Always is sometimes set to work around a device-specific presentation bug. Check the "
							+ "game still presents correctly on device after changing it.");
					}
			});
		}

		// --- iOS ---------------------------------------------------------------------

		void EvaluateIOS(AssetEntry entry, ScanContext ctx)
		{
			TryEvaluate(() =>
			{
				using (RuleTiming.Begin("PRJ035", entry.Path))
					if (PlayerSettings.iOS.scriptCallOptimization == ScriptCallOptimizationLevel.SlowAndSafe)
					{
						// Reported only. This turns a caught NullReferenceException into a hard crash.
						// Any project relying on a caught exception in shipped code would start crashing
						// in the field, and nothing here can tell whether this one does.
						entry.AddIssue("PRJ035", "Slow and Safe", "Fast but no Exceptions, once stable", 0, null,
							"This removes the null checks around managed calls: what was a caught exception becomes "
							+ "a crash. Only worth it on code you no longer rely on exceptions in.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ035 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => ReflectionUtility.SetStaticEnum(typeof(PlayerSettings.iOS), "scriptCallOptimization", "FastButNoExceptions"))
						} }
					);
					}
			});
		}

		// --- WebGL -------------------------------------------------------------------

		void EvaluateWebGL(AssetEntry entry, ScanContext ctx)
		{
			entry.Set("compression", PlayerSettings.WebGL.compressionFormat.ToString());
			using (RuleTiming.Begin("PRJ036", entry.Path))
				if (PlayerSettings.WebGL.compressionFormat != WebGLCompressionFormat.Brotli)
				{
					// Reported only. Brotli files are useless unless the host sends
					// Content-Encoding: br, and the tool cannot see or change the server. Switching this
					// without the header change makes the build fail to load.
					entry.AddIssue("PRJ036", PlayerSettings.WebGL.compressionFormat.ToString(),
						"Brotli, once the host sends Content-Encoding: br", 0, null,
						"Brotli needs the web server configured to serve .br files with the right Content-Encoding "
						+ "header. Change both together, or the build will not load.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ036 Review Fix",
							Recommended = true,
							Risk = FixRisk.High,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => ReflectionUtility.SetStaticEnum(typeof(PlayerSettings.WebGL), "compressionFormat", "Brotli")),
							Verify = _ => PlayerSettings.WebGL.compressionFormat == WebGLCompressionFormat.Brotli
						} }
					);
				}

			using (RuleTiming.Begin("PRJ037", entry.Path))
				if (PlayerSettings.WebGL.exceptionSupport != WebGLExceptionSupport.None
					&& PlayerSettings.WebGL.exceptionSupport != WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly)
				{
					// Reported only, for the same reason as PRJ035: it changes what happens when
					// something is null, from an exception to an unrecoverable crash.
					entry.AddIssue("PRJ037", PlayerSettings.WebGL.exceptionSupport.ToString(),
						"ExplicitlyThrownExceptionsOnly", 0, null,
						"Your own throws keep working; everything else stops being catchable. Check for code that "
						+ "depends on catching engine-raised exceptions first.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ037 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => ReflectionUtility.SetStaticEnum(typeof(PlayerSettings.WebGL), "exceptionSupport", "ExplicitlyThrownExceptionsOnly")),
							Verify = _ => PlayerSettings.WebGL.exceptionSupport.ToString() == "ExplicitlyThrownExceptionsOnly"
						} }
					);
				}

			using (RuleTiming.Begin("PRJ038", entry.Path))
				if (!PlayerSettings.WebGL.dataCaching)
				{
					// Kept automatic: this caches the data file in IndexedDB on the player's own machine.
					// Nothing about the build changes, and nothing in the game behaves differently.
					entry.AddIssue("PRJ038", "Off", "On", 0,
						c => { PlayerSettings.WebGL.dataCaching = true; c.ChangedProjectSettings = true; },
						verify: _ => PlayerSettings.WebGL.dataCaching);
				}

			TryEvaluate(() =>
			{
				var level = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.WebGL);
				using (RuleTiming.Begin("PRJ039", entry.Path))
					if (level != ManagedStrippingLevel.High)
					{
						// Reported only. High stripping on WebGL is worth reaching for and is also the
						// setting most likely to remove something reflection needed.
						entry.AddIssue("PRJ039", level.ToString(), "High, with a link.xml", 0, null,
							"Every byte of managed code becomes WebAssembly to download and compile. High is worth "
							+ "the risk here, but it needs a link.xml for anything reached by reflection.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ039 Review Fix",
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
			using (RuleTiming.Begin("PRJ022", entry.Path))
				if (distinct.Count < 2) return;

			entry.AddIssue("PRJ022", "Off", "On", 0, null,
					"Your quality levels use different texture mipmap limits, so the highest mips are "
					+ "unreachable on the lower ones and can be left out of the build.")
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
					using (RuleTiming.Begin("PRJ082", entry.Path))
						if (compatibility)
						{
							string renderGraphProperty = renderGraph.propertyPath;
							bool desired = IsCompatibilityModeProperty(renderGraph) ? false : true;
							attribute(entry.AddIssue("PRJ082", "Compatibility Mode", "Render Graph", 0, null,
								"Compatibility Mode disables the modern Render Graph path. Keep it only when legacy renderer features or ScriptableRenderPass code still require it.",
								options: new List<FixOption> { new FixOption {
									Label = "Enable Render Graph",
									Recommended = true,
									Risk = FixRisk.Medium,
									Warning = "Review custom renderer features and legacy passes before switching away from Compatibility Mode.",
									Apply = c => RenderPipelineUtility.SetBool(c, pipelinePath, renderGraphProperty, desired),
									Verify = _ => RenderPipelineUtility.ReadBool(pipelinePath, renderGraphProperty) == desired
								} }));
						}
				}

				var compilationCaching = FindSerializedBoolean(pipeline,
						"m_EnableCompilationCaching", "m_RenderGraphCompilationCaching", "Enable Compilation Caching", "Compilation Caching");
				if (compilationCaching != null)
				{
					entry.Set("renderGraphCompilationCaching", compilationCaching.boolValue ? "On" : "Off");
					using (RuleTiming.Begin("PRJ083", entry.Path))
						if (!compilationCaching.boolValue)
							attribute(entry.AddIssue("PRJ083", "Off", "On", 0, null,
								"Render Graph compilation caching can reduce repeated compilation work. Review it rather than changing it automatically.",
								options: new List<FixOption> { new FixOption {
									Label = "Enable Compilation Caching",
									Recommended = true,
									Risk = FixRisk.Low,
									Warning = "Enable only when the project's Render Graph compilation workflow is compatible with caching.",
									Apply = c => RenderPipelineUtility.SetBool(c, pipelinePath, compilationCaching.propertyPath, true),
									Verify = _ => RenderPipelineUtility.ReadBool(pipelinePath, compilationCaching.propertyPath) == true
								} }));
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

				using (RuleTiming.Begin("PRJ045", entry.Path))
					if (!drawerOn && srpBatcherOn && forwardPlus && computeCapable)
					{
						attribute(entry.AddIssue("PRJ045", "Disabled", "Instanced Drawing", 0,
							c => { c.EnsureWritable(); var pipeline = AssetDatabase.LoadMainAssetAtPath(pipelinePath); if (pipeline == null) throw new InvalidOperationException("The render pipeline asset could not be loaded."); var so = new SerializedObject(pipeline); var p = so.FindProperty("m_GPUResidentDrawerMode"); if (p == null) throw new InvalidOperationException("GPU Resident Drawer is not available on the selected pipeline."); int targetIndex = FindSerializedEnumIndex(p, "Instanced Drawing", -1); if (targetIndex < 0) throw new InvalidOperationException("The pipeline does not expose the Instanced Drawing GPU Resident Drawer mode."); p.enumValueIndex = targetIndex; so.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(pipeline); c.NeedsSave = true; },
							verify: _ => RenderPipelineUtility.ReadEnumName(pipelinePath, "m_GPUResidentDrawerMode") == "Instanced Drawing"));
					}
			}

			if (drawerOn)
			{
				var occlusion = pipeline.FindProperty("m_GPUResidentDrawerEnableOcclusionCullingInCameras");
				using (RuleTiming.Begin("PRJ046", entry.Path))
					if (occlusion != null && !occlusion.boolValue && forwardPlus && TargetSupportsComputeShaders(settings))
					{
						attribute(entry.AddIssue("PRJ046", "Off", "On", 0,
							c =>
							{
								var target = new SerializedObject(pipeline.targetObject);
								var prop = target.FindProperty("m_GPUResidentDrawerEnableOcclusionCullingInCameras");
								if (prop == null) throw new InvalidOperationException("GPU Resident Drawer occlusion-culling property is unavailable.");
								prop.boolValue = true;
								target.ApplyModifiedPropertiesWithoutUndo();
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
					using (RuleTiming.Begin("PRJ047", entry.Path))
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
							attribute(entry.AddIssue("PRJ047", current, "Keep All", 0,
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
				int staticBatching = ReadBatching(PlatformUtility.ToBuildTarget(settings.targetPlatform), out _);
				using (RuleTiming.Begin("PRJ048", entry.Path))
					if (staticBatching == 1)
					{
						// Reported only, to match PRJ006. Turning static batching off removes a batching
						// path on the assumption the drawer replaces it, and the drawer only engages when
						// its own prerequisites are met - which this rule cannot confirm for the content.
						attribute(entry.AddIssue("PRJ048", "Static batching on", "Off, so the drawer batches instead", 0, null,
							"The GPU Resident Drawer cannot manage geometry that static batching has already merged. "
							+ "Turn one of the two off deliberately, and profile the result.",
						options: new List<FixOption> { new FixOption {
							Label = "Disable Static Batching",
							ActionId = "disable-static-batching",
							ResultDescription = "Static batching is disabled for the selected target.",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "Disable this only when the GPU Resident Drawer is the intended batching path for the target workload.",
							Apply = c => ImporterFixUtility.Project(c, () => PlayerSettings.SetStaticBatchingForPlatform(PlatformUtility.ToBuildTarget(OptimizerSettings.Instance?.targetPlatform ?? OptimizerPlatform.Desktop), false)),
							Verify = _ => IsStaticBatchingDisabledForTarget(PlatformUtility.ToBuildTarget(OptimizerSettings.Instance?.targetPlatform ?? OptimizerPlatform.Desktop))
						} }
					));
					}
			}

			var opaque = isUrp ? pipeline.FindProperty("m_RequireOpaqueTexture") : null;
			using (RuleTiming.Begin("PRJ049", entry.Path))
				if (opaque != null && opaque.boolValue)
				{
					attribute(entry.AddIssue("PRJ049", "On", "Off", 0, null)
						?.Note("Check for shaders sampling _CameraOpaqueTexture before turning this off - "
							   + "refraction, distortion and glass effects need it."));
				}

			var depth = isUrp ? pipeline.FindProperty("m_RequireDepthTexture") : null;
			using (RuleTiming.Begin("PRJ050", entry.Path))
				if (depth != null && depth.boolValue)
				{
					attribute(entry.AddIssue("PRJ050", "On", "Off", 0, null)
						?.Note("Soft particles, SSAO and most depth-based post effects need this. "
							   + "Leave it on if any of them are in use."));
				}

			if (settings.IsMobile && isUrp)
			{
				var cascadeCount = pipeline.FindProperty("m_ShadowCascadeCount");
				if (cascadeCount != null && cascadeCount.intValue > 1)
					attribute(entry.AddIssue("PRJ040", cascadeCount.intValue + " cascades", "1 cascade or none", 0, null,
						"URP stores cascade count on the URP asset rather than QualitySettings. Extra cascades cost "
						+ "shadow rendering and sampling work on mobile."));

				var urpCrossFade = pipeline.FindProperty("m_EnableLODCrossFade");
				if (urpCrossFade != null && urpCrossFade.boolValue)
					attribute(entry.AddIssue("PRJ056", "On", "Off", 0, null,
						"URP stores LOD Cross Fade on the URP asset. Disable it on constrained mobile tiers when "
						+ "the transition quality is not worth the extra shading.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ056 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => RenderPipelineUtility.SetBool(c, pipelinePath, "m_EnableLODCrossFade", false)
						} }
					));

				var softShadows = pipeline.FindProperty("m_SoftShadowsSupported");
				if (softShadows != null && softShadows.boolValue)
					attribute(entry.AddIssue("PRJ057", "Soft shadows", "Hard shadows or lower-cost setting", 0, null,
						"URP stores soft-shadow support on the URP asset. Soft filtering adds GPU work.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ057 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => RenderPipelineUtility.SetBool(c, pipelinePath, "m_SoftShadowsSupported", false)
						} }
					));

				// Stored as the sample count, not the enum position: Disabled is 1, not 0.
				var msaa = pipeline.FindProperty("m_MSAA");
				using (RuleTiming.Begin("PAS1011", entry.Path))
					if (msaa != null && msaa.intValue >= 4)
					{
						attribute(entry.AddIssue("PAS1011", msaa.intValue + "x", "Use the lowest acceptable MSAA level", 0, null,
						options: new List<FixOption> { new FixOption {
							Label = "Reduce URP MSAA",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => RenderPipelineUtility.SetInt(c, pipelinePath, "m_MSAA", 2)
						} }
					));
					}

				var fastSrgb = pipeline.FindProperty("m_UseFastSRGBLinearConversion");
				using (RuleTiming.Begin("PRJ052", entry.Path))
					if (fastSrgb != null && !fastSrgb.boolValue)
					{
						// Reported only. This swaps an exact colour-space conversion for an
						// approximation. It is invisible on most content and is still a change to what
						// every pixel comes out as, which is not a change to make on someone's behalf.
						attribute(entry.AddIssue("PRJ052", "Off", "On, if colour accuracy allows", 0, null,
							"Replaces the exact sRGB conversion with a cheaper approximation. Check anything that "
							+ "depends on precise colour - gradients, UI, colour-matched branding - before enabling it.",
						options: new List<FixOption> { new FixOption {
							Label = "PRJ052 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => ReflectionUtility.SetStaticBool(typeof(PlayerSettings), "useFastSRGBLinearConversion", true))
						} }
					));
					}

				var hdr = pipeline.FindProperty("m_SupportsHDR");
				using (RuleTiming.Begin("PAS1010", entry.Path))
					if (hdr != null && hdr.boolValue)
						attribute(entry.AddIssue("PAS1010", "On", "Off unless post-processing needs it",
						options: new List<FixOption> { new FixOption {
							Label = "Disable URP HDR",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Project(c, () => RenderPipelineUtility.SetBool(c, pipelinePath, "m_SupportsHDR", false))
						} }
					));

				var lightsMode = pipeline.FindProperty("m_AdditionalLightsRenderingMode");
				var lightShadows = pipeline.FindProperty("m_AdditionalLightsShadowmapResolution");
				using (RuleTiming.Begin("PRJ054", entry.Path))
					if (!forwardPlus
						&& lightsMode != null && IsSerializedEnumNamed(lightsMode, "Per Pixel")
						&& lightShadows != null && lightShadows.intValue >= 2048)
					{
						attribute(entry.AddIssue("PRJ054", "Per Pixel, " + lightShadows.intValue + " shadow map",
							"Per Vertex, or a lower shadow map", 0, null,
							"Prefer the least expensive additional-light configuration that meets the target visual budget."));
					}

				var renderScale = pipeline.FindProperty("m_RenderScale");
				using (RuleTiming.Begin("PRJ058", entry.Path))
					if (renderScale != null && renderScale.floatValue > 1.001f)
					{
						attribute(entry.AddIssue("PRJ058", renderScale.floatValue.ToString("0.###"), "1 or below", 0, null,
						options: new List<FixOption> { new FixOption {
							Label = "PRJ058 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => { c.EnsureWritable(); RenderPipelineUtility.SetFloat(c, pipelinePath, "m_RenderScale", 1f); }
						} }
					));
					}

				var mainLightShadows = pipeline.FindProperty("m_MainLightShadowsSupported");
				using (RuleTiming.Begin("PRJ059", entry.Path))
					if (mainLightShadows != null && mainLightShadows.boolValue)
					{
						attribute(entry.AddIssue("PRJ059", "On", "Disable or lower for mobile when not visually required", 0, null,
						options: new List<FixOption> { new FixOption {
							Label = "PRJ059 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => RenderPipelineUtility.SetBool(c, pipelinePath, "m_MainLightShadowsSupported", false)
						} }
					));
					}

				var maxAdditionalLights = pipeline.FindProperty("m_MaxAdditionalLightsCount");
				using (RuleTiming.Begin("PRJ060", entry.Path))
					if (!forwardPlus && maxAdditionalLights != null && maxAdditionalLights.intValue > 4)
					{
						attribute(entry.AddIssue("PRJ060", maxAdditionalLights.intValue.ToString(), "4 or fewer where the scene permits", 0, null,
						options: new List<FixOption> { new FixOption {
							Label = "PRJ060 Review Fix",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => { c.EnsureWritable(); RenderPipelineUtility.SetInt(c, pipelinePath, 4, "m_AdditionalLightsCount", "m_MaxAdditionalLightsCount"); },
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
