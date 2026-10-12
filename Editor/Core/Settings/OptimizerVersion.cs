namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// The version numbers that decide whether data written by an earlier build of this tool can
	/// still be believed.
	/// <para>
	/// Gathered in one place because they are easy to forget individually and expensive to get
	/// wrong. Each cache and manifest used to carry its own private constant, so changing what a
	/// facts type recorded meant remembering to bump a number three files away - and forgetting it
	/// did not fail, it silently reused rows whose shape no longer matched, filling missing fields
	/// with defaults that read as real measurements.
	/// </para>
	/// </summary>
	public static class OptimizerVersion
	{
		/// <summary>
		/// The tool's data schema. Bump this whenever a cached measurement type, a persisted index
		/// or a report field changes shape, so anything written by an older build is discarded
		/// rather than reinterpreted.
		/// </summary>
		public const int Schema = 11;

		/// <summary>
		/// Written into fix-batch manifests and exported reports, so a file found later can be
		/// matched to the build that produced it.
		/// </summary>
		// Not const: folding an int into a string is not a constant expression in C#, and readonly
		// also stops anything reassigning what is meant to identify this build.
		public static string SemanticRelease => ProjectOptimizerConstants.PackageVersion;

		public static string Display => ProjectOptimizerConstants.DisplayVersion(Schema);
	}
}
