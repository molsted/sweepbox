using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Sweepbox.Desktop.Services;

/// <summary>
/// Objective-C runtime and Block ABI helpers for <see cref="MacNotifications"/>. Every <c>objc_msgSend</c>
/// import has the exact C signature of the methods it is used for: arm64 has no varargs for it.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacObjC
{
    private const string LibObjC = "/usr/lib/libobjc.dylib";
    // Block ABI flags: BLOCK_IS_GLOBAL | BLOCK_HAS_SIGNATURE (every clang/Swift block carries a signature;
    // NSXPC reply blocks need one, and a missing one raises an Objective-C exception, which aborts).
    private const int BlockIsGlobal = 1 << 28;
    private const int BlockHasSignature = 1 << 30;

    /// <summary>
    /// A global block (no captures, no copy/dispose helpers) with the type <paramref name="signature"/> clang
    /// would emit, in unmanaged memory and never freed.
    /// </summary>
    public static BlockLiteral* NewGlobalBlock(nint isa, void* invoke, string signature)
    {
        // Lives for the process lifetime: macOS may keep (and copy, a no-op for global blocks) a handler.
        var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
        descriptor->Size = (nuint)sizeof(BlockLiteral);
        descriptor->Signature = (byte*)Marshal.StringToCoTaskMemUTF8(signature); // NUL-terminated, never freed
        var block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
        block->Isa = isa;
        block->Flags = BlockIsGlobal | BlockHasSignature;
        block->Invoke = invoke;
        block->Descriptor = descriptor;
        return block;
    }

    // Block ABI (64-bit): https://clang.llvm.org/docs/Block-ABI-Apple.html
    [StructLayout(LayoutKind.Sequential)]
    public struct BlockLiteral
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public void* Invoke;
        public BlockDescriptor* Descriptor;
    }

    // Without BLOCK_HAS_COPY_DISPOSE the signature follows size directly.
    [StructLayout(LayoutKind.Sequential)]
    public struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
        public byte* Signature;
    }

    // ---- Objective-C runtime ----

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint objc_getClass(string name);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint objc_getProtocol(string name);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint sel_registerName(string name);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);

    [LibraryImport(LibObjC)]
    public static partial void objc_registerClassPair(nint cls);

    [LibraryImport(LibObjC)]
    public static partial void objc_disposeClassPair(nint cls);

    // BOOL class_addMethod(Class, SEL, IMP, const char *types)
    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    public static partial byte class_addMethod(nint cls, nint selector, nint imp, string types);

    // BOOL class_addProtocol(Class, Protocol *)
    [LibraryImport(LibObjC)]
    public static partial byte class_addProtocol(nint cls, nint protocol);

    // objc_msgSend is not variadic on arm64: one exactly typed import per method signature.

    // id (receiver) (no arguments): alloc, init, mainBundle, bundleIdentifier, currentNotificationCenter,
    // description, UTF8String (const char *)
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Msg_id(nint receiver, nint selector);

    // void (no arguments): drain, release
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial void Msg_void(nint receiver, nint selector);

    // + (instancetype)stringWithUTF8String:(const char *)
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Msg_id_ptr(nint receiver, nint selector, nint utf8);

    // - (BOOL)respondsToSelector:(SEL), + (BOOL)instancesRespondToSelector:(SEL)
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial byte Msg_bool_sel(nint receiver, nint selector, nint argument);

    // - (void)setTitle:(NSString *), setBody:(NSString *), setDelegate:(id<UNUserNotificationCenterDelegate>)
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial void Msg_void_id(nint receiver, nint selector, nint argument);

    // + (instancetype)requestWithIdentifier:(NSString *)content:(UNNotificationContent *)trigger:(UNNotificationTrigger *)
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Msg_id_id_id_id(nint receiver, nint selector, nint identifier, nint content, nint trigger);

    // - (void)addNotificationRequest:(UNNotificationRequest *)withCompletionHandler:(void (^)(NSError *))
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial void Msg_void_id_id(nint receiver, nint selector, nint request, nint completionHandler);

    // - (void)requestAuthorizationWithOptions:(UNAuthorizationOptions)completionHandler:(void (^)(BOOL, NSError *))
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial void Msg_void_uint_id(nint receiver, nint selector, nuint options, nint completionHandler);
}
