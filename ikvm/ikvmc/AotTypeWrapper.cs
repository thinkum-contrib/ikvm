/*
  Copyright (C) 2002, 2003, 2004, 2005, 2006, 2007 Jeroen Frijters

  This software is provided 'as-is', without any express or implied
  warranty.  In no event will the authors be held liable for any damages
  arising from the use of this software.

  Permission is granted to anyone to use this software for any purpose,
  including commercial applications, and to alter it and redistribute it
  freely, subject to the following restrictions:

  1. The origin of this software must not be misrepresented; you must not
     claim that you wrote the original software. If you use this software
     in a product, an acknowledgment in the product documentation would be
     appreciated but is not required.
  2. Altered source versions must be plainly marked as such, and must not be
     misrepresented as being the original software.
  3. This notice may not be removed or altered from any source distribution.

  Jeroen Frijters
  jeroen@frijters.net
  
*/

using System;
using System.Collections;
#if WHIDBEY
using System.Collections.Generic;
#endif
using System.Reflection;
using System.Reflection.Emit;
using System.Diagnostics;
using System.Security;
using System.Security.Permissions;
using IKVM.Runtime;
using IKVM.Attributes;

using ILGenerator = IKVM.Internal.CountingILGenerator;
using Label = IKVM.Internal.CountingLabel;

namespace IKVM.Internal
{
	public abstract class CodeEmitter
	{
		internal abstract void Emit(CountingILGenerator ilgen);
	}

	class AotTypeWrapper : DynamicTypeWrapper
	{
		private FieldInfo ghostRefField;
		private MethodBuilder ghostIsInstanceMethod;
		private MethodBuilder ghostIsInstanceArrayMethod;
		private MethodBuilder ghostCastMethod;
		private MethodBuilder ghostCastArrayMethod;
		private TypeBuilder typeBuilderGhostInterface;
		private Annotation annotation;

		internal AotTypeWrapper(ClassFile f, CompilerClassLoader loader)
			: base(f, loader)
		{
		}

		internal override bool IsGhost
		{
			get
			{
				return ((CompilerClassLoader)classLoader).IsGhost(this);
			}
		}

		internal override bool IsMapUnsafeException
		{
			get
			{
				return ((CompilerClassLoader)classLoader).IsMapUnsafeException(this);
			}
		}

		internal override Type TypeAsBaseType
		{
			get
			{
				return typeBuilderGhostInterface != null ? typeBuilderGhostInterface : base.TypeAsBaseType;
			}
		}

		internal void GetParameterNamesFromXml(string methodName, string methodSig, string[] parameterNames)
		{
			IKVM.Internal.MapXml.Param[] parameters = ((CompilerClassLoader)classLoader).GetXmlMapParameters(Name, methodName, methodSig);
			if(parameters != null)
			{
				for(int i = 0; i < parameters.Length; i++)
				{
					if(parameterNames[i] == null)
					{
						parameterNames[i] = parameters[i].Name;
					}
				}
			}
		}

		internal void AddXmlMapParameterAttributes(MethodBase method, string className, string methodName, string methodSig, ref ParameterBuilder[] pbs)
		{
			IKVM.Internal.MapXml.Param[] parameters = ((CompilerClassLoader)classLoader).GetXmlMapParameters(className, methodName, methodSig);
			if(parameters != null)
			{
				if(pbs == null)
				{
					// let's hope that the parameters array is the right length
					pbs = GetParameterBuilders(method, parameters.Length, null);
				}
				for(int i = 0; i < pbs.Length; i++)
				{
					if(parameters[i].Attributes != null)
					{
						foreach(IKVM.Internal.MapXml.Attribute attr in parameters[i].Attributes)
						{
							AttributeHelper.SetCustomAttribute(pbs[i], attr);
						}
					}
				}
			}
		}

		private void AddParameterMetadata(MethodBuilder method, MethodWrapper mw)
		{
			ParameterBuilder[] pbs;
			if((mw.DeclaringType.IsPublic && (mw.IsPublic || mw.IsProtected)) || classLoader.EmitDebugInfo)
			{
				string[] parameterNames = new string[mw.GetParameters().Length];
				GetParameterNamesFromXml(mw.Name, mw.Signature, parameterNames);
				GetParameterNamesFromSig(mw.Signature, parameterNames);
				pbs = GetParameterBuilders(method, parameterNames.Length, parameterNames);
			}
			else
			{
				pbs = GetParameterBuilders(method, mw.GetParameters().Length, null);
			}
			if((mw.Modifiers & Modifiers.VarArgs) != 0 && pbs.Length > 0)
			{
				AttributeHelper.SetParamArrayAttribute(pbs[pbs.Length - 1]);
			}
			AddXmlMapParameterAttributes(method, Name, mw.Name, mw.Signature, ref pbs);
		}

		protected override void AddMapXmlFields(ref FieldWrapper[] fields)
		{
			Hashtable mapxml = ((CompilerClassLoader)classLoader).GetMapXml();
			if(mapxml != null)
			{
				IKVM.Internal.MapXml.Class clazz = (IKVM.Internal.MapXml.Class)mapxml[this.Name];
				if(clazz != null)
				{
					if(clazz.Fields != null)
					{
						foreach(IKVM.Internal.MapXml.Field field in clazz.Fields)
						{
							// are we adding a new field?
							bool found = false;
							foreach(FieldWrapper fw in fields)
							{
								if(fw.Name == field.Name && fw.Signature == field.Sig)
								{
									found = true;
									break;
								}
							}
							if(!found)
							{
								FieldWrapper[] newFields = new FieldWrapper[fields.Length + 1];
								Array.Copy(fields, newFields, fields.Length);
								fields = newFields;
								fields[fields.Length - 1] = FieldWrapper.Create(this, null, null, field.Name, field.Sig, new ExModifiers((Modifiers)field.Modifiers, false));
							}
						}
					}
				}				
			}
		}

		protected override bool EmitMapXmlMethodBody(CountingILGenerator ilgen, ClassFile f, ClassFile.Method m)
		{
			Hashtable mapxml = ((CompilerClassLoader)classLoader).GetMapXml();
			if(mapxml != null)
			{
				CodeEmitter opcodes = (CodeEmitter)mapxml[f.Name + "." + m.Name + m.Signature];
				if(opcodes != null)
				{
					opcodes.Emit(ilgen);
					return true;
				}
			}
			return false;
		}

		private void PublishAttributes(TypeBuilder typeBuilder, IKVM.Internal.MapXml.Class clazz)
		{
			foreach(IKVM.Internal.MapXml.Attribute attr in clazz.Attributes)
			{
				AttributeHelper.SetCustomAttribute(typeBuilder, attr);
			}
		}

		private static bool CheckPropertyArgs(Type[] args1, Type[] args2)
		{
			if(args1.Length == args2.Length)
			{
				for(int i = 0; i < args1.Length; i++)
				{
					if(args1[i] != args2[i])
					{
						return false;
					}
				}
				return true;
			}
			return false;
		}

		private static MethodAttributes GetPropertyMethodAttributes(MethodWrapper mw, bool final)
		{
			MethodAttributes attribs = MethodAttributes.HideBySig;
			if(mw.IsStatic)
			{
				attribs |= MethodAttributes.Static;
			}
			else
			{
				// NOTE in order for IntelliSense to consider the property a "real" property,
				// the getter and setter methods need to have substantially the same method attributes,
				// so we may need to look at our peer to determine whether we should be final
				// or not (and vice versa).
				attribs |= MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.CheckAccessOnOverride;
				if(final)
				{
					attribs |= MethodAttributes.Final;
				}
			}
			// TODO what happens if accessibility doesn't match our peer?
			if(mw.IsPublic)
			{
				attribs |= MethodAttributes.Public;
			}
			else if(mw.IsProtected)
			{
				attribs |= MethodAttributes.FamORAssem;
			}
			else if(mw.IsPrivate)
			{
				attribs |= MethodAttributes.Private;
			}
			else
			{
				attribs |= MethodAttributes.Assembly;
			}
			return attribs;
		}

		private void PublishProperties(TypeBuilder typeBuilder, IKVM.Internal.MapXml.Class clazz)
		{
			Hashtable classCache = new Hashtable();
			foreach(IKVM.Internal.MapXml.Property prop in clazz.Properties)
			{
				TypeWrapper typeWrapper = ClassFile.RetTypeWrapperFromSig(GetClassLoader(), classCache, prop.Sig);
				TypeWrapper[] propargs = ClassFile.ArgTypeWrapperListFromSig(GetClassLoader(), classCache, prop.Sig);
				Type[] indexer = new Type[propargs.Length];
				for(int i = 0; i < propargs.Length; i++)
				{
					indexer[i] = propargs[i].TypeAsSignatureType;
				}
				PropertyBuilder propbuilder = typeBuilder.DefineProperty(prop.Name, PropertyAttributes.None, typeWrapper.TypeAsSignatureType, indexer);
				AttributeHelper.HideFromJava(propbuilder);
				if(prop.Attributes != null)
				{
					foreach(IKVM.Internal.MapXml.Attribute attr in prop.Attributes)
					{
						AttributeHelper.SetCustomAttribute(propbuilder, attr);
					}
				}
				MethodWrapper getter = null;
				MethodWrapper setter = null;
				if(prop.getter != null)
				{
					getter = GetMethodWrapper(prop.getter.Name, prop.getter.Sig, true);
					if(getter == null)
					{
						Console.Error.WriteLine("Warning: getter not found for {0}::{1}", clazz.Name, prop.Name);
					}
				}
				if(prop.setter != null)
				{
					setter = GetMethodWrapper(prop.setter.Name, prop.setter.Sig, true);
					if(setter == null)
					{
						Console.Error.WriteLine("Warning: setter not found for {0}::{1}", clazz.Name, prop.Name);
					}
				}
				bool final = (getter != null && getter.IsFinal) || (setter != null && setter.IsFinal);
				if(getter != null)
				{
					MethodWrapper mw = getter;
					if(!CheckPropertyArgs(mw.GetParametersForDefineMethod(), indexer) || mw.ReturnType != typeWrapper)
					{
						Console.Error.WriteLine("Warning: ignoring invalid property getter for {0}::{1}", clazz.Name, prop.Name);
					}
					else
					{
						MethodBuilder mb = mw.GetMethod() as MethodBuilder;
						if(mb == null || mb.DeclaringType != typeBuilder || (!mb.IsFinal && final))
						{
							mb = typeBuilder.DefineMethod(GenerateUniqueMethodName("get_" + prop.Name, mw), GetPropertyMethodAttributes(mw, final), typeWrapper.TypeAsSignatureType, indexer);
							AttributeHelper.HideFromJava(mb);
							ILGenerator ilgen = new CountingILGenerator(mb.GetILGenerator());
							if(mw.IsStatic)
							{
								for(int i = 0; i < indexer.Length; i++)
								{
									ilgen.Emit(OpCodes.Ldarg, (short)i);
								}
								mw.EmitCall(ilgen);
							}
							else
							{
								ilgen.Emit(OpCodes.Ldarg_0);
								for(int i = 0; i < indexer.Length; i++)
								{
									ilgen.Emit(OpCodes.Ldarg, (short)(i + 1));
								}
								mw.EmitCallvirt(ilgen);
							}
							ilgen.Emit(OpCodes.Ret);
						}
						propbuilder.SetGetMethod(mb);
					}
				}
				if(setter != null)
				{
					MethodWrapper mw = setter;
					Type[] args = new Type[indexer.Length + 1];
					indexer.CopyTo(args, 0);
					args[args.Length - 1] = typeWrapper.TypeAsSignatureType;
					if(!CheckPropertyArgs(args, mw.GetParametersForDefineMethod()))
					{
						Console.Error.WriteLine("Warning: ignoring invalid property setter for {0}::{1}", clazz.Name, prop.Name);
					}
					else
					{
						MethodBuilder mb = mw.GetMethod() as MethodBuilder;
						if(mb == null || mb.DeclaringType != typeBuilder || (!mb.IsFinal && final))
						{
							mb = typeBuilder.DefineMethod(GenerateUniqueMethodName("set_" + prop.Name, mw), GetPropertyMethodAttributes(mw, final), mw.ReturnTypeForDefineMethod, args);
							AttributeHelper.HideFromJava(mb);
							ILGenerator ilgen = new CountingILGenerator(mb.GetILGenerator());
							if(mw.IsStatic)
							{
								for(int i = 0; i <= indexer.Length; i++)
								{
									ilgen.Emit(OpCodes.Ldarg, (short)i);
								}
								mw.EmitCall(ilgen);
							}
							else
							{
								ilgen.Emit(OpCodes.Ldarg_0);
								for(int i = 0; i <= indexer.Length; i++)
								{
									ilgen.Emit(OpCodes.Ldarg, (short)(i + 1));
								}
								mw.EmitCallvirt(ilgen);
							}
							ilgen.Emit(OpCodes.Ret);
						}
						propbuilder.SetSetMethod(mb);
					}
				}
			}
		}

		protected override bool IsPInvokeMethod(ClassFile.Method m)
		{
			Hashtable mapxml = ((CompilerClassLoader)classLoader).GetMapXml();
			if(mapxml != null)
			{
				IKVM.Internal.MapXml.Class clazz = (IKVM.Internal.MapXml.Class)mapxml[Name];
				if(clazz != null && clazz.Methods != null)
				{
					foreach(IKVM.Internal.MapXml.Method method in clazz.Methods)
					{
						if(method.Name == m.Name && method.Sig == m.Signature)
						{
							if(method.Attributes != null)
							{
								foreach(IKVM.Internal.MapXml.Attribute attr in method.Attributes)
								{
									if(Type.GetType(attr.Type) == typeof(System.Runtime.InteropServices.DllImportAttribute))
									{
										return true;
									}
								}
							}
							break;
						}
					}
				}
			}
			return base.IsPInvokeMethod(m);
		}

		protected override void EmitMapXmlMetadata(TypeBuilder typeBuilder, ClassFile classFile, FieldWrapper[] fields, MethodWrapper[] methods)
		{
			Hashtable mapxml = ((CompilerClassLoader)classLoader).GetMapXml();
			if(mapxml != null)
			{
				IKVM.Internal.MapXml.Class clazz = (IKVM.Internal.MapXml.Class)mapxml[classFile.Name];
				if(clazz != null)
				{
					if(clazz.Attributes != null)
					{
						PublishAttributes(typeBuilder, clazz);
					}
					if(clazz.Properties != null)
					{
						PublishProperties(typeBuilder, clazz);
					}
					if(clazz.Fields != null)
					{
						foreach(IKVM.Internal.MapXml.Field field in clazz.Fields)
						{
							if(field.Attributes != null)
							{
								foreach(FieldWrapper fw in fields)
								{
									if(fw.Name == field.Name && fw.Signature == field.Sig)
									{
										FieldBuilder fb = fw.GetField() as FieldBuilder;
										if(fb != null)
										{
											foreach(IKVM.Internal.MapXml.Attribute attr in field.Attributes)
											{
												AttributeHelper.SetCustomAttribute(fb, attr);
											}
										}
									}
								}
							}
						}
					}
					if(clazz.Constructors != null)
					{
						foreach(IKVM.Internal.MapXml.Constructor constructor in clazz.Constructors)
						{
							if(constructor.Attributes != null)
							{
								foreach(MethodWrapper mw in methods)
								{
									if(mw.Name == "<init>" && mw.Signature == constructor.Sig)
									{
										ConstructorBuilder mb = mw.GetMethod() as ConstructorBuilder;
										if(mb != null)
										{
											foreach(IKVM.Internal.MapXml.Attribute attr in constructor.Attributes)
											{
												AttributeHelper.SetCustomAttribute(mb, attr);
											}
										}
									}
								}
							}
						}
					}
					if(clazz.Methods != null)
					{
						// HACK this isn't the right place to do this, but for now it suffices
						foreach(IKVM.Internal.MapXml.Method method in clazz.Methods)
						{
							// are we adding a new method?
							if(GetMethodWrapper(method.Name, method.Sig, false) == null)
							{
								bool setmodifiers = false;
								MethodAttributes attribs = method.MethodAttributes;
								Modifiers modifiers = (Modifiers)method.Modifiers;
								if((modifiers & Modifiers.Public) != 0)
								{
									attribs |= MethodAttributes.Public;
								}
								else if((modifiers & Modifiers.Protected) != 0)
								{
									attribs |= MethodAttributes.FamORAssem;
								}
								else if((modifiers & Modifiers.Private) != 0)
								{
									attribs |= MethodAttributes.Private;
								}
								else
								{
									attribs |= MethodAttributes.Assembly;
								}
								if((modifiers & Modifiers.Static) != 0)
								{
									attribs |= MethodAttributes.Static;
									if((modifiers & Modifiers.Final) != 0)
									{
										setmodifiers = true;
									}
								}
								else if(method.Name != "<init>")
								{
									attribs |= MethodAttributes.Virtual;
									if((modifiers & Modifiers.Final) != 0)
									{
										attribs |= MethodAttributes.Final;
									}
									else if((modifiers & Modifiers.Abstract) != 0)
									{
										attribs |= MethodAttributes.Abstract;
									}
								}
								if((modifiers & Modifiers.Synchronized) != 0)
								{
									throw new NotImplementedException();
								}
								if(method.Name == "<init>")
								{
									throw new NotImplementedException();
								}
								Hashtable classCache = new Hashtable();
								Type returnType = ClassFile.RetTypeWrapperFromSig(GetClassLoader(), classCache, method.Sig).TypeAsSignatureType;
								TypeWrapper[] parameterTypeWrappers = ClassFile.ArgTypeWrapperListFromSig(GetClassLoader(), classCache, method.Sig);
								Type[] parameterTypes = new Type[parameterTypeWrappers.Length];
								for(int i = 0; i < parameterTypeWrappers.Length; i++)
								{
									parameterTypes[i] = parameterTypeWrappers[i].TypeAsSignatureType;
								}
								MethodBuilder mb = typeBuilder.DefineMethod(method.Name, attribs, returnType, parameterTypes);
								if(setmodifiers)
								{
									AttributeHelper.SetModifiers(mb, modifiers, false);
								}
								ILGenerator ilgen = mb.GetILGenerator();
								method.body.Emit(ilgen);
								if(method.Attributes != null)
								{
									foreach(IKVM.Internal.MapXml.Attribute attr in method.Attributes)
									{
										AttributeHelper.SetCustomAttribute(mb, attr);
									}
								}
							}
						}
						foreach(IKVM.Internal.MapXml.Method method in clazz.Methods)
						{
							if(method.Attributes != null)
							{
								foreach(MethodWrapper mw in methods)
								{
									if(mw.Name == method.Name && mw.Signature == method.Sig)
									{
										MethodBuilder mb = mw.GetMethod() as MethodBuilder;
										if(mb != null)
										{
											foreach(IKVM.Internal.MapXml.Attribute attr in method.Attributes)
											{
												AttributeHelper.SetCustomAttribute(mb, attr);
											}
										}
									}
								}
							}
						}
					}
					if(clazz.Interfaces != null)
					{
						foreach(IKVM.Internal.MapXml.Interface iface in clazz.Interfaces)
						{
							TypeWrapper tw = GetClassLoader().LoadClassByDottedName(iface.Name);
							// NOTE since this interface won't be part of the list in the ImplementAttribute,
							// it won't be visible from Java that the type implements this interface.
							typeBuilder.AddInterfaceImplementation(tw.TypeAsBaseType);
							if(iface.Methods != null)
							{
								foreach(IKVM.Internal.MapXml.Method m in iface.Methods)
								{
									MethodWrapper mw = tw.GetMethodWrapper(m.Name, m.Sig, false);
									if(mw == null)
									{
										throw new InvalidOperationException("Method " + m.Name + m.Sig + " not found in interface " + tw.Name);
									}
									mw.Link();
									MethodBuilder mb = typeBuilder.DefineMethod(tw.Name + "/" + m.Name, MethodAttributes.Private | MethodAttributes.NewSlot | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.CheckAccessOnOverride, mw.ReturnTypeForDefineMethod, mw.GetParametersForDefineMethod());
									AttributeHelper.HideFromJava(mb);
									typeBuilder.DefineMethodOverride(mb, (MethodInfo)mw.GetMethod());
									m.Emit(mb.GetILGenerator());
								}
							}
						}
					}
				}
			}
		}

		protected override MethodBuilder DefineGhostMethod(string name, MethodAttributes attribs, MethodWrapper mw)
		{
			if(typeBuilderGhostInterface != null)
			{
				return typeBuilderGhostInterface.DefineMethod(name, attribs, mw.ReturnTypeForDefineMethod, mw.GetParametersForDefineMethod());
			}
			return null;
		}

		protected override void FinishGhost(TypeBuilder typeBuilder, MethodWrapper[] methods)
		{
			if(typeBuilderGhostInterface != null)
			{
				// TODO consider adding methods from base interface and java.lang.Object as well
				for(int i = 0; i < methods.Length; i++)
				{
					// skip <clinit>
					if(!methods[i].IsStatic)
					{
						TypeWrapper[] args = methods[i].GetParameters();
						MethodBuilder stub = typeBuilder.DefineMethod(methods[i].Name, MethodAttributes.Public, methods[i].ReturnTypeForDefineMethod, methods[i].GetParametersForDefineMethod());
						AddParameterMetadata(stub, methods[i]);
						AttributeHelper.SetModifiers(stub, methods[i].Modifiers, methods[i].IsInternal);
						ILGenerator ilgen = stub.GetILGenerator();
						Label end = ilgen.DefineLabel();
						TypeWrapper[] implementers = ((CompilerClassLoader)classLoader).GetGhostImplementers(this);
						ilgen.Emit(OpCodes.Ldarg_0);
						ilgen.Emit(OpCodes.Ldfld, ghostRefField);
						ilgen.Emit(OpCodes.Dup);
						ilgen.Emit(OpCodes.Isinst, typeBuilderGhostInterface);
						Label label = ilgen.DefineLabel();
						ilgen.Emit(OpCodes.Brfalse_S, label);
						ilgen.Emit(OpCodes.Castclass, typeBuilderGhostInterface);
						for(int k = 0; k < args.Length; k++)
						{
							ilgen.Emit(OpCodes.Ldarg_S, (byte)(k + 1));
						}
						ilgen.Emit(OpCodes.Callvirt, (MethodInfo)methods[i].GetMethod());
						ilgen.Emit(OpCodes.Br, end);
						ilgen.MarkLabel(label);
						for(int j = 0; j < implementers.Length; j++)
						{
							ilgen.Emit(OpCodes.Dup);
							ilgen.Emit(OpCodes.Isinst, implementers[j].TypeAsTBD);
							label = ilgen.DefineLabel();
							ilgen.Emit(OpCodes.Brfalse_S, label);
							ilgen.Emit(OpCodes.Castclass, implementers[j].TypeAsTBD);
							for(int k = 0; k < args.Length; k++)
							{
								ilgen.Emit(OpCodes.Ldarg_S, (byte)(k + 1));
							}
							MethodWrapper mw = implementers[j].GetMethodWrapper(methods[i].Name, methods[i].Signature, true);
							mw.EmitCallvirt(ilgen);
							ilgen.Emit(OpCodes.Br, end);
							ilgen.MarkLabel(label);
						}
						// we need to do a null check (null fails all the isinst checks)
						EmitHelper.NullCheck(ilgen);
						EmitHelper.Throw(ilgen, "java.lang.IncompatibleClassChangeError", Name);
						ilgen.MarkLabel(end);
						ilgen.Emit(OpCodes.Ret);
					}
				}
				// HACK create a scope to enable reuse of "implementers" name
				if(true)
				{
					MethodBuilder mb;
					ILGenerator ilgen;
					LocalBuilder local;
					// add implicit conversions for all the ghost implementers
					TypeWrapper[] implementers = ((CompilerClassLoader)classLoader).GetGhostImplementers(this);
					for(int i = 0; i < implementers.Length; i++)
					{
						mb = typeBuilder.DefineMethod("op_Implicit", MethodAttributes.HideBySig | MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName, TypeAsSignatureType, new Type[] { implementers[i].TypeAsSignatureType });
						ilgen = mb.GetILGenerator();
						local = ilgen.DeclareLocal(TypeAsSignatureType);
						ilgen.Emit(OpCodes.Ldloca, local);
						ilgen.Emit(OpCodes.Ldarg_0);
						ilgen.Emit(OpCodes.Stfld, ghostRefField);
						ilgen.Emit(OpCodes.Ldloca, local);
						ilgen.Emit(OpCodes.Ldobj, TypeAsSignatureType);			
						ilgen.Emit(OpCodes.Ret);
					}
					// Implement the "IsInstance" method
					mb = ghostIsInstanceMethod;
					AttributeHelper.HideFromJava(mb);
					ilgen = mb.GetILGenerator();
					Label end = ilgen.DefineLabel();
					for(int i = 0; i < implementers.Length; i++)
					{
						ilgen.Emit(OpCodes.Ldarg_0);
						ilgen.Emit(OpCodes.Isinst, implementers[i].TypeAsTBD);
						Label label = ilgen.DefineLabel();
						ilgen.Emit(OpCodes.Brfalse_S, label);
						ilgen.Emit(OpCodes.Ldc_I4_1);
						ilgen.Emit(OpCodes.Br, end);
						ilgen.MarkLabel(label);
					}
					ilgen.Emit(OpCodes.Ldarg_0);
					ilgen.Emit(OpCodes.Isinst, typeBuilderGhostInterface);
					ilgen.Emit(OpCodes.Ldnull);
					ilgen.Emit(OpCodes.Cgt_Un);
					ilgen.MarkLabel(end);
					ilgen.Emit(OpCodes.Ret);
					// Implement the "IsInstanceArray" method
					mb = ghostIsInstanceArrayMethod;
					AttributeHelper.HideFromJava(mb);
					ilgen = mb.GetILGenerator();
					LocalBuilder localType = ilgen.DeclareLocal(typeof(Type));
					ilgen.Emit(OpCodes.Ldarg_0);
					Label skip = ilgen.DefineLabel();
					ilgen.Emit(OpCodes.Brtrue_S, skip);
					ilgen.Emit(OpCodes.Ldc_I4_0);
					ilgen.Emit(OpCodes.Ret);
					ilgen.MarkLabel(skip);
					ilgen.Emit(OpCodes.Ldarg_0);
					ilgen.Emit(OpCodes.Call, typeof(object).GetMethod("GetType"));
					ilgen.Emit(OpCodes.Stloc, localType);
					skip = ilgen.DefineLabel();
					ilgen.Emit(OpCodes.Br_S, skip);
					Label iter = ilgen.DefineLabel();
					ilgen.MarkLabel(iter);
					ilgen.Emit(OpCodes.Ldarg_1);
					ilgen.Emit(OpCodes.Ldc_I4_1);
					ilgen.Emit(OpCodes.Sub);
					ilgen.Emit(OpCodes.Starg_S, (byte)1);
					ilgen.Emit(OpCodes.Ldloc, localType);
					ilgen.Emit(OpCodes.Callvirt, typeof(Type).GetMethod("GetElementType"));
					ilgen.Emit(OpCodes.Stloc, localType);
					ilgen.MarkLabel(skip);
					ilgen.Emit(OpCodes.Ldloc, localType);
					ilgen.Emit(OpCodes.Callvirt, typeof(Type).GetMethod("get_IsArray"));
					ilgen.Emit(OpCodes.Brtrue_S, iter);
					ilgen.Emit(OpCodes.Ldarg_1);
					skip = ilgen.DefineLabel();
					ilgen.Emit(OpCodes.Brfalse_S, skip);
					ilgen.Emit(OpCodes.Ldc_I4_0);
					ilgen.Emit(OpCodes.Ret);
					ilgen.MarkLabel(skip);
					for(int i = 0; i < implementers.Length; i++)
					{
						ilgen.Emit(OpCodes.Ldtoken, implementers[i].TypeAsTBD);
						ilgen.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle"));
						ilgen.Emit(OpCodes.Ldloc, localType);
						ilgen.Emit(OpCodes.Callvirt, typeof(Type).GetMethod("IsAssignableFrom"));
						Label label = ilgen.DefineLabel();
						ilgen.Emit(OpCodes.Brfalse_S, label);
						ilgen.Emit(OpCodes.Ldc_I4_1);
						ilgen.Emit(OpCodes.Ret);
						ilgen.MarkLabel(label);
					}
					ilgen.Emit(OpCodes.Ldtoken, typeBuilderGhostInterface);
					ilgen.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle"));
					ilgen.Emit(OpCodes.Ldloc, localType);
					ilgen.Emit(OpCodes.Callvirt, typeof(Type).GetMethod("IsAssignableFrom"));
					skip = ilgen.DefineLabel();
					ilgen.Emit(OpCodes.Brfalse_S, skip);
					ilgen.Emit(OpCodes.Ldc_I4_1);
					ilgen.Emit(OpCodes.Ret);
					ilgen.MarkLabel(skip);
					ilgen.Emit(OpCodes.Ldtoken, typeof(object));
					ilgen.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle"));
					ilgen.Emit(OpCodes.Ldloc, localType);
					ilgen.Emit(OpCodes.Ceq);
					ilgen.Emit(OpCodes.Ret);
						
					// Implement the "Cast" method
					mb = ghostCastMethod;
					AttributeHelper.HideFromJava(mb);
					ilgen = mb.GetILGenerator();
					end = ilgen.DefineLabel();
					for(int i = 0; i < implementers.Length; i++)
					{
						ilgen.Emit(OpCodes.Ldarg_0);
						ilgen.Emit(OpCodes.Isinst, implementers[i].TypeAsTBD);
						ilgen.Emit(OpCodes.Brtrue, end);
					}
					ilgen.Emit(OpCodes.Ldarg_0);
					ilgen.Emit(OpCodes.Castclass, typeBuilderGhostInterface);
					ilgen.Emit(OpCodes.Pop);
					ilgen.MarkLabel(end);
					local = ilgen.DeclareLocal(TypeAsSignatureType);
					ilgen.Emit(OpCodes.Ldloca, local);
					ilgen.Emit(OpCodes.Ldarg_0);
					ilgen.Emit(OpCodes.Stfld, ghostRefField);
					ilgen.Emit(OpCodes.Ldloca, local);
					ilgen.Emit(OpCodes.Ldobj, TypeAsSignatureType);	
					ilgen.Emit(OpCodes.Ret);
					// Add "ToObject" methods
					mb = typeBuilder.DefineMethod("ToObject", MethodAttributes.HideBySig | MethodAttributes.Public, typeof(object), Type.EmptyTypes);
					AttributeHelper.HideFromJava(mb);
					ilgen = mb.GetILGenerator();
					ilgen.Emit(OpCodes.Ldarg_0);
					ilgen.Emit(OpCodes.Ldfld, ghostRefField);
					ilgen.Emit(OpCodes.Ret);

					// Implement the "CastArray" method
					// NOTE unlike "Cast" this doesn't return anything, it just throws a ClassCastException if the
					// cast is unsuccessful. Also, because of the complexity of this test, we call IsInstanceArray
					// instead of reimplementing the check here.
					mb = ghostCastArrayMethod;
					AttributeHelper.HideFromJava(mb);
					ilgen = mb.GetILGenerator();
					end = ilgen.DefineLabel();
					ilgen.Emit(OpCodes.Ldarg_0);
					ilgen.Emit(OpCodes.Brfalse_S, end);
					ilgen.Emit(OpCodes.Ldarg_0);
					ilgen.Emit(OpCodes.Ldarg_1);
					ilgen.Emit(OpCodes.Call, ghostIsInstanceArrayMethod);
					ilgen.Emit(OpCodes.Brtrue_S, end);
					EmitHelper.Throw(ilgen, "java.lang.ClassCastException");
					ilgen.MarkLabel(end);
					ilgen.Emit(OpCodes.Ret);

					// Implement the "Equals" method
					mb = typeBuilder.DefineMethod("Equals", MethodAttributes.HideBySig | MethodAttributes.Public | MethodAttributes.Virtual, typeof(bool), new Type[] { typeof(object) });
					AttributeHelper.HideFromJava(mb);
					ilgen = mb.GetILGenerator();
					ilgen.Emit(OpCodes.Ldarg_0);
					ilgen.Emit(OpCodes.Ldfld, ghostRefField);
					ilgen.Emit(OpCodes.Ldarg_1);
					ilgen.Emit(OpCodes.Ceq);
					ilgen.Emit(OpCodes.Ret);

					// Implement the "GetHashCode" method
					mb = typeBuilder.DefineMethod("GetHashCode", MethodAttributes.HideBySig | MethodAttributes.Public | MethodAttributes.Virtual, typeof(int), Type.EmptyTypes);
					AttributeHelper.HideFromJava(mb);
					ilgen = mb.GetILGenerator();
					ilgen.Emit(OpCodes.Ldarg_0);
					ilgen.Emit(OpCodes.Ldfld, ghostRefField);
					ilgen.Emit(OpCodes.Callvirt, typeof(object).GetMethod("GetHashCode"));
					ilgen.Emit(OpCodes.Ret);

					// Implement the "op_Equality" method
					mb = typeBuilder.DefineMethod("op_Equality", MethodAttributes.HideBySig | MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName, typeof(bool), new Type[] { typeBuilder, typeBuilder });
					AttributeHelper.HideFromJava(mb);
					ilgen = mb.GetILGenerator();
					ilgen.Emit(OpCodes.Ldarga_S, (byte)0);
					ilgen.Emit(OpCodes.Ldfld, ghostRefField);
					ilgen.Emit(OpCodes.Ldarga_S, (byte)1);
					ilgen.Emit(OpCodes.Ldfld, ghostRefField);
					ilgen.Emit(OpCodes.Ceq);
					ilgen.Emit(OpCodes.Ret);

					// Implement the "op_Inequality" method
					mb = typeBuilder.DefineMethod("op_Inequality", MethodAttributes.HideBySig | MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName, typeof(bool), new Type[] { typeBuilder, typeBuilder });
					AttributeHelper.HideFromJava(mb);
					ilgen = mb.GetILGenerator();
					ilgen.Emit(OpCodes.Ldarga_S, (byte)0);
					ilgen.Emit(OpCodes.Ldfld, ghostRefField);
					ilgen.Emit(OpCodes.Ldarga_S, (byte)1);
					ilgen.Emit(OpCodes.Ldfld, ghostRefField);
					ilgen.Emit(OpCodes.Ceq);
					ilgen.Emit(OpCodes.Ldc_I4_0);
					ilgen.Emit(OpCodes.Ceq);
					ilgen.Emit(OpCodes.Ret);
				}
			}
		}

		protected override void FinishGhostStep2()
		{
			if(typeBuilderGhostInterface != null)
			{
				typeBuilderGhostInterface.CreateType();
			}
		}

		protected override TypeBuilder DefineGhostType(string mangledTypeName, TypeAttributes typeAttribs)
		{
			typeAttribs &= ~(TypeAttributes.Interface | TypeAttributes.Abstract);
			typeAttribs |= TypeAttributes.Class | TypeAttributes.Sealed;
			TypeBuilder typeBuilder = classLoader.GetTypeWrapperFactory().ModuleBuilder.DefineType(mangledTypeName, typeAttribs, typeof(ValueType));
			AttributeHelper.SetGhostInterface(typeBuilder);
			AttributeHelper.SetModifiers(typeBuilder, Modifiers, IsInternal);
			ghostRefField = typeBuilder.DefineField("__<ref>", typeof(object), FieldAttributes.Public | FieldAttributes.SpecialName);
			typeBuilderGhostInterface = typeBuilder.DefineNestedType("__Interface", TypeAttributes.Interface | TypeAttributes.Abstract | TypeAttributes.NestedPublic);
			AttributeHelper.HideFromJava(typeBuilderGhostInterface);
			ghostIsInstanceMethod = typeBuilder.DefineMethod("IsInstance", MethodAttributes.HideBySig | MethodAttributes.Public | MethodAttributes.Static, typeof(bool), new Type[] { typeof(object) });
			ghostIsInstanceMethod.DefineParameter(1, ParameterAttributes.None, "obj");
			ghostIsInstanceArrayMethod = typeBuilder.DefineMethod("IsInstanceArray", MethodAttributes.HideBySig | MethodAttributes.Public | MethodAttributes.Static, typeof(bool), new Type[] { typeof(object), typeof(int) });
			ghostIsInstanceArrayMethod.DefineParameter(1, ParameterAttributes.None, "obj");
			ghostIsInstanceArrayMethod.DefineParameter(2, ParameterAttributes.None, "rank");
			ghostCastMethod = typeBuilder.DefineMethod("Cast", MethodAttributes.HideBySig | MethodAttributes.Public | MethodAttributes.Static, typeBuilder, new Type[] { typeof(object) });
			ghostCastMethod.DefineParameter(1, ParameterAttributes.None, "obj");
			ghostCastArrayMethod = typeBuilder.DefineMethod("CastArray", MethodAttributes.HideBySig | MethodAttributes.Public | MethodAttributes.Static, typeof(void), new Type[] { typeof(object), typeof(int) });
			ghostCastArrayMethod.DefineParameter(1, ParameterAttributes.None, "obj");
			ghostCastArrayMethod.DefineParameter(2, ParameterAttributes.None, "rank");
			return typeBuilder;
		}

		internal override FieldInfo GhostRefField
		{
			get
			{
				return ghostRefField;
			}
		}

		internal override void EmitCheckcast(TypeWrapper context, ILGenerator ilgen)
		{
			if(IsGhost)
			{
				ilgen.Emit(OpCodes.Dup);
				ilgen.Emit(OpCodes.Call, ghostCastMethod);
				ilgen.Emit(OpCodes.Pop);
			}
			else if(IsGhostArray)
			{
				ilgen.Emit(OpCodes.Dup);
				TypeWrapper tw = this;
				int rank = 0;
				while(tw.IsArray)
				{
					rank++;
					tw = tw.ElementTypeWrapper;
				}
				ilgen.Emit(OpCodes.Ldc_I4, rank);
				ilgen.Emit(OpCodes.Call, ghostCastArrayMethod);
				ilgen.Emit(OpCodes.Castclass, ArrayTypeWrapper.MakeArrayType(typeof(object), rank));
			}
			else
			{
				base.EmitCheckcast(context, ilgen);
			}
		}

		internal override void EmitInstanceOf(TypeWrapper context, ILGenerator ilgen)
		{
			if(IsGhost)
			{
				ilgen.Emit(OpCodes.Call, ghostIsInstanceMethod);
			}
			else if(IsGhostArray)
			{
				ilgen.Emit(OpCodes.Call, ghostIsInstanceArrayMethod);
			}
			else
			{
				base.EmitInstanceOf(context, ilgen);
			}
		}

		internal void SetAnnotation(Annotation annotation)
		{
			this.annotation = annotation;
		}

		internal override Annotation Annotation
		{
			get
			{
				return annotation;
			}
		}
	}
}
