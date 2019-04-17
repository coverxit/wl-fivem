using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security;

namespace CitizenFX.Core
{
	[Guid("C068E0AB-DD9C-48F2-A7F3-69E866D27F17")]
	class MonoScriptRuntime : IScriptRuntime, IScriptFileHandlingRuntime, IScriptTickRuntime, IScriptEventRuntime, IScriptRefRuntime, IScriptMemInfoRuntime
	{
		private IScriptHost m_scriptHost;
		private readonly int m_instanceId;
		private InternalManager m_intManager;
		private IntPtr m_parentObject;

		private static readonly Random ms_random = new Random();
		private static readonly AppDomain ms_appDomain = AppDomain.CurrentDomain;

		[SecuritySafeCritical]
		static MonoScriptRuntime()
		{

		}

		public MonoScriptRuntime()
		{
			m_instanceId = ms_random.Next();
		}

		[SecuritySafeCritical]
		public void Create(IScriptHost host)
		{
			try
			{
				m_scriptHost = host;

#if IS_FXSERVER
				string resourceName = "";
				string basePath = "";

				{
					((IScriptHostWithResourceData)host).GetResourceName(out var nameString);

					resourceName = Marshal.PtrToStringAnsi(nameString);

					// we can't invoke natives if not doing this
					InternalManager.ScriptHost = host;

					basePath = Native.API.GetResourcePath(resourceName);
				}

				// Load required assemblies by resource
				ms_appDomain.AssemblyResolve += (sender, args) =>
				{
					var assemblyPath = Path.Combine(basePath, new AssemblyName(args.Name).Name + ".dll");
					if (!File.Exists(assemblyPath))
						return null;

					return Assembly.LoadFile(assemblyPath);
				};
#endif

				m_intManager = (InternalManager)ms_appDomain.CreateInstanceAndUnwrap(
					typeof(InternalManager).Assembly.FullName, typeof(InternalManager).FullName);

				// TODO: figure out a cleaner solution to Mono JIT corruption so server doesn't have to be slower
#if IS_FXSERVER
				m_intManager.SetScriptHost(new WrapScriptHost(host), m_instanceId);
#else
				m_intManager.SetScriptHost(Marshal.GetIUnknownForObject(host), m_instanceId);
#endif
			}
			catch (Exception e)
			{
				Debug.WriteLine(e.ToString());

				if (e.InnerException != null)
				{
					Debug.WriteLine(e.InnerException.ToString());
				}

				throw;
			}
		}

		public void Destroy()
		{
			m_intManager = null;
		}

		public IntPtr GetParentObject()
		{
			return m_parentObject;
		}

		public void SetParentObject(IntPtr ptr)
		{
			m_parentObject = ptr;
		}

		public int GetInstanceId()
		{
			return m_instanceId;
		}

		public int HandlesFile(string filename)
		{
			return (filename.EndsWith(".net.dll") ? 1 : 0);
		}

		[SecuritySafeCritical]
		public void LoadFile(string scriptFile)
		{
			try
			{
				using (GetPushRuntime())
				{
					m_intManager.LoadAssembly(scriptFile);
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine(e.ToString());

				throw;
			}
		}

		[SecuritySafeCritical]
		public void Tick()
		{
			using (GetPushRuntime())
			{
#if IS_FXSERVER
				m_intManager?.Tick();
#else
				// we *shouldn't* do .Id here as that's yet another free remoting call
				ms_fastTickInDomain.method(ms_appDomain);
#endif
			}
		}

		[DllImport("kernel32.dll")]
		private static extern IntPtr LoadLibrary(string dllToLoad);

		[DllImport("kernel32.dll")]
		private static extern IntPtr GetProcAddress(IntPtr hModule, string procedureName);

		private static FastMethod<Action<AppDomain>> ms_fastTickInDomain =
#if IS_FXSERVER
			null;
#else
			BuildFastTick();
#endif

		[SecurityCritical]
		private unsafe static FastMethod<Action<AppDomain>> BuildFastTick()
		{
			var lib = LoadLibrary("citizen-scripting-mono.dll");
			var fn = GetProcAddress(lib, "GI_TickInDomain");

			return new FastMethod<Action<AppDomain>>("TickInDomain", fn);
		}

		public void TriggerEvent(string eventName, byte[] argsSerialized, int serializedSize, string sourceId)
		{
			try
			{
				using (GetPushRuntime())
				{
					m_intManager?.TriggerEvent(eventName, argsSerialized, sourceId);
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine(e.ToString());

				throw;
			}
		}

		public void CallRef(int refIndex, byte[] argsSerialized, int argsSize, out IntPtr retvalSerialized, out int retvalSize)
		{
			retvalSerialized = IntPtr.Zero;
			retvalSize = 0;

			try
			{
				using (GetPushRuntime())
				{
					m_intManager?.CallRef(refIndex, argsSerialized, out retvalSerialized, out retvalSize);
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine(e.ToString());

				throw;
			}
		}

		public int DuplicateRef(int refIndex)
		{
			try
			{
				using (GetPushRuntime())
				{
					return m_intManager?.DuplicateRef(refIndex) ?? 0;
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine(e.ToString());

				throw;
			}
		}

		public void RemoveRef(int refIndex)
		{
			try
			{
				using (GetPushRuntime())
				{
					m_intManager?.RemoveRef(refIndex);
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine(e.ToString());

				throw;
			}
		}

		public void RequestMemoryUsage()
		{
		}

		public void GetMemoryUsage([Out] out ulong memoryUsage)
		{
			using (GetPushRuntime())
			{
				memoryUsage = m_intManager?.GetMemoryUsage() ?? 0;
			}
		}

		public PushRuntime GetPushRuntime()
		{
			return new PushRuntime(this);
		}

		public class WrapIStream : MarshalByRefObject, fxIStream
		{
			private readonly fxIStream m_realStream;

			public WrapIStream(fxIStream realStream)
			{
				m_realStream = realStream;
			}

			public int Read([Out] byte[] data, int size)
			{
				return m_realStream.Read(data, size);
			}

			public int Write(byte[] data, int size)
			{
				return m_realStream.Write(data, size);
			}

			public long Seek(long offset, int origin)
			{
				return m_realStream.Seek(offset, origin);
			}

			public long GetLength()
			{
				return m_realStream.GetLength();
			}
		}

		public class WrapScriptHost : MarshalByRefObject, IScriptHost
		{
			private readonly IScriptHost m_realHost;

			public WrapScriptHost(IScriptHost realHost)
			{
				m_realHost = realHost;
			}

			public void InvokeNative([MarshalAs(UnmanagedType.Struct)] IntPtr context)
			{
				m_realHost.InvokeNative(context);
			}

			[return: MarshalAs(UnmanagedType.Interface)]
			public fxIStream OpenSystemFile([MarshalAs(UnmanagedType.LPStr)] string fileName)
			{
				return new WrapIStream(m_realHost.OpenSystemFile(fileName));
			}

			[return: MarshalAs(UnmanagedType.Interface)]
			public fxIStream OpenHostFile([MarshalAs(UnmanagedType.LPStr)] string fileName)
			{
				return new WrapIStream(m_realHost.OpenHostFile(fileName));
			}

			public IntPtr CanonicalizeRef(int localRef, int instanceId)
			{
				return m_realHost.CanonicalizeRef(localRef, instanceId);
			}

			public void ScriptTrace([MarshalAs(UnmanagedType.LPStr)] string message)
			{
				m_realHost.ScriptTrace(message);
			}

			[SecurityCritical]
			public override object InitializeLifetimeService()
			{
				return null;
			}
		}
	}

	internal class FastMethod<T> where T : Delegate
	{
		[SecuritySafeCritical]
		public FastMethod(string name, IntPtr obj, int midx)
		{
			var vtblStart = Marshal.ReadIntPtr(obj);
			vtblStart += (IntPtr.Size * midx) + (IntPtr.Size * 3);

			Initialize(name, Marshal.ReadIntPtr(vtblStart));
		}

		[SecuritySafeCritical]
		public FastMethod(string name, object obj, Type tint, int midx)
		{
			if (!Marshal.IsComObject(obj))
			{
				throw new ArgumentException("Not a COM interface.");
			}

			var castObj = Marshal.GetComInterfaceForObject(obj, tint);
			var vtblStart = Marshal.ReadIntPtr(castObj);
			vtblStart += (IntPtr.Size * midx) + (IntPtr.Size * 3);

			Initialize(name, Marshal.ReadIntPtr(vtblStart));
		}

		[SecuritySafeCritical]
		public FastMethod(string name, IntPtr fn)
		{
			Initialize(name, fn);
		}

		[SecurityCritical]
		private void Initialize(string name, IntPtr fn)
		{
			var invokeMethod = typeof(T).GetMethod("Invoke");
			var paramTypes = invokeMethod.GetParameters().Select(p => p.ParameterType).ToArray();

			m_method = new DynamicMethod(name,
				invokeMethod.ReturnType, paramTypes, typeof(MonoScriptRuntime).Module, true);
			ILGenerator generator = m_method.GetILGenerator();

			for (int i = 0; i < paramTypes.Length; i++)
			{
				generator.Emit(OpCodes.Ldarg, i);
			}

			generator.Emit(OpCodes.Ldc_I8, fn.ToInt64());
			generator.EmitCalli(OpCodes.Calli, CallingConventions.Standard, invokeMethod.ReturnType, paramTypes, null);
			generator.Emit(OpCodes.Ret);

			method = (T)m_method.CreateDelegate(typeof(T));
		}

		private DynamicMethod m_method;

		public T method;
	}
}
