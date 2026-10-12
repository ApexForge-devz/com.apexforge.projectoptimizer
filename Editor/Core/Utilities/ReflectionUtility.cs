using System;
using System.Reflection;
namespace ApexForge.ProjectOptimizer
{
	public static class ReflectionUtility
	{
		const BindingFlags InstanceFlags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
		const BindingFlags StaticFlags=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
		public static object GetMember(object obj,string name){if(obj==null||string.IsNullOrEmpty(name))return null;var t=obj.GetType();var p=t.GetProperty(name,InstanceFlags);if(p!=null)return p.GetValue(obj,null);var f=t.GetField(name,InstanceFlags);return f?.GetValue(obj);}
		public static void SetMember(object obj,string name,object value){if(obj==null)throw new InvalidOperationException("Target object is null.");var t=obj.GetType();var p=t.GetProperty(name,InstanceFlags);if(p!=null&&p.CanWrite){p.SetValue(obj,value,null);return;}var f=t.GetField(name,InstanceFlags);if(f!=null){f.SetValue(obj,value);return;}throw new InvalidOperationException("Could not set "+name+".");}
		public static void SetStaticBool(Type t,string name,bool value){var p=t.GetProperty(name,StaticFlags);if(p!=null&&p.CanWrite&&p.PropertyType==typeof(bool)){p.SetValue(null,value,null);return;}var f=t.GetField(name,StaticFlags);if(f!=null&&f.FieldType==typeof(bool)){f.SetValue(null,value);return;}throw new InvalidOperationException("Could not set "+t.FullName+"."+name+".");}
		public static void SetStaticEnum(Type t,string name,string value){var p=t.GetProperty(name,StaticFlags);if(p!=null&&p.CanWrite&&p.PropertyType.IsEnum){p.SetValue(null,Enum.Parse(p.PropertyType,value),null);return;}throw new InvalidOperationException("Could not set enum "+t.FullName+"."+name+".");}
	}
}
