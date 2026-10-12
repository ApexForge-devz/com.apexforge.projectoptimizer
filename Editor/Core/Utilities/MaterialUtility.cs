using UnityEngine;
namespace ApexForge.ProjectOptimizer
{
	public static class MaterialUtility
	{
		static readonly string[] k_NormalMapProperties={"_BumpMap","_NormalMap","_DetailNormalMap","_NormalMapOS"};
		public static bool HasNormalMap(Material material){if(material==null||material.shader==null)return false;for(int i=0;i<k_NormalMapProperties.Length;i++){string p=k_NormalMapProperties[i];if(material.HasProperty(p)&&material.GetTexture(p)!=null)return true;}return false;}
	}
}
