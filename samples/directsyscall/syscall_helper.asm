; Direct NT syscall helper used by directsyscall.c.
; The guest image is scanned by ImaginedNT's syscall interceptor, so the syscall
; below is rewritten to INT3 and emulated by the managed NT layer.
PUBLIC NtSyscall

.code
NtSyscall PROC
    mov r10, rcx          ; NT syscall convention: arg0 in r10
    mov eax, edx          ; syscall number
    syscall
    ret
NtSyscall ENDP
END
