using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static class CrtJumpApis
{
    // Save the caller's Win64 _JUMP_BUFFER before a managed transition can change its context.
    [PartiallyImaginedApi("api-ms-win-crt-private-l1-1-0.dll", "__intrinsic_setjmp", ApiCategory.Process,
        "ucrtbase!__intrinsic_setjmp", EntryStub = new byte[]
        {
            0x48, 0x89, 0x11,                         // mov [rcx], rdx (Frame)
            0x48, 0x89, 0x59, 0x08,                   // mov [rcx+8], rbx
            0x48, 0x89, 0x69, 0x18,                   // mov [rcx+24], rbp
            0x48, 0x89, 0x71, 0x20,                   // mov [rcx+32], rsi
            0x48, 0x89, 0x79, 0x28,                   // mov [rcx+40], rdi
            0x4C, 0x89, 0x61, 0x30,                   // mov [rcx+48], r12
            0x4C, 0x89, 0x69, 0x38,                   // mov [rcx+56], r13
            0x4C, 0x89, 0x71, 0x40,                   // mov [rcx+64], r14
            0x4C, 0x89, 0x79, 0x48,                   // mov [rcx+72], r15
            0x4C, 0x8D, 0x44, 0x24, 0x08,             // lea r8, [rsp+8]
            0x4C, 0x89, 0x41, 0x10,                   // mov [rcx+16], r8 (Rsp)
            0x4C, 0x8B, 0x04, 0x24,                   // mov r8, [rsp]
            0x4C, 0x89, 0x41, 0x50,                   // mov [rcx+80], r8 (Rip)
            0x0F, 0xAE, 0x59, 0x58,                   // stmxcsr [rcx+88]
            0xD9, 0x79, 0x5C,                         // fnstcw [rcx+92]
            0x66, 0x0F, 0x7F, 0x71, 0x60,             // movdqa [rcx+96], xmm6
            0x66, 0x0F, 0x7F, 0x79, 0x70,             // movdqa [rcx+112], xmm7
            0x66, 0x44, 0x0F, 0x7F, 0x81, 0x80, 0, 0, 0, // movdqa [rcx+128], xmm8
            0x66, 0x44, 0x0F, 0x7F, 0x89, 0x90, 0, 0, 0, // movdqa [rcx+144], xmm9
            0x66, 0x44, 0x0F, 0x7F, 0x91, 0xA0, 0, 0, 0, // movdqa [rcx+160], xmm10
            0x66, 0x44, 0x0F, 0x7F, 0x99, 0xB0, 0, 0, 0, // movdqa [rcx+176], xmm11
            0x66, 0x44, 0x0F, 0x7F, 0xA1, 0xC0, 0, 0, 0, // movdqa [rcx+192], xmm12
            0x66, 0x44, 0x0F, 0x7F, 0xA9, 0xD0, 0, 0, 0, // movdqa [rcx+208], xmm13
            0x66, 0x44, 0x0F, 0x7F, 0xB1, 0xE0, 0, 0, 0, // movdqa [rcx+224], xmm14
            0x66, 0x44, 0x0F, 0x7F, 0xB9, 0xF0, 0, 0, 0, // movdqa [rcx+240], xmm15
            0x33, 0xC0, 0xC3                          // xor eax, eax; ret
        })]
    private static ulong SetJmp(ApiCallContext ctx) =>
        throw new InvalidOperationException($"{ctx.Name} requires its guest entry stub");

    // Restore the Win64 context saved by SetJmp and return from the original setjmp call.
    // RCX = _JUMP_BUFFER, EDX = value (longjmp returns 1 when asked to return 0).
    [PartiallyImaginedApi("api-ms-win-crt-private-l1-1-0.dll", "longjmp", ApiCategory.Process,
        "ucrtbase!longjmp", EntryStub = new byte[]
        {
            0x41, 0x89, 0xD2,                         // mov r10d, edx (return value)
            0x48, 0x8B, 0x11,                         // mov rdx, [rcx] (Frame)
            0x48, 0x8B, 0x59, 0x08,                   // mov rbx, [rcx+8]
            0x48, 0x8B, 0x69, 0x18,                   // mov rbp, [rcx+24]
            0x48, 0x8B, 0x71, 0x20,                   // mov rsi, [rcx+32]
            0x48, 0x8B, 0x79, 0x28,                   // mov rdi, [rcx+40]
            0x4C, 0x8B, 0x61, 0x30,                   // mov r12, [rcx+48]
            0x4C, 0x8B, 0x69, 0x38,                   // mov r13, [rcx+56]
            0x4C, 0x8B, 0x71, 0x40,                   // mov r14, [rcx+64]
            0x4C, 0x8B, 0x79, 0x48,                   // mov r15, [rcx+72]
            0x0F, 0xAE, 0x51, 0x58,                   // ldmxcsr [rcx+88]
            0xD9, 0x69, 0x5C,                         // fldcw [rcx+92]
            0xF3, 0x0F, 0x6F, 0x71, 0x60,             // movdqu xmm6, [rcx+96]
            0xF3, 0x0F, 0x6F, 0x79, 0x70,             // movdqu xmm7, [rcx+112]
            0xF3, 0x44, 0x0F, 0x6F, 0x81, 0x80, 0, 0, 0, // movdqu xmm8, [rcx+128]
            0xF3, 0x44, 0x0F, 0x6F, 0x89, 0x90, 0, 0, 0, // movdqu xmm9, [rcx+144]
            0xF3, 0x44, 0x0F, 0x6F, 0x91, 0xA0, 0, 0, 0, // movdqu xmm10, [rcx+160]
            0xF3, 0x44, 0x0F, 0x6F, 0x99, 0xB0, 0, 0, 0, // movdqu xmm11, [rcx+176]
            0xF3, 0x44, 0x0F, 0x6F, 0xA1, 0xC0, 0, 0, 0, // movdqu xmm12, [rcx+192]
            0xF3, 0x44, 0x0F, 0x6F, 0xA9, 0xD0, 0, 0, 0, // movdqu xmm13, [rcx+208]
            0xF3, 0x44, 0x0F, 0x6F, 0xB1, 0xE0, 0, 0, 0, // movdqu xmm14, [rcx+224]
            0xF3, 0x44, 0x0F, 0x6F, 0xB9, 0xF0, 0, 0, 0, // movdqu xmm15, [rcx+240]
            0x48, 0x8B, 0x61, 0x10,                   // mov rsp, [rcx+16]
            0x44, 0x89, 0xD0,                         // mov eax, r10d
            0x85, 0xC0,                               // test eax, eax
            0x75, 0x02,                               // jnz +2
            0xB0, 0x01,                               // mov al, 1
            0xFF, 0x61, 0x50                          // jmp qword ptr [rcx+80]
        })]
    private static ulong LongJmp(ApiCallContext ctx) =>
        throw new InvalidOperationException($"{ctx.Name} requires its guest entry stub");
}
