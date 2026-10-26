using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrivateGateway.Client.Infrastructure.Notifications;

/// <summary>
/// Минимум Objective-C для уведомлений macOS: классы и селекторы, отправка сообщений (objc_msgSend нужной формы), строки
/// NSString, массивы, блоки без захвата (глобальные: копировать и освобождать их не нужно) и вызов чужого блока. Исключение
/// Objective-C через P/Invoke завершило бы процесс, поэтому сюда идут только проверенные значения (без nil там, где nil
/// нельзя).
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class ObjC
{
    private const string Runtime = "/usr/lib/libobjc.A.dylib";

    // Флаг глобального блока (Block_private.h): блок живет всегда, Block_copy возвращает его же.
    private const int BlockIsGlobal = 1 << 28;

    private static readonly nint MsgSend = NativeLibrary.GetExport(NativeLibrary.Load(Runtime), "objc_msgSend");
    private static readonly nint GlobalBlockClass = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock");

    /// <summary>Загрузить фреймворк (его классы появляются в runtime только после этого).</summary>
    public static void Load(string framework) => NativeLibrary.Load($"/System/Library/Frameworks/{framework}.framework/{framework}");

    public static nint Class(string name) => objc_getClass(name);

    public static nint Sel(string name) => sel_registerName(name);

    public static nint Send(nint receiver, string selector) =>
        ((delegate* unmanaged<nint, nint, nint>)MsgSend)(receiver, Sel(selector));

    public static nint Send(nint receiver, string selector, nint a) =>
        ((delegate* unmanaged<nint, nint, nint, nint>)MsgSend)(receiver, Sel(selector), a);

    public static nint Send(nint receiver, string selector, nint a, nint b) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint>)MsgSend)(receiver, Sel(selector), a, b);

    public static nint Send(nint receiver, string selector, nint a, nint b, nint c) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)MsgSend)(receiver, Sel(selector), a, b, c);

    public static nint Send(nint receiver, string selector, nint a, nint b, nint c, nint d) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint>)MsgSend)(receiver, Sel(selector), a, b, c, d);

    /// <summary>Новый объект <c>[[класс alloc] init]</c> — свой (+1): освобождать <see cref="Release"/>.</summary>
    public static nint New(string className) => Send(Send(Class(className), "alloc"), "init");

    public static void Release(nint instance) => Send(instance, "release");

    /// <summary>NSString из строки .NET (автоосвобождаемая: жить ей до конца пула).</summary>
    public static nint String(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return Send(Class("NSString"), "stringWithUTF8String:", utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>Строка .NET из NSString; nil — null.</summary>
    public static string? ToString(nint value) => value == 0 ? null : Marshal.PtrToStringUTF8(Send(value, "UTF8String"));

    /// <summary>NSArray из объектов (автоосвобождаемый).</summary>
    public static nint Array(params ReadOnlySpan<nint> items)
    {
        if (items.IsEmpty)
        {
            return Send(Class("NSArray"), "array");
        }

        fixed (nint* first = items)
        {
            return Send(Class("NSArray"), "arrayWithObjects:count:", (nint)first, items.Length);
        }
    }

    /// <summary>Пул автоосвобождения на время вызовов с потока пула .NET: без него автоосвобождаемое там копилось бы.</summary>
    public static AutoreleasePool Pool() => new(objc_autoreleasePoolPush());

    /// <summary>
    /// Глобальный блок для функции <paramref name="invoke"/> (первый параметр — сам блок): память выделяется один раз и не
    /// освобождается, как у блоков без захвата, которые компилятор кладет в программу.
    /// </summary>
    public static nint GlobalBlock(nint invoke)
    {
        var descriptor = (BlockDescriptor*)NativeMemory.Alloc((nuint)sizeof(BlockDescriptor));
        *descriptor = new BlockDescriptor { Size = (nuint)sizeof(BlockLiteral) };
        var block = (BlockLiteral*)NativeMemory.Alloc((nuint)sizeof(BlockLiteral));
        *block = new BlockLiteral { Isa = GlobalBlockClass, Flags = BlockIsGlobal, Invoke = invoke, Descriptor = (nint)descriptor };
        return (nint)block;
    }

    /// <summary>Вызвать чужой блок без параметров: <c>void (^)(void)</c>.</summary>
    public static void Invoke(nint block) => ((delegate* unmanaged<nint, void>)((BlockLiteral*)block)->Invoke)(block);

    /// <summary>Вызвать чужой блок с одним целым параметром: <c>void (^)(NSUInteger)</c>.</summary>
    public static void Invoke(nint block, nint argument) => ((delegate* unmanaged<nint, nint, void>)((BlockLiteral*)block)->Invoke)(block, argument);

    /// <summary>Класс Objective-C на основе NSObject со своими методами; уже есть — он и возвращается.</summary>
    public static nint DefineClass(string name, string protocol, params ReadOnlySpan<(string Selector, nint Implementation, string Types)> methods)
    {
        if (objc_getClass(name) is var existing and not 0)
        {
            return existing;
        }

        var type = objc_allocateClassPair(Class("NSObject"), name, 0);
        foreach (var (selector, implementation, types) in methods)
        {
            _ = class_addMethod(type, Sel(selector), implementation, types);
        }

        if (objc_getProtocol(protocol) is var known and not 0)
        {
            _ = class_addProtocol(type, known);
        }

        objc_registerClassPair(type);
        return type;
    }

    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getProtocol(string name);

    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);

    [LibraryImport(Runtime)]
    private static partial void objc_registerClassPair(nint type);

    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool class_addMethod(nint type, nint selector, nint implementation, string types);

    [LibraryImport(Runtime)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool class_addProtocol(nint type, nint protocol);

    [LibraryImport(Runtime)]
    private static partial nint objc_autoreleasePoolPush();

    [LibraryImport(Runtime)]
    private static partial void objc_autoreleasePoolPop(nint pool);

    /// <summary>Пул автоосвобождения: Dispose освобождает накопленное.</summary>
    public readonly struct AutoreleasePool(nint handle) : IDisposable
    {
        public void Dispose() => objc_autoreleasePoolPop(handle);
    }

    // Устройство блока (Block_private.h): isa, флаги, функция, описание.
    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public nint Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }
}
