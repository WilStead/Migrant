/*
  Copyright (c) 2012 Ant Micro <www.antmicro.com>

  Authors:
   * Konrad Kruczynski (kkruczynski@antmicro.com)
   * Piotr Zierhoffer (pzierhoffer@antmicro.com)

  Permission is hereby granted, free of charge, to any person obtaining
  a copy of this software and associated documentation files (the
  "Software"), to deal in the Software without restriction, including
  without limitation the rights to use, copy, modify, merge, publish,
  distribute, sublicense, and/or sell copies of the Software, and to
  permit persons to whom the Software is furnished to do so, subject to
  the following conditions:

  The above copyright notice and this permission notice shall be
  included in all copies or substantial portions of the Software.

  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
  EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
  MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
  NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
  LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
  OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
  WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
using AntMicro.Migrant;
using System.IO;
using System.Collections.Generic;
using System;
using System.Reflection.Emit;
using System.Reflection;
using System.Linq;
using System.Collections;
using System.Threading;
using AntMicro.Migrant.Hooks;

namespace AntMicro.Migrant.Emitter
{
	public class GeneratingObjectWriter : ObjectWriter
	{
		public GeneratingObjectWriter(Stream stream, IDictionary<Type, int> typeIndices, bool strictTypes, Action<Type> missingTypeCallback = null, 
		                              Action<object> preSerializationCallback = null, Action<object> postSerializationCallback = null)
			: base(stream, typeIndices, strictTypes, missingTypeCallback, preSerializationCallback, postSerializationCallback)
		{
			transientTypes = new Dictionary<Type, bool>();
			writeMethods = new Action<PrimitiveWriter, object>[0];
			RegenerateWriteMethods();
		}

		internal void WriteObjectId(object o)
		{
			// this function is called when object to serialize cannot be data-inlined object such as string
			Writer.Write(Identifier.GetId(o));
		}

		internal void WriteObjectIdPossiblyInline(object o)
		{
			var refId = Identifier.GetId(o);
			var type = o.GetType();
			Writer.Write(refId);
			if(ShouldBeInlined(type, refId))
			{
				InlineWritten.Add(refId);
                InvokeCallbacksAndWriteObject(o);
			}
		}

		// TODO: inline?
		internal bool CheckTransient(object o)
		{
			return CheckTransient(o.GetType());
		}

		internal bool CheckTransient(Type type)
		{
			bool result;
			if(transientTypes.TryGetValue(type, out result))
			{
				return result;
			}
			var isTransient = type.IsDefined(typeof(TransientAttribute), false);
			transientTypes.Add(type, isTransient);
			return isTransient;
		}

		protected internal override void WriteObjectInner(object o)
		{
			var type = o.GetType();
            // TODO: touch should refresh array
            TouchType(type);
			var typeId = TypeIndices[type];
			Writer.Write(typeId);
			writeMethods[typeId](Writer, o);
		}

		protected override void AddMissingType(Type type)
		{
			base.AddMissingType(type);
			RegenerateWriteMethods();
		}

		private static void GenerateInvokeCallbacks(Type actualType, ILGenerator generator, Type attributeType)
		{
			var preSerializationMethods = Helpers.GetMethodsWithAttribute(attributeType, actualType);
			foreach(var method in preSerializationMethods)
			{
				if(!method.IsStatic)
				{
					generator.Emit(OpCodes.Ldarg_2); // object to serialize
				}
				generator.Emit(OpCodes.Call, method);
			}
		}
		private void RegenerateWriteMethods()
		{
			var newWriteMethods = new Action<PrimitiveWriter, object>[TypeIndices.Count];
			foreach(var entry in TypeIndices)
			{
				if(writeMethods.Length > entry.Value)
				{
					newWriteMethods[entry.Value] = writeMethods[entry.Value];
				}
				else
				{
					if(!CheckTransient(entry.Key))
					{
						newWriteMethods[entry.Value] = GenerateWriteMethod(entry.Key);
					}
					// for transient class the delegate will never be called
				}
			}
			writeMethods = newWriteMethods;
		}

		private Action<PrimitiveWriter, object> GenerateWriteMethod(Type actualType)
		{
			var specialWrite = LinkSpecialWrite(actualType);
			if(specialWrite != null)
			{
				return specialWrite;
			}

			// TODO: parameter types: move and unify
			DynamicMethod dynamicMethod;
			if(!actualType.IsArray)
			{
				dynamicMethod = new DynamicMethod("Write", MethodAttributes.Public | MethodAttributes.Static, CallingConventions.Standard,
			                               typeof(void), ParameterTypes, actualType, true);
			}
			else
			{
				var methodNo = Interlocked.Increment(ref WriteArrayMethodCounter);
				dynamicMethod = new DynamicMethod(string.Format("WriteArray{0}", methodNo), null, ParameterTypes, true);
			}
			var generator = dynamicMethod.GetILGenerator();

			// preserialization callbacks
			GenerateInvokeCallbacks(actualType, generator, typeof(PreSerializationAttribute));

			if(!GenerateSpecialWrite(generator, actualType))
			{
				GenerateWriteFields(generator, gen =>
				                    {
					gen.Emit(OpCodes.Ldarg_2);
				}, actualType);
			}

			// postserialization callbacks
			GenerateInvokeCallbacks(actualType, generator, typeof(PostSerializationAttribute));

			generator.Emit(OpCodes.Ret);
			var result = (Action<PrimitiveWriter, object>)dynamicMethod.CreateDelegate(typeof(Action<PrimitiveWriter, object>), this);
			return result;
		}

		private void GenerateWriteFields(ILGenerator generator, Action<ILGenerator> putValueToWriteOnTop, Type actualType)
		{
			var fields = actualType.GetAllFields().Where(Helpers.IsNotTransient).OrderBy(x => x.Name); // TODO: unify
			foreach(var field in fields)
			{
				GenerateWriteType(generator, gen => 
				                  {
					putValueToWriteOnTop(gen);
					gen.Emit(OpCodes.Ldfld, field); // TODO: consider putting that in some local variable
				}, field.FieldType);
			}
		}

		private Action<PrimitiveWriter, object> LinkSpecialWrite(Type actualType)
		{
			if(actualType == typeof(string))
			{
				return (y, obj) => y.Write((string)obj);
			}
			if(typeof(ISpeciallySerializable).IsAssignableFrom(actualType))
			{
				return (writer, obj) => {
					var startingPosition = writer.Position;
	                ((ISpeciallySerializable)obj).Save(writer);
	                writer.Write(writer.Position - startingPosition);
				};
			}
			return null;
		}

		private bool GenerateSpecialWrite(ILGenerator generator, Type actualType)
		{
			if(actualType.IsValueType)
			{
				// value type encountered here means it is in fact boxed value type
				// according to protocol it is written as it would be written inlined
				GenerateWriteValue(generator, gen =>
				                   {
					gen.Emit(OpCodes.Ldarg_2); // value to serialize
					gen.Emit(OpCodes.Unbox_Any, actualType);
				}, actualType);
				return true;
			}
			if(actualType.IsArray)
			{
				GenerateWriteArray(generator, actualType);
				return true;
			}
			bool isGeneric, isGenericallyIterable, isDictionary;
			Type elementType;
			if(Helpers.IsCollection(actualType, out elementType, out isGeneric, out isGenericallyIterable, out isDictionary))
			{
				GenerateWriteCollection(generator, elementType, isGeneric, isGenericallyIterable, isDictionary);
				return true;
			}
			return false;
		}

		private void GenerateWriteArray(ILGenerator generator, Type actualType)
		{
			PrimitiveWriter primitiveWriter = null; // TODO

			var elementType = actualType.GetElementType();
			var rank = actualType.GetArrayRank();
			if(rank != 1)
			{
				GenerateWriteMultidimensionalArray(generator, actualType, elementType);
				return;
			}

			generator.DeclareLocal(typeof(int)); // this is for counter
			generator.DeclareLocal(elementType); // this is for the current element
			generator.DeclareLocal(typeof(int)); // length of the array

			// writing rank
			generator.Emit(OpCodes.Ldarg_1); // primitiveWriter
			generator.Emit(OpCodes.Ldc_I4_1);
			generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(0)));

			// writing length
			generator.Emit(OpCodes.Ldarg_1); // primitiveWriter
			generator.Emit(OpCodes.Ldarg_2); // array to serialize
			generator.Emit(OpCodes.Castclass, actualType);
			generator.Emit(OpCodes.Ldlen);
			generator.Emit(OpCodes.Dup);
			generator.Emit(OpCodes.Stloc_2);
			generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(0)));

			// writing elements
			generator.Emit(OpCodes.Ldc_I4_0);
			generator.Emit(OpCodes.Stloc_0);
			var loopBegin = generator.DefineLabel();
			generator.MarkLabel(loopBegin);
			generator.Emit(OpCodes.Ldarg_2); // array to serialize
			generator.Emit(OpCodes.Castclass, actualType);
			generator.Emit(OpCodes.Ldloc_0); // index
			generator.Emit(OpCodes.Ldelem, elementType);
			generator.Emit(OpCodes.Stloc_1); // we put current element to local variable

			GenerateWriteType(generator, gen =>
			                  {
				gen.Emit(OpCodes.Ldloc_1); // current element
			}, elementType);

			// loop book keeping
			generator.Emit(OpCodes.Ldloc_0); // current index, which will be increased by 1
			generator.Emit(OpCodes.Ldc_I4_1);
			generator.Emit(OpCodes.Add);
			generator.Emit(OpCodes.Stloc_0);
			generator.Emit(OpCodes.Ldloc_0);
			generator.Emit(OpCodes.Ldloc_2); // length of the array
			generator.Emit(OpCodes.Blt, loopBegin);
		}

		private void GenerateWriteMultidimensionalArray(ILGenerator generator, Type actualType, Type elementType)
		{
			Array array = null; // TODO
			PrimitiveWriter primitiveWriter = null; // TODO

			var rank = actualType.GetArrayRank();
			// local for current element
			generator.DeclareLocal(elementType);
			// locals for indices
			var indexLocals = new int[rank];
			for(var i = 0; i < rank; i++)
			{
				indexLocals[i] = generator.DeclareLocal(typeof(int)).LocalIndex;
			}
			// locals for lengths
			var lengthLocals = new int[rank];
			for(var i = 0; i < rank; i++)
			{
				lengthLocals[i] = generator.DeclareLocal(typeof(int)).LocalIndex;
			}

			// writing rank
			generator.Emit(OpCodes.Ldarg_1); // primitiveWriter
			generator.Emit(OpCodes.Ldc_I4, rank);
			generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(0)));

			// writing lengths
			for(var i = 0; i < rank; i++)
			{
				generator.Emit(OpCodes.Ldarg_1); // primitiveWriter
				generator.Emit(OpCodes.Ldarg_2); // array to serialize
				generator.Emit(OpCodes.Castclass, actualType);
				generator.Emit(OpCodes.Ldc_I4, i);
				generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => array.GetLength(0)));
				generator.Emit(OpCodes.Dup);
				generator.Emit(OpCodes.Stloc, lengthLocals[i]);
				generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(0)));
			}

			// writing elements
			GenerateLoop(generator, 0, rank, indexLocals, lengthLocals, actualType, elementType);
		}

		private void GenerateLoop(ILGenerator generator, int currentDimension, int rank, int[] indexLocals, int[] lengthLocals, Type arrayType, Type elementType)
		{
			// initalization
			generator.Emit(OpCodes.Ldc_I4_0);
			generator.Emit(OpCodes.Stloc, indexLocals[currentDimension]);
			var loopBegin = generator.DefineLabel();
			generator.MarkLabel(loopBegin);
			if(currentDimension == rank - 1)
			{
				// writing the element
				generator.Emit(OpCodes.Ldarg_2); // array to serialize
				generator.Emit(OpCodes.Castclass, arrayType);
				for(var i = 0; i < rank; i++)
				{
					generator.Emit(OpCodes.Ldloc, indexLocals[i]);
				}
				generator.Emit(OpCodes.Call, arrayType.GetMethod("Get"));
				generator.Emit(OpCodes.Stloc_0);
				GenerateWriteType(generator, gen => gen.Emit(OpCodes.Ldloc_0), elementType);
			}
			else
			{
				GenerateLoop(generator, currentDimension + 1, rank, indexLocals, lengthLocals, arrayType, elementType);
			}
			// incremeting index and loop exit condition check
			generator.Emit(OpCodes.Ldloc, indexLocals[currentDimension]);
			generator.Emit(OpCodes.Ldc_I4_1);
			generator.Emit(OpCodes.Add);
			generator.Emit(OpCodes.Dup);
			generator.Emit(OpCodes.Stloc, indexLocals[currentDimension]);
			generator.Emit(OpCodes.Ldloc, lengthLocals[currentDimension]);
			generator.Emit(OpCodes.Blt, loopBegin);
		}

		private void GenerateWriteCollection(ILGenerator generator, Type formalElementType, bool isGeneric, bool isGenericallyIterable, bool isIDictionary)
		{
			PrimitiveWriter primitiveWriter = null; // TODO

			var genericTypes = new [] { formalElementType };
			var ifaceType = isGeneric ? typeof(ICollection<>).MakeGenericType(genericTypes) : typeof(ICollection);
			Type enumerableType;
			if(isIDictionary)
			{
				formalElementType = typeof(object); // convenient in our case
				enumerableType = typeof(IDictionary);
			}
			else
			{
				enumerableType = isGenericallyIterable ? typeof(IEnumerable<>).MakeGenericType(genericTypes) : typeof(IEnumerable);
			}
			Type enumeratorType;
			if(isIDictionary)
			{
				enumeratorType = typeof(IDictionaryEnumerator);
			}
			else
			{
				enumeratorType = isGenericallyIterable ? typeof(IEnumerator<>).MakeGenericType(genericTypes) : typeof(IEnumerator);
			}

			generator.DeclareLocal(enumeratorType); // iterator
			generator.DeclareLocal(formalElementType); // current element

			// length of the collection
			generator.Emit(OpCodes.Ldarg_1); // primitiveWriter
			generator.Emit(OpCodes.Ldarg_2); // collection to serialize
			var countMethod = ifaceType.GetProperty("Count").GetGetMethod();
			generator.Emit(OpCodes.Call, countMethod);
			generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(0)));

			// elements
			var getEnumeratorMethod = enumerableType.GetMethod("GetEnumerator");
			generator.Emit(OpCodes.Ldarg_2); // collection to serialize
			generator.Emit(OpCodes.Call, getEnumeratorMethod);
			generator.Emit(OpCodes.Stloc_0);
			var loopBegin = generator.DefineLabel();
			generator.MarkLabel(loopBegin);
			generator.Emit(OpCodes.Ldloc_0);
			var finish = generator.DefineLabel();
			// TODO: Helpers.GetMethod?
			generator.Emit(OpCodes.Call, typeof(IEnumerator).GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public));
			generator.Emit(OpCodes.Brfalse, finish);
			generator.Emit(OpCodes.Ldloc_0);
			if(isIDictionary)
			{
				// key
				generator.Emit(OpCodes.Call, enumeratorType.GetProperty("Key").GetGetMethod());
				generator.Emit(OpCodes.Stloc_1);
				GenerateWriteTypeLocal1(generator, formalElementType);

				// value
				generator.Emit(OpCodes.Ldloc_0);
				generator.Emit(OpCodes.Call, enumeratorType.GetProperty("Value").GetGetMethod());
				generator.Emit(OpCodes.Stloc_1);
				GenerateWriteTypeLocal1(generator, formalElementType);
			}
			else
			{
				generator.Emit(OpCodes.Call, enumeratorType.GetProperty("Current").GetGetMethod());
				generator.Emit(OpCodes.Stloc_1);
	
				// operation on current element
				GenerateWriteTypeLocal1(generator, formalElementType);
			}

			generator.Emit(OpCodes.Br, loopBegin);
			generator.MarkLabel(finish);
		}

		private void GenerateWriteTypeLocal1(ILGenerator generator, Type formalElementType)
		{
			GenerateWriteType(generator, gen =>
			                  {
				gen.Emit(OpCodes.Ldloc_1);
			}, formalElementType);
		}

		private void GenerateWriteType(ILGenerator generator, Action<ILGenerator> putValueToWriteOnTop, Type formalType)
		{
			switch(Helpers.GetSerializationType(formalType))
			{
			case SerializationType.Transient:
				// just omit it
				return;
			case SerializationType.Value:
				GenerateWriteValue(generator, putValueToWriteOnTop, formalType);
				break;
			case SerializationType.Reference:
				GenerateWriteReference(generator, putValueToWriteOnTop, formalType);
				break;
			}
		}

		private void GenerateWriteValue(ILGenerator generator, Action<ILGenerator> putValueToWriteOnTop, Type formalType)
		{
			PrimitiveWriter primitiveWriter = null; // TODO

			if(formalType.IsEnum)
			{
				formalType = Enum.GetUnderlyingType(formalType);
			}
			var writeMethod = typeof(PrimitiveWriter).GetMethod("Write", new [] { formalType });
			// if this method is null, then it is a non-primitive (i.e. custom) struct
			if(writeMethod != null)
			{
				generator.Emit(OpCodes.Ldarg_1); // primitive writer waits there
				putValueToWriteOnTop(generator);
				generator.Emit(OpCodes.Call, writeMethod);
				return;
			}
			var nullableUnderlyingType = Nullable.GetUnderlyingType(formalType);
			if(nullableUnderlyingType != null)
			{
				var hasValue = generator.DefineLabel();
				var finish = generator.DefineLabel();
				var localIndex = generator.DeclareLocal(formalType).LocalIndex;
				generator.Emit(OpCodes.Ldarg_1); // primitiveWriter
				putValueToWriteOnTop(generator);
				generator.Emit(OpCodes.Stloc_S, localIndex);
				generator.Emit(OpCodes.Ldloca_S, localIndex);
				generator.Emit(OpCodes.Call, formalType.GetProperty("HasValue").GetGetMethod());
				generator.Emit(OpCodes.Brtrue_S, hasValue);
				generator.Emit(OpCodes.Ldc_I4_0);
				generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(false)));
				generator.Emit(OpCodes.Br, finish);
				generator.MarkLabel(hasValue);
				generator.Emit(OpCodes.Ldc_I4_1);
				generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(false)));
				GenerateWriteValue(generator, gen =>
				                   {
					generator.Emit(OpCodes.Ldloca_S, localIndex);
					generator.Emit(OpCodes.Call, formalType.GetProperty("Value").GetGetMethod());
				}, nullableUnderlyingType);
				generator.MarkLabel(finish);
				return;
			}
			if(formalType.IsGenericType && formalType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
			{
				var keyValueTypes = formalType.GetGenericArguments();
				var localIndex = generator.DeclareLocal(formalType).LocalIndex;
				GenerateWriteType(generator, gen =>
				                  {
					putValueToWriteOnTop(gen);
					// TODO: is there a better method of getting address?
					// don't think so, looking at
					// http://stackoverflow.com/questions/76274/
					// we *may* do a little optimization if this value write takes
					// place when dictionary is serialized (current KVP is stored in
					// local 1 in such situation); the KVP may be, however, written
					// independently
					gen.Emit(OpCodes.Stloc_S, localIndex);
					gen.Emit(OpCodes.Ldloca_S, localIndex);
					gen.Emit(OpCodes.Call, formalType.GetProperty("Key").GetGetMethod());
				}, keyValueTypes[0]);
				GenerateWriteType(generator, gen =>
				                  {
					// we assume here that the key write was invoked earlier (it should be
					// if we're conforming to the protocol), so KeyValuePair is already
					// stored as local
					gen.Emit(OpCodes.Ldloca_S, localIndex);
					gen.Emit(OpCodes.Call, formalType.GetProperty("Value").GetGetMethod());
				}, keyValueTypes[1]);
				return;
			}
			GenerateWriteFields(generator, putValueToWriteOnTop, formalType);
		}

		private void GenerateWriteReference(ILGenerator generator, Action<ILGenerator> putValueToWriteOnTop, Type formalType)
		{
			ObjectWriter baseWriter = null; // TODO: fake, maybe promote to field
			PrimitiveWriter primitiveWriter = null; // TODO: as above
			object nullObject = null; // TODO: as above

			var finish = generator.DefineLabel();

			putValueToWriteOnTop(generator);
			var isNotNull = generator.DefineLabel();
			generator.Emit(OpCodes.Brtrue_S, isNotNull);
			generator.Emit(OpCodes.Ldarg_1); // primitiveWriter
			generator.Emit(OpCodes.Ldc_I4_M1); // TODO: Consts value
			generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(0)));
			generator.Emit(OpCodes.Br, finish);
			generator.MarkLabel(isNotNull);

			var formalTypeIsActualType = formalType.Attributes.HasFlag(TypeAttributes.Sealed); // TODO: more optimizations?
			if(formalTypeIsActualType)
			{
				var typeId = TypeIndices[formalType];
				generator.Emit(OpCodes.Ldarg_1); // primitiveWriter
				generator.Emit(OpCodes.Ldc_I4, typeId);
				generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(0))); // TODO: get it once
			}
			else
			{
				// we have to get the actual type at runtime
				generator.Emit(OpCodes.Ldarg_1); // primitiveWriter
				generator.Emit(OpCodes.Ldarg_0); // objectWriter
				putValueToWriteOnTop(generator);
				generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => baseWriter.ObjectToTypeId(null))); // TODO: better do type to type id
				generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => primitiveWriter.Write(0))); // TODO: get it once
			}

			// TODO: other opts here?
			// if there is possibity that the target object is transient, we have to check that
			var skipGetId = false;
			var skipTransientCheck = false;
			if(formalTypeIsActualType)
			{
				if(formalType.IsDefined(typeof(TransientAttribute), false))
				{
					skipGetId = true;
				}
				else
				{
					skipTransientCheck = true;
				}
			}

			if(!skipTransientCheck)
			{
				generator.Emit(OpCodes.Ldarg_0); // objectWriter
				putValueToWriteOnTop(generator); // value to serialize
				generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => CheckTransient(nullObject)));
				generator.Emit(OpCodes.Brtrue_S, finish);
			}

			if(!skipGetId)
			{
				// if the formal type is NOT object, then string or array will not be the content of the field
				// TODO: what with the abstract Array type?
				var mayBeInlined = formalType == typeof(object) || Helpers.CanBeCreatedWithDataOnly(formalType);
				generator.Emit(OpCodes.Ldarg_0); // objectWriter
				putValueToWriteOnTop(generator);
				if(mayBeInlined)
				{
					generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => WriteObjectIdPossiblyInline(null)));
				}
				else
				{
					generator.Emit(OpCodes.Call, Helpers.GetMethodInfo(() => WriteObjectId(null)));
				}
			}
			generator.MarkLabel(finish);
		}

		// TODO: actually, this field can be considered static
		private readonly Dictionary<Type, bool> transientTypes;
		private Action<PrimitiveWriter, object>[] writeMethods;

		private static int WriteArrayMethodCounter;
		private static readonly Type[] ParameterTypes = new [] { typeof(GeneratingObjectWriter), typeof(PrimitiveWriter), typeof(object) };
	}
}

