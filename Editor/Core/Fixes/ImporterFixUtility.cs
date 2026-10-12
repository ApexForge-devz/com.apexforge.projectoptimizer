using System;
using UnityEditor;
namespace ApexForge.ProjectOptimizer
{
	public static class ImporterFixUtility
	{
		public static void Model(FixContext c,Action<ModelImporter> mutate){c.EnsureWritable();var mi=c.Importer as ModelImporter;if(mi==null)throw new InvalidOperationException("Not a ModelImporter");mutate(mi);c.NeedsReimport=true;}
		public static void Audio(FixContext c,Action<AudioImporter,string> mutate){c.EnsureWritable();var ai=c.Importer as AudioImporter;if(ai==null)throw new InvalidOperationException("Not an AudioImporter");mutate(ai,OptimizerSettings.Instance?.PlatformString??"Default");c.NeedsReimport=true;}
		public static void Texture(FixContext c,Action<TextureImporter,string> mutate){c.EnsureWritable();var ti=c.Importer as TextureImporter;if(ti==null)throw new InvalidOperationException("Not a TextureImporter");string platform=string.IsNullOrEmpty(OptimizerSettings.Instance?.PlatformString)?"Default":OptimizerSettings.Instance.PlatformString;mutate(ti,platform);c.NeedsReimport=true;}
		public static void Video(FixContext c,Action<VideoClipImporter> mutate){c.EnsureWritable();var vi=c.Importer as VideoClipImporter;if(vi==null)throw new InvalidOperationException("Not a VideoClipImporter");mutate(vi);c.NeedsReimport=true;}
		public static void Project(FixContext c,Action mutate){c.EnsureWritable();mutate();c.ChangedProjectSettings=true;}
	}
}
