namespace ImaginedNT.Core.Thunks;

/// <summary>
/// Emits the tiny x64 stubs that make every imported API call flow through managed code.
///
/// Layout of the universal stub's stack frame (relative to rbp):
///   rbp+0x08 return address (into the guest image)
///   rbp+0x10 caller shadow space
///   rbp+0x30 first stack argument
///   rbp-0x80 NativeCallFrame (see NativeCallFrame.cs)
///   rbp-0x80 .. rbp-0x10 available scratch space
///
/// The stub preserves the guest's integer argument registers and XMM0-3 in the frame,
/// calls a managed [UnmanagedCallersOnly] dispatcher with (apiId, &amp;frame), and returns
/// the dispatcher's rax while restoring XMM0-3 (so the dispatcher can synthesize a float
/// return value by writing the frame).
/// </summary>
public static class X64StubEmitter
{
    public const int FrameSize = 0x80;

    public static byte[] EmitUniversalStub(ulong dispatcherAddress)
    {
        var c = new CodeWriter(128);
        c.Raw(0x55);                                     // push rbp
        c.Raw(0x48, 0x89, 0xE5);                         // mov rbp, rsp
        c.Raw(0x48, 0x81, 0xEC, 0x80, 0x00, 0x00, 0x00); // sub rsp, 0x80

        // Save the first four integer args.
        c.Raw(0x48, 0x89, 0x4D, 0x80);                   // mov [rbp-0x80], rcx
        c.Raw(0x48, 0x89, 0x55, 0x88);                   // mov [rbp-0x78], rdx
        c.Raw(0x4C, 0x89, 0x45, 0x90);                   // mov [rbp-0x70], r8
        c.Raw(0x4C, 0x89, 0x4D, 0x98);                   // mov [rbp-0x68], r9

        // Pointer to stack arguments.
        c.Raw(0x48, 0x8D, 0x45, 0x30);                   // lea rax, [rbp+0x30]
        c.Raw(0x48, 0x89, 0x45, 0xA0);                   // mov [rbp-0x60], rax

        // Save XMM0-3.  Frame offsets: 40, 56, 72, 88 -> rbp-0x58, -0x48, -0x38, -0x28.
        c.Raw(0xF3, 0x0F, 0x7F, 0x45, 0xA8);             // movdqu [rbp-0x58], xmm0
        c.Raw(0xF3, 0x0F, 0x7F, 0x4D, 0xB8);             // movdqu [rbp-0x48], xmm1
        c.Raw(0xF3, 0x0F, 0x7F, 0x55, 0xC8);             // movdqu [rbp-0x38], xmm2
        c.Raw(0xF3, 0x0F, 0x7F, 0x5D, 0xD8);             // movdqu [rbp-0x28], xmm3

        // Save return address (frame offset 104 -> rbp-0x18) and api id (112 -> rbp-0x10).
        c.Raw(0x48, 0x8B, 0x45, 0x08);                   // mov rax, [rbp+8]
        c.Raw(0x48, 0x89, 0x45, 0xE8);                   // mov [rbp-0x18], rax
        c.Raw(0x4C, 0x89, 0x5D, 0xF0);                   // mov [rbp-0x10], r11

        // Reserve the 32-byte shadow space required by the Win64 calling convention.  It must
        // sit below the saved frame; otherwise the managed callee may spill its register
        // arguments into our frame and corrupt the arguments we just saved.
        c.Raw(0x48, 0x83, 0xEC, 0x20);                   // sub rsp, 0x20
        c.Raw(0x4C, 0x89, 0xD9);                         // mov rcx, r11
        c.Raw(0x48, 0x8D, 0x55, 0x80);                   // lea rdx, [rbp-0x80]
        c.Raw(0x48, 0xB8);                               // mov rax, imm64
        c.U64(dispatcherAddress);
        c.Raw(0xFF, 0xD0);                               // call rax

        // Restore XMM0-3.  rax already contains the dispatcher result.
        c.Raw(0xF3, 0x0F, 0x6F, 0x45, 0xA8);             // movdqu xmm0, [rbp-0x58]
        c.Raw(0xF3, 0x0F, 0x6F, 0x4D, 0xB8);             // movdqu xmm1, [rbp-0x48]
        c.Raw(0xF3, 0x0F, 0x6F, 0x55, 0xC8);             // movdqu xmm2, [rbp-0x38]
        c.Raw(0xF3, 0x0F, 0x6F, 0x5D, 0xD8);             // movdqu xmm3, [rbp-0x28]

        c.Raw(0xC9);                                     // leave
        c.Raw(0xC3);                                     // ret
        return c.ToArray();
    }

    /// <summary>
    /// The launcher runs the guest entry on a managed thread.  It saves the nonvolatile Win64
    /// registers (which guest code would normally restore in its own epilogue) plus the stack
    /// pointer, then calls the guest entry.  If the guest asks to exit the thread, the exit
    /// trampoline restores that frame and returns through the launcher's epilogue, so the C#
    /// thread finishes normally instead of being killed from inside a managed dispatch frame.
    ///
    /// Win64 calling convention: rcx = guest entry, rdx = 16 integer arguments, r8 = context slot.
    /// </summary>
    public static byte[] EmitGuestLauncher()
    {
        var c = new CodeWriter(320);
        EmitPushNonvolatile(c);
        c.Raw(0x48, 0x83, 0xEC, 0x08);                   // sub rsp, 8 (16-byte call alignment)
        c.Raw(0x48, 0x81, 0xEC, 0xA0, 0x00, 0x00, 0x00); // sub rsp, 0xA0 (XMM6-15 save area)
        EmitSaveXmm(c);
        c.Raw(0x49, 0x89, 0x20);                         // mov [r8], rsp
        c.Raw(0x48, 0x89, 0xC8);                         // mov rax, rcx
        c.Raw(0x49, 0x89, 0xD2);                         // mov r10, rdx
        // Keep outgoing arguments and shadow space below the saved registers.
        c.Raw(0x48, 0x81, 0xEC, 0x80, 0x00, 0x00, 0x00); // sub rsp, 0x80
        for (var i = 4; i < 16; i++)
        {
            c.Raw(0x4D, 0x8B, 0x5A, (byte)(i * 8));      // mov r11, [r10+offset]
            c.Raw(0x4C, 0x89, 0x5C, 0x24, (byte)(i * 8)); // mov [rsp+offset], r11
        }
        c.Raw(0x49, 0x8B, 0x0A);                         // mov rcx, [r10]
        c.Raw(0x49, 0x8B, 0x52, 0x08);                   // mov rdx, [r10+8]
        c.Raw(0x4D, 0x8B, 0x42, 0x10);                   // mov r8, [r10+16]
        c.Raw(0x4D, 0x8B, 0x4A, 0x18);                   // mov r9, [r10+24]
        c.Raw(0xFF, 0xD0);                               // call rax
        c.Raw(0x48, 0x81, 0xC4, 0x80, 0x00, 0x00, 0x00); // add rsp, 0x80
        EmitRestoreXmm(c);
        c.Raw(0x48, 0x81, 0xC4, 0xA0, 0x00, 0x00, 0x00); // add rsp, 0xA0
        c.Raw(0x48, 0x83, 0xC4, 0x08);                   // add rsp, 8
        EmitPopNonvolatile(c);
        c.Raw(0xC3);                                     // ret
        return c.ToArray();
    }

    /// <summary>Shared epilogue used by both the normal guest return and the exit trampoline.</summary>
    public static byte[] EmitThreadRestoreStub()
    {
        var c = new CodeWriter(320);
        EmitRestoreXmm(c);
        c.Raw(0x48, 0x81, 0xC4, 0xA0, 0x00, 0x00, 0x00); // add rsp, 0xA0
        c.Raw(0x48, 0x83, 0xC4, 0x08);                   // add rsp, 8
        EmitPopNonvolatile(c);
        c.Raw(0xC3);                                     // ret
        return c.ToArray();
    }

    private static void EmitPushNonvolatile(CodeWriter c)
    {
        c.Raw(0x53);                                     // push rbx
        c.Raw(0x55);                                     // push rbp
        c.Raw(0x56);                                     // push rsi
        c.Raw(0x57);                                     // push rdi
        c.Raw(0x41, 0x54);                               // push r12
        c.Raw(0x41, 0x55);                               // push r13
        c.Raw(0x41, 0x56);                               // push r14
        c.Raw(0x41, 0x57);                               // push r15
    }

    private static void EmitPopNonvolatile(CodeWriter c)
    {
        c.Raw(0x41, 0x5F);                               // pop r15
        c.Raw(0x41, 0x5E);                               // pop r14
        c.Raw(0x41, 0x5D);                               // pop r13
        c.Raw(0x41, 0x5C);                               // pop r12
        c.Raw(0x5F);                                     // pop rdi
        c.Raw(0x5E);                                     // pop rsi
        c.Raw(0x5D);                                     // pop rbp
        c.Raw(0x5B);                                     // pop rbx
    }

    private static void EmitSaveXmm(CodeWriter c)
    {
        for (var register = 6; register <= 15; register++)
        {
            var offset = (register - 6) * 16;
            if (register >= 8) c.Raw(0x44);              // REX.R
            c.Raw(0x0F, 0x11, (byte)(0x84 | ((register & 7) << 3)), 0x24); // movups [rsp+disp32], xmmN
            c.I32(offset);
        }
    }

    private static void EmitRestoreXmm(CodeWriter c)
    {
        for (var register = 6; register <= 15; register++)
        {
            var offset = (register - 6) * 16;
            if (register >= 8) c.Raw(0x44);              // REX.R
            c.Raw(0x0F, 0x10, (byte)(0x84 | ((register & 7) << 3)), 0x24); // movups xmmN, [rsp+disp32]
            c.I32(offset);
        }
    }

    /// <summary>
    /// Per-thread exit trampoline.  The universal stub has already returned from managed code;
    /// this restores the launcher's stack pointer and jumps to the shared restore stub.
    /// </summary>
    public static byte[] EmitThreadExitTrampoline(ulong trampolineAddress, ulong contextAddress, ulong restoreStubAddress)
    {
        var c = new CodeWriter(32);
        c.Raw(0x49, 0xBB);                               // mov r11, imm64 (preserve exit code in eax)
        c.U64(contextAddress);
        c.Raw(0x49, 0x8B, 0x23);                         // mov rsp, [r11]
        c.Raw(0xE9);                                     // jmp rel32 restore stub
        var nextAddress = trampolineAddress + 18;
        c.I32(unchecked((int)((long)restoreStubAddress - (long)nextAddress)));
        return c.ToArray();
    }

    public static byte[] EmitLazyForwarder(ulong resolverAddress, ulong stubAddress)
    {
        var c = new CodeWriter(256);
        // The aligned target slot initially enters the resolver, then points at the bound thunk.
        c.Raw(0xFF, 0x25, 0x02, 0x00, 0x00, 0x00, 0x90, 0x90); // jmp [rip+2]; nop; nop
        c.U64(stubAddress + 16);
        c.Raw(EmitNativeForwarder(resolverAddress, stubAddress));
        return c.ToArray();
    }

    public static byte[] EmitNativeForwarder(ulong observerAddress, ulong targetAddress)
    {
        var c = new CodeWriter(256);
        // Keep the original arguments and stack intact, including floating-point/variadic calls.
        c.Raw(0x50, 0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53);
        c.Raw(0x48, 0x81, 0xEC, 0x90, 0x00, 0x00, 0x00); // sub rsp, 0x90 (shadow space + saves)
        for (var i = 0; i < 6; i++)
        {
            c.Raw(0x0F, 0x11, (byte)(0x84 | (i << 3)), 0x24);
            c.I32(32 + i * 16);                         // movups [rsp+offset], xmmN
        }
        c.Raw(0x65, 0x8B, 0x04, 0x25, 0x68, 0x00, 0x00, 0x00); // mov eax, gs:[0x68] (LastErrorValue)
        c.Raw(0x89, 0x84, 0x24, 0x80, 0x00, 0x00, 0x00);
        c.Raw(0x48, 0xB8);
        c.U64(observerAddress);
        c.Raw(0xFF, 0xD0);                             // call rax
        c.Raw(0x48, 0x85, 0xC0);                       // test rax, rax (negative = complemented exit trampoline)
        c.Raw(0x79, 0x05);                             // jns +5
        c.Raw(0x48, 0xF7, 0xD0, 0xFF, 0xE0);           // not rax; jmp rax
        c.Raw(0x8B, 0x84, 0x24, 0x80, 0x00, 0x00, 0x00);
        c.Raw(0x65, 0x89, 0x04, 0x25, 0x68, 0x00, 0x00, 0x00); // restore LastErrorValue
        for (var i = 0; i < 6; i++)
        {
            c.Raw(0x0F, 0x10, (byte)(0x84 | (i << 3)), 0x24);
            c.I32(32 + i * 16);
        }
        c.Raw(0x48, 0x81, 0xC4, 0x90, 0x00, 0x00, 0x00);
        c.Raw(0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5A, 0x59, 0x58);
        c.Raw(0xFF, 0x25, 0x00, 0x00, 0x00, 0x00);      // jmp [rip] (native callee returns directly to guest)
        c.U64(targetAddress);
        return c.ToArray();
    }

    internal static byte[] EmitExceptionFilter(ulong handlerAddress, ulong headAddress)
    {
        // Host exceptions can arrive in cooperative GC mode, before entering a CLR callback is legal.
        var c = new CodeWriter(80);
        c.Raw(0x48, 0x85, 0xC9);                         // test rcx, rcx
        c.Raw(0x74, 0x42);                               // jz miss
        c.Raw(0x48, 0x8B, 0x01);                         // mov rax, [rcx] (EXCEPTION_RECORD)
        c.Raw(0x48, 0x85, 0xC0);                         // test rax, rax
        c.Raw(0x74, 0x3A);                               // jz miss
        c.Raw(0x81, 0x38, 0x03, 0x00, 0x00, 0x80);       // cmp dword ptr [rax], EXCEPTION_BREAKPOINT
        c.Raw(0x75, 0x32);                               // jne miss
        c.Raw(0x48, 0x8B, 0x50, 0x10);                   // mov rdx, [rax+16] (ExceptionAddress)
        c.Raw(0x48, 0x85, 0xD2);                         // test rdx, rdx (cleared nodes contain zero)
        c.Raw(0x74, 0x29);                               // jz miss
        c.Raw(0x49, 0xB8);                               // mov r8, head slot
        c.U64(headAddress);
        c.Raw(0x4D, 0x8B, 0x00);                         // mov r8, [r8]
        c.Raw(0x4D, 0x85, 0xC0);                         // loop: test r8, r8
        c.Raw(0x74, 0x17);                               // jz miss
        c.Raw(0x49, 0x3B, 0x50, 0x08);                   // cmp rdx, [r8+8]
        c.Raw(0x74, 0x05);                               // je hit
        c.Raw(0x4D, 0x8B, 0x00);                         // mov r8, [r8] (next)
        c.Raw(0xEB, 0xF0);                               // jmp loop
        c.Raw(0x48, 0xB8);                               // hit: mov rax, handler
        c.U64(handlerAddress);
        c.Raw(0xFF, 0xE0);                               // jmp rax (RCX still holds EXCEPTION_POINTERS)
        c.Raw(0x31, 0xC0, 0xC3);                         // miss: xor eax, eax; ret
        return c.ToArray();
    }

    /// <summary>
    /// Emit a COM forwarding trampoline. The guest calls the stub with <c>this</c> in RCX; the stub
    /// replaces it with the host interface pointer and jumps to the host vtable entry, leaving all
    /// other argument registers, XMM registers and the stack untouched.
    /// </summary>
    public static byte[] EmitComForwarder(ulong hostThis, ulong hostTarget)
    {
        var c = new CodeWriter(32);
        c.Raw(0x48, 0xB9);                               // mov rcx, imm64
        c.U64(hostThis);
        c.Raw(0x48, 0xB8);                               // mov rax, imm64
        c.U64(hostTarget);
        c.Raw(0xFF, 0xE0);                               // jmp rax
        return c.ToArray();
    }

    public static byte[] EmitApiStub(ulong apiId, ulong stubAddress, ulong commonAddress)
    {
        var c = new CodeWriter(16);
        c.Raw(0x49, 0xBB);                               // mov r11, imm64
        c.U64(apiId);
        var jmpNext = stubAddress + 15;
        var relative = (int)((long)commonAddress - (long)jmpNext);
        c.Raw(0xE9);                                     // jmp rel32
        c.I32(relative);
        while (c.Length < 16) c.Raw(0x90);               // pad with NOP
        return c.ToArray();
    }

    private sealed class CodeWriter(int capacity)
    {
        private readonly List<byte> _bytes = new(capacity);

        public int Length => _bytes.Count;

        public void Raw(params byte[] values) => _bytes.AddRange(values);

        public void U64(ulong value)
        {
            for (var i = 0; i < 8; i++) _bytes.Add((byte)(value >> (i * 8)));
        }

        public void I32(int value)
        {
            var u = unchecked((uint)value);
            for (var i = 0; i < 4; i++) _bytes.Add((byte)(u >> (i * 8)));
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
