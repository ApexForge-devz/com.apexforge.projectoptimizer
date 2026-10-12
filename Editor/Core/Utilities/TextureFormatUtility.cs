using UnityEditor;
namespace ApexForge.ProjectOptimizer
{
	public static class TextureFormatUtility
	{
		public static bool IsUncompressed(TextureImporterFormat format){switch(format){case TextureImporterFormat.RGB24:case TextureImporterFormat.RGBA32:case TextureImporterFormat.ARGB32:case TextureImporterFormat.RGBA64:case TextureImporterFormat.RGBAHalf:case TextureImporterFormat.RHalf:case TextureImporterFormat.RGHalf:case TextureImporterFormat.RFloat:case TextureImporterFormat.RGFloat:case TextureImporterFormat.RGBAFloat:case TextureImporterFormat.RGB16:case TextureImporterFormat.R16:case TextureImporterFormat.R8:case TextureImporterFormat.RG16:case TextureImporterFormat.Alpha8:case TextureImporterFormat.RGBA16:case TextureImporterFormat.ARGB16:case TextureImporterFormat.RG32:case TextureImporterFormat.RGB48:case TextureImporterFormat.RGBA32_SIGNED:case TextureImporterFormat.R16_SIGNED:case TextureImporterFormat.RG16_SIGNED:case TextureImporterFormat.RG32_SIGNED:case TextureImporterFormat.RGB24_SIGNED:case TextureImporterFormat.RGB48_SIGNED:case TextureImporterFormat.RGBA64_SIGNED:case TextureImporterFormat.RGB9E5:return true;default:return false;}}
	}
}
