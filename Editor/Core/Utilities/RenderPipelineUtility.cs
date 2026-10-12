using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
namespace ApexForge.ProjectOptimizer
{
	public static class RenderPipelineUtility
	{
		public static UnityEngine.Object Load(string pipelinePath) => string.IsNullOrEmpty(pipelinePath) ? GraphicsSettings.currentRenderPipeline : AssetDatabase.LoadMainAssetAtPath(pipelinePath);
		public static bool? ReadBool(string pipelinePath,string property){var pipeline=Load(pipelinePath);if(pipeline==null)return null;try{var p=new SerializedObject(pipeline).FindProperty(property);return p!=null&&p.propertyType==SerializedPropertyType.Boolean?p.boolValue:(bool?)null;}catch{return null;}}
		public static int? ReadInt(string pipelinePath,string property){var pipeline=Load(pipelinePath);if(pipeline==null)return null;try{var p=new SerializedObject(pipeline).FindProperty(property);return p!=null&&p.propertyType==SerializedPropertyType.Integer?p.intValue:(int?)null;}catch{return null;}}
		public static string ReadEnumName(string pipelinePath,string property){var pipeline=Load(pipelinePath);if(pipeline==null)return null;try{var p=new SerializedObject(pipeline).FindProperty(property);if(p==null||p.propertyType!=SerializedPropertyType.Enum)return null;var names=p.enumDisplayNames;if(names!=null&&p.enumValueIndex>=0&&p.enumValueIndex<names.Length)return names[p.enumValueIndex];names=p.enumNames;if(names!=null&&p.enumValueIndex>=0&&p.enumValueIndex<names.Length)return names[p.enumValueIndex];return null;}catch{return null;}}
		public static void SetBool(FixContext context,string pipelinePath,string property,bool value){Set(context,pipelinePath,value,(p,v)=>p.boolValue=(bool)v,property);}
		public static void SetInt(FixContext context,string pipelinePath,string property,int value){Set(context,pipelinePath,value,(p,v)=>p.intValue=(int)v,property);}
		public static void SetInt(FixContext context,string pipelinePath,int value,params string[] properties){Set(context,pipelinePath,value,(p,v)=>p.intValue=(int)v,properties);}
		public static void SetFloat(FixContext context,string pipelinePath,string property,float value){Set(context,pipelinePath,value,(p,v)=>p.floatValue=(float)v,property);}
		public static void SetCurrentMember(string property,object value){var pipeline=GraphicsSettings.currentRenderPipeline;if(pipeline==null)throw new InvalidOperationException("No active render pipeline asset.");ReflectionUtility.SetMember(pipeline,property,value);EditorUtility.SetDirty(pipeline);}
		static void Set(FixContext context,string pipelinePath,object value,Action<SerializedProperty,object> assign,params string[] properties){if(context==null)throw new InvalidOperationException("Fix context is missing.");var pipeline=Load(pipelinePath);if(pipeline==null)throw new InvalidOperationException("The render pipeline asset '"+pipelinePath+"' could not be loaded.");var target=new SerializedObject(pipeline);SerializedProperty prop=null;for(int i=0;i<properties.Length;i++){prop=target.FindProperty(properties[i]);if(prop!=null)break;}if(prop==null)throw new InvalidOperationException("'"+pipelinePath+"' has none of the expected properties.");assign(prop,value);target.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(pipeline);context.NeedsSave=true;}
	}
}
