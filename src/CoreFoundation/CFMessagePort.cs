//
// CFMessagePort.cs: CFMessagePort is a wrapper around two native Mach ports with bidirectional communication support
//
// Authors:
//   Oleg Demchenko (oleg.demchenko@xamarin.com)
//
// Copyright 2015 Xamarin Inc
//

#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Foundation;
using ObjCRuntime;

using dispatch_queue_t = System.IntPtr;

#if !NET
using NativeHandle = System.IntPtr;
#endif

namespace CoreFoundation {

	// untyped enum from CFMessagePort.h
	// used as a return value of type SInt32 (always 4 bytes)
	public enum CFMessagePortSendRequestStatus {
		Success = 0,
		SendTimeout = -1,
		ReceiveTimeout = -2,
		IsInvalid = -3,
		TransportError = -4,
		BecameInvalidError = -5
	}

	internal class CFMessagePortContext {

		public Func<INativeObject>? Retain { get; set; }

		public Action? Release { get; set; }

		public Func<NSString>? CopyDescription { get; set; }
	}

#if NET
	[SupportedOSPlatform ("ios")]
	[SupportedOSPlatform ("maccatalyst")]
	[SupportedOSPlatform ("macos")]
	[SupportedOSPlatform ("tvos")]
#endif
	public class CFMessagePort : NativeObject {

		// CFMessagePortContext
		[StructLayout (LayoutKind.Sequential)]
		struct ContextProxy {
			/* CFIndex */ nint version; // must be 0
			public /* void * */ IntPtr info;
			public /* CFAllocatorRetainCallBack*/ Func<IntPtr, IntPtr> retain;
			public /* CFAllocatorReleaseCallBack*/ Action<IntPtr> release;
			public /* CFAllocatorCopyDescriptionCallBack*/ Func<IntPtr, IntPtr> copyDescription;
		}

		public delegate NSData CFMessagePortCallBack (int type, NSData data);

		delegate /* CFDataRef */ IntPtr CFMessagePortCallBackProxy (/* CFMessagePortRef */ IntPtr messagePort, /* SInt32 */ int type, /* CFDataRef */ IntPtr data, /* void* */ IntPtr info);

		delegate void CFMessagePortInvalidationCallBackProxy (/* CFMessagePortRef */ IntPtr messagePort, /* void * */ IntPtr info);

		static Dictionary <IntPtr, CFMessagePortCallBack> outputHandles = new Dictionary <IntPtr, CFMessagePortCallBack> (Runtime.IntPtrEqualityComparer);

		static Dictionary <IntPtr, Action?> invalidationHandles = new Dictionary <IntPtr, Action?> (Runtime.IntPtrEqualityComparer);

		static Dictionary <IntPtr, CFMessagePortContext?> messagePortContexts = new Dictionary <IntPtr, CFMessagePortContext?> (Runtime.IntPtrEqualityComparer);

		static CFMessagePortCallBackProxy messageOutputCallback = new CFMessagePortCallBackProxy (MessagePortCallback);

		static CFMessagePortInvalidationCallBackProxy messageInvalidationCallback = new CFMessagePortInvalidationCallBackProxy (MessagePortInvalidationCallback);

		IntPtr contextHandle;

		public bool IsRemote {
			get {
				return CFMessagePortIsRemote (GetCheckedHandle ());
			}
		}

		public string? Name {
			get {
				return CFString.FromHandle (CFMessagePortGetName (GetCheckedHandle ()));
			}
			set {
				var n = CFString.CreateNative (value);
				try {
					CFMessagePortSetName (GetCheckedHandle (), n);
				} finally {
					CFString.ReleaseNative (n);
				}
			}
		}

		public bool IsValid {
			get {
				return CFMessagePortIsValid (GetCheckedHandle ());
			}
		}

		internal CFMessagePortContext? Context {
			get {
				CFMessagePortContext? result;
				ContextProxy context = new ContextProxy ();
				CFMessagePortGetContext (GetCheckedHandle (), ref context);

				if (context.info == IntPtr.Zero)
					return null;
				
				lock (messagePortContexts)
					messagePortContexts.TryGetValue (context.info, out result);

				return result;
			}
		}

		public Action? InvalidationCallback {
			get {
				lock (invalidationHandles) {
					invalidationHandles.TryGetValue (GetCheckedHandle (), out var result);
					return result;
				}
			}
			set {
				lock (invalidationHandles) {
					if (value is null)
						invalidationHandles [GetCheckedHandle ()] = null;
					else
						invalidationHandles.Add (GetCheckedHandle (), value);
				}

				CFMessagePortSetInvalidationCallBack (Handle, messageInvalidationCallback);
			}
		}

		[Preserve (Conditional = true)]
		internal CFMessagePort (NativeHandle handle, bool owns)
			: base (handle, owns)
		{
		}

		protected override void Dispose (bool disposing)
		{
			if (Handle != IntPtr.Zero) {

				lock (outputHandles)
					outputHandles.Remove (Handle);

				lock (invalidationHandles) {
					if (invalidationHandles.ContainsKey (Handle))
						invalidationHandles.Remove (Handle);
				}

				lock (messagePortContexts) {
					if (messagePortContexts.ContainsKey (contextHandle))
						messagePortContexts.Remove (contextHandle);
				}

				contextHandle = IntPtr.Zero;
			}

			base.Dispose (disposing);
		}

		[DllImport (Constants.CoreFoundationLibrary)]
		static extern /* CFMessagePortRef */ IntPtr CFMessagePortCreateLocal (/* CFAllocatorRef */ IntPtr allocator, /* CFStringRef */ IntPtr name, CFMessagePortCallBackProxy callout, /*  CFMessagePortContext */ ref ContextProxy context, [MarshalAs (UnmanagedType.I1)] ref bool shouldFreeInfo);

		[DllImport (Constants.CoreFoundationLibrary)]
		static extern /* CFMessagePortRef */ IntPtr CFMessagePortCreateRemote (/* CFAllocatorRef */ IntPtr allocator, /* CFStringRef */ IntPtr name);

		[DllImport (Constants.CoreFoundationLibrary)]
		static extern void CFMessagePortInvalidate (/* CFMessagePortRef */ IntPtr ms);

		[DllImport (Constants.CoreFoundationLibrary)]
		static extern IntPtr CFMessagePortCreateRunLoopSource (/* CFAllocatorRef */ IntPtr allocator, /* CFMessagePortRef */ IntPtr local, /* CFIndex */ nint order);

		[DllImport (Constants.CoreFoundationLibrary)]
		static extern /* SInt32 */ CFMessagePortSendRequestStatus CFMessagePortSendRequest (/* CFMessagePortRef */ IntPtr remote, /* SInt32 */ int msgid, /* CFDataRef */ IntPtr data, /* CFTiemInterval */ double sendTimeout, /* CFTiemInterval */ double rcvTimeout, /* CFStringRef */ IntPtr replyMode, /* CFDataRef* */ out IntPtr returnData);

		[DllImport (Constants.CoreFoundationLibrary)]
		[return: MarshalAs (UnmanagedType.I1)]
		static extern /* Boolean */ bool CFMessagePortIsRemote (/* CFMessagePortRef */ IntPtr ms);

		[DllImport (Constants.CoreFoundationLibrary)]
		[return: MarshalAs (UnmanagedType.I1)]
		static extern /* Boolean */ bool CFMessagePortSetName (/* CFMessagePortRef */ IntPtr ms, /* CFStringRef */ IntPtr newName);

		[DllImport (Constants.CoreFoundationLibrary)]
		static extern /* CFStringRef */ IntPtr CFMessagePortGetName (/* CFMessagePortRef */ IntPtr ms);

		[DllImport (Constants.CoreFoundationLibrary)]
		static extern void CFMessagePortGetContext (/* CFMessagePortRef */ IntPtr ms, /* CFMessagePortContext* */ ref ContextProxy context);

		[DllImport (Constants.CoreFoundationLibrary)]
		[return: MarshalAs (UnmanagedType.I1)]
		static extern /* Boolean */ bool CFMessagePortIsValid (/* CFMessagePortRef */ IntPtr ms);

		[DllImport (Constants.CoreFoundationLibrary)]
		static extern void CFMessagePortSetDispatchQueue (/* CFMessagePortRef */ IntPtr ms, dispatch_queue_t queue);

		[DllImport (Constants.CoreFoundationLibrary)]
		static extern void CFMessagePortSetInvalidationCallBack (/* CFMessagePortRef */ IntPtr ms, CFMessagePortInvalidationCallBackProxy callout);

		[DllImport (Constants.CoreFoundationLibrary)]
		static extern IntPtr CFMessagePortGetInvalidationCallBack (/* CFMessagePortRef */ IntPtr ms);

		public static CFMessagePort? CreateLocalPort (string? name, CFMessagePortCallBack callback, CFAllocator? allocator = null)
		{
			if (callback is null)
				throw new ArgumentNullException (nameof (callback));
			
			return CreateLocalPort (allocator, name, callback, context: null);
		}
		
		internal static CFMessagePort? CreateLocalPort (CFAllocator? allocator, string? name, CFMessagePortCallBack callback, CFMessagePortContext? context)
		{
			var n = CFString.CreateNative (name);
			bool shouldFreeInfo = false;
			var contextProxy = new ContextProxy ();

			// a GCHandle is needed because we do not have an handle before calling CFMessagePortCreateLocal
			// and that will call the RetainProxy. So using this (short-lived) GCHandle allow us to find back the
			// original context defined by developer
			var shortHandle = GCHandle.Alloc (contextProxy);

			if (context is not null) {
				if (context.Retain is not null)
					contextProxy.retain = RetainProxy;
				if (context.Release is not null)
					contextProxy.release = ReleaseProxy;
				if (context.CopyDescription is not null)
					contextProxy.copyDescription = CopyDescriptionProxy;
				contextProxy.info = (IntPtr)shortHandle;
				lock (messagePortContexts)
					messagePortContexts.Add (contextProxy.info, context);
			}

			try {
				var portHandle = CFMessagePortCreateLocal (allocator.GetHandle (), n, messageOutputCallback, ref contextProxy, ref shouldFreeInfo);

				// TODO handle should free info
				if (portHandle == IntPtr.Zero)
					return null;

				var result = new CFMessagePort (portHandle, true);

				lock (outputHandles)
					outputHandles.Add (portHandle, callback);
				
				if (context is not null) {
					lock (messagePortContexts) {
						messagePortContexts.Remove (contextProxy.info);
						CFMessagePortGetContext (portHandle, ref contextProxy);
						messagePortContexts.Add (contextProxy.info, context);
					}

					result.contextHandle = contextProxy.info;
				}
			
				return result;
			} finally {
				CFString.ReleaseNative (n);

				// we won't need short GCHandle after the Create call
				shortHandle.Free ();
			}
		}

		//
		// Proxy callbacks
		//
		[MonoPInvokeCallback (typeof (Func<IntPtr, IntPtr>))]
		static IntPtr RetainProxy (IntPtr info)
		{
			INativeObject? result = null;
			CFMessagePortContext? context;

			lock (messagePortContexts) {
				messagePortContexts.TryGetValue (info, out context);
			}
			
			if (context?.Retain is not null)
				result = context.Retain ();

			return result.GetHandle ();
		}

		[MonoPInvokeCallback (typeof (Action<IntPtr>))]
		static void ReleaseProxy (IntPtr info)
		{
			CFMessagePortContext? context;

			lock (messagePortContexts)
				messagePortContexts.TryGetValue (info, out context);

			if (context?.Release is not null)
				context.Release ();
		}

		[MonoPInvokeCallback (typeof (Func<IntPtr, IntPtr>))]
		static IntPtr CopyDescriptionProxy (IntPtr info)
		{
			NSString? result = null;
			CFMessagePortContext? context;

			lock (messagePortContexts)
				messagePortContexts.TryGetValue (info, out context);

			if (context?.CopyDescription is not null)
				result = context.CopyDescription ();

			return result.GetHandle ();
		}

		[MonoPInvokeCallback (typeof (CFMessagePortCallBackProxy))]
		static IntPtr MessagePortCallback (IntPtr local, int msgid, IntPtr data, IntPtr info)
		{
			CFMessagePortCallBack callback;

			lock (outputHandles)
				callback = outputHandles [local];

			if (callback is null)
				return IntPtr.Zero;
			
			using (var managedData = Runtime.GetNSObject<NSData> (data)!) {
				var result = callback.Invoke (msgid, managedData);
				// System will release returned CFData
				result?.DangerousRetain ();
				return result.GetHandle ();
			}
		}

		[MonoPInvokeCallback (typeof (CFMessagePortInvalidationCallBackProxy))]
		static void MessagePortInvalidationCallback (IntPtr messagePort, IntPtr info)
		{
			Action? callback;

			lock (invalidationHandles)
				invalidationHandles.TryGetValue (messagePort, out callback);

			if (callback != null)
				callback.Invoke ();
		}

		public static CFMessagePort? CreateRemotePort (CFAllocator? allocator, string name)
		{
			if (name is null)
				throw new ArgumentNullException (nameof (name));

			var n = CFString.CreateNative (name);
			try {
				var portHandle = CFMessagePortCreateRemote (allocator.GetHandle (), n);
				return portHandle == IntPtr.Zero ? null : new CFMessagePort (portHandle, true);
			} finally {
				CFString.ReleaseNative (n);
			}
		}

		public void Invalidate ()
		{
			CFMessagePortInvalidate (GetCheckedHandle ());
		}

		public CFMessagePortSendRequestStatus SendRequest (int msgid, NSData? data, double sendTimeout, double rcvTimeout, NSString? replyMode, out NSData? returnData)
		{
			var result = CFMessagePortSendRequest (GetCheckedHandle (), msgid, data.GetHandle (), sendTimeout, rcvTimeout, replyMode.GetHandle (), out var returnDataHandle);

			returnData = Runtime.GetINativeObject<NSData> (returnDataHandle, false);

			return result;
		}

		public CFRunLoopSource CreateRunLoopSource ()
		{
			// note: order is currently ignored by CFMessagePort object run loop sources. Pass 0 for this value.
			var runLoopHandle = CFMessagePortCreateRunLoopSource (IntPtr.Zero, Handle, 0);
			return new CFRunLoopSource (runLoopHandle, false);
		}

		public void SetDispatchQueue (DispatchQueue? queue)
		{
			CFMessagePortSetDispatchQueue (GetCheckedHandle (), queue.GetHandle ());
		}
	}
}
