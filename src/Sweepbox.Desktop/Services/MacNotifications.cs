using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Sweepbox.Desktop.Services.MacObjC;

namespace Sweepbox.Desktop.Services;

/// <summary>
/// macOS notifications posted in-process through <c>UNUserNotificationCenter</c>, so they show the
/// Sweepbox name and icon from the app bundle. Built on the Objective-C runtime: typed <c>objc_msgSend</c>
/// calls, global block literals for the completion handlers and a delegate class created at runtime.
/// </summary>
/// <remarks>
/// An Objective-C exception raised inside <c>objc_msgSend</c> aborts the process; .NET cannot catch it.
/// Every call is therefore guarded before it is made: classes are checked for null, methods with
/// <c>respondsToSelector:</c>, and no nil is passed where Apple requires an object. Failures are only
/// traced; nothing here throws to callers or shows anything to the user.
/// </remarks>
[SupportedOSPlatform("macos")]
internal static unsafe class MacNotifications
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string UserNotificationsFramework = "/System/Library/Frameworks/UserNotifications.framework/UserNotifications";
    private const string DelegateClassName = "SweepboxNotificationDelegate";

    // UNAuthorizationOptionSound | UNAuthorizationOptionAlert
    private const nuint AuthorizationOptions = (1 << 1) | (1 << 2);
    // UNNotificationPresentationOptionSound | List | Banner
    private const nuint PresentationOptions = (1 << 1) | (1 << 3) | (1 << 4);

    private static readonly object Gate = new();
    private static bool _initialized;
    private static volatile bool _available;

    private static nint _center;      // [UNUserNotificationCenter currentNotificationCenter]; a singleton, not owned.
    private static nint _delegate;    // +1 from alloc/init and never released: the center holds its delegate weakly.
    private static BlockLiteral* _authorizationHandler;
    private static BlockLiteral* _addHandler;

    private static nint _poolClass, _stringClass, _contentClass, _requestClass;
    private static nint _selAlloc, _selInit, _selDrain, _selRelease, _selStringWithUTF8String, _selUTF8String,
        _selDescription, _selSetTitle, _selSetBody, _selRequestWithIdentifier, _selAddRequest;

    private static int _pending;      // add requests whose completion handler has not fired yet (guarded by Gate)
    private static TaskCompletionSource _idle = CompletedSource();

    /// <summary>
    /// Loads UserNotifications, checks that notifications can be used (an app bundle with an identifier),
    /// sets the delegate that shows notifications while Sweepbox is frontmost and, in the GUI app only,
    /// requests permission (macOS prompts the first time only). Runs once; later calls do nothing.
    /// </summary>
    public static void Initialize(bool requestPermission)
    {
        if (!OperatingSystem.IsMacOS())
            return;
        try
        {
            lock (Gate)
            {
                if (_initialized)
                    return;
                _initialized = true;
                InitializeCore(requestPermission);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"macOS notifications are unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Posts a notification. The task completes when macOS has accepted or refused it (and any other
    /// pending posts have finished), or at once when notifications are unavailable. It never faults.
    /// </summary>
    public static Task Show(string title, string message)
    {
        if (!OperatingSystem.IsMacOS() || !_available)
            return Task.CompletedTask;
        var begun = false;
        try
        {
            var pool = Msg_id(Msg_id(_poolClass, _selAlloc), _selInit);
            if (pool == 0)
                return Task.CompletedTask;
            try
            {
                var titleString = NewString(title);
                var bodyString = NewString(message);
                var identifier = NewString(Guid.NewGuid().ToString());
                if (titleString == 0 || bodyString == 0 || identifier == 0)
                {
                    Trace.WriteLine("macOS notification skipped: could not create its strings.");
                    return Task.CompletedTask;
                }

                var content = Msg_id(Msg_id(_contentClass, _selAlloc), _selInit);
                if (content == 0)
                {
                    Trace.WriteLine("macOS notification skipped: could not create its content.");
                    return Task.CompletedTask;
                }
                nint request;
                try
                {
                    Msg_void_id(content, _selSetTitle, titleString);
                    Msg_void_id(content, _selSetBody, bodyString);
                    // trigger nil = deliver now.
                    request = Msg_id_id_id_id(_requestClass, _selRequestWithIdentifier, identifier, content, 0);
                }
                finally
                {
                    Msg_void(content, _selRelease);
                }
                if (request == 0)
                {
                    Trace.WriteLine("macOS notification skipped: could not create its request.");
                    return Task.CompletedTask;
                }

                Task done;
                lock (Gate)
                {
                    if (_pending++ == 0)
                        _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    done = _idle.Task;
                    begun = true;
                }
                Msg_void_id_id(_center, _selAddRequest, request, (nint)_addHandler);
                return done;
            }
            finally
            {
                Msg_void(pool, _selDrain);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"macOS notification failed: {ex.Message}");
            if (begun)
                EndPending();
            return Task.CompletedTask;
        }
    }

    /// <summary>Waits until every posted notification has been handed to macOS, or until <paramref name="timeout"/>.</summary>
    public static Task WhenIdleAsync(TimeSpan timeout)
    {
        if (!OperatingSystem.IsMacOS())
            return Task.CompletedTask;
        Task idle;
        lock (Gate)
            idle = _idle.Task;
        if (idle.IsCompleted)
            return Task.CompletedTask;
        // (No await here: the class is an unsafe context.) The idle task never faults; a timeout does.
        return idle.WaitAsync(timeout).ContinueWith(
            t =>
            {
                if (t.Exception is not null)
                    Trace.WriteLine($"macOS notifications still pending after {timeout.TotalSeconds:0} s; exiting anyway.");
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void InitializeCore(bool requestPermission)
    {
        // Loading the framework also loads Foundation, which a headless --run has not loaded yet.
        if (!NativeLibrary.TryLoad(UserNotificationsFramework, out _))
        {
            Trace.WriteLine("macOS notifications are unavailable: the UserNotifications framework could not be loaded.");
            return;
        }

        var nsObject = objc_getClass("NSObject");
        var bundleClass = objc_getClass("NSBundle");
        var centerClass = objc_getClass("UNUserNotificationCenter");
        _poolClass = objc_getClass("NSAutoreleasePool");
        _stringClass = objc_getClass("NSString");
        _contentClass = objc_getClass("UNMutableNotificationContent");
        _requestClass = objc_getClass("UNNotificationRequest");
        if (nsObject == 0 || bundleClass == 0 || centerClass == 0 || _poolClass == 0 || _stringClass == 0
            || _contentClass == 0 || _requestClass == 0)
        {
            Trace.WriteLine("macOS notifications are unavailable: a required Objective-C class is missing.");
            return;
        }

        _selAlloc = sel_registerName("alloc");
        _selInit = sel_registerName("init");
        _selDrain = sel_registerName("drain");
        _selRelease = sel_registerName("release");
        _selStringWithUTF8String = sel_registerName("stringWithUTF8String:");
        _selUTF8String = sel_registerName("UTF8String");
        _selDescription = sel_registerName("description");
        _selSetTitle = sel_registerName("setTitle:");
        _selSetBody = sel_registerName("setBody:");
        _selRequestWithIdentifier = sel_registerName("requestWithIdentifier:content:trigger:");
        _selAddRequest = sel_registerName("addNotificationRequest:withCompletionHandler:");
        var selRespondsToSelector = sel_registerName("respondsToSelector:");
        var selInstancesRespond = sel_registerName("instancesRespondToSelector:");
        var selMainBundle = sel_registerName("mainBundle");
        var selBundleIdentifier = sel_registerName("bundleIdentifier");
        var selCurrentCenter = sel_registerName("currentNotificationCenter");
        var selSetDelegate = sel_registerName("setDelegate:");
        var selRequestAuthorization = sel_registerName("requestAuthorizationWithOptions:completionHandler:");
        var selWillPresent = sel_registerName("userNotificationCenter:willPresentNotification:withCompletionHandler:");

        if (Msg_bool_sel(centerClass, selRespondsToSelector, selCurrentCenter) == 0
            || Msg_bool_sel(_stringClass, selRespondsToSelector, _selStringWithUTF8String) == 0
            || Msg_bool_sel(_requestClass, selRespondsToSelector, _selRequestWithIdentifier) == 0
            || Msg_bool_sel(_contentClass, selInstancesRespond, _selSetTitle) == 0
            || Msg_bool_sel(_contentClass, selInstancesRespond, _selSetBody) == 0
            || Msg_bool_sel(centerClass, selInstancesRespond, _selAddRequest) == 0
            || Msg_bool_sel(centerClass, selInstancesRespond, selSetDelegate) == 0
            || Msg_bool_sel(centerClass, selInstancesRespond, selRequestAuthorization) == 0)
        {
            Trace.WriteLine("macOS notifications are unavailable: a required Objective-C method is missing.");
            return;
        }

        if (!NativeLibrary.TryLoad(LibSystem, out var libSystem)
            || !NativeLibrary.TryGetExport(libSystem, "_NSConcreteGlobalBlock", out var globalBlockIsa))
        {
            Trace.WriteLine("macOS notifications are unavailable: _NSConcreteGlobalBlock was not found.");
            return;
        }

        var pool = Msg_id(Msg_id(_poolClass, _selAlloc), _selInit);
        if (pool == 0)
            return;
        try
        {
            // The center may only be touched inside an app bundle: outside one it raises an exception.
            var bundle = Msg_id(bundleClass, selMainBundle);
            if (bundle == 0 || Msg_id(bundle, selBundleIdentifier) == 0)
            {
                Trace.WriteLine("macOS notifications are unavailable: not running from an app bundle.");
                return;
            }

            _center = Msg_id(centerClass, selCurrentCenter);
            if (_center == 0)
            {
                Trace.WriteLine("macOS notifications are unavailable: no notification center.");
                return;
            }

            _authorizationHandler = NewGlobalBlock(globalBlockIsa,
                (delegate* unmanaged<BlockLiteral*, byte, nint, void>)&OnAuthorizationCompleted);
            _addHandler = NewGlobalBlock(globalBlockIsa,
                (delegate* unmanaged<BlockLiteral*, nint, void>)&OnAddCompleted);

            _delegate = CreateDelegate(nsObject, selWillPresent);
            if (_delegate != 0)
                Msg_void_id(_center, selSetDelegate, _delegate);
            else
                Trace.WriteLine("macOS notifications: no delegate, so they are hidden while Sweepbox is frontmost.");

            _available = true;

            if (requestPermission)
                Msg_void_uint_id(_center, selRequestAuthorization, AuthorizationOptions, (nint)_authorizationHandler);
        }
        finally
        {
            Msg_void(pool, _selDrain);
        }
    }

    /// <summary>Creates (once per process) the delegate class and returns a +1 instance of it, or 0.</summary>
    private static nint CreateDelegate(nint nsObject, nint selWillPresent)
    {
        var cls = objc_getClass(DelegateClassName);
        if (cls == 0)
        {
            cls = objc_allocateClassPair(nsObject, DelegateClassName, 0);
            if (cls == 0)
                return 0;
            var imp = (nint)(delegate* unmanaged<nint, nint, nint, nint, BlockLiteral*, void>)&WillPresentNotification;
            if (class_addMethod(cls, selWillPresent, imp, "v@:@@@?") == 0)
            {
                objc_disposeClassPair(cls);
                return 0;
            }
            var protocol = objc_getProtocol("UNUserNotificationCenterDelegate");
            if (protocol != 0)
                class_addProtocol(cls, protocol);
            objc_registerClassPair(cls);
        }
        var instance = Msg_id(cls, _selAlloc);
        return instance == 0 ? 0 : Msg_id(instance, _selInit);
    }

    /// <summary>NSString from a .NET string (autoreleased; never passes NULL to stringWithUTF8String:).</summary>
    private static nint NewString(string? value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value ?? "");
        try
        {
            return Msg_id_ptr(_stringClass, _selStringWithUTF8String, utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary><c>[error description]</c> as text; call inside an autorelease pool.</summary>
    private static string Describe(nint error)
    {
        if (error == 0)
            return "none";
        var description = Msg_id(error, _selDescription);
        var utf8 = description == 0 ? 0 : Msg_id(description, _selUTF8String);
        return utf8 == 0 ? "unknown error" : Marshal.PtrToStringUTF8(utf8) ?? "unknown error";
    }

    private static void EndPending()
    {
        lock (Gate)
        {
            if (_pending > 0 && --_pending == 0)
                _idle.TrySetResult();
        }
    }

    // void (^)(BOOL granted, NSError *error)
    [UnmanagedCallersOnly]
    private static void OnAuthorizationCompleted(BlockLiteral* block, byte granted, nint error)
    {
        try
        {
            var pool = Msg_id(Msg_id(_poolClass, _selAlloc), _selInit);
            try
            {
                Trace.WriteLine($"macOS notification permission: granted={granted != 0}, error={Describe(error)}");
            }
            finally
            {
                if (pool != 0)
                    Msg_void(pool, _selDrain);
            }
        }
        catch
        {
            // Never let an exception unwind into native code.
        }
    }

    // void (^)(NSError *error)
    [UnmanagedCallersOnly]
    private static void OnAddCompleted(BlockLiteral* block, nint error)
    {
        try
        {
            if (error != 0)
            {
                var pool = Msg_id(Msg_id(_poolClass, _selAlloc), _selInit);
                try
                {
                    Trace.WriteLine($"macOS notification was not posted: {Describe(error)}");
                }
                finally
                {
                    if (pool != 0)
                        Msg_void(pool, _selDrain);
                }
            }
        }
        catch
        {
            // Never let an exception unwind into native code.
        }
        finally
        {
            try
            {
                EndPending();
            }
            catch
            {
                // As above.
            }
        }
    }

    // - (void)userNotificationCenter:(UNUserNotificationCenter *)center
    //         willPresentNotification:(UNNotification *)notification
    //           withCompletionHandler:(void (^)(UNNotificationPresentationOptions options))completionHandler
    [UnmanagedCallersOnly]
    private static void WillPresentNotification(nint self, nint cmd, nint center, nint notification, BlockLiteral* completionHandler)
    {
        try
        {
            if (completionHandler == null || completionHandler->Invoke == null)
                return;
            var invoke = (delegate* unmanaged<BlockLiteral*, nuint, void>)completionHandler->Invoke;
            invoke(completionHandler, PresentationOptions);
        }
        catch
        {
            // Never let an exception unwind into native code.
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
