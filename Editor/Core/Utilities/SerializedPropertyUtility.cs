using UnityEditor;
using UnityEngine;
namespace ApexForge.ProjectOptimizer
{
	public static class SerializedPropertyUtility
	{
		public static SerializedProperty Find(SerializedObject so,string path){if(so==null||string.IsNullOrEmpty(path))return null;try{return so.FindProperty(path);}catch{return null;}}
		public static string ReadString(SerializedObject so,string path,string fallback){var p=Find(so,path);return p!=null&&!string.IsNullOrEmpty(p.stringValue)?p.stringValue:fallback;}
		public static int ReadInt(SerializedObject so,string path,int fallback){var p=Find(so,path);return p!=null&&p.propertyType==SerializedPropertyType.Integer?p.intValue:fallback;}
		public static bool ReadBool(SerializedObject so,string path,bool fallback){var p=Find(so,path);return p!=null&&p.propertyType==SerializedPropertyType.Boolean?p.boolValue:fallback;}
		public static string ReadRelativeString(SerializedProperty parent,string name,string fallback){var p=parent?.FindPropertyRelative(name);return p!=null&&!string.IsNullOrEmpty(p.stringValue)?p.stringValue:fallback;}
		public static int ReadRelativeInt(SerializedProperty parent,string name,int fallback){var p=parent?.FindPropertyRelative(name);return p!=null&&p.propertyType==SerializedPropertyType.Integer?p.intValue:fallback;}
		public static bool ReadRelativeBool(SerializedProperty parent,string name,bool fallback){var p=parent?.FindPropertyRelative(name);return p!=null&&p.propertyType==SerializedPropertyType.Boolean?p.boolValue:fallback;}
	}
}
